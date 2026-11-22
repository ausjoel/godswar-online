#!/usr/bin/env python3
"""Unit test: the pets page's Pet Manager simulator (modules/_pet_sim.py). No database, no server.
  python3 -I tests/test_pet_sim_unit.py                      # against the bundled snapshot catalog-origin/pet-alter.json
  python3 -I tests/test_pet_sim_unit.py --client-sys DIR     # also: the snapshot equals the client's Pet_Alter.xml
The golden numbers are the server's own worked examples (godswar-dev/server docs/pet-system-client-derived-rules.md,
docs/pet-rebirth-balance.md) and its compiled baselines (Infrastructure/Pets/PetMergeSavvyLookupContentBaseline.cs)."""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import _pet_sim as S  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--client-sys", default=None)
a = ap.parse_args()
FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:600]))
    if not ok:
        FAIL.append(msg)


C = S.load_snapshot()

# ---- the snapshot equals the server's compiled baseline (PetMergeSavvyLookupContentBaseline.cs)
def restrict(o):
    if o <= 10: return -4000 + (o - 1) * 10
    if o <= 16: return -3910 + (o - 10) * 12
    if o <= 22: return -3838 + (o - 16) * 14
    if o <= 28: return -3754 + (o - 22) * 16
    if o <= 34: return -3658 + (o - 28) * 18
    if o <= 40: return -3550 + (o - 34) * 20
    if o <= 45: return -3430 + (o - 40) * 22
    if o <= 50: return -3320 + (o - 45) * 24
    if o <= 125: return -3200 + (o - 50) * 32
    if o <= 175: return -800 + (o - 125) * 24
    return 400 + (o - 175) * 16


base = [[restrict(o), o if o <= 100 else o * 2 - 100] for o in range(1, 201)]
step("200 savvy rows = the server baseline (Restrict / Values)", C["savvy"] == base,
     [r for r, b in zip(C["savvy"], base) if r != b][:3])
step("factors: 0.8 for species 2/3/6/10, 1.4 for 1/7, 2.6 for the rest (45 species)",
     C["factors"][2] == 80 and C["factors"][10] == 80 and C["factors"][1] == 140 and C["factors"][7] == 140
     and C["factors"][4] == 260 and C["factors"][45] == 260 and len(C["factors"]) == 45, C["factors"])
step("spirits 0-5: 0/10/20/30/40/50 % minimum, 100 % maximum",
     [C["spirits"][i] for i in range(6)] == [(0, 100), (10, 100), (20, 100), (30, 100), (40, 100), (50, 100)])
step("skill books: Vital Boost I (#400) is taught by book 10200; Vampiric I by 16400", 10200 in C["books"].get(400, [])
     and 16400 in C["books"].get(6400, []), {k: C["books"].get(k) for k in (400, 6400)})

# ---- merge savvy: the historical case (docs "Client-compatible Savvy result")
# deputy Added 447.44 -> floor(44744/5) = 8948; Agility: 8948 - 12289 + 4403 = 1062 -> row 300 -> x2.6 = 7.80;
# Luck: -635 -> Restrict -656 row, Values 162 -> 4.21
sb = S.savvy_bounds(C, [12289] * 6, [4403] * 6, [44744] * 6, 4, 0)
step("q = floor(Added/5) - primary + deputy = 1062", sb[0]["q"] == 1062, sb[0])
step("q 1062 saturates at Values 300, x2.6 = 7.80 max", sb[0]["max"] == 780 and sb[0]["min"] == 1, sb[0])
q_luck = S.savvy_bounds(C, [8948 + 4403 + 635] * 6, [4403] * 6, [44744] * 6, 4, 0)[5]
step("q -635 resolves the Restrict -656 row (Values 162): 162 x 2.6 = 4.21", q_luck["q"] == -635 and q_luck["base"] == 162
     and q_luck["max"] == 421, q_luck)
exp = {0: (1, 780, 1, 421), 1: (78, 780, 42, 421), 2: (156, 780, 84, 421), 3: (234, 780, 126, 421), 4: (312, 780, 168, 421),
       5: (390, 780, 211, 421)}
