#!/usr/bin/env python3
"""Unit test: the pets page's simulator / legal-pet / apply routes (modules/pets.py) with a fake database adapter.
  python3 -I tests/test_pet_sim_routes.py
No database, no server: rb.pg.json answers only the published pet steps (the merge tables fall back to the bundled
client snapshot), the pet-skill library is the real catalog-origin/petskills.json, and ctx.char_write runs the
build function and returns its SQL. The live proof of a write stays tests/test_pets.py on a VPS test copy."""
import copy
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import pets as P  # noqa: E402
from modules.common import Bad, Refused  # noqa: E402

FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:600]))
    if not ok:
        FAIL.append(msg)


with open(os.path.join(os.path.dirname(HERE), "catalog-origin", "petskills.json"), encoding="utf-8") as f:
    PETSKILLS = json.load(f)
CAT = {"species": {"4": {"display_name": "Panda", "food_kind": 3, "lifetime_values": [600, 1000], "starter_skill_id": 405,
                         "starter_skill_name": "Vital Boost II"}},
       "aptitudes": {"1": {"display_name": "Weak", "minimum_initial_savvy": 60, "maximum_initial_savvy": 80,
                           "minimum_total_growth": "12", "maximum_total_growth": "16"}},
       "hatch_steps": [{"aptitude": 1, "rank": "0.00"}, {"aptitude": 1, "rank": "0.30"}, {"aptitude": 1, "rank": "0.40"}],
       "pet_settings": {"maximum_level": 120, "maximum_rebirth_count": 100, "maximum_rank": "655.35"}, "pet_skill_ids": set()}


class PG:
    def json(self, q, timeout=None):
        if "pet_content_experience_steps" in q:
            return {"exp": {}, "rebirth": [{"n": 1, "level": 30, "item": "Spring Water"}, {"n": 2, "level": 80, "item": "Spring Water"},
                                           {"n": 3, "level": 100}, {"n": 4, "level": 110}, {"n": 5, "level": 120}]}
        raise RuntimeError("relation does not exist (test)")


def stats(**kw):
    out = []
    for i in range(6):
        s = {"pet_id": 7, "stat_code": i + 1, "initial_savvy": "11.66" if i < 5 else "11.70", "added_savvy": "300",
             "base_growth_rate": "2.5", "growth_acceleration": "0", "revision": 1}
        s.update(kw)
        out.append(s)
    return out


DUMP = {"character_base": [{"id": 5, "name": "Pethero", "fighter_job_lv": 100, "profession": 1, "pet_shed_capacity": 2}],
        "character_pets": [{"id": 7, "user_id": 5, "name": "Bao", "species_id": 4, "sex": 1, "aptitude": 1, "level": 120,
                            "experience": 0, "rank": "0.30", "completed_rebirths": 0, "rebirths_remaining": 3,
                            "completed_pet_merges": 0, "soul_contract_stage": 1, "current_energy": 100, "maximum_energy": 100,
                            "amity": 100, "satiety": 100, "remaining_lifetime": 1000, "available_stat_points": 0,
                            "growth_revealed": False, "bound": False, "activity_state": "owned", "talent_mask": 0,
                            "opened_skill_slots": 1, "available_skill_slots": 1, "is_carried": True, "is_summoned": True,
                            "contributes_to_character": False, "initial_savvy_baseline_total": "70", "revision": 9}],
        "character_pet_stat_values": stats(), "character_pet_skills": [], "sealed_pet_items": [], "character_items": []}


class RB:
    def __init__(self):
        self.pg = PG()
        self._cat = CAT

    def cat(self):
        return self._cat

    def client(self):
        return {"petskills": PETSKILLS, "pets": {}}

    def dump(self, cid):
        return copy.deepcopy(DUMP)


class Ctx:
    def __init__(self, body=None):
        self.rb = RB()
        self.body = body or {}
        self.written = None

    def need_rb(self):
        return self.rb

    def char_write(self, cid, label, build, fields=None, expected=None, applies=None):
        d = self.rb.dump(cid)
        lab = label(d) if callable(label) else label
        self.written = {"label": lab, "sql": build(copy.deepcopy(d)), "expected": expected}
        return {"ok": True, "label": lab}


