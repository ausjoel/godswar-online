#!/usr/bin/env python3
"""Unit test (B21 item 23): the pet planner (modules/_pet_plan.py) and server-derived vectors for _pet_sim.
  python3 -I tests/test_pet_plan_unit.py
No database. Part 1 replays the reborn server's own policy checks with the numbers copied exactly from
server/tests/Godswar.Server.ProtocolChecks (PetMergeSavvyPolicyChecks.cs, PetMergeRankPolicyChecks.cs,
PetRebirthGrowthPolicyChecks.cs); each step names the C# check it mirrors. Scripted server draws are reproduced by
giving the simulator the slider position that lands on the drawn value. Part 2 proves the planner: the lowest / highest
roll paths really bound every reachable outcome (exhaustive search over a single stat), gains only fall, and the
fictitious pet stays inside its aptitude's brackets."""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import _pet_sim as S  # noqa: E402
from modules import _pet_plan as P  # noqa: E402

FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:600]))
    if not ok:
        FAIL.append(msg)


C = S.load_snapshot()
H = S.h

# ================================================================ Part 1: server vectors
# PetMergeSavvyPolicyChecks: ScreenshotPrimary / ScreenshotDeputyBasic / ScreenshotDeputyAdded, deputy species 4 (2.6)
SP = [H(x) for x in ("122.89", "122.89", "128.98", "200.00", "200.00", "131.62")]
SD = [H(x) for x in ("44.03", "44.03", "62.98", "1.00", "1.00", "35.79")]
SA = [H("447.44")] * 6


def draw(bounds, values):
    """Positions that make _pet_sim.pick land on the scripted server draws (fixed rows take no draw)."""
    out = []
    for (lo, hi), v in zip(bounds, values):
        if hi <= lo:
            out.append(0)
            continue
        pos = next(p for p in range(1001) if S.pick(lo, hi, p) == v)
        out.append(pos)
    return out