for n, (lo7, hi7, lo4, hi4) in exp.items():
    s7 = S.savvy_bounds(C, [12289], [4403], [44744], 4, n)[0]
    s4 = S.savvy_bounds(C, [8948 + 4403 + 635], [4403], [44744], 4, n)[0]
    step("%d spirit(s): 7.80 -> %s-%s, 4.21 -> %s-%s (round half up)" % (n, S.un(lo7), S.un(hi7), S.un(lo4), S.un(hi4)),
         (s7["min"], s7["max"], s4["min"], s4["max"]) == (lo7, hi7, lo4, hi4), (s7, s4))

# "Need N more": primary Strength 157.82, Rock Elf (1.4) deputy Basic 22.05, Added 104.38 -> Need 74.90 more
nd = S.savvy_bounds(C, [15782], [2205], [10438], 1, 0)[0]
step("need: 157.82 vs Rock Elf 22.05 + 104.38/5 -> Need 74.90 more", nd["max"] == 0 and nd["need"] == 7490, nd)
# species 0.8 need q >= -39.90, others -40.00
step("factor 0.8: q -3991 gives nothing, -3990 gives 0.01", S.savvy_bounds(C, [3991], [0], [0], 2, 0)[0]["max"] == 0
     and S.savvy_bounds(C, [3990], [0], [0], 2, 0)[0]["max"] == 1)
step("factor 1.4: q -4000 gives 0.01, -4001 nothing", S.savvy_bounds(C, [4000], [0], [0], 1, 0)[0]["max"] == 1
     and S.savvy_bounds(C, [4001], [0], [0], 1, 0)[0]["max"] == 0)
step("integer factors: 300 x 2.6 = 7.80 and 162 x 2.6 = 4.21 exactly", S.savvy_bounds(C, [0], [0], [100000], 4, 0)[0]["max"] == 780)

# ---- merge rank: base 250 -> 200 / 350 / 650 (factors 0.8 / 1.4 / 2.6); no spirit = one fixed value
r250 = next(t for t, b in C["rank"] if b == 250)
for sp, want in ((2, 200), (1, 350), (4, 650)):
    rb = S.rank_bounds(C, 1000, 1000 + r250, sp, 0)
    step("rank base 250 x factor of species %d = %s, fixed with no spirit" % (sp, S.un(want)),
         rb["min"] == rb["max"] == want and rb["fixed"], rb)
rb = S.rank_bounds(C, 1000, 1000 + r250, 4, 3)
step("rank with 3 spirits: ceil(650 x 30%) = 1.95 .. 6.50", (rb["min"], rb["max"]) == (195, 650), rb)
rb = S.rank_bounds(C, 65500, 70000, 4, 0)
step("rank capped at 655.35", rb["max"] == 35 and rb["capped"], rb)
step("rank below the first Qualityadd row gains 0", S.rank_bounds(C, 10000, 0, 4, 5)["max"] == 0)

# ---- N merges are iterative: each gain lowers the next q
sim = S.simulate_merges(C, {"basic": [100] * 6, "rank": 1, "level": 60},
                        {"species": 4, "basic": [60] * 6, "rank": 1, "level": 120, "growth": [3] * 6, "accel": [0] * 6}, 30, 0, "max", "max")
g = [st["gains"][0] for st in sim["steps"]]
step("30 max merges: gains never rise and the total equals the step sum", g[0] == 780 and g[-1] < g[0] and all(x >= y for x, y in zip(g, g[1:]))
     and sim["basic"][0] == 10000 + sum(g), g[:8])
step("partner Added = level x (growth + accel) = 120 x 3 = 360.00", sim["partner_added"] == [36000] * 6, sim["partner_added"])
mn = S.simulate_merges(C, {"basic": [100] * 6, "rank": 1, "level": 60},
                       {"species": 4, "basic": [60] * 6, "rank": 1, "added": [360] * 6}, 30, 0, "min", "min")
