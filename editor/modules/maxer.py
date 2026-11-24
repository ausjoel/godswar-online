"""modules/maxer.py - bring one offline character to the top in a single change with one Undo. Backend for static/modules/maxer.js.

Preview first. Apply needs the character's name typed back. It stays within what the game can load: skills the class
may learn at the chosen level, talents up to the client's rank, gear up to what each item forges to, main quests of the
character's camp up to the level. Picking gear is not its job (use Favourites or Worn gear presets), nor filling zodiac
grids (a player choice). The warehouse is left out: a database trigger only accepts an increase with a
warehouse_expansion_settlements row.

W23 (the admin 2026-11-24):
  * "For main quest finisher in the editor, add all awards to the prize chest / to character." Every main quest the
    finisher marks done also pays what its hand-in pays (GameClientHandler.Quests.cs, in the same order): the quest's
    EXP / Talent EXP / Silver / Gold (quest_reward_values when the GM tool has a row, else the quest's own values from
    StarterQuestChain), the quest EXP appraisal bonus, the server_rates quest multipliers with the hand-in caps; the
    reward item (one slot per quest, as the game pays one; the class's own item where the menu offers a choice), the
    Mycenae boss crystals, and the quest title (TitleExpertPolicy.QuestTitles). Items go into the bag the way the
    server grants items (GrantVariantAsync: top up matching stacks, then free cells 0-95, never above 99); what does
    not fit goes to the Prize Chest (prize_chest_entries). Quests carry no skills. Already finished quests pay nothing.
  * "check account maxer - stats class appropriate? sanity checking gear that gets maxed." The plan is applied to a
    copy of the character's rows and checked with the Natural check and Item legality rules before anything is
    written; a change that would add a finding is refused. Worn gear the class cannot use, or above the level, is left
    alone; quality / grade go exactly to what the item forges to (never kept above it). The maxer never lowers a level.
"""
import copy
import json
import os

from . import character, common, inventory, skills
from .common import Bad, NotFound, Refused, update_sql

M = common.module("maxer", "Character maxer", icon={"sprite": 684}, order=28, status="ready", needs_character=True,
                  description="Level, skills, talents, zodiac, gear quality, pet shed and main quests with their rewards for one offline character, with one Undo.")

PET_SHED_MAX = 8                    # ck_character_base_pet_shed_capacity
MAX_STACK = 99                      # the admin's rule: no stack above 99 (also GrantVariantAsync DefaultStackCap)
BAG_SLOTS = 96                      # KitBagSlotCount
TITLE_LIMIT = 128                   # CharacterSnapshotLimits.OwnedTitleCount (title_grants ownedLimit)
QUEST_RATE_CAP = 100_000_000        # GameClientHandler.Quests.cs QuestRateCap (Talent / Silver / Gold)
QUEST_EXP_CAP = 500_000_000         # GameClientHandler.Quests.cs QuestExperienceCap
I32 = 2 ** 31 - 1
RATE_KEYS = {"exp": "exp.quest", "tp": "tp.quest", "silver": "money.quest_silver", "gold": "money.quest_gold"}
TITLE_SWITCH = "npc.title_expert"
FIELDS = ["character_base.fighter_job_lv", "character_base.fighter_job_exp", "character_base.zodiac_level", "character_base.zodiac_energy",
          "character_base.pet_shed_capacity", "character_base.Money", "character_base.Stone", "character_base.SkillPoint",
          "character_base.SkillExp", "character_skills.*", "character_talents.*", "character_items.*", "character_quests.*"]
CHEST_FIELD, TITLE_FIELD = "prize_chest_entries.*", "character_title_grants.*"
DEFAULTS = {"level": character.LEVEL_CAP, "skills": True, "talent_rank": skills.CLIENT_TALENT_CAP, "zodiac": True, "gear": True,
            "capacity": True, "quests": True, "rewards": True}
HERE = os.path.dirname(os.path.abspath(__file__))
_REWARDS = {}


def options(raw):
    o = dict(DEFAULTS)
    for k, v in (raw or {}).items():
        if k not in DEFAULTS:
            raise Bad("unknown option %s" % k)
        o[k] = v
    o["level"] = common.to_int(o["level"], 1, character.LEVEL_CAP, "level")
    o["talent_rank"] = common.to_int(o["talent_rank"], 0, skills.TALENT_RANK_CAP, "talent rank")
    for k in ("skills", "zodiac", "gear", "capacity", "quests", "rewards"):
        o[k] = bool(o[k])
    return o


def quest_reward_data():
    """skills_data/quest_rewards.json (build_quest_rewards.py): reward item menus, quest titles, Mycenae crystals."""
    if "data" not in _REWARDS:
        try:
            with open(os.path.join(HERE, "skills_data", "quest_rewards.json"), encoding="utf-8") as f:
                _REWARDS["data"] = json.load(f)
        except (OSError, ValueError) as e:
            _REWARDS["data"] = {"items": {}, "titles": {}, "mycenae": {"quests": [], "item": 0, "quantity": 0},
                                "error": "skills_data/quest_rewards.json: %s" % e}
    return _REWARDS["data"]


# =========================================================================== pure game rules (ported, unit-tested)
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


def rate_multiply(value, rate, maximum):
    """ServerRates.Multiply: value x rate, truncated; a rate of 1 or a value <= 0 passes unchanged; capped."""
    value = int(value)
    rate = float(rate if rate is not None else 1)
    if rate == 1 or value <= 0:
        return value
    scaled = int(value * rate)
    if scaled <= 0:
        return 0
    return maximum if scaled >= maximum else scaled


