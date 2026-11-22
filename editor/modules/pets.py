"""modules/pets.py - the "Pets & mounts" editor. Contract: ../MODULES.md. Owned by the pets module task.

What it edits (one character at a time, the in-game Pet window, Pet details window and the bag's Mount tab):

  * pets (character_pets + character_pet_stat_values + character_pet_skills): add (the server's own hatch insert,
    reborn.op_pet_add), delete, every editable column of the Pet details window (name, sex, aptitude -> innate
    talents + savvy bracket, level -> added savvy, EXP, rank, rebirths, merges, soul contract, satiety, amity,
    lifetime, energy, stat points, bound, carried / summoned / merged-into-owner, opened / available skill cells,
    the six basic savvy values and growth rates), and the 12 skill cells;
  * the pet shed (character_base.pet_shed_capacity, 2..8) through the shared live-or-database save path;
  * mounts (items of kind 'mount' and the five mount-gear kinds): add (to the bag: LIVE on a round-2 server when the
    player is online; to the riding slot or a gear slot: database), make another owned mount the active one,
    unequip, quality / grade / bound, delete.

The skill cells follow the game server's own canonical form (src/Godswar.Server, commit b85a1244):
  * PacketBuilder.Pets.cs WritePetSkillIds: the active skills, ordered by cell, must sit in cells 0..n-1 with no gap,
    or the owned-pet list (10237) throws and the login is rejected;
  * PostgresPetDurableCommandExecutor.SkillBooks.cs ValidateSkillBookPetState: every row active, rank >= 1, inside
    the opened cells, and skill_id = the FIRST runtime id of its (Type, Priority) tier with skill_rank = Priority,
    or every later skill book on that pet throws "not canonical";
  * the client's "May only learn 1 of each type": one skill per family (Pet_Skill.xml Type); learning a higher tier
    of a family the pet has replaces it in its cell (UpgradeLearnedPetSkillAsync);
  * PostgresPetDurableCommandExecutor.SkillUnlearn.cs CompactPetSkillsAsync: removing a skill moves the later ones
    down one cell.
A write here always leaves the pet's skills in that form (guard DO-block in the same transaction).

Wire limits the client sees (PacketBuilder.Pets.cs WriteOwnedPetRecord): satiety and amity are clamped to 0..100,
lifetime and pet merges to 0..65,535, EXP to 0..4,294,967,295, rebirths done + left to one byte (0..255), rank to
0..655.35 in hundredths. The editor refuses values the client would show differently.

Pets have no live operation in the round-2 server (its capabilities list money, progression, teleport, kick and
kit-bag items only), so a pet edit on an online character answers 423 with the reason; the banner offers Kick when
the server can kick. Mount items given to / removed from the kit bag go through ctx.bag_item (live when possible).
"""
import copy
import re
from decimal import Decimal as D

from . import common
from . import _pet_sim as PS
from .common import Bad, Refused, NotFound, Conflict, OnlineRefused, lit

try:
    import reborn as R
except Exception:            # pragma: no cover - the editor shows the import error
    R = None

M = common.module(
    "pets", "Pets & mounts",
    icon={"sprite": 993},          # catalog-icons.png tile (Rock Elf)
    order=30,
    status="ready",
    description="The in-game pet windows and the Mount tab: every pet's card (aptitude, talents, savvy, the 12 skill "
                "cells, bars, rebirths, soul contract), the pet shed, adding and removing pets; owned mounts, the active "
                "mount, mount gear and quality.",
    needs_character=True,
)

I32 = 2 ** 31 - 1
U32 = 2 ** 32 - 1
STAT_NAMES = {1: "Agility", 2: "Strength", 3: "Accuracy", 4: "Technique", 5: "Wisdom", 6: "Luck"}
TALENTS = [(1, "Random Event", "The pet can trigger random events while it is out."),
           (2, "Quest Dispatch", "The pet can be sent on dispatch quests (Pet Manager)."),
           (4, "Work", "The pet can be sent to work for rewards (Pet Manager, Work)."),
           (8, "Healing", "The pet heals its owner now and then."),
           (16, "Merge", "Pet Unite: a summoned pet with full energy and 40+ amity can merge into its owner and lend its stats.")]
FOOD = {1: "Herbivore", 2: "Carnivore", 3: "Omnivore"}   # pet-catalog.md: Pet_Confect.xml food values
SOUL_BONUS = [0, 3, 4, 5, 6, 7, 8]                       # pet-soul-contract.md: Pet_Alter.xml Base_Alter / 100
MOUNT_KINDS = {"mount": 20, "mounthead": 15, "mountarmor": 16, "mountsoul": 17, "mountornament": 18, "mountamulet": 19}
GEAR_NAMES = {15: "Coronet", 16: "Armor", 17: "Soul", 18: "Saddle", 19: "Tassels", 20: "Mount (riding)"}
ROMAN = re.compile(r"\s+[IVX]+$|(?<=[a-z])[IVX]+$")   # "Sharp Claw III", also "BlockI" (no space in the client)
ONLY_RX = (re.compile(r"Available to (.+?) only"), re.compile(r"Only available to ([^.]+?)\s*\."))

MERGE_REFUSED = ("'Merged into owner' cannot be switched on here: the game server ends every owner Merge when the character "
                 "logs in (GameClientHandler.PetOwnerMergeLifecycle.cs RecoverStalePetOwnerMergeOnLoginAsync, log line "
                 "'[pet] recovered stale owner Merge'), so it would never take effect. Merge in game: the pet summoned, with the "
                 "Merge talent, full energy and 40+ amity. Switching it off is allowed.")

# wire limits (PacketBuilder.Pets.cs WriteOwnedPetRecord) on top of reborn.PET_EDITABLE's column ranges
WIRE = {"satiety": (0, 100, "the client shows 0-100 (a byte clamped to 100)"),
        "amity": (0, 100, "the client shows 0-100 (a byte clamped to 100)"),
        "remaining_lifetime": (0, 65535, "the client field is 16 bits"),
        "completed_pet_merges": (0, 65535, "the client field is 16 bits"),
        "experience": (0, U32, "the client field is 32 bits"),
        "available_stat_points": (0, I32, ""),
        "current_energy": (0, I32, ""), "maximum_energy": (1, I32, "")}


# ------------------------------------------------------------------------------------------------ catalogue helpers
_SK = {}


def skill_library(rb):
    """Pet skills by id with the family / tier facts the server's canonical form needs (cached per catalogue)."""
    cat = rb.cat()
    if _SK.get("cat") is cat:
        return _SK["v"]
    ps = rb.client()["petskills"]
    heads = {}
    for k, s in ps.items():
        key = (int(s.get("type", -1)), int(s.get("priority", 0)))
        if key not in heads or int(k) < heads[key]:
            heads[key] = int(k)
    fam = {}
    for (t, pr), hid in heads.items():
        f = fam.setdefault(t, {"type": t, "name": ROMAN.sub("", (ps.get(str(hid)) or {}).get("name") or "family %d" % t),
                               "tiers": {}, "species": None, "only": None})
        f["tiers"][pr] = hid
    # species-only families: the client text says "Available to <family> only" (reborn.petskill_species) or "Only
    # available to <family>." (e.g. Mind Refresh = Lil QT, which petskill_species does not catch); a species belongs to
    # the family whose Type its starter skill has (pet_content_species_definitions.starter_skill_id)
    starter_type = {}
    for sp_id, sp in cat["species"].items():
        st = ps.get(str(sp.get("starter_skill_id"))) or {}
        if st:
            starter_type.setdefault(int(st.get("type", -1)), []).append(int(sp_id))
    for t, f in fam.items():
        info = " ".join((ps.get(str(h)) or {}).get("info") or "" for h in f["tiers"].values())
        m = ONLY_RX[0].search(info) or ONLY_RX[1].search(info)
        if m:
            f["only"] = m.group(1).strip()
            f["species"] = sorted(starter_type.get(t, [])) or None
            if not f["species"]:
                f["only"] += " (no species has this family's starter skill: refused for every pet unless forced)"
                f["species"] = [0]
    published = cat.get("pet_skill_ids") or set()
    out = {}
    for k, s in ps.items():
        t, pr = int(s.get("type", -1)), int(s.get("priority", 0))
        hid = heads[(t, pr)]
        trait = [int(x) for x in str(s.get("trait") or "").split(",") if x.strip().lstrip("-").isdigit()]
        out[int(k)] = {"id": int(k), "name": s.get("name") or "pet skill %s" % k, "type": t, "tier": pr, "head": hid,
                       "is_head": hid == int(k), "family": fam[t]["name"], "only": fam[t]["only"],
                       "species": fam[t]["species"], "sprite": s.get("sprite"),
                       "trait": [round(x / 100.0, 2) for x in trait] if len(trait) == 6 else None,
                       "add": s.get("add"), "published": (not published) or int(k) in published,
                       # the effect curve: the highest Restrict (pet rank) not above the pet's rank picks Values (absolute)
                       "restrict": _nums(s.get("restrict")), "values": _nums(s.get("values"))}
    v = {"skills": out, "families": {t: {"type": t, "name": f["name"], "only": f["only"], "species": f["species"],
                                         "tiers": {str(p): h for p, h in sorted(f["tiers"].items())}} for t, f in fam.items()}}
    _SK.update(cat=cat, v=v)
    return v


def _nums(v):
    out = []
    for x in str(v or "").replace("，", ",").split(","):
        try:
            out.append(float(x))
        except ValueError:
            pass
    return out


