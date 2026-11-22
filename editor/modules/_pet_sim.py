"""modules/_pet_sim.py - the Pet Manager NPC's maths, for the pets page's simulator and "legal pet" checks.

Pure functions, no database: the content tables come in as a dict (pets.py reads them from the published pet content
of the reborn server, or from the bundled client snapshot catalog-origin/pet-alter.json). Every number is an integer
in HUNDREDTHS, the native client's and the server's fixed-point unit (rank 12.34 = 1234, savvy 7.80 = 780).

Sources (server tree godswar-dev/server, client Localization/en_us/Settings/Sys/Pet_Alter.xml):

  Pet-to-pet Merge (Pet Manager "Merge" / client "Inosculate", opcode 10268):
    src/Godswar.Server/State/PetMergeSavvyPolicy.cs  TryResolveBounds, per stat, primary Basic P, deputy Basic D,
      deputy Added A (level x (growth + acceleration)):
        q    = floor(A / 5) - P + D                       (all hundredths; Pet_Alter Inosculate Modulus="5")
        base = Values of the greatest Restrict <= q       (Pet_Alter <Restrict> Restrict/Values, 200 rows)
        max  = trunc(base x species factor)               (deputy species, Pet_Alter <typePoint>: 0.8 / 1.4 / 2.6)
        min  = 0 when max = 0; 1 (0.01) with no spirit; else round-half-up(max x 10/20/30/40/50 %) for 1-5 spirits
      Only Basic changes (Added, growth and acceleration do not).
    src/Godswar.Server/State/PetMergeRankPolicy.cs  TryResolveBounds:
        d    = deputy rank - primary rank
        base = Values of the greatest Qualityadd <= d     (same 200 rows, Qualityadd column)
        fa   = trunc(base x species factor)
        no spirit: exactly fa (no range); 1-5 spirits: ceil(fa x 10..50 %) .. floor(fa x 100 %)
        capped at the maximum rank 655.35 (UInt16 hundredths)
    Both pets level >= 30 (Pet_Alter Leading_PetLv / Minor_PetLv), the deputy is consumed. Rolls are uniform over the
    inclusive hundredth interval (server policy; the stock server's distribution is unknown).
  Rebirth (Pet Manager "Rebirth" / client "Resurge", opcode 10272):
    src/Godswar.Server/State/PetRebirthSpiritPolicy.cs  GetIncreaseRange: every stat's growth_acceleration
      += a roll of 0.01 / 0.02 / 0.04 / 0.06 / 0.08 / 0.10 .. 0.20 for 0-5 Rebirth Spirits; level -> 1, rank and
      Basic unchanged (PostgresPetDurableCommandExecutor.Rebirth.cs: RankAfter = ownedPet.Rank); Added becomes
      level x (growth + acceleration) (PetSavvyRuntimeSemantics.ResolveLevelScaledAdded).
    Gates: pet_content_rebirth_steps (docs/pet-rebirth-balance.md: 30, 80, 100, 110, then 120), a Soul Contract
    (PetManagerPlanner.TryPlanRebirth SoulContractRequired) and one rebirth attempt (waters, PetRebirthGrowthPolicy).
  Phoenix growth reset: acceleration is redrawn at 0.10 x n .. 0.20 x n for n completed rebirths
    (docs/pet-rebirth-balance.md "Growth-acceleration roll").
"""
import json
import os
import re
from decimal import Decimal as D, ROUND_HALF_UP

