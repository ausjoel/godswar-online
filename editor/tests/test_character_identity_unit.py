#!/usr/bin/env python3
"""Unit test (W4A): faction / gender / class change, hair and face legality and the legal limits of the Character page
(modules/_character_identity.py and the identity route of modules/character.py). No database, no server: a fake
context answers the reads and records the writes. Where the rules come from: the module docstring.

    python3 -I tests/test_character_identity_unit.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.dirname(HERE))
from modules import _character_identity as CI  # noqa: E402
from modules import character as CH  # noqa: E402
from modules.common import Bad, Refused  # noqa: E402

FAIL = []
SRV = os.path.join(ROOT, "server", "src", "Godswar.Server")
CLIENT = "/path/to/GodsWar"


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:500]))
    if not ok:
        FAIL.append(msg)


def refused(fn, *a, **k):
    try:
        fn(*a, **k)
    except (Bad, Refused) as e:
        return str(e)
    return None


# ---- hair / face
step("hair 53 is a male Original style", CI.check_hair("male", 53) == 53)
step("hair 150 (new-models only) is refused", "new models" in (refused(CI.check_hair, "male", 150) or ""))
step("... unless the new set is allowed", CI.check_hair("male", 150, allow_new=True) == 150)
step("hair 180 is female only", refused(CI.check_hair, "male", 180) is not None and CI.check_hair("female", 180) == 180)
step("hair 255 has no model", refused(CI.check_hair, "male", 255) is not None)
step("faces 0..4 only", CI.check_face(4) == 4 and refused(CI.check_face, 5) is not None)
step("hair_for keeps a shared style", CI.hair_for(53, True) == 53 and CI.hair_for(53, False) == 53)
step("hair_for drops a female-only style for a male", CI.hair_for(183, True) == 3)
step("hair_for drops a male-only style for a female", CI.hair_for(232, False) == 2)
step("hair lists = the preview sheets' row counts (80 / 90 / 100 / 105)",
     [len(CI.HAIR[s][g]) for s, g in (("old", "male"), ("old", "female"), ("new", "male"), ("new", "female"))] == [80, 90, 100, 105])
if os.path.isdir(os.path.join(CLIENT, "Characters")):
    for setname, folder in (("old", "Characters"), ("new", "Characters_New")):
        for g in ("male", "female"):
            have = sorted(int(m.group(1)) for f in os.listdir(os.path.join(CLIENT, folder))
                          for m in [re.match(r"^%s_hair_(\d+)\.gwo$" % g, f)] if m)
            step("hair ids %s/%s = the client's %s models" % (setname, g, folder), have == CI.HAIR[setname][g], (have, CI.HAIR[setname][g]))
else:
    print("skip client model cross-check (no client at %s)" % CLIENT)
srv = open(os.path.join(SRV, "Infrastructure", "Characters", "PostgresCharacterIdentityStore.cs")).read()
step("one-gender hair and model lists match the server",
     "[230, 231, 232, 233, 234, 250, 251, 252, 253, 254]" in srv and "[2190, 2590, 2690, 2790, 2990, 3301, 3302, 3303]" in srv
     and "[1100, 1102, 1103, 1104, 1105, 1106, 1107]" in srv)

# ---- gender
worn = [{"item_location": 0, "slot_index": 12, "prop_id": 5000}, {"item_location": 0, "slot_index": 3, "prop_id": 2190},
        {"item_location": 1, "slot_index": 3, "prop_id": 2190}]
step("fashion and a female-only model block a change to male", len(CI.gender_worn_conflicts(worn, True)) == 2)
step("a female-only model does not block a change to female", len(CI.gender_worn_conflicts(worn[1:], False)) == 0)
hair, st = CI.gender_sql(5, "female", "male", 183)
step("gender SQL: male, hair remapped, guarded", hair == 3 and "gender = 'male', hair_style = 3 WHERE id = 5 AND gender = 'female'" in st[0]
     and "RAISE EXCEPTION" in st[1], st)
step("same gender refused", refused(CI.gender_sql, 5, "male", "male", 3) is not None)

# ---- faction
new, st = CI.faction_sql(9, 0)
step("Sparta -> Athens, capital 165,-97, position revision", new == 1 and 'camp = 1, "Map" = 1, "Pos_X" = 165.0, "Pos_Z" = -97.0, position_revision = position_revision + 1' in st[1], st[1])
step("faction: guild guard first", "guild_members" in st[0] and "consortia <> 0" in st[0])
step("faction: Sparta portal skills 3062/3063 become Athens 3060/3061", "WHEN 3062 THEN 3060 ELSE 3061" in st[2] and "IN (3062, 3063)" in st[3], st[2:])
step("faction: unknown camp refused", refused(CI.faction_sql, 9, 4) is not None)
pol = open(os.path.join(SRV, "Domain", "Characters", "FactionPortalSkillPolicy.cs")).read()
step("portal skill ids match FactionPortalSkillPolicy", all(x in pol for x in ("AthensCapitalPortalSkillId = 3060", "AthensSuburbPortalSkillId = 3061",
                                                                               "SpartaCapitalPortalSkillId = 3062", "SpartaSuburbPortalSkillId = 3063")))

# ---- class
step("talent stock costs: rank 1 = 1, 11 = 10, 41 = 380, 61 = 900, 81 = 1900, 91 = 2900",
     [CI.talent_stock_cost(r) for r in (0, 10, 40, 60, 80, 90)] == [1, 10, 380, 900, 1900, 2900])
step("no cost past rank 100", CI.talent_stock_cost(100) == 0)
cum = CI.talent_refunds(1)
step("refund table: rank 10 = 55, rank 100 = 103,505", cum[10] == 55 and cum[100] == 103505, (cum[10], cum[100]))
step("cost.talent 1.5 truncates per rank like ServerRates.Multiply", CI.talent_cost(0, 1.5) == 1 and CI.talent_cost(1, 1.5) == 3)
grids = [{"grid_index": 0, "selected_skill_id": 10003}, {"grid_index": 1, "selected_skill_id": 10025},
         {"grid_index": 9, "selected_skill_id": 10003}, {"grid_index": 2, "selected_skill_id": -1}]
step("zodiac: another class's attack grid cleared, defence grids kept", CI.zodiac_to_clear(grids, 1) == [{"grid": 0, "skill": 10003}])
sql = CI.class_sql(5, 2, 0, 3, 120, 1, [{"grid": 0, "skill": 10003}])
step("class SQL: locks the row at the old class", "profession = 0" in sql and "FOR UPDATE" in sql)
step("class SQL: refuses worn slots 0-14", "slot_index <= 14" in sql)
step("class SQL: seals, unseals, grants with the server's statements",
     "INSERT INTO character_sealed_skills" in sql and "DELETE FROM character_sealed_skills" in sql and "'class-change'" in sql
     and "st.skill_id = 4904" in sql)
step("class SQL: refunds into SkillPoint, int-capped", 'LEAST(pts::bigint + refund, 2147483647)' in sql)
step("class SQL: writes the receipt (talent command epoch)", "INSERT INTO character_class_change_receipts" in sql)
step("class SQL: clears the zodiac grid", "grid_index = ANY(ARRAY[0]::smallint[])" in sql)
step("same class refused", refused(CI.class_sql, 5, 2, 1, 1, 1, 1, []) is not None)
store = open(os.path.join(SRV, "Infrastructure", "Characters", "PostgresCharacterClassChangeStore.cs")).read()
step("the server's class change keeps the same rules (worn 0-14, 4904, sealed skills)",
     "LastCharacterSlot = 14" in store and "st.skill_id = 4904" in store and "character_sealed_skills" in store)

# ---- limits
step("level 201 refused", refused(CI.check_limit, "fighter_job_lv", 201) is not None)
step("base max HP above 1500 refused for a player", refused(CI.check_limit, "MaxHP", 1501) is not None)
step("... allowed for a GM-flagged character", CI.check_limit("MaxHP", 109000, gm=True) == 109000)
step("talent points above the class ceiling refused", refused(CI.check_limit, "SkillPoint", 10 ** 7, ceiling=2_000_000) is not None)
step("talent ceiling = talents x 103,505", CI.talent_point_ceiling(20) == 2070100)
step("Silver max is the int32 packet field", refused(CI.check_limit, "Money", 2 ** 31) is not None and CI.check_limit("Money", 2 ** 31 - 1) == 2 ** 31 - 1)
step("face 7 refused by the limits too", refused(CI.check_limit, "face_shap", 7) is not None)
cre = open(os.path.join(SRV, "Infrastructure", "Characters", "PostgresCharacterLifecycleCommandExecutor.Create.cs")).read()
step("creation writes MaxHP 1500 / MaxMP 177 (the base the limits use)", re.search(r"1500,\s*177,\s*transaction_timestamp", cre) is not None)


# ---- the identity route with a fake context
class PG:
    def __init__(self, st, refund=None):
        self.st, self.refund = st, refund or {"n": 2, "ranks": [10, 40]}

    def json(self, sql, timeout=None):
        if "'class_tables'" in sql:
            return self.st
        if "'ranks'" in sql:
            return self.refund
        if "server_rates" in sql:
            return {"rate_key": "cost.talent", "value": "1.0000"}
        raise AssertionError(sql[:200])


class Cat:
    @staticmethod
    def name(t, i):
        return "item %s" % i


class Ctx:
    def __init__(self, st, body):
        self.rb = type("RB", (), {"pg": PG(st), "writable": True})()
        self.body = body
        self.catalog = Cat()
        self.writes = []

    def need_rb(self):
        return self.rb

    def char_write(self, cid, action, build, **kw):
        self.writes.append(("char", action, build({}), kw))
        return {"change": {"id": "c"}}

    def rows_write(self, scope, action, build, **kw):
        self.writes.append(("rows", action, build({}), dict(kw, scope=scope)))
        return {"change": {"id": "r"}}


def state(**kw):
    base = {"id": 5, "account_id": 2, "name": "Hero", "gender": "male", "camp": 0, "profession": 0, "level": 120, "hair": 53,
            "consortia": 0, "points": 10, "state": "active", "map": 3}
    base.update(kw.pop("base", {}))
    st = {"base": base, "worn": [], "guild_member": False, "quests": 0, "grids": [], "class_tables": True}
    st.update(kw)
    return st


c = Ctx(state(quests=2), {"op": "faction"})
p = CH.identity(c, "5")
step("faction plan: Sparta -> Athens, no blockers, warns about quests", p["after"] == "Athens" and not p["blockers"]
     and any("quest" in w for w in p["warnings"]) and not c.writes, p)
c = Ctx(state(base={"consortia": 7}), {"op": "faction", "apply": True})
step("faction in a guild refused", isinstance(refused(CH.identity, c, "5"), str) and not c.writes)
c = Ctx(state(), {"op": "faction", "apply": True})
CH.identity(c, "5")
w = c.writes[0]
step("faction apply: one guarded char_write with the expected camp", w[0] == "char" and w[3]["expected"][0]["values"] == {"camp": 0}
     and any('camp = 1' in s for s in w[2]), w)
c = Ctx(state(worn=[{"item_location": 0, "slot_index": 12, "prop_id": 9}]), {"op": "gender", "apply": True})
step("gender with fashion worn refused", refused(CH.identity, c, "5") is not None and not c.writes)
c = Ctx(state(base={"gender": "female", "hair": 183}), {"op": "gender"})
p = CH.identity(c, "5")
step("gender plan shows the hair remap", p["after"] == "male" and any("hair 183 -> 3" in x for x in p["what"]), p)
c = Ctx(state(worn=[{"item_location": 0, "slot_index": 10, "prop_id": 1000}]), {"op": "class", "target": 3, "apply": True})
step("class change with a weapon worn refused", refused(CH.identity, c, "5") is not None and not c.writes)
c = Ctx(state(), {"op": "class", "target": 3})
p = CH.identity(c, "5")
step("class plan: refund = 55 + 7,460 at today's price", p["refund"] == 7515 and p["after"] == "Mage", p)
c = Ctx(state(), {"op": "class", "target": 3, "apply": True})
CH.identity(c, "5")
w = c.writes[0]
step("class apply: rows_write with Undo over base, skills, sealed skills, talents, zodiac grids, receipts; character locked",
     w[0] == "rows" and {s["table"] for s in w[3]["scope"]} == {"character_base", "character_skills", "character_sealed_skills",
                                                                 "character_talents", "character_zodiac_skill_grids",
                                                                 "character_class_change_receipts"} and w[3]["lock_cids"] == [5], w[3])
c = Ctx(state(class_tables=False), {"op": "class", "target": 1, "apply": True})
step("class change before the server made its tables is refused", refused(CH.identity, c, "5") is not None)
c = Ctx(state(), {"op": "class", "target": 0})
step("class change to the same class refused", refused(CH.identity, c, "5") is not None)
c = Ctx(state(), {"op": "teleport"})
step("unknown op refused", refused(CH.identity, c, "5") is not None)

# ---- the Character page's save validation (_validate) applies the limits and the hair / face rules
VB = {"name": "Hero", "gender": "male", "fighter_job_lv": 120, "fighter_job_exp": 0, "fighter_level_sealed": False, "GM": 0,
      "zodiac_level": 1, "zodiac_energy": 0, "curHP": 10, "curMP": 10, "MaxHP": 1500, "MaxMP": 177, "hair_style": 53}
vc = Ctx(state(), {})
step("save: hair 255 refused", refused(CH._validate, vc, 5, {"hair_style": 255}, VB, {}) is not None)
step("save: hair 3 accepted", CH._validate(vc, 5, {"hair_style": 3}, VB, {}) == {"hair_style": 3})
step("save: face 9 refused", refused(CH._validate, vc, 5, {"face_shap": 9}, VB, {}) is not None)
step("save: base max HP 109,000 refused for a player", refused(CH._validate, vc, 5, {"MaxHP": 109000}, VB, {}) is not None)
step("save: ... accepted for a GM", CH._validate(vc, 5, {"MaxHP": 109000}, dict(VB, GM=1), {})["MaxHP"] == 109000)
step("save: talent points over the ceiling refused", refused(CH._validate, vc, 5, {"SkillPoint": 5_000_000}, VB, {"talent_ceiling": 2_070_100}) is not None)
step("save: a plain gender change is refused (use the guarded change)", isinstance(refused(CH._validate, vc, 5, {"gender": "female"}, VB, {}), str))
step("save: the stored gender passes", CH._validate(vc, 5, {"gender": "male"}, VB, {}) == {"gender": "male"})

step("save: a stored value above the talent ceiling may be kept or lowered", CH._validate(vc, 5, {"SkillPoint": 2_200_000}, dict(VB, SkillPoint=2_277_145), {"talent_ceiling": 2_070_100}) == {"SkillPoint": 2_200_000})
step("save: ... but not raised", refused(CH._validate, vc, 5, {"SkillPoint": 2_300_000}, dict(VB, SkillPoint=2_277_145), {"talent_ceiling": 2_070_100}) is not None)
print("ALL OK" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