def item_sprites():
    ix = common.CATALOG.index("item")
    return {r[0]: r[5] for r in ix["rows"]}


def species_info(rb):
    cat, cli = rb.cat(), rb.client()
    out = {}
    for sid, sp in cat["species"].items():
        c = cli["pets"].get(str(sid)) or {}
        lt = [int(x) for x in (sp.get("lifetime_values") or [])]
        out[int(sid)] = {"id": int(sid), "name": c.get("name") or sp.get("display_name"), "server_name": sp.get("display_name"),
                         "food": FOOD.get(int(sp.get("food_kind") or 0), str(sp.get("food_kind"))),
                         "starter": sp.get("starter_skill_id"), "starter_name": sp.get("starter_skill_name"),
                         "lifetime_values": lt, "max_lifetime": max(lt) if lt else 1500, "egg": sp.get("egg_item_id"),
                         "jade": sp.get("magic_jade_item_id"), "sprite_male": c.get("sprite_male"),
                         "sprite_female": c.get("sprite_female"), "in_client": bool(c)}
    return out


def aptitude_info(rb):
    cat = rb.cat()
    labels = (common.CATALOG.file("labels").get("pet_aptitudes") or {})
    out = {}
    for a, x in cat["aptitudes"].items():
        n = int(a)
        mask = 31 if n >= 14 else 26 if n >= 10 else 26 if n == 8 else 31 if n == 7 else 0
        # the in-game Pet details window prints text.lua PETAPTITUDE<n> (PetDetailProc.lua PetDetail_PetSetAptitude), the
        # same words as ItemColor.xml <Aptitude n>: the admin's client says 7 Smart, 8 Zealous, 9 Grumpy, 10 Brave, while the
        # reborn server's pet_aptitude_templates renamed 6-10 (docs/pet-catalog.md "Project reorder"). Show the in-game
        # word first; 15 / 16 are "Backup" in the client, so the server's Celestial / Transcendent are shown there.
        cn = (labels.get(str(n)) or {}).get("name")
        game = cn if cn and cn.lower() != "backup" else None
        out[n] = {"id": n, "name": game or x.get("display_name"), "server_name": x.get("display_name"), "client_name": cn,
                  "savvy": [int(x["minimum_initial_savvy"]), int(x["maximum_initial_savvy"])],
                  "growth": [str(x["minimum_total_growth"]), str(x["maximum_total_growth"])],
                  "talent_mask": mask, "cells": 2 if n >= 10 else 1}
    return out


_STEPS = {}


def content_steps(rb):
    cat = rb.cat()
    if _STEPS.get("cat") is cat:
        return _STEPS["v"]
    rev = "(SELECT revision FROM pet_content_publication WHERE family = 'pets')"
    v = rb.pg.json("json_build_object('exp', (SELECT coalesce(json_object_agg(current_level, required_experience), '{}') "
                   "FROM pet_content_experience_steps WHERE revision = %s), 'rebirth', (SELECT coalesce(json_agg(json_build_object("
                   "'n', rebirth_number, 'level', required_pet_level, 'item', chance_item_name) ORDER BY rebirth_number), '[]') "
                   "FROM pet_content_rebirth_steps WHERE revision = %s))" % (rev, rev))
    _STEPS.update(cat=cat, v=v)
    return v


# ------------------------------------------------------------------------------------------------ reads
def _need_char(rb, cid):
    d = rb.dump(cid)
    if not d["character_base"]:
        raise NotFound("no reborn character #%s" % cid)
    return d


def _pet_rows(rb, d, lib, species, apts, steps):
    stats, skills = {}, {}
    for s in d["character_pet_stat_values"]:
        stats.setdefault(s["pet_id"], []).append(s)
    for s in d["character_pet_skills"]:
        skills.setdefault(s["pet_id"], []).append(s)
    sealed = {s["pet_id"] for s in d["sealed_pet_items"]}
    out = []
    for p in sorted(d["character_pets"], key=lambda x: x["id"]):
        sp = species.get(int(p["species_id"])) or {}
        apt = apts.get(int(p["aptitude"])) or {}
        soul = SOUL_BONUS[int(p["soul_contract_stage"] or 0)] if 0 <= int(p["soul_contract_stage"] or 0) <= 6 else 0
        st = []
        for s in sorted(stats.get(p["id"], []), key=lambda s: s["stat_code"]):
            basic, added = D(str(s["initial_savvy"])), D(str(s["added_savvy"]))
            st.append(dict(s, stat_name=STAT_NAMES.get(s["stat_code"], str(s["stat_code"])),
                           growth=str(D(str(s["base_growth_rate"])) + D(str(s["growth_acceleration"]))),
                           soul=soul, total=str(basic + added + soul)))
        sk = []
        for s in sorted(skills.get(p["id"], []), key=lambda s: (s["slot_index"], s["skill_id"])):
            e = lib["skills"].get(int(s["skill_id"])) or {}
            sk.append(dict(s, name=e.get("name") or "pet skill %s" % s["skill_id"], sprite=e.get("sprite"), type=e.get("type"),
                           tier=e.get("tier"), family=e.get("family"), head=e.get("head"), only=e.get("only"),
                           species_ok=(not e.get("species")) or int(p["species_id"]) in e["species"], known=bool(e),
                           published=e.get("published", False), trait=e.get("trait")))
        problems = skill_problems(p, sk)
        req = (steps.get("exp") or {}).get(str(p["level"]))
        gb = apts.get(int(p["aptitude"]) if p["growth_revealed"] else 1) or {}
        out.append(dict(p, species_name=sp.get("name") or "species %s" % p["species_id"], food=sp.get("food"),
                        sprite=sp.get("sprite_male") if int(p["sex"]) == 1 else sp.get("sprite_female"),
                        # what the client shows as the lifetime maximum: the SMALLEST species lifetime value >= the current
                        # lifetime, else the current one (PacketBuilder.Pets.cs ResolveMaximumPetLifetime)
                        max_lifetime=min([v for v in sp.get("lifetime_values") or [] if v >= int(p["remaining_lifetime"])] or
                                         [int(p["remaining_lifetime"]) or sp.get("max_lifetime") or 1500]),
                        aptitude_name=apt.get("name") or str(p["aptitude"]), aptitude_client_name=apt.get("client_name"),
                        talents=[{"bit": b, "name": n, "help": h, "on": bool(int(p["talent_mask"]) & b)} for b, n, h in TALENTS],
                        stats=st, skills=sk, skill_problems=problems, sealed=p["id"] in sealed or p["activity_state"] != "owned",
                        exp_to_next=req, soul_bonus=soul, growth_bracket=gb.get("growth"),
                        growth_bracket_name=gb.get("name"), savvy_bracket=apt.get("savvy"),
                        savvy_total=str(sum((D(str(s["initial_savvy"])) for s in st), D(0))),
                        growth_total=str(sum((D(str(s["base_growth_rate"])) for s in st), D(0)))))
    return out


def skill_problems(pet, skills):
    """What makes this pet's skill cells non-canonical (see the module docstring); [] when fine."""
    out = []
    act = sorted([s for s in skills if s["is_active"]], key=lambda s: s["slot_index"])
    if [s["slot_index"] for s in act] != list(range(len(act))):
        out.append("the active skills are not in cells 1..%d without a gap (the pet list throws at login)" % len(act))
    if any(not s["is_active"] for s in skills):
        out.append("an inactive skill row (the game never makes one; skill books on this pet would fail)")
    if any(s["slot_index"] >= int(pet["opened_skill_slots"]) for s in act):
        out.append("a skill sits outside the %s opened cells" % pet["opened_skill_slots"])
    for s in skills:
        if s.get("head") and (int(s["skill_id"]) != int(s["head"]) or int(s["skill_rank"]) != int(s.get("tier") or 0)):
            out.append("%s (#%s): not the tier's first id / rank %s (canonical: #%s rank %s)" % (
                s["name"], s["skill_id"], s["skill_rank"], s["head"], s.get("tier")))
    types = [s.get("type") for s in skills if s.get("type") is not None]
    if len(types) != len(set(types)):
        out.append("two skills of the same family (the game allows one of each type)")
    return out


def mount_rows(rb, d):
    spr = item_sprites()
    cat = rb.cat()
    rows = []
    for it in d["character_items"]:
        m = rb.item_meta(it["prop_id"])
        if m["kind"] not in MOUNT_KINDS:
            continue
        cli = rb.client()["items"].get(str(it["prop_id"])) or {}
        q = int(it["item_quality"] or 0)
        speed = None
        sv = cli.get("speed_by_quality") or (cli.get("stats") or {}).get("Speed")
        if sv and q >= 1:
            speed = sv[min(q, len(sv)) - 1]
        rows.append({"row": it["id"], "prop_id": it["prop_id"], "name": m["name"], "kind": m["kind"], "minlv": m["minlv"],
                     "sprite": spr.get(int(it["prop_id"])), "location": it["item_location"], "slot": it["slot_index"],
                     "where": R.slot_label(it["item_location"], it["slot_index"]), "quality": q,
                     "quality_name": cat["qualities"].get(str(q), "Q%s" % q), "grade": it["item_grade"], "bound": it["bound"],
                     "stack": it["stack"], "speed": speed, "known_server": m["server"], "known_client": m["client"],
                     "attrs": [{"id": it.get("attribute%d" % i), "level": it.get("attribute_level%d" % i)}
                               for i in range(1, 6) if it.get("attribute%d" % i) is not None],
                     "expected": {k: it[k] for k in ("prop_id", "item_location", "slot_index", "item_quality", "item_grade", "bound")}})
    return sorted(rows, key=lambda r: (0 if r["location"] == R.LOC_EQUIP else 1, r["location"], r["slot"]))


