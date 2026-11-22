"""modules/_character_identity.py - W4A: change a character's faction, gender or class the way the game itself does it,
the legal hair / face values, and the legal limits of the Character page's number fields. Pure helpers (SQL text +
rules) for modules/character.py; unit test: tests/test_character_identity_unit.py.

Sources (server/src/Godswar.Server, "S/"):
  * faction: S/Infrastructure/Characters/PostgresCharacterIdentityStore.cs ChangeFactionAsync - refused in a guild
    (consortia <> 0 or a guild_members row), camp 0 <-> 1, moved to the new capital (map = camp, 165, -97,
    position_revision + 1), the camp's "return to capital / suburb" portal skills swapped
    (S/Domain/Characters/FactionPortalSkillPolicy.cs: Athens 3060 / 3061, Sparta 3062 / 3063).
    The editor charges no Gold and writes no operator_log 'faction-change' row, so the player's in-game Faction Changer
    cooldown does not start.
  * gender: ChangeGenderAsync - refused while fashion (slot 12) or a one-gender model item is worn (the client cannot
    draw it), hair kept when the new gender has the model, else hair % 10 (HairFor).
  * class: S/Infrastructure/Characters/PostgresCharacterClassChangeStore.cs ChangeClassAsync - refused while anything
    is worn in slots 0..14; the old classes' talents go to rank 0 and become talent points (the editor uses today's
    price, ClassChangePolicy.TalentRefund; the server prefers the price paid when its receipts prove it); the skills
    the new class cannot use move to character_sealed_skills, the new class's sealed ones come back, its start skills
    are granted; attack zodiac grid selections of another class are cleared; a character_class_change_receipts row is
    written (it is also the talent command epoch, ClassChangeTalentEpoch.cs - without it a rebuy of rank 1 would be
    answered as a duplicate). No Class Shift Permit is used.
  * hair / face: the client's models, Characters/<gender>_hair_NNNN.gwo and <gender>_head_000N.gwo (Original set,
    config.ini AVATAR=1) and Characters_New (the "new models" set). Hair number = style x 10 + colour.
"""
from decimal import Decimal, ROUND_DOWN

from .common import Bad

CLASSES = {0: "Warrior", 1: "Champion", 2: "Priest", 3: "Mage"}
CAMPS = {0: "Sparta", 1: "Athens"}
CAPITAL_X, CAPITAL_Z = 165.0, -97.0
PORTALS = {1: (3060, 3061), 0: (3062, 3063)}          # camp -> (capital, suburb) portal skill
STYLISH_SLOT = 12
LAST_CHARACTER_SLOT = 14
FEMALE_ONLY_MODEL_ITEMS = [2190, 2590, 2690, 2790, 2990, 3301, 3302, 3303]
MALE_ONLY_MODEL_ITEMS = [1100, 1102, 1103, 1104, 1105, 1106, 1107]
MALE_ONLY_HAIR = {230, 231, 232, 233, 234, 250, 251, 252, 253, 254}
FEMALE_ONLY_HAIR = {180, 181, 182, 183, 184, 210, 211, 212, 213, 214, 240, 241, 242, 243, 244, 260, 261, 262, 263, 264}


def _styles(*s):
    return [st * 10 + c for st in s for c in range(5)]


# sorted model ids = the tiles of static/avatar-previews/<set>_<gender>_hair.png (5 per row, row = style)
HAIR = {
    "old": {"male": _styles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 19, 23, 25),
            "female": _styles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 18, 19, 21, 24, 26)},
    "new": {"male": _styles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 15, 17, 19, 20, 22, 23, 25),
            "female": _styles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 14, 15, 17, 18, 19, 21, 22, 24, 26)},
}
FACES = [0, 1, 2, 3, 4]
# preview sheet geometry (static/avatar-previews/*.png, measured 2026-11-12): 128 px tiles
SHEET = {"x0": 4, "y0": 22, "dx": 132, "dy": 146, "tile": 128, "cols": 5}