def quest_payout(rule, override, rates, appraisal_bp=0):
    """What one hand-in pays in EXP / Talent EXP / Silver / Gold (GameClientHandler.Quests.cs): the GM override row when
    there is one, else the quest's own values; the rates on Talent / Silver / Gold; the appraisal bonus, then the rate,
    on EXP."""
    src = override or rule
    base = {k: max(0, int(src.get(k) or 0)) for k in ("exp", "tp", "silver", "gold")}
    out = {k: rate_multiply(base[k], rates.get(RATE_KEYS[k], 1), QUEST_RATE_CAP) for k in ("tp", "silver", "gold")}
    exp = base["exp"]
    if exp > 0 and appraisal_bp > 0:
        exp = min(I32, exp * (10000 + int(appraisal_bp)) // 10000)
    out["exp"] = rate_multiply(exp, rates.get(RATE_KEYS["exp"], 1), QUEST_EXP_CAP)
    return out


def apply_exp(level, exp, gained, sealed=False, cap_level=character.LEVEL_CAP):
    """PlayerExperienceCatalog.Apply, with the maxer's own ceiling: levels never pass cap_level (the level chosen on the
    page) and EXP stops one short of the next level there. Returns (level, exp)."""
    level = min(max(1, int(level)), character.LEVEL_CAP)
    exp = min(max(0, int(exp)), character.UINT32_MAX)
    gained = max(0, int(gained))
    if sealed:
        return level, exp + min(gained, character.UINT32_MAX - exp)
    if level >= character.LEVEL_CAP or gained == 0:
        return level, exp
    cap_level = min(max(level, int(cap_level)), character.LEVEL_CAP)
    acc = exp + gained
    while level < cap_level and acc >= character.EXP_NEXT[level - 1]:
        acc -= character.EXP_NEXT[level - 1]
        level += 1
    if level >= character.LEVEL_CAP:
        acc = 0                                             # the server keeps no EXP at the top level
    elif level >= cap_level:
        acc = min(acc, character.EXP_NEXT[level - 1] - 1)
    return level, min(acc, character.UINT32_MAX)


def apply_talent_exp(talent_exp, points, gained):
    """TalentExperienceCatalog.Apply: 100 Talent EXP make one talent point. Returns (talent_exp, points)."""
    talent_exp, points, gained = int(talent_exp), int(points), max(0, int(gained))
    cap = (I32 - points) * 100 + 99 - talent_exp
    acc = talent_exp + min(gained, cap)
    return acc % 100, points + acc // 100


def reward_menu(qid, static_items, db_slots):
    """[{slot, item, quality, grade, attrs}] one hand-in offers: the GM override (quest_reward_slots) replaces the whole
    menu (PacketBuilder.ApplyRewardSlotOverrides), else the quest's own captured menu."""
    rows = db_slots.get(qid) if db_slots else None
    if rows:
        return [{"slot": int(r["slot_index"]), "item": int(r["item_id"]),
                 "quality": r.get("item_quality"), "grade": r.get("item_grade"),
                 "attrs": [(r.get("attribute%d" % i), r.get("attribute_level%d" % i)) for i in range(1, 6)]}
                for r in sorted(rows, key=lambda r: int(r["slot_index"]))]
    return [{"slot": int(x["slot"]), "item": int(x["item"]), "quality": None, "grade": None, "attrs": []}
            for x in static_items.get(str(qid)) or []]


def pick_slot(menu, profession, meta_of):
    """The game pays ONE slot per hand-in (the client's pick; the offer is cleared after one claim). A one-item menu
    pays that item; a choice pays the first item this class may use (quest 519 = the four class weapons), else slot 0.
    Returns (chosen or None, [skipped])."""
    if not menu:
        return None, []
    pick = next((m for m in menu if profession in (meta_of(m["item"]).get("classes") or [])
                 and len(meta_of(m["item"]).get("classes") or []) < 4), None) \
        or next((m for m in menu if not meta_of(m["item"]).get("classes") or profession in meta_of(m["item"])["classes"]), None) \
        or menu[0]
    return pick, [m for m in menu if m is not pick]


def place_items(rows, grants, policy):
    """GrantVariantAsync for the editor: top up bag stacks of exactly that item, binding, quality and grade (no added
    attributes) to min(Overlap, 99), then free cells 0-95. What does not fit goes to the Prize Chest in stacks of at
    most 99. rows = the character's item rows; policy(item) -> {"overlap", "bound"}.
    Returns {"updates": [(row, new_stack, added, grant)], "inserts": [(slot, grant, stack)], "chest": [(grant, qty)]}."""
    bag = [r for r in rows if int(r["item_location"]) == inventory.LOC_BAG]
    occupied = {int(r["slot_index"]) for r in bag}
    stacks = {int(r["id"]): int(r["stack"]) for r in bag}
    out = {"updates": [], "inserts": [], "chest": []}
    for g in grants:
        cap = max(1, min(int(policy(g["item"])["overlap"] or 1), MAX_STACK))
        left = int(g["qty"])
        if not any(a for a, _ in g["attrs"] if a is not None):
            for r in bag:
                if left <= 0:
                    break
                if (int(r["prop_id"]), int(r["bound"]), int(r["item_quality"]), int(r["item_grade"])) != \
                        (g["item"], g["bound"], g["quality"], g["grade"]) or any(r.get("attribute%d" % i) is not None for i in range(1, 6)):
                    continue
                room = cap - stacks[int(r["id"])]
                if room <= 0:
                    continue
                add = min(room, left)
                stacks[int(r["id"])] += add
                left -= add
                out["updates"].append((r, stacks[int(r["id"])], add, g))
        for slot in range(BAG_SLOTS):
            if left <= 0:
                break
            if slot in occupied:
                continue
            occupied.add(slot)
            n = min(left, cap)
            left -= n
            out["inserts"].append((slot, g, n))
        while left > 0:
            n = min(left, MAX_STACK)
            left -= n
            out["chest"].append((g, n))
    return out


def gear_target(current, max_quality, max_grade, caps):
    """(quality, grade) the maxer gives one worn item. Each goes to what the item forges to (the client's max_quality /
    max_grade) but not past this server's end-game limits (caps); one the client gives no limit for stays. A value
    above the item's own forge limit is brought down to it (impossible otherwise); a value above only the server's
    limit is kept (the game keeps items that went past the limits while they were open)."""
    out = []
    for cur, mx, cap in ((current[0], max_quality, caps["quality"]), (current[1], max_grade, caps["grade"])):
        if mx is None:
            out.append(cur)
        elif cur > int(mx):
            out.append(int(mx))
        else:
            out.append(max(cur, min(int(mx), int(cap))))
    return tuple(out)


def gear_caps(rates):
    """The forge limits the running server enforces (Application/Limits/EndgameSwitch.cs, GameLimitProfile.cs):
    endgame.enabled 0 = Original limits, 1 = Origin limits, 2 (default) = limits.original_gw decides. With the Origin
    limits but no god gems, +25 cannot be made (the Olympian gem is its only material), so grades stop at +24."""
    mode = int(float(rates.get("endgame.enabled", 2)))
    original = mode == 0 or (mode not in (0, 1) and float(rates.get("limits.original_gw", 0)) == 1)
    god_gems = mode == 1 or (mode not in (0, 1) and float(rates.get("gem.god_gem_drops", 0)) >= 1)
    if original:
        return {"quality": 10, "grade": 12, "why": "Original GodsWar limits (limits.original_gw)"}
    return {"quality": inventory.CLIENT_Q, "grade": inventory.CLIENT_G if god_gems else inventory.CLIENT_G - 1,
            "why": "Origin limits" + ("" if god_gems else "; no god gems, so no +25")}


def pay_rewards(after, todo, rules, rw, meta_of, cap_level, level, exp):
    """What the hand-ins of `todo` pay, applied to `after` (a copy of one character's rows, changed in place).
    Returns (rewards, SQL, character_base values). Shared by the maxer and the Skills page's main-chain finisher."""
    b = after["character_base"][0]
    cid = int(b["id"])
    sql, values = [], {}
    items = after.setdefault("character_items", [])
    rewards = _quest_rewards(b, todo, rules, rw, meta_of, cap_level, level, exp)
    if rewards["tp_points"] or rewards["talent_exp"] != int(b.get("SkillExp") or 0):
        values.update(SkillPoint=rewards["points_after"], SkillExp=rewards["talent_exp"])
    if rewards["silver"]:
        values["Money"] = min(I32, int(b.get("Money") or 0) + rewards["silver"])
    if rewards["gold"]:
        values["Stone"] = min(I32, int(b.get("Stone") or 0) + rewards["gold"])
    placed = place_items(items, rewards["grants"], rw.get("policy") or _default_policy(meta_of))
    _item_sql(cid, placed, sql, items, rw)
    rewards["bag"] = [{"item": g["item"], "name": g["name"], "qty": add, "quest": g["quest"]} for _r, _s, add, g in placed["updates"]] + \
                     [{"item": g["item"], "name": g["name"], "qty": n, "quest": g["quest"]} for _s, g, n in placed["inserts"]]
    rewards["chest"] = [{"item": g["item"], "name": g["name"], "qty": n, "quest": g["quest"]} for g, n in placed["chest"]]
    if placed["chest"] and not rw.get("chest_ready"):
        raise Refused("the bag is full and this database has no Prize Chest (prize_chest_entries); make room in the bag first")
    for t in rewards["titles"]:
        sql.append("INSERT INTO character_title_grants (character_id, title_id, granted_by, note) VALUES (%d, %d, 'quest-title', %s) "
                   "ON CONFLICT (character_id, title_id) DO NOTHING" % (cid, t["id"], common.lit("editor: quest %d finished" % t["quest"])))
        after.setdefault("character_title_grants", []).append({"character_id": cid, "title_id": t["id"]})
    if rewards["titles"]:
        lim = int(((rw.get("titles") or {}).get("limit")) or TITLE_LIMIT)
        sql.append("DO $gwtitle$ BEGIN IF (SELECT count(*) FROM character_title_entitlements WHERE character_id = %d) > %d "
                   "THEN RAISE EXCEPTION 'gwadmin: title ownership exceeds the compatible reader limit'; END IF; END $gwtitle$" % (cid, lim))
    return rewards, sql, values


# =========================================================================== the plan
def _new_findings(before, after):
    seen = {(f.get("rule"), f.get("where")) for f in before}
    return [f for f in after if (f.get("rule"), f.get("where")) not in seen]


def build_plan(d, o, families, talents, rules, client_items, meta_of, rw=None, checker=None):
    """Everything one Apply would do, computed on the rows of one rb.dump(). Nothing is written here.
    rw: the reward inputs (_reward_inputs); checker(d) -> findings (Natural check + Item legality) for the validator.
    Returns {sql, changes, rewards, warnings, findings, after}."""
    rw = rw or {}
    b = d["character_base"][0]
    cid = int(b["id"])
    prof = int(b["profession"])
    after = copy.deepcopy(d)
    ab = after["character_base"][0]
    sql, changes, warnings = [], [], []
    cur_level = int(b["fighter_job_lv"])
    if o["level"] < cur_level:
        raise Bad("%s is level %d; the maxer only raises a level (lower it on the Character page if you mean to)" % (b["name"], cur_level))
    values = {}
    level, exp = int(b["fighter_job_lv"]), int(b["fighter_job_exp"])
    if level != o["level"]:
        level, exp = o["level"], 0
    if o["zodiac"]:
        top = len(character.ZODIAC_CAPS)
        if int(b["zodiac_level"]) != top or int(b["zodiac_energy"]) != character.zodiac_cap(top):
            values.update(zodiac_level=top, zodiac_energy=character.zodiac_cap(top))
            changes.append("Zodiac level %d, energy full" % top)
    if o["capacity"]:
        if int(b.get("pet_shed_capacity") or 0) != PET_SHED_MAX:
            values["pet_shed_capacity"] = PET_SHED_MAX
            changes.append("Pet shed %d" % PET_SHED_MAX)
    # ---- skills
    if o["skills"]:
        rows = after.setdefault("character_skills", [])
        have = {int(r["skill_id"]) for r in rows}
        learned = 0
        for base, s in sorted(skill_targets(families, o["level"]).items()):
            ids = {m["id"] for m in families[base]}
            if have & ids == {s["id"]}:
                continue
            if have & ids:
                sql.append("DELETE FROM character_skills WHERE user_id = %d AND skill_id IN (%s)" % (cid, ",".join(map(str, sorted(ids)))))
                rows[:] = [r for r in rows if int(r["skill_id"]) not in ids]
            lv = max(1, int(s.get("level") or 1))
            sql.append("INSERT INTO character_skills (user_id, skill_id, skill_level, source) VALUES (%d, %d, %d, 'gwadmin-web')" % (cid, s["id"], lv))
            rows.append({"user_id": cid, "skill_id": s["id"], "skill_level": lv, "source": "gwadmin-web"})
            learned += 1
        if learned:
            changes.append("%d skill families to their top rank" % learned)
    # ---- talents (only the class's own; a talent whose prefix needs a higher rank than the target is left out)
    if o["talent_rank"]:
        rows = after.setdefault("character_talents", [])
        cur = {int(r["talent_id"]): int(r["rank"]) for r in rows}
        ok, moved = set(), 0
        for t in talents:
            if int(t.get("class", prof)) != prof:
                continue
            pre = int(t.get("prefix") or 0)
            if pre and (int(t.get("prefix_rank") or 0) > o["talent_rank"] or (pre not in ok and cur.get(pre, 0) < int(t.get("prefix_rank") or 0))):
                warnings.append("Talent %s left alone: it needs talent %d at rank %s first." % (t.get("name") or t["id"], pre, t.get("prefix_rank")))
                continue
            ok.add(t["id"])
            if cur.get(t["id"], 0) >= o["talent_rank"]:
                continue
            sql.append("INSERT INTO character_talents (user_id, talent_id, rank, updated_at) VALUES (%d, %d, %d, now()) "
                       "ON CONFLICT (user_id, talent_id) DO UPDATE SET rank = EXCLUDED.rank, updated_at = now()" % (cid, t["id"], o["talent_rank"]))
            rows[:] = [r for r in rows if int(r["talent_id"]) != t["id"]] + [{"user_id": cid, "talent_id": t["id"], "rank": o["talent_rank"]}]
            moved += 1
        if moved:
            changes.append("%d talents to rank %d" % (moved, o["talent_rank"]))
    # ---- worn gear: quality / grade exactly to what the item forges to, never on gear the class or level cannot wear
    items = after.setdefault("character_items", [])
    if o["gear"]:
        caps = rw.get("gear_caps") or {"quality": inventory.CLIENT_Q, "grade": inventory.CLIENT_G}
        raised = 0
        for r in items:
            if int(r["item_location"]) != inventory.LOC_EQUIP:
                continue
            meta = meta_of(int(r["prop_id"]))
            if meta["slot"] < 0:
                continue
            if meta.get("classes") and prof not in meta["classes"]:
                warnings.append("%s is worn but a %s cannot use it: not maxed (Item legality can remove it)." % (meta["name"], common.CLASS_NAMES.get(prof, prof)))
                continue
            if (meta.get("minlv") or 0) > o["level"]:
                warnings.append("%s needs level %s: not maxed." % (meta["name"], meta["minlv"]))
                continue
            cli = client_items.get(str(r["prop_id"])) or {}
            old = (int(r["item_quality"]), int(r["item_grade"]))
            q, g = gear_target(old, cli.get("max_quality"), cli.get("max_grade"), caps)
            if cli.get("max_quality") is None and cli.get("max_grade") is None:
                warnings.append("%s: the client gives no forge limits for it, so it is not maxed." % meta["name"])
                continue
            if (q, g) == old:
                continue
            sql.append("UPDATE character_items SET item_quality = %d, item_grade = %d, updated_at = now() WHERE id = %d AND user_id = %d" % (q, g, int(r["id"]), cid))
            sql.append(_audit(cid, "update", r, {"prop_id": int(r["prop_id"]), "item_quality": q, "item_grade": g, "item_exp": r.get("item_exp")}))
            r["item_quality"], r["item_grade"] = q, g
            raised += 1
        if raised:
            changes.append("%d worn items to their top quality and grade (this server: Q%d, +%d)" % (raised, caps["quality"], caps["grade"]))
            sql.append("SELECT public.recompute_character_holy_suit_points(%d)" % cid)
    # ---- main quests, and what their hand-ins pay
    rewards = None
    if o["quests"]:
        camp = skills.CAMP_KEYS.get(int(b["camp"]))
        done = {int(r["quest_id"]) for r in d.get("character_quests") or [] if int(r["state"]) == 1}
        todo = [q for q in main_quests(rules, camp, o["level"]) if q not in done]
        qrows = after.setdefault("character_quests", [])
        for q in todo:
            sql.append("INSERT INTO character_quests (character_id, quest_id, state, progress) VALUES (%d, %d, 1, 0) "
                       "ON CONFLICT (character_id, quest_id) DO UPDATE SET state = 1, progress = 0" % (cid, q))
            qrows[:] = [r for r in qrows if int(r["quest_id"]) != q] + [{"character_id": cid, "quest_id": q, "state": 1, "progress": 0}]
        if todo:
            changes.append("%d main quests marked done" % len(todo))
        if o["rewards"] and todo:
            rewards, rsql, rvalues = pay_rewards(after, todo, rules, rw, meta_of, o["level"], level, exp)
            level, exp = rewards["level"], rewards["exp_after"]
            sql += rsql
            values.update(rvalues)
            changes.extend(_reward_lines(rewards))
    if level != int(b["fighter_job_lv"]) or exp != int(b["fighter_job_exp"]):
        values.update(fighter_job_lv=level, fighter_job_exp=exp)
        changes.insert(0, "Level %s to %d, EXP %s" % (b["fighter_job_lv"], level, format(exp, ",")))
    if values:
        sql.insert(0, update_sql("character_base", {"id": cid}, values))
        ab.update(values)
    findings = []
    if checker and sql:
        findings = _new_findings(checker(d), checker(after))
    return {"sql": sql, "changes": changes, "rewards": rewards, "warnings": warnings, "findings": findings, "after": after}


def plan(d, o, families, talents, rules, client_items, meta_of, rw=None):
    """(SQL statements, what changes) for one rb.dump(). Nothing is written here."""
    p = build_plan(d, o, families, talents, rules, client_items, meta_of, rw)
    return p["sql"], p["changes"]


def _audit(cid, action, row, vals):
    return ("INSERT INTO character_item_audit (source, action, user_id, item_location, slot_index, prop_id, item_quality, "
            "item_grade, item_exp, old_item) VALUES ('gwadmin-web', %s, %d, %d, %d, %s, %s, %s, %s, %s)" % (
                common.lit(action), cid, int(row["item_location"]), int(row["slot_index"]), common.lit(vals.get("prop_id")),
                common.lit(vals.get("item_quality")), common.lit(vals.get("item_grade")), common.lit(vals.get("item_exp")),
                ("%s::jsonb" % common.lit(json.dumps({k: row[k] for k in sorted(row)}, default=str))) if row.get("id") else "NULL"))


def _default_policy(meta_of):
    def pol(item):
        m = meta_of(item)
        return {"overlap": m.get("overlap") or 1, "bound": 1 if (m.get("defaults") or {}).get("bound") else 0}
    return pol


def _quest_rewards(b, todo, rules, rw, meta_of, cap_level, level, exp):
    """Sum what every hand-in of `todo` pays, in quest order, starting from (level, exp) after the level step."""
    data = rw.get("data") or quest_reward_data()
    rates, overrides, slots = rw.get("rates") or {}, rw.get("values") or {}, rw.get("slots") or {}
    policy = rw.get("policy") or _default_policy(meta_of)
    prof = int(b["profession"])
    sealed = bool(b.get("fighter_level_sealed"))
    out = {"quests": len(todo), "exp": 0, "exp_paid": 0, "tp": 0, "silver": 0, "gold": 0, "grants": [], "skipped": [],
           "titles": [], "notes": [], "start_level": level}
    myc = data.get("mycenae") or {}
    start = _exp_total(level, exp)
    for q in todo:
        pay = quest_payout(rules.get(str(q)) or {}, overrides.get(q), rates, rw.get("appraisal_bp") or 0)
        out["exp"] += pay["exp"]
        level, exp = apply_exp(level, exp, pay["exp"], sealed, cap_level)
        out["tp"] += pay["tp"]
        out["silver"] += pay["silver"]
        out["gold"] += pay["gold"]
        if q in (myc.get("quests") or []) and myc.get("item"):
            out["grants"].append(_grant(q, {"item": int(myc["item"]), "quality": None, "grade": None, "attrs": []},
                                        int(myc.get("quantity") or 1), policy, meta_of))
        menu = reward_menu(q, data.get("items") or {}, slots)
        if rw.get("all_choices") and len(menu) > 1:
            # W24 (the admin d154): the quest finisher pays EVERY item a choice menu offers, not only the class's own pick
            for m in menu:
                out["grants"].append(_grant(q, m, 1, policy, meta_of))
            continue
        chosen, skipped = pick_slot(menu, prof, meta_of)
        if chosen:
            out["grants"].append(_grant(q, chosen, 1, policy, meta_of))
            out["skipped"] += [{"quest": q, "item": s["item"], "name": meta_of(s["item"])["name"]} for s in skipped]
    out["level"], out["exp_after"] = level, exp
    out["exp_paid"] = max(0, _exp_total(level, exp) - start)
    out["silver"], out["gold"] = min(out["silver"], I32), min(out["gold"], I32)
    pts0, tx0 = int(b.get("SkillPoint") or 0), int(b.get("SkillExp") or 0)
    tx, pts = apply_talent_exp(min(max(tx0, 0), 99), pts0, out["tp"])
    ceiling = rw.get("talent_ceiling")
    if ceiling and pts > max(ceiling, pts0) and str(b.get("GM")) != "1":
        out["notes"].append("Talent points stop at %s: every talent of the class at rank 100 needs no more." % format(ceiling, ","))
        pts, tx = max(ceiling, pts0), 0
    out["tp_points"], out["points_after"], out["talent_exp"] = pts - pts0, pts, tx
    if out["exp"] and out["exp_paid"] < out["exp"]:
        out["notes"].append("Quest EXP above the chosen level %d is not kept (at level 200 the game keeps no EXP)." % cap_level)
    # quest titles: granted on hand-in when the Title Expert is on (TitleExpertPolicy.IsEnabled), within the catalogue,
    # not already owned, at most 128 owned titles
    tmap = data.get("titles") or {}
    want = [(q, int(tmap[str(q)])) for q in todo if str(q) in tmap]
    tr = rw.get("titles") or {}
    if want:
        if not rw.get("title_expert"):
            out["notes"].append("Quest titles are off on this server (%s); none granted." % TITLE_SWITCH)
        elif not tr.get("available"):
            out["notes"].append("Quest title %s not granted here (%s); the Title Expert grants it at the next visit." % (
                ", ".join(str(t) for _, t in want), tr.get("reason") or "title grants unavailable"))
        else:
            owned, room = set(tr.get("owned") or []), int(tr.get("limit") or TITLE_LIMIT) - len(tr.get("owned") or [])
            for q, t in want:
                if t in owned or t in {x["id"] for x in out["titles"]}:
                    continue
                if t not in set(tr.get("catalog") or []):
                    out["notes"].append("Quest title %d is not in the title catalogue; not granted." % t)
                    continue
                if room <= 0:
                    out["notes"].append("Quest title %d not granted: the character already owns the most titles the game shows." % t)
                    continue
                room -= 1
                out["titles"].append({"id": t, "quest": q, "name": (tr.get("names") or {}).get(t) or "title %d" % t})
    return out


def _exp_total(level, exp):
    return sum(character.EXP_NEXT[:max(0, int(level) - 1)]) + int(exp)


def _grant(q, entry, qty, policy, meta_of):
    pol = policy(entry["item"])
    meta = meta_of(entry["item"])
    return {"quest": q, "item": entry["item"], "name": meta["name"], "qty": qty, "bound": int(pol["bound"]),
            "quality": int(entry["quality"]) if entry.get("quality") is not None else 1,
            "grade": int(entry["grade"]) if entry.get("grade") is not None else 1,
            "attrs": [(a, lv) for a, lv in entry.get("attrs") or []]}


def _item_sql(cid, placed, sql, items, rw):
    for r, stack, _add, _g in placed["updates"]:
        sql.append("UPDATE character_items SET stack = %d, updated_at = now() WHERE id = %d AND user_id = %d" % (stack, int(r["id"]), cid))
        sql.append(_audit(cid, "update", r, {"prop_id": int(r["prop_id"]), "item_quality": r["item_quality"], "item_grade": r["item_grade"],
                                             "item_exp": r.get("item_exp")}))
        r["stack"] = stack
    for n, (slot, g, stack) in enumerate(placed["inserts"]):
        vals = {"user_id": cid, "item_location": inventory.LOC_BAG, "slot_index": slot, "prop_id": g["item"], "stack": stack,
                "bound": g["bound"], "item_quality": g["quality"], "item_grade": g["grade"], "item_exp": 0, "holy_suit_code": 0}
        for i, (a, lv) in enumerate(g["attrs"][:5], 1):
            if a is not None:
                vals["attribute%d" % i], vals["attribute_level%d" % i] = int(a), int(lv if lv is not None else 1)
        sql.append(common.insert_sql("character_items", vals))
        sql.append(_audit(cid, "create", vals, vals))
        items.append(dict(vals, id=-(n + 1)))
    for g, n in placed["chest"]:
        sql.append(common.insert_sql("prize_chest_entries", {
            "character_id": cid, "kind": "item", "item_id": g["item"], "quantity": n, "item_quality": g["quality"],
            "item_grade": g["grade"], "bound": g["bound"], "source": "editor:maxer quest %d" % g["quest"],
            "note": "Quest reward (the bag was full when the maxer finished the quest)"}))


def _reward_lines(r):
    out = []
    bits = []
    if r["exp"]:
        bits.append("%s EXP%s" % (format(r["exp"], ","), "" if r["exp_paid"] >= r["exp"] else
                                  " (%s kept: the level stops at %d)" % (format(r["exp_paid"], ","), r["level"])))
    if r["tp"]:
        bits.append("%s Talent EXP (%d talent points)" % (format(r["tp"], ","), r["tp_points"]))
    if r["silver"]:
        bits.append("%s Silver" % format(r["silver"], ","))
    if r["gold"]:
        bits.append("%s Gold" % format(r["gold"], ","))
    if bits:
        out.append("Quest rewards: " + ", ".join(bits))
    if r.get("bag"):
        out.append("Into the bag: " + ", ".join("%s x%d" % (x["name"], x["qty"]) for x in r["bag"]))
    if r.get("chest"):
        out.append("To the Prize Chest (bag full): " + ", ".join("%s x%d" % (x["name"], x["qty"]) for x in r["chest"]))
    if r["titles"]:
        out.append("Quest titles: " + ", ".join(t["name"] for t in r["titles"]))
    return out


# =========================================================================== database inputs
def _rows(rb, sql):
    try:
        return rb.pg.json(sql) or []
    except Exception:          # noqa: BLE001 - an older database without the table: no override
        return []


LIMIT_KEYS = ["endgame.enabled", "limits.original_gw", "gem.god_gem_drops"]


def read_rates(rb):
    return {r["rate_key"]: float(r["value"]) for r in _rows(
        rb, "(SELECT coalesce(json_agg(json_build_object('rate_key', rate_key, 'value', value)), '[]') FROM server_rates "
            "WHERE rate_key IN (%s))" % ",".join(common.lit(k) for k in list(RATE_KEYS.values()) + [TITLE_SWITCH] + LIMIT_KEYS))}


def _reward_inputs(ctx, rb, d, quest_ids):
    """Everything the payout reads from the database, gathered once (pure plan afterwards)."""
    b = d["character_base"][0]
    cid = int(b["id"])
    ids = ",".join(str(int(q)) for q in quest_ids) or "0"
    data = quest_reward_data()
    values = {int(r["quest_id"]): {"exp": r["experience"], "tp": r["talent_points"], "silver": r["silver"], "gold": r["gold"]}
              for r in _rows(rb, "(SELECT coalesce(json_agg(v), '[]') FROM quest_reward_values v WHERE quest_id IN (%s))" % ids)}
    slots = {}
    for r in _rows(rb, "(SELECT coalesce(json_agg(s), '[]') FROM quest_reward_slots s WHERE quest_id IN (%s))" % ids):
        slots.setdefault(int(r["quest_id"]), []).append(r)
    rates = read_rates(rb)
    appraisal = _rows(rb, "(SELECT coalesce(json_agg(bonus_basis_points), '[]') FROM character_quest_appraisal WHERE character_id = %d)" % cid)
    item_ids = {int(x["item"]) for v in (data.get("items") or {}).values() for x in v}
    item_ids |= {int(r["item_id"]) for v in slots.values() for r in v}
    if (data.get("mycenae") or {}).get("item"):
        item_ids.add(int(data["mycenae"]["item"]))
    pol = {}
    for r in _rows(rb, "(SELECT coalesce(json_agg(json_build_object('id', id, 'overlap', stats->>'Overlap', 'bind', stats ? 'BindType')), '[]') "
                       "FROM item_templates WHERE id IN (%s))" % (",".join(map(str, sorted(item_ids))) or "0")):
        try:
            ov = max(1, int(r.get("overlap") or 1))
        except ValueError:
            ov = 1
        pol[int(r["id"])] = {"overlap": ov, "bound": 1 if r.get("bind") else 0}
    fallback = _default_policy(rb.item_meta)
    chest_ready = bool(_rows(rb, "(SELECT to_jsonb(to_regclass('public.prize_chest_entries') IS NOT NULL))"))
    try:
        from . import title_grants as TG
        tr = TG.support(ctx)
    except Exception as e:     # noqa: BLE001
        tr = {"available": False, "reason": str(e)}
    if tr.get("available"):
        tr["owned"] = [int(x) for x in _rows(rb, "(SELECT coalesce(json_agg(title_id), '[]') FROM character_title_entitlements WHERE character_id = %d)" % cid)]
        cat = _rows(rb, "(SELECT coalesce(json_agg(json_build_object('id', title_id, 'name', display_name)), '[]') FROM editor_title_catalog)")
        tr["catalog"] = [int(t["id"]) for t in cat]
        tr["names"] = {int(t["id"]): t["name"] for t in cat}
        tr["limit"] = min(TITLE_LIMIT, int(tr.get("owned_limit") or TITLE_LIMIT))
    ceiling = None
    try:
        n = len(skills.talents_for(ctx, int(b["profession"])))
        if n:
            ceiling = character.CI.talent_point_ceiling(n, character._talent_rate(ctx))
    except Exception:          # noqa: BLE001
        ceiling = None
    return {"data": data, "values": values, "slots": slots, "rates": rates, "appraisal_bp": int(appraisal[0]) if appraisal else 0,
            "policy": lambda item: pol.get(int(item)) or fallback(item), "chest_ready": chest_ready,
            "title_expert": float(rates.get(TITLE_SWITCH, 0)) == 1, "titles": tr, "talent_ceiling": ceiling}


# ---- the Skills page's "Finish main chains through Lv" (modules/skills.py quest_chain) pays the same rewards
def finisher_inputs(ctx, cid):
    """The reward inputs for one character's main-chain finisher (read once, before the write)."""
    rb = ctx.need_rb()
    d = rb.dump(int(cid))
    b = d["character_base"][0]
    rules = skills.quest_rules()["quests"]
    camp = skills.CAMP_KEYS.get(int(b["camp"]))
    rw = _reward_inputs(ctx, rb, d, main_quests(rules, camp, character.LEVEL_CAP))
    # W24 (the admin d155): the finisher pays each quest's BASE values (its own or the GM override row), not scaled by the
    # server rates; (d154) every item of a choice menu. The account maxer keeps the hand-in rules (rates, one pick).
    rw["rates"] = {}
    rw["all_choices"] = True
    return {"rw": rw, "meta_of": rb.item_meta}


def finisher_rewards(inputs, d, quest_ids, rules):
    """(rewards, SQL, base values) for finishing quest_ids on the character `d` as the server's hand-ins would: EXP
    levels the character up like the game does (up to 200), the rest as in the maxer. The SQL includes the
    character_base UPDATE (with its revision counters)."""
    after = copy.deepcopy(d)
    b = d["character_base"][0]
    rewards, sql, values = pay_rewards(after, sorted(int(q) for q in quest_ids), rules, inputs["rw"], inputs["meta_of"],
                                       character.LEVEL_CAP, int(b["fighter_job_lv"]), int(b["fighter_job_exp"]))
    if (rewards["level"], rewards["exp_after"]) != (int(b["fighter_job_lv"]), int(b["fighter_job_exp"])):
        values.update(fighter_job_lv=rewards["level"], fighter_job_exp=rewards["exp_after"])
    if values:
        sql.insert(0, update_sql("character_base", {"id": int(b["id"])}, values))
    return rewards, sql, values


def finisher_fields(rewards):
    f = ["character_quests.*", "character_base.fighter_job_lv", "character_base.fighter_job_exp", "character_base.Money",
         "character_base.Stone", "character_base.SkillPoint", "character_base.SkillExp", "character_items.*"]
    return f + [x for x in _fields({"rewards": rewards}) if x in (CHEST_FIELD, TITLE_FIELD)]


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


def _checker(ctx, rb):
    """Natural check (level / EXP / zodiac / skills / talents / items) and Item legality (gear rules), as one list."""
    from . import natural, legality
    content = skills.content(ctx)
    cat, cli = rb.cat(), rb.client()["items"]

    def check(d):
        b = d["character_base"][0]
        nat = [f for f in natural.check(rb, d, content) if f.get("rule") != "structure"]
        leg = [{"rule": f["rule"], "where": "%s (%s)" % (f["item"], f["where"]), "message": f["message"], "severity": f["severity"]}
               for f in legality.item_findings(b, d.get("character_items") or [], rb.item_meta, cli, cat["attributes"])]
        return nat + leg
    return check


def _prepare(ctx, cid, o):
    """The plan's inputs (read once) and a function that plans against any dump of the character."""
    rb, d, fams, talents, rules = _inputs(ctx, cid)
    b = d["character_base"][0]
    camp = skills.CAMP_KEYS.get(int(b["camp"]))
    rw = _reward_inputs(ctx, rb, d, main_quests(rules, camp, o["level"])) if o["quests"] and o["rewards"] else {}
    rw["gear_caps"] = gear_caps(rw.get("rates") or read_rates(rb))
    check = _checker(ctx, rb)
    return d, lambda dump: build_plan(dump, o, fams, talents, rules, rb.client()["items"], rb.item_meta, rw, check)


def _fields(p):
    f = list(FIELDS)
    if p.get("rewards"):            # a fresh bag at Apply time may send items to the chest even when the preview did not
        f += [CHEST_FIELD, TITLE_FIELD]
    return f


def _public(p):
    r = p.get("rewards")
    if r:
        r = {k: v for k, v in r.items() if k != "grants"}
    return {"changes": p["changes"], "rewards": r, "warnings": p["warnings"], "findings": p["findings"]}


@M.post(r"preview/(\d+)")
def preview(ctx, cid):
    cid = int(cid)
    o = options(ctx.body.get("options"))
    d, run = _prepare(ctx, cid, o)
    p = run(d)
    b = d["character_base"][0]
    return dict(_public(p), name=b["name"], level=b["fighter_job_lv"], **{"class": common.CLASS_NAMES.get(b["profession"]),
                "online": common.online_info(ctx.app, cid)["online"]})


@M.post(r"apply/(\d+)")
def apply(ctx, cid):
    cid = int(cid)
    o = options(ctx.body.get("options"))
    d, run = _prepare(ctx, cid, o)
    p = run(d)
    name = d["character_base"][0]["name"]
    if ctx.body.get("confirm") != name:
        raise Bad("type the character's name, %s, to confirm" % name)
    if p["findings"]:
        raise Refused("refused: the result would not pass the editor's own checks - " +
                      "; ".join("%s: %s" % (f["where"], f["message"]) for f in p["findings"][:8]))

    def build(now):
        p2 = run(now)
        if not p2["sql"]:
            raise Bad("nothing to change: %s is already there" % name)
        if p2["findings"]:
            raise Refused("refused: the character changed and the result would no longer pass the editor's checks")
        return p2["sql"]
    # titles: database triggers own those revisions (title_grants writes with cross_bumps=False); inventory_revision is
    # added once by char_write for character_items.
    r = ctx.char_write(cid, "maxer: " + "; ".join(p["changes"])[:300], build, fields=_fields(p))
    r.update(_public(p))
    return r