STATS = ("Agility", "Strength", "Accuracy", "Technique", "Wisdom", "Luck")
MAX_RANK_H = 65535                 # 655.35, PetRankWirePolicy / pet_content_settings.maximum_rank
MERGE_MIN_LEVEL = 30               # Pet_Alter.xml Leading_PetLv="30" Minor_PetLv="30"
ADDED_DIVISOR = 5                  # PetMergeSavvyPolicy.AddedDivisor (Pet_Alter Modulus="5")
REBIRTH_MIN_H = {0: 1, 1: 2, 2: 4, 3: 6, 4: 8, 5: 10}     # PetRebirthSpiritPolicy.GetIncreaseRange
REBIRTH_MAX_H = 20
MAX_REBIRTHS = 100                 # PetRebirthGrowthPolicy.MaximumRebirthCount
REBIRTH_WATERS = ((1, 30, 10107, "Spring Water"), (31, 60, 10145, "Juice of Rebirth"), (61, 100, 11095, "Ambrosia of Rebirth"))
MODEL_CHANGES = (8, 20)            # Pet.xml <PetModel Samsara="8"> / "20"
MERGE_SPIRIT, MERGE_SPIRIT_BOUND = 10103, 10097            # Merged Spirit / Fused Harpyia (bound primary only)
REBIRTH_SPIRIT, REBIRTH_SPIRIT_BOUND = 10104, 10098        # Rebirth Spirit / Reborn Harpyia (bound pet only)
DEFAULT_SPIRITS = {0: (0, 100), 1: (10, 100), 2: (20, 100), 3: (30, 100), 4: (40, 100), 5: (50, 100)}
SNAPSHOT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "catalog-origin", "pet-alter.json")


class SimError(ValueError):
    """Bad simulator input (HTTP 400)."""


# ------------------------------------------------------------------------------------------------ units
def h(v):
    """A number (int, float, str, Decimal) -> integer hundredths, half away from zero (TryToWireHundredths)."""
    if v is None or v == "":
        return 0
    try:
        d = D(str(v).replace(",", ""))
    except Exception:          # noqa: BLE001
        raise SimError("not a number: %r" % (v,))
    if not d.is_finite():
        raise SimError("not a finite number: %r" % (v,))
    return int((d * 100).quantize(D(1), rounding=ROUND_HALF_UP))