# Zodiac attack-grid skill kinds and their class (S/State/ZodiacSkillGridSelection.cs SkillClasses, MagicMini.xml)
ZODIAC_SKILL_CLASS = {20010: 0, 20001: 0, 20002: 0, 10003: 0, 10004: 0, 10005: 0, 10006: 0,
                      10025: 1, 10026: 1, 10027: 1, 20028: 1, 20029: 1, 10030: 1, 10031: 1, 20032: 1, 20033: 1,
                      10080: 2, 10081: 2, 20082: 2, 20083: 2, 20084: 2, 10085: 2, 10086: 2, 10075: 2, 10076: 2,
                      10050: 3, 10051: 3, 10052: 3, 20053: 3, 20054: 3, 20055: 3, 20056: 3, 10057: 3, 20058: 3}
ZODIAC_GRIDS = 16                                       # grids >= 8 are defence grids (kept for every class)
TALENT_RANK_CAP = 100                                   # S/Application/Talents/TalentProgression.cs

GAMEPLAY_REVISION = ("(SELECT publication.revision FROM public.gameplay_content_publication publication "
                     "WHERE publication.family = 'gameplay')")


# --------------------------------------------------------------------------- hair / face
def hair_sets(gender):
    g = "female" if str(gender) == "female" else "male"
    return {"old": HAIR["old"][g], "new": HAIR["new"][g]}


def check_hair(gender, hair, allow_new=False):
    """Legal hair for this gender: a model in the shipped (Original) set; the 'new models' set only with allow_new."""
    sets = hair_sets(gender)
    h = int(hair)
    if h in sets["old"]:
        return h
    if h in sets["new"]:
        if allow_new:
            return h
        raise Bad("hair %d exists only in the client's 'new models' set (Characters_New); the shipped client draws the "
                  "Original set (config.ini AVATAR=1), so a %s with hair %d may not be drawn. Pick one of the Original "
                  "styles." % (h, gender, h))
    raise Bad("hair %d has no %s model in the client (Characters/%s_hair_%04d.gwo is missing)" % (h, gender, gender, h))


def check_face(face):
    f = int(face)
    if f not in FACES:
        raise Bad("face must be 0..4 (the client ships 5 faces per gender, Characters/<gender>_head_000N.gwo)")
    return f


def hair_for(hair, to_male):
    """PostgresCharacterIdentityStore.HairFor: keep the hair when the new gender has it, else hair % 10."""
    hair = int(hair)
    without = FEMALE_ONLY_HAIR if to_male else MALE_ONLY_HAIR
    return hair % 10 if hair in without else hair


# --------------------------------------------------------------------------- gender
def gender_worn_conflicts(items, to_male):
    """Worn rows that stop a gender change (fashion, or a model the new gender does not have)."""
    bad_ids = set(FEMALE_ONLY_MODEL_ITEMS if to_male else MALE_ONLY_MODEL_ITEMS)
    out = []
    for r in items or []:
        if int(r.get("item_location", -1)) != 0:
            continue
        if int(r.get("slot_index", -1)) == STYLISH_SLOT or int(r.get("prop_id", 0)) in bad_ids:
            out.append(r)
    return out


def gender_sql(cid, current, target, hair):
    cid = int(cid)
    if target not in ("male", "female") or current not in ("male", "female"):
        raise Bad("gender must be male or female")
    if target == current:
        raise Bad("the character is %s already" % target)
    new_hair = hair_for(hair, target == "male")
    return new_hair, [
        "UPDATE character_base SET gender = '%s', hair_style = %d WHERE id = %d AND gender = '%s'" % (target, new_hair, cid, current),
        "DO $gwg$ BEGIN IF NOT EXISTS (SELECT 1 FROM character_base WHERE id = %d AND gender = '%s') THEN RAISE EXCEPTION "
        "'gwadmin: the gender changed meanwhile; nothing written'; END IF; IF EXISTS (SELECT 1 FROM character_items WHERE "
        "user_id = %d AND item_location = 0 AND (slot_index = %d OR prop_id = ANY(ARRAY[%s]))) THEN RAISE EXCEPTION "
        "'gwadmin: fashion or a one-gender model is worn; take it off first; nothing written'; END IF; END $gwg$" % (
            cid, target, cid, STYLISH_SLOT, ",".join(str(x) for x in (FEMALE_ONLY_MODEL_ITEMS if target == "male" else MALE_ONLY_MODEL_ITEMS))),
    ]


