"""modules/_pet_plan.py - B21 item 23: the pet planner on top of _pet_sim (the Pet Manager's exact maths).

Pure functions, no database. Everything is integer hundredths like _pet_sim.

* fictitious_pet(): a made-up pet built from a species + aptitude (nature) + level the way the server hatches one: Basic
  total inside the aptitude's savvy bracket, growth total inside its growth bracket, both split evenly over the six
  stats (reborn.pet_new_values / _growth_vector do the same), rank = one of the aptitude's published hatch outcomes, and
  optional rebirths already done (growth acceleration = rebirths x one roll).
* merge_curve(): the pet's Basic, rank and visible savvy after 0, 1, 2 ... N merges with the SAME partner, along the
  lowest-roll, average-roll and highest-roll paths. Nothing is scaled by hand: the gains fall because the server's own
  formulas compare the pet with the partner (q = partner Basic + partner Added / 5 - pet Basic; rank: partner rank -
  pet rank), so every merge closes the gap the next one feeds on (PetMergeSavvyPolicy / PetMergeRankPolicy).
* rebirth_curve(): growth acceleration and Added at a level after 0..R rebirths. The server adds a flat 0.01-0.20 per
  stat per rebirth whatever the rebirth number (PetRebirthSpiritPolicy.Roll ignores rebirthNumber), so the gain in
  points stays the same; what shrinks is the gain as a share of what the pet already has, and the level gates rise
  (30, 80, 100, 110, 120) with the attempt item (Spring Water, Juice, Ambrosia).
"""
from decimal import Decimal as D

from . import _pet_sim as PS

QUALITY = {"min": 0, "mid": 500, "max": 1000}


def _split(total_h):
    """Integer hundredths -> six positive parts, the first ones 0.01 larger (reborn._growth_vector)."""
    base, rem = divmod(int(total_h), 6)
    return [base + (1 if i < rem else 0) for i in range(6)]


def _at(lo, hi, quality):
    q = quality if isinstance(quality, int) else QUALITY.get(quality)
    if q is None:
        raise PS.SimError("quality must be min, mid, max or 0..1000")
    return PS.pick(lo, hi, q)


