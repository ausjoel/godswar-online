"""modules/maxer.py - bring one offline character to the top in a single change with one Undo. Backend for static/modules/maxer.js.

Preview first. Apply needs the character's name typed back. It stays within what the game can load: skills the class
may learn at the chosen level, talents up to the client's rank, gear up to what each item forges to, main quests of the
character's camp up to the level. Picking gear is not its job (use Favourites), nor filling zodiac grids (a player choice).
The warehouse is left out: a database trigger only accepts an increase with a warehouse_expansion_settlements row.
"""
from . import character, common, inventory, skills
from .common import Bad, NotFound, Refused, update_sql

M = common.module("maxer", "Character maxer", icon={"sprite": 684}, order=28, status="ready", needs_character=True,
                  description="Level, skills, talents, zodiac, gear quality, pet shed and main quests for one offline character, with one Undo.")

PET_SHED_MAX = 8                    # ck_character_base_pet_shed_capacity
FIELDS = ["character_base.fighter_job_lv", "character_base.fighter_job_exp", "character_base.zodiac_level", "character_base.zodiac_energy",
          "character_base.pet_shed_capacity", "character_skills.*", "character_talents.*",
          "character_items.*", "character_quests.*"]
DEFAULTS = {"level": character.LEVEL_CAP, "skills": True, "talent_rank": skills.CLIENT_TALENT_CAP, "zodiac": True, "gear": True, "capacity": True, "quests": True}


def options(raw):
    o = dict(DEFAULTS)
    for k, v in (raw or {}).items():
        if k not in DEFAULTS:
            raise Bad("unknown option %s" % k)
        o[k] = v
    o["level"] = common.to_int(o["level"], 1, character.LEVEL_CAP, "level")
    o["talent_rank"] = common.to_int(o["talent_rank"], 0, skills.TALENT_RANK_CAP, "talent rank")
    for k in ("skills", "zodiac", "gear", "capacity", "quests"):
        o[k] = bool(o[k])
    return o


def skill_targets(families, level):
    """{family: top member usable at this level}. A family the level can't reach yet has no target."""
    out = {}
    for base, members in families.items():
        usable = [s for s in members if (s.get("minlv") or 1) <= level]
        if usable:
            out[base] = usable[-1]
    return out


def main_quests(rules, camp, level):
    """Ids of the main-line quests of this camp up to this level. Daily and repeatable ones never count as finished."""
    return sorted(int(q["id"]) for q in rules.values()
                  if q.get("main") and not q.get("daily") and not q.get("repeat") and (q.get("minlv") or 1) <= level
                  and (not q.get("camp") or q["camp"] == camp))