# --------------------------------------------------------------------------- faction
def faction_sql(cid, old_camp):
    cid, old = int(cid), int(old_camp)
    if old not in CAMPS:
        raise Bad("camp %s is not Sparta (0) or Athens (1)" % old)
    new = 1 - old
    oc, os_ = PORTALS[old]
    nc, ns = PORTALS[new]
    return new, [
        "DO $gwf$ BEGIN IF NOT EXISTS (SELECT 1 FROM character_base WHERE id = %d AND camp = %d) THEN RAISE EXCEPTION "
        "'gwadmin: the faction changed meanwhile; nothing written'; END IF; IF EXISTS (SELECT 1 FROM character_base WHERE "
        "id = %d AND consortia <> 0) OR EXISTS (SELECT 1 FROM guild_members WHERE character_id = %d) THEN RAISE EXCEPTION "
        "'gwadmin: the character is in a guild; it must leave it first (the game refuses a faction change in a guild); "
        "nothing written'; END IF; END $gwf$" % (cid, old, cid, cid),
        "UPDATE character_base SET camp = %d, \"Map\" = %d, \"Pos_X\" = %s, \"Pos_Z\" = %s, position_revision = "
        "position_revision + 1 WHERE id = %d AND camp = %d" % (new, new, CAPITAL_X, CAPITAL_Z, cid, old),
        "UPDATE character_skills s SET skill_id = CASE s.skill_id WHEN %d THEN %d ELSE %d END, source = 'faction-change' "
        "WHERE s.user_id = %d AND s.skill_id IN (%d, %d) AND NOT EXISTS (SELECT 1 FROM character_skills t WHERE "
        "t.user_id = %d AND t.skill_id = CASE s.skill_id WHEN %d THEN %d ELSE %d END)" % (oc, nc, ns, cid, oc, os_, cid, oc, nc, ns),
        "DELETE FROM character_skills WHERE user_id = %d AND skill_id IN (%d, %d)" % (cid, oc, os_),
    ]


# --------------------------------------------------------------------------- class
def talent_stock_cost(current_rank):
    """TalentProgression.CalculateStockUpgradeCost: points to go from current_rank to current_rank + 1."""
    n = int(current_rank) + 1
    tri = lambda k: k * (k + 1) // 2  # noqa: E731
    if n > TALENT_RANK_CAP or n < 1:
        return 0
    if n <= 10:
        return n
    if n <= 40:
        o = n - 11
        return 10 + o * 6 + tri(o)
    if n <= 60:
        o = n - 41
        return 380 + o * 18 + tri(o)
    if n <= 80:
        o = n - 61
        return 900 + o * 32 + tri(o) * 2
    if n <= 90:
        o = n - 81
        return 1900 + o * 65 + tri(o) * 5
    o = n - 91
    return 2900 + o * 90 + tri(o) * 7


def talent_cost(current_rank, rate):
    """TalentProgression.CalculateUpgradeCost: ServerRates.Multiply(cost.talent, stock) - truncated, int-capped."""
    v = talent_stock_cost(current_rank)
    r = Decimal(str(rate))
    if r == 1 or v <= 0:
        return v
    s = int((Decimal(v) * r).to_integral_value(rounding=ROUND_DOWN))
    return max(0, min(s, 2 ** 31 - 1))


def talent_refunds(rate):
    """cum[r] = points a talent at rank r is worth at today's price (ClassChangePolicy.TalentRefund), r = 0..100."""
    cum = [0]
    for r in range(TALENT_RANK_CAP):
        cum.append(cum[-1] + talent_cost(r, rate))
    return cum