@M.get(r"sheet/(\d+)")
def sheet(ctx, cid):
    rb = ctx.need_rb()
    cid = int(cid)
    d = _need_char(rb, cid)
    b = d["character_base"][0]
    cat = rb.cat()
    lib = skill_library(rb)
    species, apts, steps = species_info(rb), aptitude_info(rb), content_steps(rb)
    settings = {k: (str(v) if isinstance(v, D) else v) for k, v in (cat.get("pet_settings") or {}).items()
                if k not in ("added_savvy_weights",)}
    info = common.online_info(ctx.app, cid)
    caps = ctx.caps
    ops = caps.get("operations") or {}
    mounts = mount_rows(rb, d)
    equip = {r["slot"]: r for r in mounts if r["location"] == R.LOC_EQUIP}
    used_bag = {i["slot_index"] for i in d["character_items"] if i["item_location"] == R.LOC_BAG}
    pets = _pet_rows(rb, d, lib, species, apts, steps)
    owned = [p for p in d["character_pets"] if p["activity_state"] == "owned"]
    return {
        "character": {"id": cid, "name": b["name"], "level": b["fighter_job_lv"], "class": common.CLASS_NAMES.get(b["profession"]),
                      "pet_shed_capacity": b["pet_shed_capacity"], "pet_shed_revision": b.get("pet_shed_revision"),
                      "owned_pets": len(owned), "bag_free": common.BAG_SLOTS - len(used_bag)},
        "database": rb.db, "writable": rb.writable, "allow_force": bool(rb.allow_force),
        "online": info, "live": {"available": bool(caps.get("available")), "bag_add": "character.give_item" in ops,
                                 "bag_remove": "character.item_delete" in ops, "kick": "character.kick" in ops,
                                 "pets": False, "why_pets": "the game server has no live pet operation (round-2 lists money, "
                                 "progression, teleport, kick and kit-bag items only): pet edits need the character logged out"},
        "settings": settings, "aptitudes": [apts[k] for k in sorted(apts)], "species": [species[k] for k in sorted(species)],
        "exp_steps": steps.get("exp") or {}, "rebirth_steps": steps.get("rebirth") or [],
        "soul_bonus": SOUL_BONUS, "stat_names": STAT_NAMES, "wire": {k: [lo, hi, why] for k, (lo, hi, why) in WIRE.items()},
        "pets": pets,
        "mounts": {"items": mounts, "ride": equip.get(R.MOUNT_SLOT), "gear": {str(s): equip.get(s) for s in R.MOUNT_GEAR_SLOTS},
                   "slot_names": {str(k): v for k, v in GEAR_NAMES.items()}, "kinds": MOUNT_KINDS},
        # the cross-table checks without the three start-up validator queries (2-3 s on the VPS); GET checks/<cid> runs
        # them, and every write runs them inside its transaction anyway
        "findings": _mine(rb.check_character(d, validators={})),
    }


def _mine(findings):
    return [f for f in findings if f["where"].startswith(("pet", "equipment Mount")) or "mount" in f["msg"].lower()]


@M.get(r"checks/(\d+)")
def checks(ctx, cid):
    """The pet / mount findings including the reborn server's three pet start-up validators (read-only)."""
    rb = ctx.need_rb()
    d = _need_char(rb, int(cid))
    return {"findings": _mine(rb.check_character(d)), "validators": True}


@M.get("skills")
def skills(ctx):
    """Every pet skill (1,655 ids) with family, tier, canonical id, species restriction and Trait need."""
    rb = ctx.need_rb()
    lib = skill_library(rb)
    books = sim_content(rb).get("books") or {}
    fams = []
    for f in lib["families"].values():      # which tiers a skill book teaches (ItemBaseAttribute.xml PetSkill)
        fams.append(dict(f, books={t: books.get(int(hid), []) for t, hid in f["tiers"].items()}))
    return {"skills": list(lib["skills"].values()), "families": fams,
            "species": [{"id": k, "name": v["name"], "starter": v.get("starter")} for k, v in sorted(species_info(rb).items())]}


# ------------------------------------------------------------------------------------------------ Pet Manager simulator
_SIM = {}


def sim_content(rb):
    """The merge / rebirth tables of the server's PUBLISHED pet content (pet_content_merge_* at the pets revision, the
    rebirth gates, the skill books of item_templates), cached per catalogue; the bundled client snapshot
    (catalog-origin/pet-alter.json, = Pet_Alter.xml) when the database has no such tables."""
    cat = rb.cat()
    if _SIM.get("cat") is cat:
        return _SIM["v"]
    snap = PS.load_snapshot()
    rev = "(SELECT revision FROM pet_content_publication WHERE family = 'pets')"
    try:
        v = rb.pg.json(
            "json_build_object("
            "'savvy', (SELECT json_agg(json_build_array(minimum_savvy_difference, base_increase) ORDER BY minimum_savvy_difference) "
            "FROM pet_content_merge_savvy_lookup WHERE revision = %(r)s), "
            "'rank', (SELECT json_agg(json_build_array(minimum_rank_difference, base_increase) ORDER BY minimum_rank_difference) "
            "FROM pet_content_merge_rank_lookup WHERE revision = %(r)s), "
            "'factors', (SELECT json_object_agg(species_id, factor) FROM pet_content_merge_rank_species_factors WHERE revision = %(r)s), "
            "'spirits', (SELECT json_object_agg(spirit_count, json_build_array(minimum_percent, maximum_percent)) "
            "FROM pet_content_merge_rank_spirit_steps WHERE revision = %(r)s), "
            "'revision', %(r)s)" % {"r": rev})
        if not (v.get("savvy") and v.get("rank") and v.get("factors")):
            raise ValueError("no published merge lookups")
        v["source"] = "the server's published pet content (revision %s)" % str(v.get("revision") or "?")[:12]
        sp = v.get("spirits") or {}
        sp.setdefault("0", [0, 100])
        v["spirits"] = sp
    except Exception as e:      # noqa: BLE001 - an older database or a test without one: the client snapshot
        v = dict(snap, source=snap.get("source", "client snapshot") + " - the database has no merge tables (%s)" % str(e)[:80])
    try:
        books = rb.pg.json("(SELECT json_object_agg(s, ids) FROM (SELECT (stats->>'PetSkill')::int AS s, json_agg(id ORDER BY id) AS ids "
                           "FROM item_templates WHERE stats->>'PetSkill' ~ '^[0-9]+$' GROUP BY 1) b)")
        v["books"] = books or snap.get("books") or {}
    except Exception:           # noqa: BLE001
        v["books"] = snap.get("books") or {}
    steps = content_steps(rb).get("rebirth") or []
    v["rebirth_levels"] = {int(s["n"]): int(s["level"]) for s in steps} or snap.get("rebirth_levels") or {}
    st = cat.get("pet_settings") or {}
    v["max_level"] = int(st.get("maximum_level") or 120)
    v["max_rebirths"] = int(st.get("maximum_rebirth_count") or PS.MAX_REBIRTHS)
    v["max_rank_h"] = PS.h(st.get("maximum_rank") or "655.35")
    # skill books are keyed by the tier's canonical (first) id, like the server stores learned skills
    lib = skill_library(rb)["skills"]
    heads = {}
    for sid, items in (v["books"] or {}).items():
        e = lib.get(int(sid))
        heads.setdefault(int(e["head"]) if e else int(sid), set()).update(int(x) for x in items)
    v["books"] = {k: sorted(x) for k, x in heads.items()}
    out = PS.normalise(v)
    out["source"] = v.get("source")
    _SIM.update(cat=cat, v=out)
    return out


def _hatch_ranks(rb, aptitude):
    return [str(r["rank"]) for r in (rb.cat().get("hatch_steps") or []) if int(r.get("aptitude") or 0) == int(aptitude)]


def _sim(fn, *a, **kw):
    try:
        return fn(*a, **kw)
    except PS.SimError as e:
        raise Bad(str(e))


@M.get("sim/content")
def sim_content_route(ctx):
    """The tables the simulator uses (for the page: species factors, spirits, gates, per-merge ceilings, source)."""
    rb = ctx.need_rb()
    c = sim_content(rb)
    sp = species_info(rb)
    return {"source": c.get("source"), "factors": {str(k): PS.un(v) for k, v in c["factors"].items()},
            "species": [{"id": k, "name": v["name"], "factor": PS.un(c["factors"].get(k, 0))} for k, v in sorted(sp.items())],
            "spirits": {str(k): list(v) for k, v in c["spirits"].items()}, "rebirth_spirits": {str(k): [v, PS.REBIRTH_MAX_H] for k, v in PS.REBIRTH_MIN_H.items()},
            "rebirth_levels": {str(k): v for k, v in c["rebirth_levels"].items()}, "waters": [list(w) for w in PS.REBIRTH_WATERS],
            "max_level": c["max_level"], "max_rebirths": c["max_rebirths"], "max_rank": PS.un(c["max_rank_h"]),
            "ceiling": {k: PS.un(v) for k, v in PS.max_merge_gain(c).items()}, "merge_min_level": PS.MERGE_MIN_LEVEL,
            "model_changes": list(PS.MODEL_CHANGES),
            "items": {"merge_spirit": PS.MERGE_SPIRIT, "merge_spirit_bound": PS.MERGE_SPIRIT_BOUND,
                      "rebirth_spirit": PS.REBIRTH_SPIRIT, "rebirth_spirit_bound": PS.REBIRTH_SPIRIT_BOUND}}