step("min preset with no spirit adds 0.01 per merge per stat while gains last",
     mn["steps"][0]["gains"] == [1] * 6 and mn["basic"][0] == 10030 and mn["basic"][0] < sim["basic"][0], mn["basic"])
weak = S.simulate_merges(C, {"basic": [400] * 6, "rank": 50, "level": 60}, {"species": 2, "basic": [1] * 6, "rank": 0, "added": [0] * 6}, 3, 5)
step("a weak partner stalls and says so", weak["stalled_at"] == 1 and any("adds nothing" in x for x in weak["notes"]), weak["notes"])
low = S.simulate_merges(C, {"basic": [10] * 6, "rank": 1, "level": 20}, {"species": 4, "basic": [10] * 6, "rank": 1, "level": 25}, 1, 0)
step("level gate 30 is reported for both pets", sum("level 30" in x for x in low["notes"]) == 2, low["notes"])
pos = S.simulate_merges(C, {"basic": [100] * 6, "rank": 1}, {"species": 4, "basic": [60] * 6, "rank": 1, "added": [360] * 6}, 1, 5,
                        [0, 250, 500, 750, 1000, 500], 1000)
b0 = pos["steps"][0]["bounds"]
step("per-stat sliders land where they point (0 / 25 / 50 / 75 / 100 %)",
     pos["steps"][0]["gains"][0] == b0[0][0] and pos["steps"][0]["gains"][4] == b0[4][1]
     and pos["steps"][0]["gains"][2] == S.pick(b0[2][0], b0[2][1], 500), pos["steps"][0])

# ---- rebirth
step("rebirth ranges 0-5 spirits: 0.01/0.02/0.04/0.06/0.08/0.10 .. 0.20",
     [S.rebirth_range(i) for i in range(6)] == [(1, 20), (2, 20), (4, 20), (6, 20), (8, 20), (10, 20)])
rs = S.simulate_rebirths(C, {"completed": 0, "growth": ["2.5"] * 6, "accel": [0] * 6, "soul_contract_stage": 1, "rebirths_remaining": 10},
                         10, 5, "max", 120)
step("10 max rebirths: +2.00 acceleration per stat", rs["accel"] == [200] * 6, rs["accel"])
step("gates 30, 80, 100, 110 then 120; waters Spring Water to 30", [x["level_needed"] for x in rs["steps"]][:6] == [30, 80, 100, 110, 120, 120]
     and rs["steps"][0]["water"]["item"] == 10107, [x["level_needed"] for x in rs["steps"]])
step("model changes at rebirths 8 (and 20)", [x["n"] for x in rs["steps"] if x["model_change"]] == [8])
step("Added at level 1 = growth + accel = 4.50; at 120 = 540.00", rs["added_level1"] == [450] * 6 and rs["added_at_level"] == [54000] * 6,
     (rs["added_level1"], rs["added_at_level"]))
nc = S.simulate_rebirths(C, {"completed": 0, "growth": ["2.5"] * 6, "accel": [0] * 6, "soul_contract_stage": 0, "rebirths_remaining": 0}, 2, 0)
step("rebirth without a contract / without attempts is noted", len(nc["notes"]) == 2, nc["notes"])
step("Phoenix with 5 rebirths: 0.50-1.00 on top of a 14-16 nature = 14.50-17.00",
     (1400 + S.phoenix_range(5)[0], 1600 + S.phoenix_range(5)[1]) == (1450, 1700))
try:
    S.simulate_rebirths(C, {"completed": 99}, 2, 0)
    step("more than 100 rebirths refused", False)
except S.SimError:
    step("more than 100 rebirths refused", True)

# ---- legal checks
APT = {"savvy": ["60", "80"], "growth": ["12", "16"]}


def pet(**kw):
    p = {"level": 120, "rank": "2.70", "completed_pet_merges": 0, "completed_rebirths": 0, "rebirths_remaining": 0,
         "soul_contract_stage": 0, "maximum_energy": 100, "current_energy": 100, "satiety": 100, "amity": 100,
         "remaining_lifetime": 1000, "opened_skill_slots": 2, "available_skill_slots": 2, "initial_savvy_baseline_total": "70",
         "stats": [{"stat_code": i + 1, "initial_savvy": "11.66" if i < 5 else "11.70", "base_growth_rate": "2.5",
                    "growth_acceleration": "0", "total": "300"} for i in range(6)]}
    p.update(kw)
    return p