def zodiac_to_clear(grids, target):
    """Attack-grid selections (grid < 8) whose skill kind belongs to another class (ClearAttackZodiacSelectionsAsync)."""
    out = []
    for g in grids or []:
        gi, sk = int(g.get("grid_index", -1)), int(g.get("selected_skill_id", -1) if g.get("selected_skill_id") is not None else -1)
        if sk == -1 or not 0 <= gi < ZODIAC_GRIDS or gi >= ZODIAC_GRIDS // 2:
            continue
        if ZODIAC_SKILL_CLASS.get(sk) != int(target):
            out.append({"grid": gi, "skill": sk})
    return out


def class_sql(cid, aid, old, target, level, rate, zodiac_clear):
    """One block of statements = PostgresCharacterClassChangeStore.ChangeClassAsync without the permit and the price."""
    cid, aid, old, target, level = int(cid), int(aid), int(old), int(target), int(level)
    if old not in CLASSES or target not in CLASSES:
        raise Bad("class must be 0 Warrior, 1 Champion, 2 Priest or 3 Mage")
    if old == target:
        raise Bad("the character is a %s already" % CLASSES[target])
    cum = talent_refunds(rate)
    arr = "ARRAY[%s]::bigint[]" % ",".join(str(x) for x in cum)
    grids = [int(z["grid"]) for z in zodiac_clear]
    zjson = "'[%s]'::jsonb" % ",".join('{"grid":%d,"skill":%d}' % (z["grid"], z["skill"]) for z in zodiac_clear)
    return ("DO $gwc$\n"
            "DECLARE rev text; pts integer; refund bigint; tal jsonb; sealed jsonb; unsealed jsonb; granted jsonb; cum bigint[] := %(arr)s;\n"
            "BEGIN\n"
            "  SELECT \"SkillPoint\" INTO pts FROM character_base WHERE id = %(cid)d AND account_id = %(aid)d AND profession = %(old)d\n"
            "    AND lifecycle_state = 'active' FOR UPDATE;\n"
            "  IF NOT FOUND THEN RAISE EXCEPTION 'gwadmin: the class changed meanwhile; nothing written'; END IF;\n"
            "  IF EXISTS (SELECT 1 FROM character_items WHERE user_id = %(cid)d AND item_location = 0 AND slot_index <= %(last)d) THEN\n"
            "    RAISE EXCEPTION 'gwadmin: equipment is worn (slots 0-%(last)d); the game requires nothing worn for a class change; nothing written';\n"
            "  END IF;\n"
            "  rev := %(rev)s;\n"
            "  IF rev IS NULL THEN RAISE EXCEPTION 'gwadmin: no published gameplay content revision; nothing written'; END IF;\n"
            "  SELECT coalesce(sum(cum[ct.rank + 1]), 0), coalesce(jsonb_agg(jsonb_build_object('talent', ct.talent_id, 'rank', ct.rank,\n"
            "         'refund', cum[ct.rank + 1], 'basis', 'current-price') ORDER BY ct.talent_id), '[]')\n"
            "    INTO refund, tal FROM character_talents ct JOIN gameplay_talent_definitions tt ON tt.id = ct.talent_id AND tt.revision = rev\n"
            "    WHERE ct.user_id = %(cid)d AND tt.class_id <> %(target)d AND ct.rank > 0;\n"
            "  UPDATE character_talents ct SET rank = 0, updated_at = now() FROM gameplay_talent_definitions tt\n"
            "    WHERE ct.user_id = %(cid)d AND tt.id = ct.talent_id AND tt.revision = rev AND tt.class_id <> %(target)d AND ct.rank > 0;\n"
            "  WITH moved AS (DELETE FROM character_skills cs USING gameplay_skill_combat_definitions d WHERE cs.user_id = %(cid)d\n"
            "      AND d.skill_id = cs.skill_id AND d.revision = rev AND NOT (%(target)d = ANY(d.class_ids))\n"
            "      RETURNING cs.user_id, cs.skill_id, cs.skill_level, cs.acquired_at, cs.source),\n"
            "    kept AS (INSERT INTO character_sealed_skills (user_id, skill_id, skill_level, acquired_at, source)\n"
            "      SELECT user_id, skill_id, skill_level, acquired_at, source FROM moved ON CONFLICT (user_id, skill_id) DO UPDATE\n"
            "      SET skill_level = EXCLUDED.skill_level, acquired_at = EXCLUDED.acquired_at, source = EXCLUDED.source, sealed_at = now()\n"
            "      RETURNING skill_id)\n"
            "    SELECT coalesce(jsonb_agg(skill_id ORDER BY skill_id), '[]') INTO sealed FROM kept;\n"
            "  WITH back AS (DELETE FROM character_sealed_skills s USING gameplay_skill_combat_definitions d WHERE s.user_id = %(cid)d\n"
            "      AND d.skill_id = s.skill_id AND d.revision = rev AND %(target)d = ANY(d.class_ids)\n"
            "      RETURNING s.user_id, s.skill_id, s.skill_level, s.acquired_at, s.source),\n"
            "    restored AS (INSERT INTO character_skills (user_id, skill_id, skill_level, acquired_at, source)\n"
            "      SELECT user_id, skill_id, skill_level, acquired_at, source FROM back ON CONFLICT (user_id, skill_id) DO UPDATE\n"
            "      SET skill_level = GREATEST(character_skills.skill_level, EXCLUDED.skill_level) RETURNING skill_id)\n"
            "    SELECT coalesce(jsonb_agg(skill_id ORDER BY skill_id), '[]') INTO unsealed FROM restored;\n"
            "  WITH g AS (INSERT INTO character_skills (user_id, skill_id, skill_level, source)\n"
            "      SELECT %(cid)d, st.skill_id, COALESCE(st.skill_level, 1), 'class-change' FROM gameplay_skill_combat_definitions st\n"
            "      WHERE %(target)d = ANY(st.class_ids) AND st.revision = rev\n"
            "        AND ((st.previous_skill_id IS NULL AND COALESCE(st.min_level, 1) <= 1 AND st.skill_level = 1) OR st.skill_id = 4904)\n"
            "        AND NOT EXISTS (SELECT 1 FROM character_skills existing JOIN gameplay_skill_combat_definitions et\n"
            "          ON et.skill_id = existing.skill_id AND et.revision = st.revision WHERE existing.user_id = %(cid)d\n"
            "          AND et.base_name = st.base_name AND COALESCE(et.skill_level, 0) > COALESCE(st.skill_level, 0))\n"
            "      ON CONFLICT (user_id, skill_id) DO NOTHING RETURNING skill_id)\n"
            "    SELECT coalesce(jsonb_agg(skill_id ORDER BY skill_id), '[]') INTO granted FROM g;\n"
            "%(zclear)s"
            "  UPDATE character_base SET profession = %(target)d, \"SkillPoint\" = LEAST(pts::bigint + refund, 2147483647)::integer\n"
            "    WHERE id = %(cid)d AND profession = %(old)d;\n"
            "  INSERT INTO character_class_change_receipts (character_id, account_id, from_profession, to_profession, character_level,\n"
            "      permit_item_id, permit_slot, permit_ledger_inbox_id, talent_points_before, talent_points_refunded, talents,\n"
            "      sealed_skills, unsealed_skills, granted_skills, cleared_zodiac)\n"
            "    VALUES (%(cid)d, %(aid)d, %(old)d, %(target)d, %(level)d, NULL, NULL, NULL, pts,\n"
            "      LEAST(refund, 2147483647 - pts)::integer, tal, sealed, unsealed, granted, %(zjson)s);\n"
            "END $gwc$" % {
                "arr": arr, "cid": cid, "aid": aid, "old": old, "target": target, "level": level, "last": LAST_CHARACTER_SLOT,
                "rev": GAMEPLAY_REVISION, "zjson": zjson,
                "zclear": ("  UPDATE character_zodiac_skill_grids SET selected_skill_id = -1, updated_at = now() WHERE user_id = %d "
                           "AND grid_index = ANY(ARRAY[%s]::smallint[]);\n" % (cid, ",".join(str(g) for g in grids))) if grids else ""})