def call(fn, *a, body=None):
    c = Ctx(body)
    return fn(c, *a), c


def err(fn, *a, body=None):
    try:
        call(fn, *a, body=body)
    except (Bad, Refused) as e:
        return str(e)
    return None


P._SIM.clear(), P._SK.clear(), P._STEPS.clear()
r, _ = call(P.sim_content_route)
step("sim/content falls back to the client snapshot without merge tables", "snapshot" in r["source"] and r["ceiling"] == {"savvy": "7.80", "rank": "7.80"}, r["source"])
step("sim/content: rebirth gates from the published steps, waters, spirits", r["rebirth_levels"]["2"] == 80 and r["spirits"]["5"] == [50, 100]
     and r["rebirth_spirits"]["0"] == [1, 20], r)

m, _ = call(P.sim_merge, body={"primary": {"basic": [100] * 6, "rank": "1", "level": 60},
                               "partner": {"species": 4, "basic": [60] * 6, "rank": "1", "level": 120, "growth": [3] * 6, "accel": [0] * 6},
                               "merges": 5, "spirits": 0, "savvy_pos": "max", "rank_pos": "max"})
step("sim/merge: 5 max merges, per-merge steps and totals in 2-place strings", len(m["steps"]) == 5 and m["steps"][0]["gains"][0] == "7.80"
     and m["basic"][0] == "%.2f" % (100 + sum(float(s["gains"][0]) for s in m["steps"])), m["steps"][0])
step("sim/merge refuses 7 spirits", err(P.sim_merge, body={"primary": {"basic": [1] * 6}, "partner": {"species": 4, "basic": [1] * 6}, "spirits": 7}) is not None)
step("sim/merge refuses an unknown species", err(P.sim_merge, body={"primary": {"basic": [1] * 6}, "partner": {"species": 99, "basic": [1] * 6}}) is not None)

rr, _ = call(P.sim_rebirth, body={"pet": {"completed": 0, "growth": ["2.5"] * 6, "accel": [0] * 6, "soul_contract_stage": 1,
                                          "rebirths_remaining": 3}, "rebirths": 3, "spirits": 5, "pos": "min", "level": 30})
step("sim/rebirth: 3 min rebirths with 5 spirits = +0.30, Added at 30 = 30 x 2.80", rr["accel"] == ["0.30"] * 6 and rr["added_at_level"] == ["84.00"] * 6, rr)

lg, _ = call(P.legal, 5, body={"pet": 7})
step("legal: the stored pet passes", [x["level"] for x in lg["checks"]] == ["ok"], lg["checks"])
step("legal: limits (Basic 70 + 0 merges, rank max = best hatch 0.40)", lg["limits"]["basic_total_max"] == "70.00" and lg["limits"]["rank_max"] == "0.40", lg["limits"])
lg, _ = call(P.legal, 5, body={"pet": 7, "fields": {"maximum_energy": 1000, "rank": "12", "savvy": {"1": "40"}}})
lv = {(x["level"], x["what"]) for x in lg["checks"]}
step("legal with pending edits: energy, rank, Basic warnings", {("warn", "energy"), ("warn", "rank"), ("warn", "basic")} <= lv, lv)

body = {"pet": 7, "revision": 9, "merge": {"partner": {"species": 4, "basic": [60] * 6, "rank": "1", "level": 120, "growth": [3] * 6},
                                           "merges": 2, "spirits": 0, "savvy_pos": "max", "rank_pos": "max"},
        "rebirth": {"rebirths": 2, "spirits": 5, "pos": "max", "level": 50}}