def _fmt_merge(r):
    u = PS.un
    return {"basic": [u(x) for x in r["basic"]], "rank": u(r["rank"]), "start_basic": [u(x) for x in r["start_basic"]],
            "start_rank": u(r["start_rank"]), "partner_added": [u(x) for x in r["partner_added"]], "notes": r["notes"],
            "stalled_at": r["stalled_at"], "ceiling": {k: u(v) for k, v in r["per_merge_ceiling"].items()},
            "gain": [u(b - a) for a, b in zip(r["start_basic"], r["basic"])], "rank_gain": u(r["rank"] - r["start_rank"]),
            "steps": [{"n": s["n"], "bounds": [[u(lo), u(hi)] for lo, hi in s["bounds"]], "gains": [u(x) for x in s["gains"]],
                       "need": [u(x) for x in s["need"]], "rank_bounds": [u(x) for x in s["rank_bounds"]],
                       "rank_gain": u(s["rank_gain"]), "basic": [u(x) for x in s["basic"]], "rank": u(s["rank"])} for s in r["steps"]]}


def _fmt_rebirth(r):
    u = PS.un
    return {"accel": [u(x) for x in r["accel"]], "range": [u(x) for x in r["range"]], "completed": r["completed"], "level": r["level"],
            "added_level1": [u(x) for x in r["added_level1"]], "added_at_level": [u(x) for x in r["added_at_level"]],
            "phoenix": [u(x) for x in r["phoenix"]], "notes": r["notes"],
            "steps": [{"n": s["n"], "level_needed": s["level_needed"], "water": s["water"], "increase": [u(x) for x in s["increase"]],
                       "accel": [u(x) for x in s["accel"]], "model_change": s["model_change"]} for s in r["steps"]]}


@M.post("sim/merge")
def sim_merge(ctx):
    """{primary: {basic: [6], rank, level}, partner: {species, basic: [6], rank, level, growth: [6], accel: [6]},
    merges, spirits, savvy_pos: min|avg|max|0..1000|[6], rank_pos} -> every merge's ranges and the result. Read-only."""
    rb = ctx.need_rb()
    b = ctx.body or {}
    r = _sim(PS.simulate_merges, sim_content(rb), b.get("primary") or {}, b.get("partner") or {}, common.to_int(b.get("merges", 1), 0, 1000, "merges"),
             common.to_int(b.get("spirits", 0), 0, 5, "spirits"), b.get("savvy_pos", "avg"), b.get("rank_pos", "avg"))
    return _fmt_merge(r)


@M.post("sim/rebirth")
def sim_rebirth(ctx):
    """{pet: {completed, growth: [6], accel: [6], soul_contract_stage, rebirths_remaining}, rebirths, spirits, pos, level}
    -> each rebirth's gate, water, acceleration roll, and Added at level 1 / at <level>. Read-only."""
    rb = ctx.need_rb()
    b = ctx.body or {}
    r = _sim(PS.simulate_rebirths, sim_content(rb), b.get("pet") or {}, common.to_int(b.get("rebirths", 1), 0, 100, "rebirths"),
             common.to_int(b.get("spirits", 0), 0, 5, "spirits"), b.get("pos", "avg"), b.get("level"))
    return _fmt_rebirth(r)


# ---- B21 item 23: merge-count planner (modules/_pet_plan.py; read-only)
@M.post("sim/fictitious")
def sim_fictitious(ctx):
    """{species, aptitude, level, quality: min|mid|max, rebirths, rebirth_spirits} -> a made-up pet hatched the server's
    way (Basic and growth inside the nature's brackets, a published hatch rank). Read-only."""
    from . import _pet_plan as PP
    rb = ctx.need_rb()
    b = ctx.body or {}
    apt_id = common.to_int(b.get("aptitude", 1), 1, 99, "aptitude")
    apt = aptitude_info(rb).get(apt_id)
    if not apt:
        raise Bad("aptitude %s is not published" % apt_id)
    sp = common.to_int(b.get("species", 1), 1, 9999, "species")
    if sp not in species_info(rb):
        raise Bad("species %s is not published" % sp)
    c = sim_content(rb)
    out = _sim(PP.fictitious_pet, apt, _hatch_ranks(rb, apt_id), sp, common.to_int(b.get("level", 120), 1, c["max_level"], "level"),
               b.get("quality", "mid") if b.get("quality", "mid") in PP.QUALITY else "mid",
               common.to_int(b.get("rebirths", 0), 0, c["max_rebirths"], "rebirths"), common.to_int(b.get("rebirth_spirits", 0), 0, 5, "rebirth spirits"))
    out["aptitude"] = {"id": apt_id, "name": apt["name"], "savvy": apt["savvy"], "growth": apt["growth"]}
    return out


@M.post("sim/plan")
def sim_plan(ctx):
    """{primary: {basic, rank, level, growth, accel, completed}, partner: {...}, merges, spirits, rebirths, rebirth_spirits,
    level, rebirth_pos} -> per merge count the exact reachable range and the low / average / high roll paths, per
    rebirth count the Added and its gain, and the end state. Read-only."""
    from . import _pet_plan as PP
    rb = ctx.need_rb()
    b = ctx.body or {}
    c = sim_content(rb)
    r = _sim(PP.plan, c, b.get("primary") or {}, b.get("partner") or {}, common.to_int(b.get("merges", 10), 0, 300, "merges"),
             common.to_int(b.get("spirits", 0), 0, 5, "spirits"), common.to_int(b.get("rebirths", 0), 0, 100, "rebirths"),
             common.to_int(b.get("rebirth_spirits", 0), 0, 5, "rebirth spirits"), common.to_int(b.get("level", c["max_level"]), 1, c["max_level"], "level"),
             common.to_int(b.get("rebirth_pos", 500), 0, 1000, "rebirth roll"))
    return PP.fmt_plan(r)


def _overlay(p, fields):
    """The pet row with the page's pending edits on top (fields, savvy {code: v}, growth {code: v})."""
    q = dict(p)
    for k, v in (fields or {}).items():
        if k not in ("savvy", "growth", "presence", "aptitude"):
            q[k] = v
    st = [dict(s) for s in p["stats"]]
    for s in st:
        c = str(s["stat_code"])
        if c in ((fields or {}).get("savvy") or {}):
            s["initial_savvy"] = fields["savvy"][c]
        if c in ((fields or {}).get("growth") or {}):
            s["base_growth_rate"] = fields["growth"][c]
        lv = D(str(q.get("level") or 1))
        added = (D(str(s["base_growth_rate"])) + D(str(s["growth_acceleration"]))) * lv
        s["added_savvy"] = str(added)
        s["total"] = str(D(str(s["initial_savvy"])) + added + D(str(q.get("soul_bonus") or 0)))
    q["stats"] = st
    if (fields or {}).get("aptitude") is not None:
        q["aptitude"] = int(fields["aptitude"])
    return q


def legal_report(rb, pet, fields=None):
    apts = aptitude_info(rb)
    fields = dict(fields or {})
    stg = int(fields.get("soul_contract_stage", pet.get("soul_contract_stage")) or 0)
    q = _overlay(pet, dict(fields, soul_bonus=SOUL_BONUS[stg] if 0 <= stg <= 6 else 0))
    apt = apts.get(int(q["aptitude"])) or {}
    gb = apts.get(int(q["aptitude"]) if q.get("growth_revealed") else 1) or {}
    if (fields or {}).get("aptitude") is not None:
        q["initial_savvy_baseline_total"] = None        # the new nature rewrites the baseline (aptitude_statements)
    c = sim_content(rb)
    sp = species_info(rb).get(int(q["species_id"])) or {}
    checks = PS.legal_checks(c, q, {"savvy": apt.get("savvy") or [0, 0], "growth": gb.get("growth") or ["0", "1000"]},
                             _hatch_ranks(rb, q["aptitude"]), {"max_lifetime": max(sp.get("lifetime_values") or [0]) or None,
                                                              "starter": sp.get("starter")},
                             skills=pet.get("skills") or [], lib=skill_library(rb))
    reb = int(q.get("completed_rebirths") or 0)
    merges = int(q.get("completed_pet_merges") or 0)
    ceil = PS.max_merge_gain(c)
    hr = sorted(PS.h(x) for x in _hatch_ranks(rb, q["aptitude"])) or [0]
    base = PS.h(q.get("initial_savvy_baseline_total")) if q.get("initial_savvy_baseline_total") not in (None, "") else PS.h((apt.get("savvy") or [0, 0])[1])
    return {"checks": checks, "source": c.get("source"),
            "limits": {"basic_total_max": PS.un(base + merges * 6 * ceil["savvy"]), "basic_hatch": PS.un(base),
                       "rank_max": PS.un(min(c["max_rank_h"], hr[-1] + merges * ceil["rank"])), "hatch_ranks": [PS.un(x) for x in hr],
                       "accel": [PS.un(PS.REBIRTH_MIN_H[0] * reb), PS.un(PS.REBIRTH_MAX_H * reb)],
                       "phoenix": [PS.un(x) for x in PS.phoenix_range(reb)], "level_max": c["max_level"], "energy": 100,
                       "per_merge": {k: PS.un(v) for k, v in ceil.items()}}}