# --------------------------------------------------------------------------- legal limits of the Character page
I32 = 2 ** 31 - 1
LIMITS = {
    # col: (min, max, source)
    "fighter_job_lv": (1, 200, "PlayerExperienceCatalog.MaximumLevel = 200"),
    "Money": (0, I32, "int32 in the status packet (PacketBuilder.PlayerStatusUpdate +120) and the DB CHECK; no lower game cap exists"),
    "Stone": (0, I32, "int32 (PlayerStatusUpdate +124), DB CHECK"),
    "BindingGold": (0, I32, "int32 (PlayerStatusUpdate +136), DB CHECK"),
    "medusa_honor_points": (0, I32, "int32 (PlayerStatusUpdate +128); the game adds with LEAST(..., 2147483647)"),
    "exchange_point": (0, I32, "int32 (PlayerStatusUpdate +132)"),
    "exchange_medal": (0, I32, "int32 (PlayerStatusUpdate +140)"),
    "MaxHP": (1, 1500, "base max HP: every character is created with 1500 (PostgresCharacterLifecycleCommandExecutor.Create.cs) "
                       "and no server path changes it; level, gear, talents and buffs add on top at login"),
    "MaxMP": (1, 177, "base max MP: created with 177 and never changed by the server (same source)"),
    "SkillExp": (0, 99, "TalentExperienceCatalog.ExperiencePerPoint = 100 (100 talent EXP becomes a point)"),
    "zodiac_level": (1, 30, "ZodiacEnergyCatalog.StorageLimits has 30 levels"),
    "hair_style": (0, 264, "the client's hair models (see check_hair)"),
    "face_shap": (0, 4, "5 faces per gender (Characters/<gender>_head_0000..0004.gwo)"),
    "pet_shed_capacity": (2, 8, "DB CHECK; the pet shed window has 8 cells"),
    "belief": (0, 255, "smallint; must be one of the client's faiths (catalog)"),
    "consortia_contribute": (0, I32, "int32 (PlayerStatusUpdate +80)"),
}