def fictitious_pet(apt, hatch_ranks, species, level, quality="mid", rebirths=0, rebirth_spirits=0, rebirth_pos=500):
    """apt: {savvy: [lo, hi] whole points, growth: [lo, hi]}; hatch_ranks: the aptitude's hatch outcomes.
    Returns the simulator's pet shape (strings with 2 places, plus the hundredths)."""
    if not apt or not apt.get("savvy") or not apt.get("growth"):
        raise PS.SimError("unknown aptitude")
    level = int(level)
    if level < 1:
        raise PS.SimError("level must be 1 or more")
    rebirths = int(rebirths)
    if rebirths < 0 or rebirths > PS.MAX_REBIRTHS:
        raise PS.SimError("rebirths must be 0..%d" % PS.MAX_REBIRTHS)
    slo, shi = PS.h(apt["savvy"][0]), PS.h(apt["savvy"][1])
    glo, ghi = PS.h(apt["growth"][0]), PS.h(apt["growth"][1])
    basic = _split(_at(slo, shi, quality))
    growth = _split(_at(glo, ghi, quality))
    ranks = sorted(PS.h(r) for r in (hatch_ranks or [])) or [0]
    q = quality if isinstance(quality, int) else QUALITY[quality]
    rank = ranks[min(len(ranks) - 1, (q * len(ranks)) // 1001)]
    lo, hi = PS.rebirth_range(rebirth_spirits)
    accel = [rebirths * PS.pick(lo, hi, rebirth_pos)] * 6
    u = PS.un
    return {"species": int(species), "level": level, "rank": u(rank), "basic": [u(x) for x in basic],
            "growth": [u(x) for x in growth], "accel": [u(x) for x in accel], "completed": rebirths,
            "added": [u(x) for x in PS.added_of(level, [D(g) / 100 for g in growth], [D(a) / 100 for a in accel])],
            "basic_total": u(sum(basic)), "growth_total": u(sum(growth))}


def _merge_intervals(iv):
    out = []
    for a, b in sorted(iv):
        if out and a <= out[-1][1] + 1:
            out[-1][1] = max(out[-1][1], b)
        else:
            out.append([a, b])
    return out


def _segments(lookup, k):
    """The lookup as pieces of the pet's own value p, where the compared value is x = k - p:
    [(p_lo, p_hi, base)] with base None below the first row (p_hi is inclusive, None = no upper limit)."""
    segs = []
    thr = [t for t, _ in lookup]
    # x >= thr[i] and x < thr[i+1]  <=>  k - thr[i+1] < p <= k - thr[i]
    for i, (t, base) in enumerate(lookup):
        hi = k - t
        lo = k - thr[i + 1] + 1 if i + 1 < len(thr) else None
        segs.append((lo, hi, base))
    segs.append((k - thr[0] + 1, None, None))        # x below the first row
    return segs


def _step_intervals(iv, segs, gain):
    """Every reachable value after one more merge: each piece of each interval gets that row's [min, max] gain range.
    Within one row the gain range is fixed, so [a, b] -> [a + lo(a), b + hi(b)] is contiguous (gain(p) is a function of
    the row and, for rank, the cap)."""
    out = []
    for a, b in iv:
        for lo, hi, base in segs:
            x0 = a if lo is None else max(a, lo)
            x1 = b if hi is None else min(b, hi)
            if x0 > x1:
                continue
            g0, g1 = gain(base, x0), gain(base, x1)
            out.append((x0 + g0[0], x1 + g1[1]))
    return _merge_intervals(out)


def exact_ranges(c, primary, partner, n, spirits):
    """The true lowest and highest Basic per stat and rank reachable after 0..n merges with this partner (every
    combination of server rolls). The lowest-roll path is not always the lowest result (a small gain keeps the next
    merge's gap wider), so this walks every reachable value as intervals. Returns [{basic: [[lo, hi] x6], rank: [lo, hi]}]."""
    pb = PS.six(primary.get("basic"), "primary Basic")
    pr = PS.h(primary.get("rank", 0))
    db = PS.six(partner.get("basic"), "partner Basic")
    if partner.get("added") is not None:
        da = PS.six(partner["added"], "partner Added")
    else:
        da = PS.added_of(int(partner.get("level") or 0), PS.dec6(partner.get("growth") or [0] * 6, "partner growth"),
                         PS.dec6(partner.get("accel") or [0] * 6, "partner acceleration"))
    dr = PS.h(partner.get("rank", 0))
    species = int(partner.get("species") or 0)
    f = PS.species_factor(c, species)
    lo_pct, hi_pct = PS.spirit_step(c, spirits)
    sp = int(spirits)

    def savvy_gain(base, _p):
        mx = base * f // 100 if base is not None else 0
        mn = 0 if mx == 0 else 1 if sp == 0 else (mx * lo_pct + 50) // 100
        return mn, mx

    cap = c["max_rank_h"]

    def rank_gain(base, p):
        fa = base * f // 100 if base is not None else 0
        if sp == 0:
            umn = umx = fa
        else:
            umn = (fa * lo_pct + 99) // 100 if fa > 0 else 0
            umx = fa * hi_pct // 100 if fa > 0 else 0
        mx = min(umx, max(0, cap - p))
        return min(umn, mx), mx

    stat_iv = [[[p, p]] for p in pb]
    stat_segs = [_segments(c["savvy"], a // PS.ADDED_DIVISOR + d) for d, a in zip(db, da)]
    rank_iv = [[pr, pr]]
    rank_segs = _segments(c["rank"], dr)
    out = []
    for k in range(int(n) + 1):
        if k:
            stat_iv = [_step_intervals(iv, sg, savvy_gain) for iv, sg in zip(stat_iv, stat_segs)]
            rank_iv = _step_intervals(rank_iv, rank_segs, rank_gain)
        out.append({"basic": [[iv[0][0], iv[-1][1]] for iv in stat_iv], "rank": [rank_iv[0][0], rank_iv[-1][1]]})
    return out


def merge_curve(c, primary, partner, n, spirits, level=None):
    """Per merge count 0..n, along the lowest / average / highest roll paths: Basic total, rank, the average path's
    per-stat Basic, that merge's gain and its share of the first merge's gain, and the visible savvy (Basic + Added
    at `level`; Added does not change with merges). primary also needs growth / accel for the visible column."""
    n = int(n)
    paths = {}
    for name, pos in (("low", 0), ("avg", 500), ("high", 1000)):
        paths[name] = PS.simulate_merges(c, primary, partner, n, spirits, pos, pos)
    lv = int(level or primary.get("level") or 1)
    pg = PS.dec6(primary.get("growth") or [0] * 6, "growth")
    pa = PS.dec6(primary.get("accel") or [0] * 6, "acceleration")
    added = PS.added_of(lv, pg, pa)
    rows = []
    start_b = paths["avg"]["start_basic"]
    start_r = paths["avg"]["start_rank"]
    first_gain = None
    exact = exact_ranges(c, primary, partner, n, spirits)
    for k in range(n + 1):
        ex = exact[k]
        row = {"n": k, "range": {"basic": ex["basic"], "rank": ex["rank"],
                                 "basic_total": [sum(x[0] for x in ex["basic"]), sum(x[1] for x in ex["basic"])]}}
        for name, r in paths.items():
            st = r["steps"][k - 1] if k else None
            b = st["basic"] if st else start_b
            row[name] = {"basic_total": sum(b), "rank": st["rank"] if st else start_r}
        st = paths["avg"]["steps"][k - 1] if k else None
        row["basic"] = st["basic"] if st else list(start_b)
        gain = sum(st["gains"]) if st else 0
        rgain = st["rank_gain"] if st else 0
        if k == 1:
            first_gain = (gain, rgain)
        row["gain"], row["rank_gain"] = gain, rgain
        row["gain_share"] = (None if not k or not first_gain or not first_gain[0] else round(100.0 * gain / first_gain[0], 1))
        row["visible"] = [b + a for b, a in zip(row["basic"], added)]
        rows.append(row)
    stall = paths["high"]["stalled_at"]
    return {"rows": rows, "added": added, "level": lv, "partner_added": paths["avg"]["partner_added"],
            "stalled_at": {k: v["stalled_at"] for k, v in paths.items()}, "notes": paths["avg"]["notes"],
            "ceiling": PS.max_merge_gain(c), "stall_high": stall}


def rebirth_curve(c, pet, r, spirits, level=None, pos=500):
    """Per rebirth count 0..r: acceleration per stat (one roll position for all stats), Added at `level`, the gain over
    the previous count and its share of what the pet had before, the level gate and the attempt item."""
    r = int(r)
    done = int(pet.get("completed") or 0)
    if r < 0 or done + r > c["max_rebirths"]:
        raise PS.SimError("rebirths must be 0..%d in all (this pet has %d)" % (c["max_rebirths"], done))
    lv = max(1, min(c["max_level"], int(level or c["max_level"])))
    growth = PS.dec6(pet.get("growth") or [0] * 6, "growth")
    acc0 = [PS.h(x) for x in PS.dec6(pet.get("accel") or [0] * 6, "acceleration")]
    lo, hi = PS.rebirth_range(spirits)
    inc = PS.pick(lo, hi, pos)
    rows, prev = [], None
    for k in range(r + 1):
        acc = [a + k * inc for a in acc0]
        added = PS.added_of(lv, growth, [D(a) / 100 for a in acc])
        tot = sum(added)
        n = done + k
        row = {"n": n, "accel": acc, "added": added, "added_total": tot,
               "gain": None if prev is None else tot - prev,
               "gain_share": None if not prev else round(100.0 * (tot - prev) / prev, 1),
               "level_needed": PS.rebirth_gate(c, n) if k else None, "water": PS.water_for(n) if k else None,
               "model_change": n in PS.MODEL_CHANGES if k else False}
        rows.append(row)
        prev = tot
    return {"rows": rows, "range": [lo, hi], "per_rebirth": inc, "level": lv}


def plan(c, primary, partner, merges, merge_spirits, rebirths, rebirth_spirits, level=None, rebirth_pos=500):
    """Both together. Merges change only Basic and rank, rebirths only growth acceleration (and reset the level), so
    the order does not change the end result; the end state is Basic (average merge path) + Added at `level`."""
    m = merge_curve(c, primary, partner, merges, merge_spirits, level)
    rb = rebirth_curve(c, primary, rebirths, rebirth_spirits, level or m["level"], rebirth_pos)
    end_basic = m["rows"][-1]["basic"]
    end_added = rb["rows"][-1]["added"]
    return {"merge": m, "rebirth": rb, "end": {"basic": end_basic, "added": end_added, "level": rb["level"],
                                               "visible": [b + a for b, a in zip(end_basic, end_added)],
                                               "rank": m["rows"][-1]["avg"]["rank"],
                                               "rank_range": [m["rows"][-1]["low"]["rank"], m["rows"][-1]["high"]["rank"]]}}


def fmt_plan(p):
    """Integer hundredths -> 2-place strings for the page."""
    u = PS.un

    def mrow(x):
        return {"n": x["n"], "range": {"basic": [[u(a), u(b)] for a, b in x["range"]["basic"]], "rank": [u(v) for v in x["range"]["rank"]],
                                       "basic_total": [u(v) for v in x["range"]["basic_total"]]},
                "low": {k: u(v) for k, v in x["low"].items()}, "avg": {k: u(v) for k, v in x["avg"].items()},
                "high": {k: u(v) for k, v in x["high"].items()}, "basic": [u(v) for v in x["basic"]], "gain": u(x["gain"]),
                "rank_gain": u(x["rank_gain"]), "gain_share": x["gain_share"], "visible": [u(v) for v in x["visible"]],
                "visible_total": u(sum(x["visible"]))}

    def rrow(x):
        return {"n": x["n"], "accel": [u(v) for v in x["accel"]], "added": [u(v) for v in x["added"]], "added_total": u(x["added_total"]),
                "gain": None if x["gain"] is None else u(x["gain"]), "gain_share": x["gain_share"], "level_needed": x["level_needed"],
                "water": x["water"], "model_change": x["model_change"]}
    m, r, e = p["merge"], p["rebirth"], p["end"]
    return {"merge": {"rows": [mrow(x) for x in m["rows"]], "added": [u(v) for v in m["added"]], "level": m["level"],
                      "partner_added": [u(v) for v in m["partner_added"]], "stalled_at": m["stalled_at"], "notes": m["notes"],
                      "ceiling": {k: u(v) for k, v in m["ceiling"].items()}},
            "rebirth": {"rows": [rrow(x) for x in r["rows"]], "range": [u(v) for v in r["range"]], "per_rebirth": u(r["per_rebirth"]),
                        "level": r["level"]},
            "end": {"basic": [u(v) for v in e["basic"]], "added": [u(v) for v in e["added"]], "visible": [u(v) for v in e["visible"]],
                    "visible_total": u(sum(e["visible"])), "level": e["level"], "rank": u(e["rank"]),
                    "rank_range": [u(v) for v in e["rank_range"]]}}