@M.post(r"legal/(\d+)")
def legal(ctx, cid):
    """{pet, fields (the page's pending edits)} -> "legal pet" checks: what real merges, rebirths and items could not
    have produced (warn) and what the server refuses (error), with the reachable limits. Read-only."""
    rb = ctx.need_rb()
    cid = int(cid)
    body = ctx.body or {}
    pid = common.to_int(body.get("pet"), 1, 2 ** 62, "pet")
    d = _need_char(rb, cid)
    pets = _pet_rows(rb, d, skill_library(rb), species_info(rb), aptitude_info(rb), content_steps(rb))
    p = next((x for x in pets if int(x["id"]) == pid), None)
    if not p:
        raise NotFound("pet #%s does not belong to this character" % pid)
    f = body.get("fields") or {}
    if not isinstance(f, dict):
        raise Bad("fields must be an object")
    try:
        return legal_report(rb, p, f)
    except PS.SimError as e:
        raise Bad(str(e))


@M.post(r"pet/(\d+)/apply-sim")
def apply_sim(ctx, cid):
    """{pet, revision, merge: {partner, merges, spirits, savvy_pos, rank_pos} | null, rebirth: {rebirths, spirits, pos,
    level} | null} -> write what the simulation gives, re-run HERE from the pet's stored values (never the page's
    numbers): Basic savvy + merge gains, rank, merge count; growth acceleration + rebirth rolls, rebirth count, attempts
    left, level, Added = (growth + acceleration) x level. One transaction with the start-up checks; Undo restores it."""
    rb = ctx.need_rb()
    cid = int(cid)
    body = ctx.body or {}
    pid = common.to_int(body.get("pet"), 1, 2 ** 62, "pet")
    mg, rbt = body.get("merge"), body.get("rebirth")
    if not mg and not rbt:
        raise Bad("nothing to apply: give merge and / or rebirth")
    c = sim_content(rb)
    force = _force(ctx, body)
    label = {}
    if mg and not force:
        par = mg.get("partner") or {}
        if par.get("added") is None and common.to_int(par.get("level") or 0, 0, 1000, "partner level") < PS.MERGE_MIN_LEVEL:
            raise Bad("the partner must be level %d or more to merge (Pet_Alter Minor_PetLv); tick force to apply anyway" % PS.MERGE_MIN_LEVEL)

    def b(d):
        pet = _pet_of(d, pid)
        stats = sorted([s for s in d["character_pet_stat_values"] if s["pet_id"] == pid], key=lambda s: s["stat_code"])
        if len(stats) != 6:
            raise Refused("pet #%s does not have its six savvy rows" % pid)
        basic = [str(s["initial_savvy"]) for s in stats]
        rank = str(pet["rank"])
        merges, done, left, level = int(pet["completed_pet_merges"]), int(pet["completed_rebirths"]), int(pet["rebirths_remaining"]), int(pet["level"])
        accel = [D(str(s["growth_acceleration"])) for s in stats]
        parts = []
        if mg:
            n = common.to_int(mg.get("merges", 1), 1, 1000, "merges")
            r = _sim(PS.simulate_merges, c, {"basic": basic, "rank": rank, "level": level}, mg.get("partner") or {}, n,
                     common.to_int(mg.get("spirits", 0), 0, 5, "spirits"), mg.get("savvy_pos", "avg"), mg.get("rank_pos", "avg"))
            basic = [PS.un(x) for x in r["basic"]]
            rank = PS.un(r["rank"])
            if merges + n > 65535:
                raise Bad("merges would pass 65,535 (the client field is 16 bits)")
            merges += n
            parts.append("%d merge(s): Basic +%s, rank +%s" % (n, PS.un(sum(r["basic"]) - sum(r["start_basic"])), PS.un(r["rank"] - r["start_rank"])))
        if rbt:
            n = common.to_int(rbt.get("rebirths", 1), 1, 100, "rebirths")
            if int(pet["soul_contract_stage"] or 0) < 1:
                raise Refused("rebirth needs a Soul Contract: set the contract stage (1-6) and save it first")
            r = _sim(PS.simulate_rebirths, c, {"completed": done, "growth": [str(s["base_growth_rate"]) for s in stats],
                                               "accel": [str(x) for x in accel], "soul_contract_stage": pet["soul_contract_stage"],
                                               "rebirths_remaining": left}, n, common.to_int(rbt.get("spirits", 0), 0, 5, "spirits"),
                     rbt.get("pos", "avg"), rbt.get("level") or 1)
            accel = [(D(x) / 100).quantize(D("0.01")) for x in r["accel"]]
            done += n
            if left < n:
                parts.append("%d extra rebirth attempt(s) assumed (waters used; attempts left 0)" % (n - left))
            left = max(0, left - n)
            level = r["level"]
            before = sum(PS.h(s["growth_acceleration"]) for s in stats)
            parts.append("%d rebirth(s): growth acceleration +%s in all, level %d" % (n, PS.un(sum(r["accel"]) - before), level))
        label["t"] = "; ".join(parts)
        stm = []
        for s, bv, av in zip(stats, basic, accel):
            added = (D(str(s["base_growth_rate"])) + av) * level
            stm.append("UPDATE character_pet_stat_values SET initial_savvy = %s, growth_acceleration = %s, added_savvy = %s, "
                       "revision = revision + 1 WHERE pet_id = %d AND stat_code = %d" % (lit(D(bv)), lit(av), lit(added), pid, int(s["stat_code"])))
        stm.append("UPDATE character_pets SET rank = %s, completed_pet_merges = %d, completed_rebirths = %d, rebirths_remaining = %d, "
                   "level = %d, revision = revision + 1, updated_at = now() WHERE id = %d AND user_id = %d"
                   % (lit(D(rank)), merges, done, left, level, pid, cid))
        return stm

    def lab(d):
        b(copy.deepcopy(d))
        return "pet %s: simulated %s" % (_pname(_pet_of(d, pid)), label.get("t", ""))
    r = _write_pet(ctx, cid, lab, b, body.get("revision"), pid)
    # EXP is kept (the rebirth carries the pool; docs/pet-rebirth-balance.md). Re-check the written pet in legal mode.
    try:
        d = rb.dump(cid)
        p = next((x for x in _pet_rows(rb, d, skill_library(rb), species_info(rb), aptitude_info(rb), content_steps(rb)) if int(x["id"]) == pid), None)
        warn = [x["msg"] for x in legal_report(rb, p)["checks"] if x["level"] != "ok"] if p else []
        if warn and isinstance(r, dict):
            r["note"] = "Saved. Legal pet checks on the result:\n- " + "\n- ".join(warn)
    except Exception:          # noqa: BLE001 - the write is done; the check is advice only
        pass
    return r


# ------------------------------------------------------------------------------------------------ writes: helpers
PET_FIELDS =["character_pets.*", "character_pet_stat_values.*", "character_pet_skills.*"]


def _rev_expect(cid, pid, rev):
    if rev in (None, ""):
        return None
    return [{"table": "character_pets", "where": {"id": int(pid), "user_id": int(cid)}, "values": {"revision": int(rev)}}]


def _pet_of(d, pid):
    for p in d["character_pets"]:
        if int(p["id"]) == int(pid):
            if p["activity_state"] != "owned" or any(s["pet_id"] == p["id"] for s in d["sealed_pet_items"]):
                raise Refused("pet #%s is %s: a packed (sealed), dispatched or working pet belongs to its item / job; the "
                              "server's seal, dispatch and work commands keep its evidence, so it is read-only here"
                              % (pid, p["activity_state"]))
            return p
    raise NotFound("pet #%s does not belong to this character" % pid)


def _force(ctx, body):
    f = bool(body.get("force"))
    if f and not ctx.rb.allow_force:
        raise Bad("'force' is disabled; start the editor with --allow-force to enable it")
    return f


def _canonical_guard(pid):
    """DO-block: after the statements, this pet's skills must be in the server's canonical form (module docstring)."""
    return ("DO $gwp$\nDECLARE bad text;\nBEGIN\n"
            "  SELECT format('pet #%%s: skill %%s sits in cell %%s, expected cell %%s (no gaps; the pet list throws at login)', "
            "s.pet_id, s.skill_id, s.slot_index + 1, s.n) INTO bad FROM (SELECT pet_id, skill_id, slot_index, "
            "row_number() OVER (ORDER BY slot_index) AS n FROM character_pet_skills WHERE pet_id = %d AND is_active) s "
            "WHERE s.slot_index <> s.n - 1 LIMIT 1;\n"
            "  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %%; nothing written', bad; END IF;\n"
            "  SELECT format('pet #%%s: skill %%s is inactive (the game never stores one; skill books would fail)', pet_id, skill_id) "
            "INTO bad FROM character_pet_skills WHERE pet_id = %d AND NOT is_active LIMIT 1;\n"
            "  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %%; nothing written', bad; END IF;\n"
            "END\n$gwp$" % (int(pid), int(pid)))


def _bump_pet(pid):
    return "UPDATE character_pets SET revision = revision + 1, updated_at = now() WHERE id = %d" % int(pid)


def _write_pet(ctx, cid, label, build, rev=None, pid=None, applies=None):
    return ctx.char_write(cid, label, build, fields=PET_FIELDS, expected=_rev_expect(cid, pid, rev) if pid else None,
                          applies=applies)


def _pname(p):
    return "%s (#%s)" % (p["name"], p["id"])