def plan(d, o, families, talents, rules, client_items, meta_of):
    """(SQL statements, what changes) for one rb.dump(). Nothing is written here."""
    b = d["character_base"][0]
    cid = int(b["id"])
    sql, changes = [], []
    values = {}
    if int(b["fighter_job_lv"]) != o["level"] or int(b["fighter_job_exp"]) != 0:
        values.update(fighter_job_lv=o["level"], fighter_job_exp=0)
        changes.append("Level %s to %d, EXP 0" % (b["fighter_job_lv"], o["level"]))
    if o["zodiac"]:
        top = len(character.ZODIAC_CAPS)
        if int(b["zodiac_level"]) != top or int(b["zodiac_energy"]) != character.zodiac_cap(top):
            values.update(zodiac_level=top, zodiac_energy=character.zodiac_cap(top))
            changes.append("Zodiac level %d, energy full" % top)
    if o["capacity"]:
        if int(b.get("pet_shed_capacity") or 0) != PET_SHED_MAX:
            values["pet_shed_capacity"] = PET_SHED_MAX
            changes.append("Pet shed %d" % PET_SHED_MAX)
    if values:
        sql.append(update_sql("character_base", {"id": cid}, values))
    if o["skills"]:
        have = {int(r["skill_id"]) for r in d.get("character_skills") or []}
        learned = 0
        for base, s in sorted(skill_targets(families, o["level"]).items()):
            ids = {m["id"] for m in families[base]}
            if have & ids == {s["id"]}:
                continue
            if have & ids:
                sql.append("DELETE FROM character_skills WHERE user_id = %d AND skill_id IN (%s)" % (cid, ",".join(map(str, sorted(ids)))))
            sql.append("INSERT INTO character_skills (user_id, skill_id, skill_level, source) VALUES (%d, %d, %d, 'gwadmin-web')" % (cid, s["id"], max(1, int(s.get("level") or 1))))
            learned += 1
        if learned:
            changes.append("%d skill families to their top rank" % learned)
    if o["talent_rank"]:
        cur = {int(r["talent_id"]): int(r["rank"]) for r in d.get("character_talents") or []}
        moved = 0
        for t in talents:
            if cur.get(t["id"], 0) >= o["talent_rank"]:
                continue
            sql.append("INSERT INTO character_talents (user_id, talent_id, rank, updated_at) VALUES (%d, %d, %d, now()) "
                       "ON CONFLICT (user_id, talent_id) DO UPDATE SET rank = EXCLUDED.rank, updated_at = now(), "
                       "outbox_revision = character_talents.outbox_revision + 1" % (cid, t["id"], o["talent_rank"]))
            moved += 1
        if moved:
            changes.append("%d talents to rank %d" % (moved, o["talent_rank"]))
    if o["gear"]:
        raised = 0
        for r in d.get("character_items") or []:
            if r["item_location"] != inventory.LOC_EQUIP or meta_of(int(r["prop_id"]))["slot"] < 0:
                continue
            cli = client_items.get(str(r["prop_id"])) or {}
            mq, mg = int(cli.get("max_quality") or 1), int(cli.get("max_grade") or 1)
            q, g = max(int(r["item_quality"]), mq), max(int(r["item_grade"]), mg)
            if (q, g) != (int(r["item_quality"]), int(r["item_grade"])):
                sql.append("UPDATE character_items SET item_quality = %d, item_grade = %d, updated_at = now() WHERE id = %d AND user_id = %d" % (q, g, int(r["id"]), cid))
                raised += 1
        if raised:
            changes.append("%d worn items to their top quality and grade" % raised)
    if o["quests"]:
        camp = skills.CAMP_KEYS.get(int(b["camp"]))
        done = {int(r["quest_id"]) for r in d.get("character_quests") or [] if int(r["state"]) == 1}
        todo = [q for q in main_quests(rules, camp, o["level"]) if q not in done]
        for q in todo:
            sql.append("INSERT INTO character_quests (character_id, quest_id, state, progress) VALUES (%d, %d, 1, 0) "
                       "ON CONFLICT (character_id, quest_id) DO UPDATE SET state = 1, progress = 0" % (cid, q))
        if todo:
            changes.append("%d main quests marked done" % len(todo))
    return sql, changes


def _inputs(ctx, cid):
    rb = ctx.need_rb()
    d = rb.dump(cid)
    if not d.get("character_base"):
        raise NotFound("no such character")
    b = d["character_base"][0]
    if b.get("lifecycle_state") != "active":
        raise Refused("%s is deleted" % b["name"])
    prof = int(b["profession"])
    return rb, d, skills.families_for(ctx, prof), skills.talents_for(ctx, prof), skills.quest_rules()["quests"]


@M.post(r"preview/(\d+)")
def preview(ctx, cid):
    cid = int(cid)
    o = options(ctx.body.get("options"))
    rb, d, fams, talents, rules = _inputs(ctx, cid)
    _sql, changes = plan(d, o, fams, talents, rules, rb.client()["items"], rb.item_meta)
    b = d["character_base"][0]
    return {"name": b["name"], "level": b["fighter_job_lv"], "class": common.CLASS_NAMES.get(b["profession"]), "changes": changes,
            "online": common.online_info(ctx.app, cid)["online"]}


@M.post(r"apply/(\d+)")
def apply(ctx, cid):
    cid = int(cid)
    o = options(ctx.body.get("options"))
    rb, d, fams, talents, rules = _inputs(ctx, cid)
    name = d["character_base"][0]["name"]
    if ctx.body.get("confirm") != name:
        raise Bad("type the character's name, %s, to confirm" % name)

    def build(now):
        sql, _changes = plan(now, o, fams, talents, rules, rb.client()["items"], rb.item_meta)
        if not sql:
            raise Bad("nothing to change: %s is already there" % name)
        return sql
    _sql, changes = plan(d, o, fams, talents, rules, rb.client()["items"], rb.item_meta)
    r = ctx.char_write(cid, "maxer: " + "; ".join(changes)[:300], build, fields=FIELDS)
    r["changes"] = changes
    return r