def levels(p, **kw):
    return {(x["level"], x["what"]) for x in S.legal_checks(C, p, APT, ["2.00", "2.70", "3.00"], {"max_lifetime": 1100}, **kw)}


step("a fresh hatched pet is legal", levels(pet()) == {("ok", "all")}, levels(pet()))
step("max energy 1000 warns", ("warn", "energy") in levels(pet(maximum_energy=1000)))
step("Basic above hatch + merges x 6 x 7.80 warns", ("warn", "basic") in levels(pet(stats=[dict(s, initial_savvy="20") for s in pet()["stats"]])))
step("... and 2 merges make 120.00 total reachable (70 + 2 x 46.80 = 163.60)",
     ("warn", "basic") not in levels(pet(completed_pet_merges=2, rank="3.00", stats=[dict(s, initial_savvy="20") for s in pet()["stats"]])))
step("Basic below the birth baseline is an error (start-up check)",
     ("error", "basic") in levels(pet(stats=[dict(s, initial_savvy="5") for s in pet()["stats"]])))
step("rank without merges must be a hatch outcome", ("warn", "rank") in levels(pet(rank="2.50")))
step("rank 20 with 2 merges warns (3.00 + 2 x 7.80 = 18.60)", ("warn", "rank") in levels(pet(rank="20", completed_pet_merges=2)))
step("acceleration 2.00 with 5 rebirths warns (max 1.00)", ("warn", "acceleration") in levels(pet(
    completed_rebirths=5, soul_contract_stage=1, stats=[dict(s, growth_acceleration="2") for s in pet()["stats"]])))
step("acceleration without rebirths warns", ("warn", "acceleration") in levels(pet(stats=[dict(s, growth_acceleration="0.1") for s in pet()["stats"]])))
step("rebirths without a contract warn", ("warn", "contract") in levels(pet(completed_rebirths=1, stats=[dict(s, growth_acceleration="0.1") for s in pet()["stats"]])))
step("growth total outside the bracket is an error", ("error", "growth") in levels(pet(stats=[dict(s, base_growth_rate="5") for s in pet()["stats"]])))
step("rebirths done + left above 100 warns", ("warn", "rebirths") in levels(pet(rebirths_remaining=101)))
step("lifetime above the species bar warns", ("warn", "lifetime") in levels(pet(remaining_lifetime=5000)))
step("one stat holding > 92% of Basic without merges warns",
     ("warn", "basic") in levels(pet(stats=[dict(s, initial_savvy="66" if s["stat_code"] == 1 else "0.80") for s in pet()["stats"]])))
lib = {"skills": {405: {"type": 1}, 9999: {"type": 77}}}
sk = [{"name": "Ghost book", "skill_id": 9999, "head": 9999, "type": 77, "published": True, "species_ok": True,
       "trait": [0, 0, 0, 0, 0, 0]}]
step("a skill tier with no skill book warns", ("warn", "skills") in levels(pet(), skills=sk, lib=lib))
sk2 = [{"name": "Vital Boost I", "skill_id": 400, "head": 400, "type": 0, "published": True, "species_ok": True,
        "trait": [0, 0, 0, 0, 999, 0]}]
step("a Trait need above the pet's savvy warns", ("warn", "skills") in levels(pet(), skills=sk2, lib=lib))

# ---- the client file, when present
if a.client_sys:
    with open(os.path.join(a.client_sys, "Pet_Alter.xml"), encoding="utf-8-sig", errors="replace") as f:
        X = S.content_from_pet_alter(f.read())
    step("the snapshot equals the client's Pet_Alter.xml", X["savvy"] == C["savvy"] and X["rank"] == C["rank"]
         and X["factors"] == C["factors"] and X["spirits"] == C["spirits"])

print("all passed" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
