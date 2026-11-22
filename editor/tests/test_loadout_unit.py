#!/usr/bin/env python3
"""Unit test (W4A): the quick-add loadout's item choice (modules/gear_presets.py loadout_pick / loadout_inputs) against
the real client catalogue (catalog-origin/items.json). No database, no server.

    python3 -I tests/test_loadout_unit.py
"""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import gear_presets as GP  # noqa: E402
from modules import gearstats as GS  # noqa: E402
from modules.common import Bad  # noqa: E402

FAIL = []
ITEMS = json.load(open(os.path.join(os.path.dirname(HERE), "catalog-origin", "items.json")))


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:400]))
    if not ok:
        FAIL.append(msg)


for cls in range(4):
    for lv in (1, 10, 60, 100, 110, 125, 140, 160, 200):
        p = GP.loadout_pick(ITEMS, cls, lv)
        bad = [(s, e["id"]) for s, e in p.items() if not (int(e.get("minlv") or 1) <= lv <= int(e.get("maxlv") or 200))]
        step("class %d level %d: every piece wearable at that level" % (cls, lv), not bad, bad)
        wrong = [(s, e["id"]) for s, e in p.items() if (e.get("classes") or []) and cls not in e["classes"]]
        step("class %d level %d: every piece usable by the class" % (cls, lv), not wrong, wrong)
        slot = [(s, e["id"], e.get("slot")) for s, e in p.items() if e.get("slot") != s and not (s == 9 and e.get("slot") == 8)]
        step("class %d level %d: every piece fits its slot" % (cls, lv), not slot, slot)
        gm = [e["id"] for e in p.values() if str(e.get("name", "")).startswith("GM ") or int(e["id"]) >= 3500]
        step("class %d level %d: no GM / novelty / server-only items" % (cls, lv), not gm, gm)
        guide = [(s, e["id"], GS.guide_type(e)[1]) for s, e in p.items() if not GS.guide_type(e)[0]]
        step("class %d level %d: every piece has legal 4-stat families (IGG table)" % (cls, lv), not guide, guide)
        if lv >= 60:
            want = set(range(12)) - (set() if cls in GP.CLASS_SHIELD else {11})
            step("class %d level %d: a full set (%d pieces)" % (cls, lv, len(want)), set(p) == want, sorted(set(want) - set(p)))
p = GP.loadout_pick(ITEMS, 0, 100)
step("Warrior: one-hand sword + shield, armour", p[10]["category"] == "One-Hand Sword" and 11 in p and p[3]["kind"] == "armor")
p = GP.loadout_pick(ITEMS, 1, 100)
step("Champion: spear, no shield", p[10]["category"] == "Spear" and 11 not in p)
p = GP.loadout_pick(ITEMS, 3, 100)
step("Mage: two-hand wand, cloth", p[10]["category"] == "Two-Hand Wand" and p[3]["kind"] == "cloth" and 11 not in p)
p = GP.loadout_pick(ITEMS, 2, 140)
step("Priest 140: class-specific pieces before all-class ones", all((e.get("classes") or []) == [2] for s, e in p.items() if s not in (8, 9)),
     {s: e.get("classes") for s, e in p.items()})
step("level 200 never picks above 200", all(int(e.get("minlv") or 1) <= 200 for e in GP.loadout_pick(ITEMS, 0, 200).values()))
for body, ok in (({"level": 0}, False), ({"level": 201}, False), ({"level": 50, "quality": 21}, False), ({"level": 50, "grade": 26}, False),
                 ({"level": 50, "profession": 4}, False), ({"level": 50}, True)):
    try:
        o = GP.loadout_inputs(body, 2)
        step("inputs %s accepted" % body, ok and o["profession"] == 2 and o["quality"] == GP.DEFAULT_QUALITY, o)
    except Bad as e:
        step("inputs %s refused" % body, not ok, e)
# every chosen piece has at least four legal stat families (else attributes() would refuse at preview)
from modules import common  # noqa: E402


class _Ctx:
    catalog = common.CATALOG


short = []
for cls in range(4):
    for lv in (10, 60, 110, 140, 160):
        for s_, e in GP.loadout_pick(ITEMS, cls, lv).items():
            t, why, legal = GS.options(_Ctx(), e, 10)
            if len(legal) < 4:
                short.append((cls, lv, s_, e["id"], len(legal), why))
step("every chosen piece has >= 4 legal stat families", not short, short[:5])

print("ALL OK" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