# ------------------------------------------------------------------------------------------------ writes: pets
@M.post(r"pet/(\d+)/add")
def pet_add(ctx, cid):
    """{species, aptitude, sex, level, name} -> the hatch insert (reborn.op_pet_add): published species, hatch-rank
    evidence, basic savvy in the aptitude's bracket, Weak growth, the species' starter skill in cell 1."""
    rb = ctx.need_rb()
    cid = int(cid)
    p = dict(ctx.body or {})
    sp = species_info(rb).get(common.to_int(p.get("species"), 1, 255, "species"))
    if not sp:
        raise Bad("species %s is not published in the server's pet content" % p.get("species"))
    if p.get("name") in (None, ""):
        p["name"] = sp["name"]
    b = R.op_pet_add(rb, cid, p)
    label = "add pet %s (%s), aptitude %s, level %s" % (p["name"], sp["name"], (aptitude_info(rb).get(int(p.get("aptitude", 1))) or {})
                                                        .get("name", p.get("aptitude")), p.get("level", 1))
    exp = None
    if ctx.body.get("shed") is not None:
        exp = [{"table": "character_base", "where": {"id": cid}, "values": {"pet_shed_capacity": common.to_int(ctx.body["shed"], 2, 8, "shed")}}]
    return ctx.char_write(cid, label, b, fields=PET_FIELDS, expected=exp)


@M.post(r"pet/(\d+)/delete")
def pet_delete(ctx, cid):
    """{pet, revision} -> delete one owned pet (its savvy and skill rows go with it)."""
    rb = ctx.need_rb()
    cid = int(cid)
    pid = common.to_int(ctx.body.get("pet"), 1, 2 ** 62, "pet")

    def b(d):
        _pet_of(d, pid)
        return R.op_pet_delete(rb, cid, {"pet": pid})(d)
    return _write_pet(ctx, cid, lambda d: "delete pet %s" % _pname(_pet_of(d, pid)), b, ctx.body.get("revision"), pid)


@M.post(r"pet/(\d+)/edit")
def pet_edit(ctx, cid):
    """{pet, revision, fields: {column: value, level, aptitude, savvy: {code: v}, growth: {code: v},
    presence: {carried, summoned, contributes}}} -> one transaction (reborn.op_pet_edit + wire checks)."""
    rb = ctx.need_rb()
    cid = int(cid)
    pid = common.to_int(ctx.body.get("pet"), 1, 2 ** 62, "pet")
    fields = ctx.body.get("fields") or {}
    if not isinstance(fields, dict):
        raise Bad("fields must be an object {column: value}")
    fields = dict(fields)
    if not fields:
        raise Bad("nothing to change")
    for k in ("savvy", "growth"):
        if k in fields:
            if not isinstance(fields[k], dict):
                raise Bad("%s must be an object {stat code 1-6: value}" % k)
            bad = [c for c in fields[k] if str(c) not in ("1", "2", "3", "4", "5", "6")]
            if bad:
                raise Bad("%s: unknown stat code %s (1 Agility, 2 Strength, 3 Accuracy, 4 Technique, 5 Wisdom, 6 Luck)" % (k, ", ".join(map(str, bad))))
            fields[k] = {str(c): v for c, v in fields[k].items()}
    if "presence" in fields and not isinstance(fields["presence"], dict):
        raise Bad("presence must be an object {carried, summoned, contributes}")
    if "bound" in fields:
        bv = fields["bound"]
        if not (isinstance(bv, bool) or bv in (0, 1) or str(bv).strip().lower() in ("0", "1", "true", "false", "yes", "no")):
            raise Bad("bound must be true or false (got %r)" % (bv,))
    for k in ("species_id", "talent_mask", "has_owner_merge_talent", "growth_revealed", "activity_state", "birth_rank",
              "hatch_rank_roll", "hatch_rank_outcome_order", "hatch_rank_content_revision", "has_soul_contract"):
        if k in fields:
            f = common.GLOSSARY.field("character_pets", k)
            raise Refused("%s cannot be edited: %s" % (f.get("label") or k, {
                "species_id": "the species decides the model, food and starter skill; delete the pet and add one of the new species",
                "talent_mask": "the innate talents follow the aptitude (database CHECK ck_character_pets_quality_innate_talents); change the aptitude",
                "has_owner_merge_talent": "it is bit 16 of the talent mask, which follows the aptitude",
                "growth_revealed": "owned by the server's Phoenix / rebirth paths",
                "activity_state": "packing, dispatch and work belong to the server's own commands",
                "has_soul_contract": "kept in step with the soul contract stage by a trigger; change the stage",
            }.get(k, "hatch evidence: immutable after the pet hatched (trigger guard_pet_hatch_rank_evidence)")))
    for k, (lo, hi, why) in WIRE.items():
        if k in fields:
            v = common.to_int(fields[k], None, None, k)
            if v < lo or v > hi:
                raise Bad("%s must be %s..%s%s" % (common.GLOSSARY.field("character_pets", k).get("label") or k,
                                                    format(lo, ","), format(hi, ","), (": " + why) if why else ""))
            fields[k] = v
    settings = rb.cat().get("pet_settings") or {}
    if "rank" in fields:
        mx = D(str(settings.get("maximum_rank") or "655.35"))
        try:
            rv = D(str(fields["rank"]).replace(",", ""))
        except Exception:      # noqa: BLE001
            raise Bad("rank must be a number with at most two decimals")
        if not rv.is_finite():
            raise Bad("rank must be a number with at most two decimals (got %r)" % (fields["rank"],))
        if rv < 0 or rv > mx or rv * 100 != (rv * 100).to_integral_value():
            raise Bad("rank must be 0..%s in hundredths (the client field is rank x 100 in 16 bits)" % mx)
    holder = {}

    def b(d):
        pet = _pet_of(d, pid)
        holder["pet"] = pet
        if R.to_bool((fields.get("presence") or {}).get("contributes")) and not pet["contributes_to_character"]:
            raise Refused(MERGE_REFUSED)
        cr = int(fields.get("completed_rebirths", pet["completed_rebirths"]))
        rr = int(fields.get("rebirths_remaining", pet["rebirths_remaining"]))
        if cr + rr > 255:
            raise Bad("rebirths done + rebirths left must be at most 255 together (the client keeps the total in one byte); "
                      "now %d + %d" % (cr, rr))
        stm = R.op_pet_edit(rb, cid, {"pet": pid, "fields": fields})(d)
        if stm and not any(re.search(r"UPDATE character_pets SET .*WHERE id = %d\b" % pid, s) for s in stm):
            stm.append(_bump_pet(pid))       # a savvy / growth change still moves the pet's revision (server CAS)
        return stm
    parts = []
    for k, v in fields.items():
        if k == "savvy":
            parts.append("basic savvy " + ", ".join("%s %s" % (STAT_NAMES.get(int(c), c), x) for c, x in sorted(v.items())))
        elif k == "growth":
            parts.append("growth " + ", ".join("%s %s" % (STAT_NAMES.get(int(c), c), x) for c, x in sorted(v.items())))
        elif k == "presence":
            flags = [n for n, key in (("carried", "carried"), ("summoned", "summoned"), ("merged into owner", "contributes"))
                     if R.to_bool((v or {}).get(key))]
            parts.append(", ".join(flags) if flags else "kept in the shed")
        elif k == "aptitude":
            parts.append("aptitude %s" % ((aptitude_info(rb).get(int(v)) or {}).get("name") or v))
        else:
            parts.append("%s %s" % (common.GLOSSARY.field("character_pets", k).get("label") or k, v))
    r = _write_pet(ctx, cid, lambda d: "pet %s: %s" % (_pname(_pet_of(d, pid)), "; ".join(parts)), b, ctx.body.get("revision"), pid)
    return r