sb = S.savvy_bounds(C, SP, SD, SA, 4, 0)
step("CheckHistoricalScreenshotWithoutSpirits: Agility added/5 = 89.48, q = 10.62, base 300, range 0.01-7.80",
     (SA[0] // 5, sb[0]["q"], sb[0]["base"], sb[0]["min"], sb[0]["max"]) == (8948, 1062, 300, 1, 780), sb[0])
step("CheckHistoricalScreenshotWithoutSpirits: Luck q = -6.35, row -656 / base 162, range 0.01-4.21",
     (sb[5]["q"], sb[5]["base"], sb[5]["min"], sb[5]["max"]) == (-635, 162, 1, 421), sb[5])
step("CheckHistoricalScreenshotWithoutSpirits: Technique below the first Restrict row is a zero delta",
     sb[3]["base"] is None and (sb[3]["min"], sb[3]["max"]) == (0, 0), sb[3])
# SequenceRandom(1, 780, 400, 421) -> gains (0.01, 7.80, 4.00, 0, 0, 4.21)
want = [1, 780, 400, 0, 0, 421]
r = S.simulate_merges(C, {"basic": [S.un(x) for x in SP], "rank": "0", "level": 30},
                      {"species": 4, "basic": [S.un(x) for x in SD], "added": [S.un(x) for x in SA], "rank": "0"}, 1, 0,
                      draw([(b["min"], b["max"]) for b in sb], want), 0)
step("CheckHistoricalScreenshotWithoutSpirits: gains (0.01, 7.80, 4.00, 0, 0, 4.21)", r["steps"][0]["gains"] == want, r["steps"][0]["gains"])

sb5 = S.savvy_bounds(C, SP, SD, SA, 4, 5)
step("CheckFiveSpiritMinimumsAndIndependentRolls: five spirits use half-up 50% minimums 3.90 and 2.11",
     (sb5[0]["min"], sb5[5]["min"], sb5[0]["max"], sb5[5]["max"]) == (390, 211, 780, 421), (sb5[0], sb5[5]))
want5 = [390, 780, 500, 0, 0, 211]
r5 = S.simulate_merges(C, {"basic": [S.un(x) for x in SP], "rank": "0", "level": 30},
                       {"species": 4, "basic": [S.un(x) for x in SD], "added": [S.un(x) for x in SA], "rank": "0"}, 1, 5,
                       draw([(b["min"], b["max"]) for b in sb5], want5), 0)
step("CheckFiveSpiritMinimumsAndIndependentRolls: gains (3.90, 7.80, 5.00, 0, 0, 2.11)", r5["steps"][0]["gains"] == want5, r5["steps"][0]["gains"])

z = S.savvy_bounds(C, [4000] * 6, [0, 10, 0, 0, 0, 0], [0] * 6, 2, 0)
step("CheckZeroMaximumRowsAndValidation: factor 0.8, Values 1 truncates to 0 and Values 2 to 0.01",
     (z[0]["base"], z[0]["max"], z[1]["base"], z[1]["max"], z[1]["min"]) == (1, 0, 2, 1, 1), (z[0], z[1]))

# PetMergeRankPolicyChecks.CheckLookupBoundaries
lk = [(-3001, None), (-3000, 1), (-1, 248), (0, 250), (500, 300), (5000, 300)]
got = [(d, S.resolve(C["rank"], d)) for d, _ in lk]
step("CheckLookupBoundaries: -30.01 none, -30.00 -> 1, -0.01 -> 248, 0 -> 250, 5.00 / 50.00 -> 300", got == lk, got)
step("CheckLookupBoundaries: -0.01 resolves the -0.24 row (greatest threshold not above)",
     max(t for t, _ in C["rank"] if t <= -1) == -24)


def rank_roll(pr, dr, sp, spirits, value):
    b = S.rank_bounds(C, H(pr), H(dr), sp, spirits)
    return b, (b["min"] <= value <= b["max"])


# CheckDeputySpeciesAndFiveSpiritBounds: 15 vs 15, five spirits
for sp, val, lab in ((2, 100, "0.8 deputy factor minimum"), (1, 350, "1.4 deputy factor maximum"), (37, 325, "2.6 deputy factor minimum")):
    b, ok = rank_roll("15", "15", sp, 5, val)
    edge = b["min"] if "minimum" in lab else b["max"]
    step("CheckDeputySpeciesAndFiveSpiritBounds: %s = %s" % (lab, S.un(val)), ok and edge == val, b)
b = S.rank_bounds(C, H("15"), H("15"), 37, 5)
step("CheckDeputySpeciesAndFiveSpiritBounds: 15 + 6.50 = 21.50 accepted, 21.51 rejected",
     H("15") + b["max"] == H("21.50") and H("21.51") - H("15") > b["max"], b)
# CheckHistoricalDecimalFactorSemantics: ranks 30 vs 0.48 (base 5) and 15 vs 15 (base 250) -> exact decimal products
for pr, dr, sp, val in (("30", "0.48", 2, 4), ("30", "0.48", 1, 7), ("30", "0.48", 37, 13),
                        ("15", "15", 2, 200), ("15", "15", 1, 350), ("15", "15", 37, 650)):
    b = S.rank_bounds(C, H(pr), H(dr), sp, 5)
    step("CheckHistoricalDecimalFactorSemantics: %s vs %s species %d -> factor-adjusted %s" % (pr, dr, sp, S.un(val)),
         b["factor_adjusted"] == val and b["max"] == val, b)
# CheckNoGainAndWireCap
b = S.rank_bounds(C, H("40"), 0, 37, 5)
step("CheckNoGainAndWireCap: 40 vs 0 is a valid zero gain", (b["min"], b["max"]) == (0, 0), b)
b = S.rank_bounds(C, H("654"), H("654"), 37, 5)
step("CheckNoGainAndWireCap: 654 vs 654 caps at 655.35 (+1.35)", b["max"] == 135 and b["capped"] and H("654") + b["max"] == 65535, b)
b = S.rank_bounds(C, H("100.94"), H("100.94"), 37, 5)
step("CheckNoGainAndWireCap: 100.94 vs 100.94 with 3.25 is valid", b["min"] <= 325 <= b["max"], b)

# PetRebirthGrowthPolicyChecks.CheckRanges / CheckRolls
step("CheckRanges: 0-5 spirits -> 0.01/0.02/0.04/0.06/0.08/0.10 .. 0.20",
     [S.rebirth_range(n) for n in range(6)] == [(1, 20), (2, 20), (4, 20), (6, 20), (8, 20), (10, 20)])
rr = S.simulate_rebirths(C, {"completed": 0, "growth": ["1"] * 6, "accel": ["1", "2", "3", "4", "5", "6"], "soul_contract_stage": 1}, 1, 5, "min")
step("CheckRolls: five-spirit minimum adds 0.10 -> (1.10 .. 6.10)", rr["accel"] == [110, 210, 310, 410, 510, 610], rr["accel"])
rr = S.simulate_rebirths(C, {"completed": 0, "growth": ["1"] * 6, "accel": ["1"] * 6, "soul_contract_stage": 1}, 1, 5, "max")
step("CheckRolls: maximum adds 0.20", rr["accel"] == [120] * 6, rr["accel"])
rr = S.simulate_rebirths(C, {"completed": 0, "growth": ["1"] * 6, "accel": ["1"] * 6, "soul_contract_stage": 1}, 1, 0, "min")
step("CheckRolls: zero-spirit minimum adds 0.01", rr["accel"] == [101] * 6, rr["accel"])
step("PetRebirthSpiritPolicy.Roll ignores the rebirth number: rebirth 1 and rebirth 90 roll the same range",
     S.simulate_rebirths(C, {"completed": 89, "growth": ["1"] * 6, "accel": ["0"] * 6, "soul_contract_stage": 1}, 1, 3, "max")["steps"][0]["increase"]
     == S.simulate_rebirths(C, {"completed": 0, "growth": ["1"] * 6, "accel": ["0"] * 6, "soul_contract_stage": 1}, 1, 3, "max")["steps"][0]["increase"])

# ================================================================ Part 2: the planner
# exhaustive search, one stat (stats roll independently): every reachable Basic after k merges lies between the
# lowest-roll and highest-roll paths, and both ends are reached
def reachable(p0, d, a, sp, spirits, k):
    cur = {p0}
    for _ in range(k):
        nxt = set()
        for p in cur:
            b = S.savvy_bounds(C, [p], [d], [a], sp, spirits)[0]
            nxt.update(p + g for g in range(b["min"], b["max"] + 1))
        cur = nxt
    return cur


for p0, d, a, sp, spirits, k in ((H("60"), H("45"), H("300"), 4, 0, 4), (H("100"), H("80"), H("447.44"), 1, 3, 4),
                                  (H("40"), H("10"), H("200"), 2, 0, 5), (H("150"), H("120"), H("600"), 4, 5, 3)):
    reach = reachable(p0, d, a, sp, spirits, k)
    pri = {"basic": [S.un(p0)] * 6, "rank": "0", "level": 30}
    par = {"species": sp, "basic": [S.un(d)] * 6, "added": [S.un(a)] * 6, "rank": "0"}
    lo = S.simulate_merges(C, pri, par, k, spirits, 0, 0)["basic"][0]
    hi = S.simulate_merges(C, pri, par, k, spirits, 1000, 1000)["basic"][0]
    step("paths bound every outcome: Basic %s vs partner %s + %s/5, factor of species %d, %d spirit(s), %d merges: %s .. %s"
         % (S.un(p0), S.un(d), S.un(a), sp, spirits, k, S.un(lo), S.un(hi)),
         min(reach) == lo and max(reach) == hi, (S.un(min(reach)), S.un(max(reach)), S.un(lo), S.un(hi)))

# the lowest / highest roll PATHS are not always the extremes for rank (a small gain keeps the next gap wider), so the
# planner walks every reachable value as intervals (exact_ranges); check it against an exhaustive search
def reach_rank(p0, d, sp, spirits, k):
    cur = {p0}
    for _ in range(k):
        nxt = set()
        for pr in cur:
            b = S.rank_bounds(C, pr, d, sp, spirits)
            nxt.update(pr + g for g in range(b["min"], b["max"] + 1))
        cur = nxt
    return cur


for p0, d, sp, spirits, k in ((H("20"), H("25"), 4, 5, 4), (H("1"), H("30"), 1, 2, 5), (H("650"), H("655"), 4, 5, 3), (H("10"), H("10"), 2, 0, 6)):
    cur = reach_rank(p0, d, sp, spirits, k)
    ex = P.exact_ranges(C, {"basic": ["10"] * 6, "rank": S.un(p0)}, {"species": sp, "basic": ["0"] * 6, "added": ["0"] * 6, "rank": S.un(d)}, k, spirits)
    step("exact rank range = exhaustive search (%s vs %s, species %d, %d spirit(s), %d merges): %s .. %s" % (
        S.un(p0), S.un(d), sp, spirits, k, S.un(min(cur)), S.un(max(cur))), ex[-1]["rank"] == [min(cur), max(cur)], (ex[-1]["rank"], min(cur), max(cur)))
for p0, d, a, sp, spirits, k in ((H("60"), H("45"), H("300"), 4, 0, 4), (H("100"), H("80"), H("447.44"), 1, 3, 4), (H("40"), H("10"), H("200"), 2, 0, 5)):
    reach = reachable(p0, d, a, sp, spirits, k)
    ex = P.exact_ranges(C, {"basic": [S.un(p0)] * 6, "rank": "0"}, {"species": sp, "basic": [S.un(d)] * 6, "added": [S.un(a)] * 6, "rank": "0"}, k, spirits)
    step("exact Basic range = exhaustive search (%s vs %s + %s/5, %d merges)" % (S.un(p0), S.un(d), S.un(a), k),
         ex[-1]["basic"][0] == [min(reach), max(reach)], (ex[-1]["basic"][0], min(reach), max(reach)))

APT = {"savvy": [60, 80], "growth": ["12", "16"]}
fp = P.fictitious_pet(APT, ["0.00", "0.30", "0.40"], 4, 120, "mid")
step("fictitious pet: Basic total 70.00 (middle of 60-80) split 11.67 x4 + 11.66 x2", fp["basic_total"] == "70.00"
     and fp["basic"] == ["11.67", "11.67", "11.67", "11.67", "11.66", "11.66"], fp)
step("fictitious pet: growth total 14.00, Added at 120 = 120 x growth", fp["growth_total"] == "14.00" and fp["added"][0] == "280.80", fp)
step("fictitious pet: min / max sit on the bracket edges and hatch ranks", P.fictitious_pet(APT, ["0.00", "0.30", "0.40"], 4, 30, "min")["basic_total"] == "60.00"
     and P.fictitious_pet(APT, ["0.00", "0.30", "0.40"], 4, 30, "max")["rank"] == "0.40"
     and P.fictitious_pet(APT, ["0.00", "0.30", "0.40"], 4, 30, "min")["rank"] == "0.00")
fr = P.fictitious_pet(APT, ["0.30"], 4, 120, "mid", rebirths=5, rebirth_spirits=5, rebirth_pos=1000)
step("fictitious pet with 5 max rebirths: acceleration 1.00 per stat", fr["accel"] == ["1.00"] * 6, fr["accel"])

pri = {"basic": ["11.67"] * 6, "rank": "0.30", "level": 120, "growth": ["2.33"] * 6, "accel": ["0"] * 6}
par = dict(P.fictitious_pet(APT, ["0.40"], 4, 120, "mid"))       # a same-nature partner: q saturates, then falls
mc = P.merge_curve(C, pri, par, 30, 0, 120)
rows = mc["rows"]
step("merge curve: 31 rows (0..30), every path inside the exact range",
     len(rows) == 31 and all(x["range"]["basic_total"][0] <= x[p]["basic_total"] <= x["range"]["basic_total"][1] and
                             x["range"]["rank"][0] <= x[p]["rank"] <= x["range"]["rank"][1] for x in rows for p in ("low", "avg", "high")))
gains = [x["gain"] for x in rows[1:]]
step("merge curve: average-path gains only fall (diminishing returns come from the formula)",
     all(a >= b for a, b in zip(gains, gains[1:])) and gains[0] > gains[-1], gains)
step("merge curve: share of the first merge's gain starts at 100%", rows[1]["gain_share"] == 100.0, rows[1])
step("merge curve: visible = Basic + level x (growth + acceleration)", rows[0]["visible"][0] == H("11.67") + H("279.60"), rows[0]["visible"])

rc = P.rebirth_curve(C, {"completed": 0, "growth": ["2.33"] * 6, "accel": ["0"] * 6}, 5, 0, 120, 500)
step("rebirth curve: the same points every rebirth (0.11 x 120 = 13.20 per stat)", [x["gain"] for x in rc["rows"][1:]] == [7920] * 5, [x["gain"] for x in rc["rows"]])
sh = [x["gain_share"] for x in rc["rows"][1:]]
step("rebirth curve: the gain as a share of what the pet had falls after the first", all(a > b for a, b in zip(sh, sh[1:])), sh)
step("rebirth curve: gates 30, 80, 100, 110, 120", [x["level_needed"] for x in rc["rows"][1:]] == [30, 80, 100, 110, 120])

pl = P.plan(C, pri, par, 10, 0, 3, 0, 120)
f = P.fmt_plan(pl)
step("plan: end = merged Basic + Added after the rebirths, formatted", f["end"]["visible"][0] == S.un(pl["merge"]["rows"][-1]["basic"][0] + pl["rebirth"]["rows"][-1]["added"][0])
     and f["end"]["level"] == 120 and len(f["merge"]["rows"]) == 11 and len(f["rebirth"]["rows"]) == 4, f["end"])

print("all passed" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