def un(x):
    """Integer hundredths -> a 2-place string ("7.80")."""
    neg = x < 0
    x = abs(int(x))
    return ("-" if neg else "") + "%d.%02d" % (x // 100, x % 100)


def factor_h(f):
    """Species factor (0.8 / "1.4" / 2.6) -> integer hundredths (80 / 140 / 260): exact, no binary float."""
    return h(f)


def pick(lo, hi, pos):
    """The value at slider position pos (0..1000 per mille) of the inclusive interval lo..hi (hundredths)."""
    pos = max(0, min(1000, int(pos)))
    if hi <= lo:
        return lo
    return lo + (pos * (hi - lo) + 500) // 1000


def positions(pos, n=6):
    """A slider spec -> n per-mille positions: a number (all the same), "min" / "avg" / "max", or a list."""
    named = {"min": 0, "avg": 500, "max": 1000}
    if isinstance(pos, str) and pos in named:
        return [named[pos]] * n
    if isinstance(pos, (int, float)):
        return [int(pos)] * n
    if isinstance(pos, (list, tuple)) and len(pos) == n:
        out = []
        for p in pos:
            p = named.get(p, p) if isinstance(p, str) else p
            try:
                out.append(max(0, min(1000, int(p))))
            except (TypeError, ValueError):
                raise SimError("slider position must be 0..1000 (got %r)" % (p,))
        return out
    raise SimError("slider positions must be min / avg / max, a number 0..1000 or a list of %d" % n)


def six(v, what):
    if not isinstance(v, (list, tuple)) or len(v) != 6:
        raise SimError("%s must be 6 numbers (Agility, Strength, Accuracy, Technique, Wisdom, Luck)" % what)
    out = [h(x) for x in v]
    if any(x < 0 for x in out):
        raise SimError("%s cannot be negative" % what)
    return out


# ------------------------------------------------------------------------------------------------ content
def content_from_pet_alter(xml_text):
    """Pet_Alter.xml (the client file) -> the content dict the simulator uses."""
    savvy, rank = [], []
    for m in re.finditer(r'<Val\d+\s+Qualityadd="(-?\d+)"\s+Restrict="(-?\d+)"\s+Values="(\d+)"', xml_text):
        rank.append([int(m.group(1)), int(m.group(3))])
        savvy.append([int(m.group(2)), int(m.group(3))])
    factors = {}
    for m in re.finditer(r'<Type\d+\s+PetType="(\d+)"\s+Values="([\d.]+)"', xml_text):
        factors[int(m.group(1))] = factor_h(m.group(2))
    ino = re.search(r'<Inosculate ItemID="\d+"[^>]*Min_Alter="([\d,]+)"\s+Max_Alter="([\d,]+)"', xml_text)
    spirits = dict(DEFAULT_SPIRITS)
    if ino:
        lo = [int(x) for x in ino.group(1).split(",")]
        hi = [int(x) for x in ino.group(2).split(",")]
        spirits = {0: (0, 100)}
        spirits.update({i + 1: (lo[i], hi[i]) for i in range(min(len(lo), len(hi)))})
    if len(savvy) < 2:
        raise SimError("Pet_Alter.xml: no <Restrict> rows found")
    return normalise({"savvy": savvy, "rank": rank, "factors": factors, "spirits": spirits})


def normalise(c):
    """Sort the lookups, make factor / spirit keys ints and fill defaults; returns a new dict."""
    out = dict(c)
    out["savvy"] = sorted([int(a), int(b)] for a, b in (c.get("savvy") or []))
    out["rank"] = sorted([int(a), int(b)] for a, b in (c.get("rank") or []))
    out["factors"] = {int(k): (v if isinstance(v, int) else factor_h(v)) for k, v in (c.get("factors") or {}).items()}
    sp = c.get("spirits") or DEFAULT_SPIRITS
    out["spirits"] = {int(k): (int(v[0]), int(v[1])) for k, v in sp.items()}
    out.setdefault("max_rank_h", MAX_RANK_H)
    out["rebirth_levels"] = {int(k): int(v) for k, v in (c.get("rebirth_levels") or {}).items()}
    out.setdefault("max_level", 120)
    out.setdefault("max_rebirths", MAX_REBIRTHS)
    out["books"] = {int(k): [int(x) for x in v] for k, v in (c.get("books") or {}).items()}
    return out


def load_snapshot(path=SNAPSHOT):
    with open(path, encoding="utf-8") as f:
        return normalise(json.load(f))


def resolve(lookup, x):
    """Values of the greatest threshold <= x (PinnedPetContentCatalog.TryResolveMerge*Lookup), or None below row 1."""
    best = None
    for thr, base in lookup:
        if x >= thr:
            best = base
        else:
            break
    return best


def species_factor(c, species):
    f = c["factors"].get(int(species))
    if f is None:
        raise SimError("species %s has no merge factor in the pet content (Pet_Alter typePoint)" % species)
    return f


def spirit_step(c, n):
    n = int(n)
    if n not in c["spirits"]:
        raise SimError("spirits must be 0..5 (got %s)" % n)
    return c["spirits"][n]


def need_threshold(c, factor):
    """The smallest q (hundredths) that gives a gain of at least 0.01 with this factor (-3990 for 0.8, -4000 else)."""
    for thr, base in c["savvy"]:
        if base * factor // 100 >= 1:
            return thr
    return None


# ------------------------------------------------------------------------------------------------ merge
def dec6(v, what):
    """6 exact decimals (growth rates are numeric(18, 6) in the database; hundredths would lose precision)."""
    if not isinstance(v, (list, tuple)) or len(v) != 6:
        raise SimError("%s must be 6 numbers (Agility, Strength, Accuracy, Technique, Wisdom, Luck)" % what)
    out = []
    for x in v:
        try:
            d = D(str(x if x not in (None, "") else 0).replace(",", ""))
        except Exception:      # noqa: BLE001
            raise SimError("%s: not a number: %r" % (what, x))
        if not d.is_finite() or d < 0:
            raise SimError("%s must be 0 or more" % what)
        out.append(d)
    return out


def added_of(level, growth, accel):
    """Added hundredths per stat = level x (growth + acceleration) (decimals), rounded half up like the wire value."""
    return [int((D(int(level)) * (D(g) + D(a)) * 100).quantize(D(1), rounding=ROUND_HALF_UP)) for g, a in zip(growth, accel)]


def savvy_bounds(c, primary_basic, deputy_basic, deputy_added, species, spirits):
    """Per stat: {q, base, min, max, need} for ONE merge (PetMergeSavvyPolicy.TryResolveBounds)."""
    f = species_factor(c, species)
    lo_pct, _ = spirit_step(c, spirits)
    thr = need_threshold(c, f)
    out = []
    for p, d, a in zip(primary_basic, deputy_basic, deputy_added):
        q = a // ADDED_DIVISOR - p + d
        base = resolve(c["savvy"], q)
        mx = base * f // 100 if base is not None else 0
        mn = 0 if mx == 0 else 1 if int(spirits) == 0 else (mx * lo_pct + 50) // 100
        out.append({"q": q, "base": base, "min": mn, "max": mx, "need": max(0, thr - q) if thr is not None and mx == 0 else 0})
    return out


def rank_bounds(c, primary_rank, deputy_rank, species, spirits):
    """{diff, base, min, max, fixed, capped} for ONE merge (PetMergeRankPolicy.TryResolveBounds)."""
    f = species_factor(c, species)
    lo_pct, hi_pct = spirit_step(c, spirits)
    diff = deputy_rank - primary_rank
    base = resolve(c["rank"], diff)
    fa = base * f // 100 if base is not None else 0
    if int(spirits) == 0:
        umn = umx = fa
    else:
        umn = (fa * lo_pct + 99) // 100 if fa > 0 else 0
        umx = fa * hi_pct // 100 if fa > 0 else 0
    remaining = max(0, c["max_rank_h"] - primary_rank)
    mx = min(umx, remaining)
    mn = min(umn, mx)
    return {"diff": diff, "base": base, "factor_adjusted": fa, "min": mn, "max": mx, "fixed": int(spirits) == 0, "capped": mx < umx}


def simulate_merges(c, primary, partner, n, spirits, savvy_pos="avg", rank_pos="avg"):
    """N merges of the same fake partner into the primary pet, one at a time (each gain lowers the next q).
    primary: {basic: [6], rank, level}; partner: {species, basic: [6], rank, level, growth: [6], accel: [6]} or
    {..., added: [6]}. Positions: per-mille slider spec (see positions). Returns the steps and the final state."""
    n = int(n)
    if n < 0 or n > 1000:
        raise SimError("merges must be 0..1000")
    pb = six(primary.get("basic"), "primary Basic")
    pr = h(primary.get("rank", 0))
    db = six(partner.get("basic"), "partner Basic")
    if partner.get("added") is not None:
        da = six(partner["added"], "partner Added")
    else:
        lv = int(partner.get("level") or 0)
        da = added_of(lv, dec6(partner.get("growth") or [0] * 6, "partner growth"), dec6(partner.get("accel") or [0] * 6, "partner acceleration"))
    dr = h(partner.get("rank", 0))
    species = int(partner.get("species") or 0)
    sp = positions(savvy_pos)
    rp = positions(rank_pos, 1)[0]
    notes = []
    if int(primary.get("level") or MERGE_MIN_LEVEL) < MERGE_MIN_LEVEL:
        notes.append("the pet must be level %d or more to merge (Pet_Alter Leading_PetLv)" % MERGE_MIN_LEVEL)
    if partner.get("added") is None and int(partner.get("level") or 0) < MERGE_MIN_LEVEL:
        notes.append("the partner must be level %d or more (Pet_Alter Minor_PetLv)" % MERGE_MIN_LEVEL)
    steps = []
    stalled_at = None
    for i in range(n):
        sb = savvy_bounds(c, pb, db, da, species, spirits)
        rb = rank_bounds(c, pr, dr, species, spirits)
        gains = [pick(b["min"], b["max"], p) for b, p in zip(sb, sp)]
        rg = pick(rb["min"], rb["max"], rp)
        pb = [x + g for x, g in zip(pb, gains)]
        pr = pr + rg
        steps.append({"n": i + 1, "bounds": [[b["min"], b["max"]] for b in sb], "q": [b["q"] for b in sb],
                      "need": [b["need"] for b in sb], "gains": gains, "rank_bounds": [rb["min"], rb["max"]], "rank_gain": rg,
                      "basic": list(pb), "rank": pr})
        if stalled_at is None and not any(gains) and rg == 0:
            stalled_at = i + 1
    if stalled_at:
        notes.append("from merge %d on this partner adds nothing: its Basic + Added/5 is too low next to the pet's Basic "
                      "(q below the first Restrict row) and its rank is too low" % stalled_at)
    return {"steps": steps, "basic": pb, "rank": pr, "partner_added": da, "start_basic": six(primary.get("basic"), "primary Basic"),
            "start_rank": h(primary.get("rank", 0)), "notes": notes, "stalled_at": stalled_at,
            "per_merge_ceiling": max_merge_gain(c)}


def max_merge_gain(c):
    """The most one merge can add to one stat (and to rank): top Values x top factor (300 x 2.6 = 7.80)."""
    top = max(c["factors"].values()) if c["factors"] else 260
    return {"savvy": max(b for _, b in c["savvy"]) * top // 100, "rank": max(b for _, b in c["rank"]) * top // 100}


# ------------------------------------------------------------------------------------------------ rebirth
def rebirth_range(spirits):
    s = int(spirits)
    if s not in REBIRTH_MIN_H:
        raise SimError("rebirth spirits must be 0..5")
    return REBIRTH_MIN_H[s], REBIRTH_MAX_H


def phoenix_range(completed):
    return 10 * int(completed), 20 * int(completed)


def water_for(n):
    for lo, hi, item, name in REBIRTH_WATERS:
        if lo <= n <= hi:
            return {"item": item, "name": name}
    return None


def rebirth_gate(c, n):
    """Pet level needed for rebirth number n (1-based): the published steps, else the last one (120)."""
    lv = c.get("rebirth_levels") or {}
    if n in lv:
        return lv[n]
    return lv[max(lv)] if lv else 120


def simulate_rebirths(c, pet, n, spirits, pos="avg", level_after=None):
    """N rebirths: {completed, growth: [6], accel: [6], soul_contract_stage, rebirths_remaining, level}.
    Returns each step and the final acceleration, plus Added at level 1 and at level_after."""
    n = int(n)
    done = int(pet.get("completed") or 0)
    if n < 0:
        raise SimError("rebirths must be 0 or more")
    if done + n > c["max_rebirths"]:
        raise SimError("at most %d rebirths in all (this pet has %d)" % (c["max_rebirths"], done))
    growth = dec6(pet.get("growth") or [0] * 6, "growth")
    acc = [h(x) for x in dec6(pet.get("accel") or [0] * 6, "acceleration")]
    lo, hi = rebirth_range(spirits)
    ps = positions(pos)
    steps = []
    for i in range(n):
        k = done + i + 1
        inc = [pick(lo, hi, p) for p in ps]
        acc = [a + x for a, x in zip(acc, inc)]
        steps.append({"n": k, "level_needed": rebirth_gate(c, k), "water": water_for(k), "increase": inc, "accel": list(acc),
                      "model_change": k in MODEL_CHANGES})
    lvl = int(level_after or 1)
    lvl = max(1, min(c["max_level"], lvl))
    notes = []
    if n and int(pet.get("soul_contract_stage") or 0) < 1:
        notes.append("rebirth needs a Soul Contract (Contract Spirit at the Pet Manager): stage 0 is refused (PetCodeReturn114)")
    rem = pet.get("rebirths_remaining")
    if n and rem is not None and int(rem) < n:
        notes.append("each rebirth uses one rebirth attempt (Spring Water / Juice / Ambrosia); the pet has %s left, so %d more "
                     "waters are needed" % (rem, n - int(rem)))
    return {"steps": steps, "accel": acc, "range": [lo, hi], "completed": done + n,
            "added_level1": added_of(1, growth, [D(x) / 100 for x in acc]), "level": lvl,
            "added_at_level": added_of(lvl, growth, [D(x) / 100 for x in acc]),
            "phoenix": phoenix_range(done + n), "notes": notes}


# ------------------------------------------------------------------------------------------------ legal pet
def legal_checks(c, pet, apt=None, hatch_ranks=None, species=None, skills=None, lib=None):
    """What a real game could not have produced, given the pet's counters. pet: the editor's pet row (level, rank,
    completed_pet_merges, completed_rebirths, rebirths_remaining, soul_contract_stage, maximum_energy, current_energy,
    satiety, amity, remaining_lifetime, opened/available_skill_slots, initial_savvy_baseline_total, stats[6] with
    initial_savvy / base_growth_rate / growth_acceleration). apt: the aptitude row {savvy: [lo, hi], growth: [lo, hi]};
    hatch_ranks: the aptitude's hatch outcomes (ranks); species: {max_lifetime, starter}; skills: the pet's skill rows
    (with type, tier, head); lib: {skills: {id: entry}} plus c["books"] (skill head id -> book item ids).
    Returns [{level: error | warn | ok, what, msg}]; error = the server refuses it, warn = not reachable in game."""
    out = []

    def add(level, what, msg):
        out.append({"level": level, "what": what, "msg": msg})

    ceil = max_merge_gain(c)
    merges = int(pet.get("completed_pet_merges") or 0)
    reb = int(pet.get("completed_rebirths") or 0)
    left = int(pet.get("rebirths_remaining") or 0)
    stats = sorted(pet.get("stats") or [], key=lambda s: int(s["stat_code"]))
    basic = [h(s["initial_savvy"]) for s in stats]
    growth = [h(s["base_growth_rate"]) for s in stats]
    accel = [h(s.get("growth_acceleration") or 0) for s in stats]
    level = int(pet.get("level") or 1)

    # level / rebirths
    if level > c["max_level"]:
        add("error", "level", "level %d is above the maximum %d" % (level, c["max_level"]))
    if reb > c["max_rebirths"]:
        add("warn", "rebirths", "%d rebirths: the game stops at %d" % (reb, c["max_rebirths"]))
    if reb + left > c["max_rebirths"]:
        add("warn", "rebirths", "rebirths done + left = %d: only %d rebirths exist, %d attempts can never be used"
            % (reb + left, c["max_rebirths"], reb + left - c["max_rebirths"]))
    if reb and int(pet.get("soul_contract_stage") or 0) < 1:
        add("warn", "contract", "%d rebirth(s) but no Soul Contract: rebirth needs a contract, and a contract never goes "
            "back to stage 0" % reb)

    # growth acceleration: rebirth rolls 0.01..0.20 each, Phoenix redraws 0.10n..0.20n
    lo, hi = REBIRTH_MIN_H[0] * reb, REBIRTH_MAX_H * reb
    for name, a in zip(STATS, accel):
        if a < lo or a > hi:
            add("warn", "acceleration", "%s growth acceleration %s is outside %s..%s (what %d rebirth(s) can roll: 0.01-0.20 "
                "each)" % (name, un(a), un(lo), un(hi), reb))

    # Basic savvy: hatch total + merges x 6 x 7.80 at most
    if basic:
        tot = sum(basic)
        baseline = h(pet.get("initial_savvy_baseline_total")) if pet.get("initial_savvy_baseline_total") not in (None, "") else None
        hatch_max = baseline if baseline is not None else (h(apt["savvy"][1]) if apt else None)
        if baseline is not None and tot < baseline:
            add("error", "basic", "Basic total %s is below the birth baseline %s: the server refuses to start" % (un(tot), un(baseline)))
        if hatch_max is not None:
            cap = hatch_max + merges * 6 * ceil["savvy"]
            if tot > cap:
                add("warn", "basic", "Basic total %s is above %s = hatch %s + %d merge(s) x 6 x %s (the most one merge adds per "
                    "stat). Raise 'Merge' to at least %d, or lower Basic" % (
                        un(tot), un(cap), un(hatch_max), merges, un(ceil["savvy"]),
                        -(-(tot - hatch_max) // (6 * ceil["savvy"]))))
            if merges == 0:
                for name, b in zip(STATS, basic):
                    if tot and b * 100 > tot * 92:
                        add("warn", "basic", "%s Basic %s is more than 92%% of the total %s: Fairy's Feather puts at most 92%% "
                            "in one stat, and without merges nothing else moves Basic" % (name, un(b), un(tot)))
        if merges == 0 and baseline is not None and tot != baseline:
            add("warn", "basic", "no merges, but the Basic total %s differs from the hatch total %s (only merges add Basic; "
                "Fairy's Feather keeps the total)" % (un(tot), un(baseline)))

    # growth total in the bracket (start-up validator)
    if apt and growth:
        g = sum(growth)
        glo, ghi = h(apt["growth"][0]), h(apt["growth"][1])
        if g < glo or g > ghi:
            add("error", "growth", "growth total %s is outside %s..%s: the server refuses to start" % (un(g), un(glo), un(ghi)))

    # rank: a hatch outcome + merges x 7.80, at most 655.35
    rank = h(pet.get("rank") or 0)
    if rank > c["max_rank_h"]:
        add("error", "rank", "rank %s is above %s" % (un(rank), un(c["max_rank_h"])))
    if hatch_ranks:
        hr = sorted(h(x) for x in hatch_ranks)
        if merges == 0 and rank not in hr:
            add("warn", "rank", "no merges, so the rank should be one of the hatch outcomes %s (only merges raise rank)"
                % ", ".join(un(x) for x in hr))
        elif rank > hr[-1] + merges * ceil["rank"]:
            add("warn", "rank", "rank %s is above %s = best hatch %s + %d merge(s) x %s. Raise 'Merge' to at least %d" % (
                un(rank), un(hr[-1] + merges * ceil["rank"]), un(hr[-1]), merges, un(ceil["rank"]),
                -(-(rank - hr[-1]) // ceil["rank"])))
        elif rank < hr[0]:
            add("warn", "rank", "rank %s is below the lowest hatch outcome %s (rank never goes down)" % (un(rank), un(hr[0])))

    # care, energy
    me, ce = int(pet.get("maximum_energy") or 0), int(pet.get("current_energy") or 0)
    if ce > me:
        add("error", "energy", "energy %d is above the maximum %d (database check)" % (ce, me))
    if me != 100:
        add("warn", "energy", "max energy %d: every pet has 100 (both hatch inserts write 100); the merge drain is 1 point "
            "per 3 s and the recharge 5 per 6 s, so %d makes owner Merge last %d%% as long" % (me, me, me))
    for k in ("satiety", "amity"):
        if int(pet.get(k) or 0) > 100:
            add("warn", k, "%s %s: the client shows at most 100" % (k, pet.get(k)))
    if species and species.get("max_lifetime") and int(pet.get("remaining_lifetime") or 0) > int(species["max_lifetime"]):
        add("warn", "lifetime", "lifetime %s is above the species' full bar %s (Effective Water refills to it)" % (
            pet.get("remaining_lifetime"), species["max_lifetime"]))

    # skill cells
    op, av = int(pet.get("opened_skill_slots") or 1), int(pet.get("available_skill_slots") or 1)
    if op > av:
        add("error", "cells", "opened cells %d > available cells %d" % (op, av))
    for s in skills or []:
        nm = "%s (#%s)" % (s.get("name"), s.get("skill_id"))
        if not s.get("species_ok", True):
            add("warn", "skills", "%s is for %s only" % (nm, s.get("only")))
        if s.get("published") is False:
            add("warn", "skills", "%s is not a published runtime skill: no effect in game" % nm)
        books = c.get("books") or {}
        head = s.get("head")
        starter = species and species.get("starter") is not None and lib and \
            (lib["skills"].get(int(species["starter"])) or {}).get("type") == s.get("type")
        if books and head and int(head) not in books and not starter:
            add("warn", "skills", "%s: no skill book teaches this tier (ItemBaseAttribute.xml PetSkill)" % nm)
        trait = s.get("trait")
        if trait and stats:
            vis = [h(st.get("total") or 0) for st in stats]
            short = [(STATS[i], t) for i, t in enumerate(trait) if h(t) > vis[i]]
            if short:
                add("warn", "skills", "%s needs %s at the time it is learned (the pet has less now; a skill learned earlier "
                    "keeps working)" % (nm, ", ".join("%s %s" % (n_, t) for n_, t in short)))
    if not any(x["level"] != "ok" for x in out):
        add("ok", "all", "nothing here is out of reach of real merges, rebirths and items")
    return out


# ------------------------------------------------------------------------------------------------ snapshot builder
def build_snapshot(client_sys, out=SNAPSHOT):
    """python3 -I modules/_pet_sim.py <client .../Settings/Sys> : writes catalog-origin/pet-alter.json from
    Pet_Alter.xml (merge lookups, factors, spirits) and ItemBaseAttribute.xml (pet skill books)."""
    with open(os.path.join(client_sys, "Pet_Alter.xml"), encoding="utf-8-sig", errors="replace") as f:
        c = content_from_pet_alter(f.read())
    books = {}
    with open(os.path.join(client_sys, "ItemBaseAttribute.xml"), encoding="utf-8-sig", errors="replace") as f:
        for m in re.finditer(r'\bID="(\d+)"[^>]*\bPetSkill="(\d+)"', f.read()):
            books.setdefault(int(m.group(2)), set()).add(int(m.group(1)))
    # server-published books the client catalogue lacks (PetSkillBookItemContentBaseline.cs)
    for item, skill in ((10745, 6020), (16400, 6400), (16401, 6401), (16402, 6402), (16403, 6403), (16404, 6404), (16405, 6405)):
        books.setdefault(skill, set()).add(item)
    snap = {"source": "client Pet_Alter.xml + ItemBaseAttribute.xml (snapshot; the editor prefers the server's published pet content)",
            "savvy": c["savvy"], "rank": c["rank"], "factors": {str(k): un(v) for k, v in sorted(c["factors"].items())},
            "spirits": {str(k): list(v) for k, v in sorted(c["spirits"].items())},
            "rebirth_levels": {"1": 30, "2": 80, "3": 100, "4": 110, "5": 120},
            "books": {str(k): sorted(v) for k, v in sorted(books.items())}}
    with open(out, "w", encoding="utf-8") as f:
        json.dump(snap, f, separators=(",", ":"))
        f.write("\n")
    return snap


if __name__ == "__main__":          # pragma: no cover
    import sys
    s = build_snapshot(sys.argv[1])
    print("wrote %s: %d savvy rows, %d rank rows, %d factors, %d book skills" % (
        SNAPSHOT, len(s["savvy"]), len(s["rank"]), len(s["factors"]), len(s["books"])))