@M.post(r"pet/(\d+)/skill")
def pet_skill(ctx, cid):
    """{pet, revision, action: "set" | "remove" | "repair", skill, cell, experience, force}
    set    - learn a skill (any id of a tier stores the tier's canonical id + rank); a skill of a family the pet already
             has replaces that one in its cell; otherwise it goes into cell <cell> (replacing what is there) or the
             next free cell.
    remove - forget the skill in <cell>; the later skills move down one cell (like the game's unlearn).
    (There is no "swap two cells": two rows trading cells cannot be undone - the generic Undo restores one row at a
    time and the unique (pet_id, slot_index) index is not deferrable - and the order of the cells has no effect.)
    repair - rewrite the pet's skills in canonical form (cells 1..n, active, tier ids and ranks, one per family)."""
    rb = ctx.need_rb()
    cid = int(cid)
    body = ctx.body or {}
    pid = common.to_int(body.get("pet"), 1, 2 ** 62, "pet")
    action = body.get("action")
    force = _force(ctx, body)
    lib = skill_library(rb)
    label = {}

    def ins(sid, slot, rank, exp, rev=0):
        return ("INSERT INTO character_pet_skills (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active, revision) "
                "VALUES (%d, %d, %d, %d, %d, true, %d)" % (pid, sid, slot, rank, exp, rev))

    def b(d):
        pet = _pet_of(d, pid)
        cur = sorted([s for s in d["character_pet_skills"] if s["pet_id"] == pid], key=lambda s: (s["slot_index"], s["skill_id"]))
        opened = int(pet["opened_skill_slots"])
        stm = []
        if action == "set":
            raw = common.to_int(body.get("skill"), 1, 65535, "skill id")
            e = lib["skills"].get(raw)
            if not e:
                if not force:
                    raise Bad("pet skill %d is not in the client's Pet_Skill.xml" % raw)
                e = {"id": raw, "head": raw, "tier": 1, "type": None, "name": "pet skill %d" % raw, "species": None, "published": False}
            sid, tier = int(e["head"]), int(e["tier"] or 1)
            if not e.get("published") and not force:
                raise Bad("%s (#%d) is not a published runtime pet skill of this server (no effect in game); tick force to add it anyway"
                          % (e["name"], sid))
            if e.get("species") and int(pet["species_id"]) not in e["species"] and not force:
                names = ", ".join(x["name"] for k, x in species_info(rb).items() if k in e["species"])
                raise Bad("%s is for %s only (%s); this pet is a %s. Tick force (needs --allow-force) to add it anyway" % (
                    e["name"], e["only"], names, (species_info(rb).get(int(pet["species_id"])) or {}).get("name")))
            exp = common.to_int(body.get("experience", 0), 0, I32, "skill EXP")
            same = next((s for s in cur if e.get("type") is not None and (lib["skills"].get(int(s["skill_id"])) or {}).get("type") == e["type"]), None)
            cell = body.get("cell")
            cell = None if cell in (None, "") else common.to_int(cell, 0, 11, "cell")
            if same and int(same["skill_id"]) == sid:
                if int(same["skill_rank"]) == tier and int(same["skill_experience"]) == exp:
                    raise Bad("the pet already has %s in cell %d" % (e["name"], same["slot_index"] + 1))
                stm.append("UPDATE character_pet_skills SET skill_rank = %d, skill_experience = %d, is_active = true, revision = revision + 1 "
                           "WHERE pet_id = %d AND skill_id = %d" % (tier, exp, pid, sid))
                label["t"] = "set %s (#%d) EXP %d in cell %d" % (e["name"], sid, exp, same["slot_index"] + 1)
            elif same:          # another tier of the same family: replace it in its cell (the game's upgrade)
                stm.append("DELETE FROM character_pet_skills WHERE pet_id = %d AND skill_id = %d" % (pid, same["skill_id"]))
                stm.append(ins(sid, same["slot_index"], tier, exp, int(same["revision"]) + 1))
                label["t"] = "%s (#%s) -> %s (#%d) in cell %d (same family: one of each type)" % (
                    (lib["skills"].get(int(same["skill_id"])) or {}).get("name"), same["skill_id"], e["name"], sid, same["slot_index"] + 1)
            elif cell is not None and any(s["slot_index"] == cell for s in cur):
                old = next(s for s in cur if s["slot_index"] == cell)
                stm.append("DELETE FROM character_pet_skills WHERE pet_id = %d AND skill_id = %d" % (pid, old["skill_id"]))
                stm.append(ins(sid, cell, tier, exp, int(old["revision"]) + 1))
                label["t"] = "replace %s (#%s) with %s (#%d) in cell %d" % (
                    (lib["skills"].get(int(old["skill_id"])) or {}).get("name"), old["skill_id"], e["name"], sid, cell + 1)
            else:
                slot = len(cur)
                if slot >= opened:
                    raise Bad("all %d opened skill cells are used; open another cell first (Golden Apple Juice in game, or raise "
                              "'Opened cells' here; at most 'Available cells')" % opened)
                stm.append(ins(sid, slot, tier, exp))
                label["t"] = "learn %s (#%d) in cell %d" % (e["name"], sid, slot + 1)
        elif action == "remove":
            cell = common.to_int(body.get("cell"), 0, 11, "cell")
            old = next((s for s in cur if s["slot_index"] == cell), None)
            if not old:
                raise Bad("cell %d is empty" % (cell + 1))
            stm.append("DELETE FROM character_pet_skills WHERE pet_id = %d AND skill_id = %d" % (pid, old["skill_id"]))
            rest = [s for s in cur if s is not old]
            for i, s in enumerate(rest):          # ascending: each target cell is already free
                if s["slot_index"] != i:
                    stm.append("UPDATE character_pet_skills SET slot_index = %d, revision = revision + 1 WHERE pet_id = %d AND skill_id = %d"
                               % (i, pid, s["skill_id"]))
            label["t"] = "forget %s (#%s) from cell %d%s" % ((lib["skills"].get(int(old["skill_id"])) or {}).get("name"), old["skill_id"],
                                                            cell + 1, "; later skills move down one cell" if any(s["slot_index"] > cell for s in cur) else "")
        elif action == "repair":
            keep, seen = [], set()
            for s in sorted(cur, key=lambda s: (not s["is_active"], s["slot_index"])):
                e = lib["skills"].get(int(s["skill_id"])) or {}
                t = e.get("type")
                if t is not None and t in seen:
                    continue
                if t is not None:
                    seen.add(t)
                keep.append((int(e.get("head") or s["skill_id"]), int(e.get("tier") or s["skill_rank"] or 1), s))
            keep = keep[:opened]
            stm.append("DELETE FROM character_pet_skills WHERE pet_id = %d" % pid)
            for i, (sid, tier, s) in enumerate(keep):
                stm.append(ins(sid, i, tier, int(s["skill_experience"] or 0), int(s["revision"]) + 1))
            label["t"] = "repair skill cells: %d skill(s) in cells 1..%d, canonical ids and ranks" % (len(keep), len(keep))
        else:
            raise Bad("action must be set, remove or repair")
        stm.append(_bump_pet(pid))
        stm.append(_canonical_guard(pid))
        return stm

    def lab(d):
        b(copy.deepcopy(d))              # fills label["t"] (and raises the same errors as the write)
        return "pet %s: %s" % (_pname(_pet_of(d, pid)), label.get("t", action))
    return _write_pet(ctx, cid, lab, b, body.get("revision"), pid)


@M.post(r"shed/(\d+)")
def shed(ctx, cid):
    """{capacity, expected} -> character_base.pet_shed_capacity through the shared save path (live or database)."""
    ctx.need_rb()
    n = common.to_int(ctx.body.get("capacity"), 2, 8, "pet shed cells")
    exp = ctx.body.get("expected")
    return ctx.set_character_fields(int(cid), {"pet_shed_capacity": n},
                                    expected={"pet_shed_capacity": int(exp)} if exp not in (None, "") else None,
                                    label="pet shed cells %s -> %d" % (exp if exp not in (None, "") else "?", n))


# ------------------------------------------------------------------------------------------------ writes: mounts
def _mount_meta(rb, pid, force):
    m = rb.item_meta(pid)
    if m["kind"] not in MOUNT_KINDS and not force:
        raise Bad("%s (#%d) is a '%s', not a mount or mount gear" % (m["name"], pid, m["kind"] or "unknown"))
    if not m["server"] and not force:
        raise Bad("item %d is not an item template of this server (it would not load)" % pid)
    return m


def _row_of(d, row_id):
    for it in d["character_items"]:
        if int(it["id"]) == int(row_id):
            return it
    raise NotFound("item row #%s does not belong to this character" % row_id)


def _item_expect(row, exp):
    if not exp:
        return None
    vals = {k: exp[k] for k in ("prop_id", "item_location", "slot_index", "item_quality", "item_grade", "bound") if k in exp}
    return [{"table": "character_items", "where": {"id": int(row)}, "values": vals}]


def _free_bag(d, n=1):
    used = {r["slot_index"] for r in d["character_items"] if r["item_location"] == R.LOC_BAG}
    free = [s for s in range(common.BAG_SLOTS) if s not in used]
    if len(free) < n:
        raise Bad("the bag has no free cell (%d of %d used); free one first" % (len(used), common.BAG_SLOTS))
    return free[:n]


