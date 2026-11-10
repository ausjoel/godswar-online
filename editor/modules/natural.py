"""modules/natural.py - read-only report: what on a character could not come from normal play. Backend for static/modules/natural.js.

The writing pages keep their own limits. This one looks at a whole character at once and changes nothing, so a GM's
deliberate edit shows up here without being refused anywhere. Every rule names where its number comes from.
Severity "impossible": the server or client cannot produce or show it. "unnatural": it works, but play can't reach it.
"""
from . import character, common, inventory, skills
from .common import Bad

M = common.module("natural", "Natural check", icon={"sprite": 684}, order=27, status="ready",
                  description="Read-only. Finds levels, skills, gear and pets on a character that normal play could not produce.")

REPORT_MAX = 200
PET_LEVEL_MAX = 120                 # PetManagerPlanner.MaximumPetLevel
PET_EXP_MAX = 252947820             # PetExperienceCatalog.TotalExperienceToMaximumLevel
TALENT_RANK_MAX = 100               # Q20 client exe cap (AUTONOMOUS-QUEUE item 4)
HOLY_LEVEL_MAX = 10                 # HolyStoneUpgradePolicy.MaximumLevel
SOCKETS_MAX = inventory.CLIENT_SOCKETS
SERVER_GRANTED = ("starter", "faction-starter", "mount-compatibility")   # character_skills.source: the server adds these at creation
NOT_CHECKED = [
    "Total talent points (SkillPoint): no formula for what play earns was found in the server source.",
    "A talent's required total rank: how the server counts it was not confirmed.",
    "Money, gold and other currencies: there is no natural maximum.",
    "Pet savvy and growth beyond the server's own start-up checks, listed under structure.",
    "Calculated combat stats: these come from the running server, see Build analysis.",
]


def _f(out, severity, rule, where, message):
    out.append({"severity": severity, "rule": rule, "where": where, "message": message})


def _base_checks(b, out):
    level = int(b["fighter_job_lv"])
    if not 1 <= level <= character.LEVEL_CAP:
        _f(out, "impossible", "level", "character", "Level %d is outside 1-%d." % (level, character.LEVEL_CAP))
    cap = character.exp_next(level, bool(b.get("fighter_level_sealed")))
    if int(b["fighter_job_exp"]) >= cap and level < character.LEVEL_CAP and not b.get("fighter_level_sealed"):
        _f(out, "unnatural", "exp", "character", "EXP %s is at or above the %s that level %d needs; play would have levelled up." % (
            format(int(b["fighter_job_exp"]), ","), format(cap, ","), level))
    zodiac = int(b["zodiac_level"])
    if not 1 <= zodiac <= len(character.ZODIAC_CAPS):
        _f(out, "impossible", "zodiac", "character", "Zodiac level %d is outside 1-%d." % (zodiac, len(character.ZODIAC_CAPS)))
    elif int(b["zodiac_energy"]) > character.zodiac_cap(zodiac):
        _f(out, "unnatural", "zodiac", "character", "Zodiac energy %s is above the %s cap of zodiac level %d." % (
            format(int(b["zodiac_energy"]), ","), format(character.zodiac_cap(zodiac), ","), zodiac))


def _skill_checks(b, rows, content, out):
    known = {s["id"]: s for s in content["skills"]}
    level, prof = int(b["fighter_job_lv"]), int(b["profession"])
    for r in rows:
        s = known.get(int(r["skill_id"]))
        where = "skill %s" % r["skill_id"]
        if s is None:
            _f(out, "impossible", "skill", where, "This skill is not in the server's published list.")
            continue
        where = "skill %s (%s)" % (s["name"], s["id"])
        if r.get("source") in SERVER_GRANTED:
            continue
        if s.get("classes") and prof not in s["classes"]:
            _f(out, "unnatural", "skill-class", where, "This class can't learn it.")
        if (s.get("minlv") or 1) > level:
            _f(out, "unnatural", "skill-level", where, "Needs level %d; the character is level %d." % (s["minlv"], level))


def _talent_checks(b, rows, content, out):
    known = {t["id"]: t for t in content["talents"]}
    ranks = {int(r["talent_id"]): int(r["rank"]) for r in rows}
    prof = int(b["profession"])
    for tid, rank in ranks.items():
        if rank <= 0:
            continue
        t = known.get(tid)
        if t is None:
            _f(out, "impossible", "talent", "talent %d" % tid, "This talent is not in the server's published list.")
            continue
        where = "talent %s (%d)" % (t["name"], tid)
        if t["class"] != prof:
            _f(out, "unnatural", "talent-class", where, "This talent belongs to another class.")
        if rank > TALENT_RANK_MAX:
            _f(out, "impossible", "talent-rank", where, "Rank %d is above the client's %d." % (rank, TALENT_RANK_MAX))
        if t.get("prefix") and ranks.get(int(t["prefix"]), 0) < int(t.get("prefix_rank") or 0):
            _f(out, "unnatural", "talent-prefix", where, "Needs talent %s at rank %s first." % (t["prefix"], t["prefix_rank"]))