w, c = call(P.apply_sim, 5, body=body)
sql = "\n".join(c.written["sql"])
step("apply-sim: Basic + 2 x 7.80 (11.66 -> 27.26)", "initial_savvy = 27.26" in sql, sql[:400])
step("apply-sim: acceleration +0.40, Added = (2.5 + 0.40) x 50 = 145.00", "growth_acceleration = 0.40" in sql and re.search(r"added_savvy = 145\.0+,", sql), sql[:500])
step("apply-sim: merges 2, rebirths 2, attempts 3 -> 1, level 50, rank 0.30 + 2 x 1.95 cap", "completed_pet_merges = 2" in sql
     and "completed_rebirths = 2" in sql and "rebirths_remaining = 1" in sql and "level = 50" in sql, sql[-300:])
step("apply-sim: revision expectation and a readable change label", c.written["expected"][0]["values"] == {"revision": 9}
     and "2 merge(s)" in c.written["label"] and "2 rebirth(s)" in c.written["label"], c.written)
DUMP["character_pets"][0]["soul_contract_stage"] = 0
step("apply-sim: rebirth without a Soul Contract refused", "Soul Contract" in (err(P.apply_sim, 5, body=body) or ""))
DUMP["character_pets"][0]["soul_contract_stage"] = 1
low = json.loads(json.dumps(body)); low["merge"]["partner"]["level"] = 20
step("apply-sim: a partner below level 30 refused", "level 30" in (err(P.apply_sim, 5, body=low) or ""))
many = json.loads(json.dumps(body)); many["rebirth"]["rebirths"] = 5
DUMP["character_pets"][0]["maximum_energy"] = 1000      # the fake dump does not apply the write: a warning to report
w, c = call(P.apply_sim, 5, body=many)
DUMP["character_pets"][0]["maximum_energy"] = 100
step("apply-sim: rebirths beyond the attempts left say so in the change label", "2 extra rebirth attempt(s)" in c.written["label"], c.written["label"])
step("apply-sim: the result is re-checked (note with legal warnings)", "Legal pet checks" in (w.get("note") or ""), w)
step("apply-sim: nothing to apply refused", err(P.apply_sim, 5, body={"pet": 7, "revision": 9}) is not None)

lib = P.skill_library(RB())
e = lib["skills"].get(400) or {}
step("skill library carries the effect curve (Restrict / Values)", e.get("restrict") and e.get("values") and len(e["restrict"]) == len(e["values"]), e)

# ---- B21 item 23: planner routes
fx, _ = call(P.sim_fictitious, body={"species": 4, "aptitude": 1, "level": 120, "quality": "max", "rebirths": 2})
step("sim/fictitious: best Weak Panda = Basic 80.00, growth 16.00, best hatch rank 0.40, 2 rebirths of 0.11",
     fx["basic_total"] == "80.00" and fx["growth_total"] == "16.00" and fx["rank"] == "0.40" and fx["accel"] == ["0.22"] * 6, fx)
step("sim/fictitious refuses an unpublished nature", err(P.sim_fictitious, body={"species": 4, "aptitude": 42}) is not None)
pl, _ = call(P.sim_plan, body={"primary": {"basic": ["11.66"] * 6, "rank": "0.30", "level": 120, "growth": ["2.5"] * 6, "accel": [0] * 6},
                               "partner": {k: fx[k] for k in ("species", "basic", "rank", "level", "growth", "accel")},
                               "merges": 20, "spirits": 0, "rebirths": 3, "rebirth_spirits": 0, "level": 120})
step("sim/plan: 21 merge rows with an exact range around the average, 4 rebirth rows, an end state",
     len(pl["merge"]["rows"]) == 21 and len(pl["rebirth"]["rows"]) == 4 and
     all(float(r["range"]["basic_total"][0]) <= float(r["avg"]["basic_total"]) <= float(r["range"]["basic_total"][1]) for r in pl["merge"]["rows"])
     and pl["end"]["level"] == 120, pl["end"])
step("sim/plan refuses 301 merges", err(P.sim_plan, body={"primary": {"basic": [1] * 6}, "partner": {"species": 4, "basic": [1] * 6}, "merges": 301}) is not None)

print("all passed" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