@M.post(r"mount/(\d+)/add")
def mount_add(ctx, cid):
    """{item, quality, grade, bound, to: "bag" | "ride" | "gear"} -> a new mount / gear item.
    bag: the shared live-or-database bag path (the running round-2 server gives it to an online player at once);
    ride / gear: equipment slot 20 / 15-19 (database, the character must be logged out). Riding: the old mount
    moves to the first free bag cell."""
    rb = ctx.need_rb()
    cid = int(cid)
    body = ctx.body or {}
    force = _force(ctx, body)
    pid = common.to_int(body.get("item"), 1, I32, "item")
    m = _mount_meta(rb, pid, force)
    q = common.to_int(body.get("quality", 1), 0, 20, "quality")
    g = common.to_int(body.get("grade", 1), 0, 25, "grade")
    bound = common.to_int(body.get("bound", 0), 0, 1, "bound")
    to = body.get("to", "bag")
    if to == "bag":
        if common.online_info(ctx.app, cid)["online"]:
            # the shared live-or-database path: the running round-2 server gives it to the player at once
            return ctx.bag_item(cid, "add", {"item": pid, "quantity": 1, "quality": q, "grade": g, "bound": bound})
        # offline: our own guarded write. (common.Ctx.bag_item's offline "add" appends a placeholder row to the dump it
        # is given, which is also the change record's before-image, so its Undo fails - reported to the framework.)
        item = {"prop_id": pid, "stack": 1, "item_quality": q, "item_grade": g, "bound": bound}

        def bb(d):
            return R.op_item(rb, cid, {"location": R.LOC_BAG, "slot": _free_bag(d)[0], "item": item, "force": force})(d)
        r = ctx.char_write(cid, "add %s (#%d) x1 Q%d G%d%s to the bag" % (m["name"], pid, q, g, " bound" if bound else ""), bb,
                           fields=["character_items.*"])
        r["mode"] = "database"
        return r
    slot = MOUNT_KINDS.get(m["kind"], R.MOUNT_SLOT)
    if to == "ride" and slot != R.MOUNT_SLOT:
        raise Bad("%s is mount gear (%s); it goes in a gear slot, not the riding slot" % (m["name"], GEAR_NAMES.get(slot)))
    if to == "gear" and slot == R.MOUNT_SLOT:
        raise Bad("%s is a mount; use 'riding slot' or 'bag'" % m["name"])
    if to not in ("ride", "gear"):
        raise Bad("to must be bag, ride or gear")
    item = {"prop_id": pid, "stack": 1, "item_quality": q, "item_grade": g, "bound": bound}

    def b(d):
        stm = []
        d2 = copy.deepcopy(d)
        old = R.find_item(d2, R.LOC_EQUIP, slot)
        if old:                  # the mount / gear piece there now goes to the first free bag cell
            free = _free_bag(d2)[0]
            stm.append("UPDATE character_items SET item_location = %d, slot_index = %d, updated_at = now() WHERE id = %d"
                       % (R.LOC_BAG, free, old["id"]))
            stm.append(R._audit(cid, "move", R.LOC_BAG, free, old, old))
            old["item_location"], old["slot_index"] = R.LOC_BAG, free
        stm += R.op_item(rb, cid, {"location": R.LOC_EQUIP, "slot": slot, "item": item, "force": force})(d2)
        if slot == R.MOUNT_SLOT:
            for gs in R.MOUNT_GEAR_SLOTS:
                gr = R.find_item(d2, R.LOC_EQUIP, gs)
                if gr and (rb.item_meta(gr["prop_id"])["minlv"] or 0) > (m["minlv"] or 0) and not force:
                    raise Bad("the equipped %s needs a mount of level %s or more; %s is level %s (unequip the gear first)" % (
                        GEAR_NAMES[gs], rb.item_meta(gr["prop_id"])["minlv"], m["name"], m["minlv"]))
        return stm
    where = "the riding slot" if slot == R.MOUNT_SLOT else "the %s slot" % GEAR_NAMES[slot]
    r = ctx.char_write(cid, "put a new %s (#%d) Q%d G%d%s in %s" % (m["name"], pid, q, g, " bound" if bound else "", where), b,
                       fields=["character_items.*"])
    r["mode"] = "database"
    return r


@M.post(r"mount/(\d+)/ride")
def mount_ride(ctx, cid):
    """{row, expected} -> make this owned mount the active one (it moves into slot 20; the current one takes its place)."""
    rb = ctx.need_rb()
    cid = int(cid)
    row = common.to_int(ctx.body.get("row"), 1, 2 ** 62, "item row")
    force = _force(ctx, ctx.body or {})

    def b(d):
        it = _row_of(d, row)
        if rb.item_meta(it["prop_id"])["kind"] != "mount":
            raise Bad("that item is not a mount")
        if it["item_location"] == R.LOC_EQUIP and it["slot_index"] == R.MOUNT_SLOT:
            raise Bad("it is already the active mount")
        return R.op_move(rb, cid, {"from": {"location": it["item_location"], "slot": it["slot_index"]},
                                   "to": {"location": R.LOC_EQUIP, "slot": R.MOUNT_SLOT}, "force": force})(d)

    def lab(d):
        it = _row_of(d, row)
        cur = R.find_item(d, R.LOC_EQUIP, R.MOUNT_SLOT)
        return "ride %s (#%s) from %s%s" % (rb.item_meta(it["prop_id"])["name"], it["prop_id"], R.slot_label(it["item_location"], it["slot_index"]),
                                            " (the old %s goes there)" % rb.item_meta(cur["prop_id"])["name"] if cur else "")
    r = ctx.char_write(cid, lab, b, fields=["character_items.*"], expected=_item_expect(row, ctx.body.get("expected")))
    r["mode"] = "database"
    return r


@M.post(r"mount/(\d+)/unequip")
def mount_unequip(ctx, cid):
    """{row, expected} -> move an equipped mount / gear piece to the first free bag cell (the mount only when no gear
    is equipped: EquipmentEligibility)."""
    rb = ctx.need_rb()
    cid = int(cid)
    row = common.to_int(ctx.body.get("row"), 1, 2 ** 62, "item row")

    def b(d):
        it = _row_of(d, row)
        if rb.item_meta(it["prop_id"])["kind"] not in MOUNT_KINDS:
            raise Bad("that item is not a mount or mount gear (this page only moves mounts; use the Inventory page)")
        if it["item_location"] != R.LOC_EQUIP:
            raise Bad("that item is not equipped")
        free = _free_bag(d)[0]
        return R.op_move(rb, cid, {"from": {"location": R.LOC_EQUIP, "slot": it["slot_index"]}, "to": {"location": R.LOC_BAG, "slot": free}})(d)

    def lab(d):
        it = _row_of(d, row)
        return "unequip %s (#%s) from %s to the bag" % (rb.item_meta(it["prop_id"])["name"], it["prop_id"], R.slot_label(it["item_location"], it["slot_index"]))
    r = ctx.char_write(cid, lab, b, fields=["character_items.*"], expected=_item_expect(row, ctx.body.get("expected")))
    r["mode"] = "database"
    return r


@M.post(r"mount/(\d+)/set")
def mount_set(ctx, cid):
    """{row, quality, grade, bound, expected} -> change quality / grade / binding of one mount or gear item (every
    other column of the row is kept)."""
    rb = ctx.need_rb()
    cid = int(cid)
    body = ctx.body or {}
    row = common.to_int(body.get("row"), 1, 2 ** 62, "item row")
    q = common.to_int(body.get("quality"), 0, 20, "quality") if body.get("quality") not in (None, "") else None
    g = common.to_int(body.get("grade"), 0, 25, "grade") if body.get("grade") not in (None, "") else None
    bd = common.to_int(body.get("bound"), 0, 1, "bound") if body.get("bound") not in (None, "") else None
    if q is None and g is None and bd is None:
        raise Bad("nothing to change")

    def full(it):
        p = {k: it.get(k) for k in R.ITEM_FIELDS}
        if q is not None:
            p["item_quality"] = q
        if g is not None:
            p["item_grade"] = g
        if bd is not None:
            p["bound"] = bd
        return p

    def b(d):
        it = _row_of(d, row)
        if rb.item_meta(it["prop_id"])["kind"] not in MOUNT_KINDS:
            raise Bad("that item is not a mount or mount gear")
        return R.op_item(rb, cid, {"location": it["item_location"], "slot": it["slot_index"], "item": full(it)})(d)

    def lab(d):
        it = _row_of(d, row)
        ch = []
        if q is not None and q != it["item_quality"]:
            ch.append("quality Q%s -> Q%d" % (it["item_quality"], q))
        if g is not None and g != it["item_grade"]:
            ch.append("grade G%s -> G%d" % (it["item_grade"], g))
        if bd is not None and bd != it["bound"]:
            ch.append("bound %s -> %d" % (it["bound"], bd))
        return "%s (#%s) in %s: %s" % (rb.item_meta(it["prop_id"])["name"], it["prop_id"], R.slot_label(it["item_location"], it["slot_index"]),
                                       ", ".join(ch) or "no change")
    r = ctx.char_write(cid, lab, b, fields=["character_items.*"], expected=_item_expect(row, body.get("expected")))
    r["mode"] = "database"
    return r


@M.post(r"mount/(\d+)/delete")
def mount_delete(ctx, cid):
    """{row, expected} -> delete a mount / gear item. In the kit bag: the shared live-or-database path (an online
    player on a round-2 server sees it vanish at once); elsewhere: database (logged out)."""
    rb = ctx.need_rb()
    cid = int(cid)
    row = common.to_int(ctx.body.get("row"), 1, 2 ** 62, "item row")
    d = rb.dump(cid)
    it = _row_of(d, row)
    exp = ctx.body.get("expected") or {}
    if exp and any(str(it.get(k)) != str(v) for k, v in exp.items() if k in it):
        raise Conflict("character_items [%d] changed since you opened it" % row,
                       [{"table": "character_items", "row": "[%d]" % row, "what": "%s: %s -> %s" % (k, v, it.get(k))}
                        for k, v in exp.items() if k in it and str(it.get(k)) != str(v)])
    if rb.item_meta(it["prop_id"])["kind"] not in MOUNT_KINDS:
        raise Bad("that item is not a mount or mount gear")
    if it["item_location"] == R.LOC_BAG:
        return ctx.bag_item(cid, "remove", {"slot": it["slot_index"]})
    if it["item_location"] == R.LOC_EQUIP and it["slot_index"] == R.MOUNT_SLOT and any(R.find_item(d, R.LOC_EQUIP, s) for s in R.MOUNT_GEAR_SLOTS):
        raise Bad("remove the mount gear first: gear needs a mount (EquipmentEligibility)")

    def b(d2):
        it2 = _row_of(d2, row)
        return R.op_item(rb, cid, {"location": it2["item_location"], "slot": it2["slot_index"], "item": None})(d2)
    r = ctx.char_write(cid, "delete %s (#%s) from %s" % (rb.item_meta(it["prop_id"])["name"], it["prop_id"],
                                                          R.slot_label(it["item_location"], it["slot_index"])), b,
                       fields=["character_items.*"], expected=_item_expect(row, exp))
    r["mode"] = "database"
    return r