def _item_checks(b, rows, meta_of, client_items, attributes, equip_location, out):
    level, prof = int(b["fighter_job_lv"]), int(b["profession"])
    for r in rows:
        pid = int(r["prop_id"])
        meta, cli = meta_of(pid), client_items.get(str(pid)) or {}
        where = "%s (row %s)" % (meta["name"], r["id"])
        if not meta["server"]:
            _f(out, "impossible", "item", where, "The server has no such item.")
            continue
        if int(r["stack"]) > meta["overlap"]:
            _f(out, "unnatural", "stack", where, "Stack of %d; this item stacks to %d." % (r["stack"], meta["overlap"]))
        if meta["slot"] >= 0:
            q, g = int(r["item_quality"]), int(r["item_grade"])
            mq, mg = int(cli.get("max_quality") or inventory.CLIENT_Q), int(cli.get("max_grade") or inventory.CLIENT_G)
            if q > mq:
                _f(out, "unnatural", "quality", where, "Quality Q%d; this item forges to Q%d." % (q, mq))
            if g > mg:
                _f(out, "unnatural", "grade", where, "Grade G%d; this item forges to G%d." % (g, mg))
        if r["item_location"] == equip_location:
            if (meta.get("minlv") or 0) > level:
                _f(out, "unnatural", "equip-level", where, "Worn at level %d; it needs level %d." % (level, meta["minlv"]))
            if meta.get("classes") and prof not in meta["classes"]:
                _f(out, "unnatural", "equip-class", where, "Worn by a class that can't use it.")
        allowed = cli.get("append_attributes") or []
        for i in range(1, 6):
            a = r.get("attribute%d" % i)
            if a is None:
                continue
            lv = r.get("attribute_level%d" % i)
            mx = int((attributes.get(str(a)) or {}).get("max") or 1)
            if lv is not None and int(lv) > mx:
                _f(out, "impossible", "attribute-level", where, "Attribute %s at level %s; its maximum is %d." % (a, lv, mx))
            if allowed and int(a) not in allowed:
                _f(out, "unnatural", "attribute", where, "Attribute %s can't roll on this item." % a)
        sockets = int(r.get("holy_socket_count") or 0)
        if sockets > SOCKETS_MAX:
            _f(out, "impossible", "sockets", where, "%d holy sockets; the client has %d." % (sockets, SOCKETS_MAX))
        for i in range(1, sockets + 1):
            lv = r.get("holy_socket%d_level" % i)
            if lv is not None and int(lv) > HOLY_LEVEL_MAX:
                _f(out, "unnatural", "holy-level", where, "Holy stone level %s; stones stop at %d." % (lv, HOLY_LEVEL_MAX))


def _pet_checks(rows, settings, out):
    max_rank = float((settings or {}).get("maximum_rank") or 655.35)
    for p in rows:
        if p.get("activity_state") != "owned":
            continue
        where = "pet %s (#%s)" % (p.get("name"), p["id"])
        if int(p["level"]) > PET_LEVEL_MAX:
            _f(out, "impossible", "pet-level", where, "Level %s is above %d." % (p["level"], PET_LEVEL_MAX))
        if int(p["experience"]) > PET_EXP_MAX:
            _f(out, "unnatural", "pet-exp", where, "EXP %s is above the %s total to the top level." % (format(int(p["experience"]), ","), format(PET_EXP_MAX, ",")))
        if float(p["rank"]) > max_rank:
            _f(out, "impossible", "pet-rank", where, "Rank %s is above the server's %s." % (p["rank"], max_rank))


def check(rb, d, content):
    """Every finding for one rb.dump() of a character."""
    out = []
    b = d["character_base"][0]
    cat = rb.cat()
    _base_checks(b, out)
    _skill_checks(b, d.get("character_skills") or [], content, out)
    _talent_checks(b, d.get("character_talents") or [], content, out)
    _item_checks(b, d.get("character_items") or [], rb.item_meta, rb.client()["items"], cat["attributes"], inventory.LOC_EQUIP, out)
    _pet_checks(d.get("character_pets") or [], cat.get("pet_settings"), out)
    for f in rb.check_character(d, validators={}):
        _f(out, "impossible", "structure", f["where"], f["msg"])
    return out


def _summary(rb, d, content):
    b = d["character_base"][0]
    findings = check(rb, d, content)
    return {"id": b["id"], "name": b["name"], "level": b["fighter_job_lv"], "class": common.CLASS_NAMES.get(b["profession"], str(b["profession"])),
            "gm": str(b.get("GM")) == "1", "findings": findings}


@M.get("report")
def report(ctx):
    rb = ctx.need_rb()
    ids = rb.pg.json("(SELECT coalesce(json_agg(id ORDER BY id), '[]') FROM (SELECT id FROM character_base WHERE lifecycle_state = 'active' ORDER BY id LIMIT %d) c)" % REPORT_MAX)
    content = skills.content(ctx)
    return {"characters": [_summary(rb, rb.dump(int(c)), content) for c in ids], "not_checked": NOT_CHECKED}


@M.get(r"character/(\d+)")
def one(ctx, cid):
    rb = ctx.need_rb()
    d = rb.dump(int(cid))
    if not d.get("character_base"):
        raise Bad("no such character")
    return dict(_summary(rb, d, skills.content(ctx)), not_checked=NOT_CHECKED)