def talent_point_ceiling(class_talents, rate=1):
    """The most talent points a character can ever spend: every talent of its class to rank 100 at today's price.
    More than that is not reachable in the game."""
    return min(I32, int(class_talents) * talent_refunds(rate)[TALENT_RANK_CAP])


def check_limit(col, value, gm=False, ceiling=None, current=None):
    """Refuse a value outside the legal range of col. A GM character may go past the 'game' limits of MaxHP / MaxMP /
    SkillPoint (never past the storage limits). A stored value already above the game limit (earned under higher rates,
    or set before these limits) is never forced down: lowering it, or keeping it, is allowed; raising it further is not."""
    lim = dict(LIMITS)
    if ceiling is not None:
        lim["SkillPoint"] = (0, int(ceiling), "talent points: every talent of the class to rank 100 at today's price")
    if col not in lim:
        return value
    lo, hi, src = lim[col]
    v = int(value)
    if gm and col in ("MaxHP", "MaxMP", "SkillPoint"):
        hi = I32
    if current is not None and col in ("MaxHP", "MaxMP", "SkillPoint") and v > hi and v <= int(current):
        return v
    if not lo <= v <= hi:
        raise Bad("%s must be %s..%s (%s)%s" % (col, format(lo, ","), format(hi, ","), src,
                                                "" if gm or col not in ("MaxHP", "MaxMP", "SkillPoint") else
                                                "; a GM-flagged character may go higher"))
    return v
