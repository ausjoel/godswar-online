"""modules/character.py - the "Character" editor: the in-game character window (C) with every stored value editable,
plus the character's ACCOUNT (create, rename, password, e-mail, donator tier, ban, entitlements, characters,
delete). Contract: ../MODULES.md. Owned by the character module (this file, static/modules/character.js/.css).

Where the rules come from (reborn source src/Godswar.Server, commit b85a1244, and schema/write_rules.json):
  * level 1..200 and the EXP each level needs: State/PlayerExperienceCatalog.cs (NextLevelExperience, MaximumLevel).
    EXP must stay below the next level's cost, or the next kill levels the character up (Apply); a sealed level or
    level 200 stores up to the UInt32 ceiling.
  * zodiac level 1..30 and the energy each level can hold: State/ZodiacEnergyCatalog.cs (StorageLimits).
  * talent EXP 0..99 (100 = one point): State/TalentExperienceCatalog.cs.
  * selected title: 0 or a title the character owns (Infrastructure/Characters/PostgresCharacterTitleSelectionStore.cs
    reads character_title_ownership + atlantis_character_title_ownership; Wonderland titles have their own table).
  * map must exist in map_templates; positions are the game's -256..256 world square (Client-RE/origin/MAP-FORMATS.md s.9).
  * pet shed cells must hold the owned pets (PacketBuilder.Pets.cs:160-185).
  * accounts: the reborn registration insert (Infrastructure/Accounts/PostgresAccountStore.cs
    TryCreateAccountWithCredentialAsync) is done by modules/accounts.py (verifier = PBKDF2-SHA256 of the MD5 hex);
    this module adds the client's user-name rule (the 2.51 client scrambles capital letters in a way reborn's
    PacketText.DecodeLoginName does not undo, GodsWar-VPS/signup/README.md), rename, entitlements and deletion.
  * deleting a character the game's way = the server's own compatibility delete
    (State/PostgresGameStore.CharacterLifecycle.cs DeleteCharacterAsync): lifecycle_state 'deleted', restorable for
    30 days, purged 7 days later (State/CharacterLifecycleState.cs), account_realm.character_lifecycle_version + 1,
    the accounts mirror for realm 1 (Tempest); refused when the durable lifecycle stream has started for the account.
    Restore mirrors PostgresCharacterLifecycleCommandExecutor.Mutations.cs RestoreCharacterAsync.
  * deleting an ACCOUNT for good = the same cascade the server's purge uses (DELETE ... ON DELETE CASCADE); refused
    while the account is online and while evidence rows (settlements, claims, guild leadership, sealed pets, titles)
    reference it - those foreign keys are RESTRICT, and evidence is never deleted by the editor.

Every write goes through the framework (ctx.set_character_fields: live through the round-2 server when the character
is online and the server offers the field, else a guarded database write; ctx.rows_write for account rows):
backup first, one guarded transaction, change log, Undo.
"""
import datetime
import json
import re

from . import common
from .common import Bad, Refused, NotFound, Conflict, OnlineRefused, lit, ident

try:
    from . import accounts as ACC          # the framework's Accounts module: verifier(), create/password/edit/ban
except Exception as _e:                    # noqa: BLE001 - the character sheet works without it
    ACC = None
    ACC_ERROR = str(_e)

M = common.module(
    "character", "Character",
    icon={"sprite": 248},          # catalog-icons.png tile
    order=10,
    status="ready",
    description="The character window (C): level, EXP, HP/MP, Silver/Gold/B-Gold, honour, faith, zodiac, position, title, "
                "plus the account: new accounts, rename, password, donator tier, ban, battle pass, delete.",
    needs_character=True,
)

# ------------------------------------------------------------------ game tables (copied from the reborn source)
LEVEL_CAP = 200                       # PlayerExperienceCatalog.MaximumLevel
UINT32_MAX = 4294967295               # PlayerExperienceCatalog.MaximumStoredExperience
# PlayerExperienceCatalog.NextLevelExperience: entry n-1 = EXP needed to advance from level n
EXP_NEXT = [
    200, 252, 286, 345, 432, 500, 598, 729, 840, 986,
    1170, 1408, 1650, 1904, 2205, 2556, 2923, 3382, 3861, 4400,
    5043, 5754, 6536, 7392, 8370, 9682, 10896, 12299, 13800, 15504,
    17368, 19451, 21708, 24696, 27474, 30566, 33984, 37740, 41785, 46252,
    51968, 57395, 63294, 69747, 76772, 84387, 94004, 103176, 113077, 123802,
    135450, 149996, 163800, 178619, 194720, 212058, 233562, 253932, 275910, 299452,
    328504, 356089, 385560, 417326, 456165, 492936, 532380, 574464, 625926, 674586,
    726500, 781942, 849441, 913120, 981015, 1053322, 1140912, 1223525, 1311420, 1404927,
    1517816, 1624158, 1737075, 1873053, 2001162, 2137002, 2280960, 2453664, 2616333, 2788636,
    2994516, 3188589, 3393792, 3638570, 3869216, 4112724, 4402570, 4675590, 4963456, 5266965,
    5627415, 5966660, 6323850, 6747169, 7145568, 7564650, 8060451, 8526724, 9016735, 9595295,
    10139464, 10780924, 11384130, 12017304, 12762292, 13462848, 14197280, 15060168, 15871473, 16721276,
    17718010, 18654902, 19635504, 20784030, 21863205, 23125429, 24311802, 25551750, 27000288, 28361096,
    29782557, 31440786, 32998056, 34812984, 36517520, 38296098, 40365668, 42309351, 44569086, 46690944,
    48902340, 51470250, 53880988, 56678094, 59303990, 62037400, 65205196, 68178565, 71621260, 74852778,
    78590928, 82099798, 85747200, 89962200, 93917664, 98485050, 102771288, 107222304, 112357074, 117174640,
    122727594, 127937553, 133938450, 139568334, 146048136, 152127219, 158430440, 165678624, 172477318, 180290120,
    187618112, 196033460, 203926272, 212984232, 221479852, 230275548, 240361170, 249818904, 260656928, 270819870,
    282458834, 293372604, 305863752, 317576235, 330973338, 343535256, 357895460, 371360196, 386743341, 401166744,
    416066165, 433077471, 449024084, 467220960, 484278355, 503732229, 521967534, 542754000, 562237703, 584435250,
]
assert len(EXP_NEXT) == LEVEL_CAP
# ZodiacEnergyCatalog.StorageLimits (ConstellationConfig.lua MaxPower1..30): energy a zodiac level can hold
ZODIAC_CAPS = [1000, 3000, 8000, 20000, 28000, 42000, 60000, 80000, 100000, 125000, 150000, 180000, 210000, 245000,
               280000, 315000, 355000, 400000, 445000, 500000, 555000, 610000, 665000, 725000, 785000, 845000, 905000,
               965000, 1025000, 1090000]
TALENT_EXP_PER_POINT = 100            # TalentExperienceCatalog.ExperiencePerPoint
WORLD = 256                           # game x / z run -256..256 (MAP-FORMATS.md s.9)
TEMPEST = 1                           # RealmId.Tempest (the realm with the accounts.character_lifecycle_version mirror)
RESTORE_DAYS, PURGE_DAYS = 30, 7      # CharacterLifecyclePolicy.DefaultRestoreWindow / DefaultPurgeDelay
I32 = 2 ** 31 - 1
# Domain/World/Instances/DynamicDungeonContentMapPolicy.cs: maps only an exact dungeon run can host. A character saved on
# one of them is moved back to its home town at the next login ("[instance] recovered unavailable saved instance").
DUNGEON_MAPS = {200: "Medusa's Island", 204: "Medusa's Island (second map)", 205: "Atlantis portal", 207: "Wonderland",
                208: "Harbour attack (first map)", 209: "Harbour attack (second map)"}
DUNGEON_WHY = ("a dungeon map: only a running dungeon instance can hold a character there, so the server moves the "
               "character back to its home town at the next login (DynamicDungeonContentMapPolicy). Enter it through its NPC in game.")
TIERS = {0: "none", 1: "Kijin Patron", 2: "Oni Patron", 3: "Demon Lord Seed", 4: "True Demon Lord", 5: "Octagram Patron"}
ENTITLEMENTS = {"battle_pass": "Battle pass: the Battle Pass EXP status while it lasts and no daily limit on Holy Suit EXP "
                               "storage (PostgresExperienceBoostStateReader.cs, PostgresHolySuitCommandExecutor.Quota.cs)."}
# The 2.51 client sends a user name through a letter/digit shift reborn undoes only for a-z and 0-9
# (Packets/PacketText.cs DecodeLoginName): capitals and other symbols would not match at login.
GAME_USER_RX = re.compile(r"^[a-z0-9]{4,16}$")
GAME_USER_RULE = "4-16 characters: lower-case letters a-z and digits 0-9 (the game client cannot log in with capitals)"
# Character names: ASCII only (PacketText.ReadFixedAscii / WriteFixedAscii). At most 31 characters: the server writes the
# name into 32-byte packet fields (PacketBuilder.WorldEntry/PlayerWorld/Utilities) and a 32-character name would leave
# no terminating zero for the client. No leading/trailing space: ReadFixedAscii trims, so the client could never send it
# back. The Origin client's own create box takes 8 characters (LoginUI.xml EditName MaxText="8"); longer names work
# but may be cut off in narrow windows - the page warns, it does not refuse.
CHAR_NAME_RX = re.compile(r"^[A-Za-z0-9_\-\.](?:[A-Za-z0-9_\-\. ]{0,29}[A-Za-z0-9_\-\.])?$")
CHAR_NAME_RULE = "1-31 ASCII letters, digits, _ - . or inner spaces"
TEXT_COLS = {"name", "gender"}

# character_base columns this page edits: kind, and whether the field is "advanced" (edit_advanced in write_rules)
EDIT = {
    "name": "name", "gender": "gender",
    "fighter_job_lv": "int", "fighter_job_exp": "int", "fighter_level_sealed": "bool",
    "curHP": "int", "curMP": "int", "MaxHP": "int", "MaxMP": "int",
    "Money": "int", "Stone": "int", "BindingGold": "int",
    "medusa_honor_points": "int", "exchange_point": "int", "exchange_medal": "int",
    "SkillPoint": "int", "SkillExp": "int",
    "belief": "int", "zodiac_type": "int", "zodiac_level": "int", "zodiac_energy": "int",
    "Map": "int", "Pos_X": "float", "Pos_Z": "float",
    "hair_style": "int", "face_shap": "int", "pet_shed_capacity": "int", "selected_title_id": "int",
    "consortia_contribute": "int",
}
ADVANCED = {"MaxHP", "MaxMP", "consortia_contribute"}


def exp_next(level, sealed=False):
    """EXP the client bar fills to at this level (PlayerExperienceCatalog.GetClientExperienceMaximum)."""
    level = int(level)
    if sealed or level >= LEVEL_CAP:
        return UINT32_MAX
    return EXP_NEXT[max(1, level) - 1]


def zodiac_cap(level):
    level = int(level)
    return ZODIAC_CAPS[min(30, max(1, level)) - 1]


def _rb(ctx):
    return ctx.need_rb()


def _num(v):
    """Decimal / str from psql JSON -> int or float."""
    if v is None:
        return None
    try:
        f = float(v)
    except (TypeError, ValueError):
        return v
    return int(f) if f.is_integer() and "." not in str(v) else f


# =========================================================================== character sheet
def _base(ctx, cid):
    rb = _rb(ctx)
    r = rb.pg.json("""(SELECT json_build_object(
        'base', to_jsonb(c) - 'quest_completed_ids',
        'account', (SELECT (to_jsonb(a) - 'password' - 'login_presence_token')
                           || jsonb_build_object('presence', a.login_presence_token IS NOT NULL,
                                                 'hashed', a.password LIKE 'gws$%%')
                    FROM accounts a WHERE a.id = c.account_id),
        'guild', (SELECT json_build_object('id', g.id, 'name', g.name, 'level', g.level, 'lord', g.lord_character_id)
                  FROM guilds g WHERE g.id = c.consortia),
        'titles_owned', (SELECT coalesce(json_agg(x.t ORDER BY x.t), '[]') FROM (
              SELECT title_id AS t FROM character_title_ownership WHERE character_id = c.id
              UNION SELECT title_id FROM atlantis_character_title_ownership WHERE character_id = c.id
              UNION SELECT title_id FROM wonderland_character_title_ownership WHERE character_id = c.id) x),
        'pets_owned', (SELECT count(*) FROM character_pets p WHERE p.user_id = c.id AND p.activity_state = 'owned'),
        'map_known', EXISTS (SELECT 1 FROM map_templates m WHERE m.map_id = c."Map"))
      FROM character_base c WHERE c.id = %d)""" % int(cid))
    if not r:
        raise NotFound("no character #%s in %s" % (cid, rb.db))
    return r


def _label(col):
    return common.GLOSSARY.field("character_base", col).get("label") or col


@M.get(r"sheet/(\d+)")
def sheet(ctx, cid):
    """GET /api/m/character/sheet/<cid> - the character window: every stored value, its English names, the level /
    EXP / zodiac rules, which fields are editable and which the running server can change live."""
    cid = int(cid)
    rb = _rb(ctx)
    r = _base(ctx, cid)
    b = r["base"]
    lv, sealed = int(b["fighter_job_lv"]), bool(b.get("fighter_level_sealed"))
    cat = ctx.catalog
    fields = {}
    for col in EDIT:
        f = common.GLOSSARY.field("character_base", col)
        fields[col] = {"label": f.get("label") or col, "editable": bool(f.get("editable")) or (col in ADVANCED and f.get("advanced")),
                       "advanced": col in ADVANCED, "why": f.get("why") or "", "live": col in common.LIVE_FIELDS,
                       "min": f.get("min"), "max": f.get("max")}
    for col in ("profession", "camp", "prestige", "earl_rank", "consortia", "guild_duty", "consortia_job", "warehouse_capacity",
                "holy_suit_points", "GM", "Register_time", "LastLogin_time", "lifecycle_state", "bag_num", "store_num"):
        f = common.GLOSSARY.field("character_base", col)
        fields[col] = {"label": f.get("label") or col, "editable": False, "why": f.get("why") or "", "live": False}
    acct = r.get("account") or {}
    return {
        "cid": cid, "database": rb.db, "writable": bool(rb.writable), "live_db": bool(rb.live),
        "base": b, "account": dict(acct, tier_name=TIERS.get(acct.get("donator_tier"), "?")),
        "guild": r.get("guild"), "titles_owned": r.get("titles_owned") or [], "pets_owned": r.get("pets_owned") or 0,
        "map_known": r.get("map_known"),
        "names": {"class": cat.name("class", b["profession"]), "camp": cat.name("camp", b["camp"]),
                  "faith": cat.name("faith", b["belief"]), "zodiac": cat.name("zodiac_sign", b["zodiac_type"]),
                  "map": cat.name("map", b["Map"]), "title": cat.name("title", b["selected_title_id"]) if b["selected_title_id"] else "",
                  "guild_duty": cat.name("guild_duty", b["guild_duty"]) if b.get("guild_duty") is not None else ""},
        "level": {"cap": LEVEL_CAP, "next": exp_next(lv, sealed), "sealed": sealed, "uint32": UINT32_MAX},
        "zodiac": {"cap": zodiac_cap(b["zodiac_level"] or 1), "caps": ZODIAC_CAPS, "levels": len(ZODIAC_CAPS)},
        "talent_exp_per_point": TALENT_EXP_PER_POINT,
        "exp_table": EXP_NEXT, "world": WORLD,
        "fields": fields, "online": common.online_info(ctx.app, cid),
        "tiers": TIERS, "entitlement_keys": ENTITLEMENTS,
        "gm": {"supported": ctx.caps.get("gm_authority") == "character_flag", "enabled": b.get("GM") == 1},
    }


@M.get(r"map/(\d+)")
def map_points(ctx, mid):
    """GET /api/m/character/map/<id> - named points, safe areas and exits of one map (database + client), for the
    coordinate picker; plus the minimap picture when the client has one."""
    mid = int(mid)
    rb = _rb(ctx)
    db = rb.pg.json("""json_build_object(
        'template', (SELECT json_build_object('id', map_id, 'scene', scene_key, 'mode', map_mode) FROM map_templates WHERE map_id = %d),
        'points', (SELECT coalesce(json_agg(json_build_object('group', group_name, 'name', name, 'x', pos_x, 'z', pos_z)
                    ORDER BY group_index, point_index), '[]') FROM map_address_points WHERE map_id = %d),
        'safe', (SELECT coalesce(json_agg(json_build_object('x1', x1, 'z1', z1, 'x2', x2, 'z2', z2) ORDER BY area_index), '[]')
                 FROM map_safe_areas WHERE map_id = %d))""" % (mid, mid, mid))
    try:
        e = ctx.catalog.entry("map", mid)
    except NotFound:
        e = {}
    scene = (e.get("scene") or ((db or {}).get("template") or {}).get("scene") or "").lower()
    import os
    png = os.path.join(common.HERE, "static", "modules", "character-minimaps", scene + ".png") if scene else ""
    pts = [{"group": p["group"], "name": p["name"], "x": _num(p["x"]), "z": _num(p["z"]), "src": "server"} for p in (db or {}).get("points") or []]
    if not pts:
        pts = [{"group": x.get("group_name") or "", "name": x.get("name"), "x": x.get("x"), "z": x.get("z"), "src": "client"}
               for x in e.get("regions") or []]
    links = [{"to": l.get("to"), "to_name": ctx.catalog.name("map", l.get("to")), "x": l.get("x"), "z": l.get("z")}
             for l in e.get("links") or []]
    return {"id": mid, "name": e.get("name") or ctx.catalog.name("map", mid), "scene": scene,
            "in_server": bool((db or {}).get("template")),
            "dungeon": DUNGEON_WHY if mid in DUNGEON_MAPS else None,
            "minimap": "/static/modules/character-minimaps/%s.png" % scene if png and os.path.exists(png) else None,
            "points": pts, "safe": [{k: _num(v) for k, v in s.items()} for s in (db or {}).get("safe") or []], "links": links,
            "world": WORLD}


def _validate(ctx, cid, vals, base, extra):
    """Game rules on top of the glossary ranges (set_character_fields checks min / max / read-only)."""
    out = {}
    for col, v in vals.items():
        kind = EDIT.get(col)
        if not kind:
            raise Refused("%s (%s) is not edited on this page%s" % (_label(col), col,
                          ": " + (common.GLOSSARY.field("character_base", col).get("why") or "") if col in base else ""))
        if kind == "name":
            s = str(v or "").strip()
            if not CHAR_NAME_RX.match(s):
                raise Bad("a character name is %s (got %r)" % (CHAR_NAME_RULE, v))
            if s != base["name"]:
                # ux_character_base_active_name_ci: lower(name) unique among ACTIVE characters (the live index; the
                # older ux_character_base_name in schema/live-schema-meta.json is gone). A deleted character that can
                # still be restored keeps its name too: restoring it would otherwise hit that index.
                hold = ctx.rb.pg.json(
                    "(SELECT json_build_object('id', id, 'name', name, 'state', lifecycle_state, 'until', restore_until) "
                    "FROM character_base WHERE lower(name) = lower(%s) AND id <> %d AND (lifecycle_state = 'active' "
                    "OR (lifecycle_state = 'deleted' AND restore_until > now())) ORDER BY lifecycle_state LIMIT 1)" % (lit(s), int(cid)))
                if hold and hold.get("state") == "active":
                    raise Bad("the name %s is taken by character #%s %s (names are unique without regard to case)" % (s, hold["id"], hold["name"]))
                if hold:
                    raise Bad("the name %s belongs to deleted character #%s %s, which can be restored until %s; restoring it would "
                              "fail if another character had the name. Pick another name (or purge / rename that character "
                              "first)." % (s, hold["id"], hold["name"], hold.get("until")))
            v = s
        elif kind == "gender":
            v = str(v).strip().lower()
            if v not in ("male", "female"):
                raise Bad("gender must be male or female")
        elif kind == "bool":
            if not isinstance(v, bool):
                v = str(v).strip().lower() in ("1", "true", "yes", "on")
        elif kind == "float":
            try:
                v = round(float(v), 2)
            except (TypeError, ValueError):
                raise Bad("%s must be a number" % _label(col))
            if not -WORLD <= v <= WORLD:
                raise Bad("%s must be inside the map square %d..%d (got %s)" % (_label(col), -WORLD, WORLD, v))
        else:
            v = common.to_int(v, None, None, _label(col))
        out[col] = v
    nb = dict(base)
    nb.update(out)
    lv = int(nb["fighter_job_lv"])
    if "fighter_job_lv" in out and not 1 <= lv <= LEVEL_CAP:
        raise Bad("level must be 1..%d (PlayerExperienceCatalog.MaximumLevel)" % LEVEL_CAP)
    if {"fighter_job_lv", "fighter_job_exp", "fighter_level_sealed"} & set(out):
        sealed = bool(nb.get("fighter_level_sealed"))
        exp = int(nb["fighter_job_exp"]) if not ("fighter_job_lv" in out and "fighter_job_exp" not in out and extra.get("live")) else 0
        cap = exp_next(lv, sealed)
        if cap == UINT32_MAX:
            if exp > UINT32_MAX:
                raise Bad("EXP must be at most %s" % format(UINT32_MAX, ","))
        elif exp >= cap:
            raise Bad("EXP %s is not below the %s EXP level %d needs: the next kill would level the character up. "
                      "Raise the level instead, or seal the level first." % (format(exp, ","), format(cap, ","), lv))
    if "zodiac_level" in out and not 1 <= out["zodiac_level"] <= len(ZODIAC_CAPS):
        raise Bad("zodiac level must be 1..%d (ZodiacEnergyCatalog)" % len(ZODIAC_CAPS))
    if {"zodiac_level", "zodiac_energy"} & set(out):
        cap = zodiac_cap(nb["zodiac_level"] or 1)
        if not 0 <= int(nb["zodiac_energy"]) <= cap:
            raise Bad("zodiac energy %s is more than zodiac level %s can hold (%s, ZodiacEnergyCatalog.StorageLimits)" % (
                format(int(nb["zodiac_energy"]), ","), nb["zodiac_level"], format(cap, ",")))
    if "zodiac_type" in out and not ctx.catalog.name("zodiac_sign", out["zodiac_type"]):
        raise Bad("zodiac sign %s is not one of the 12 signs" % out["zodiac_type"])
    if "belief" in out:
        try:
            ctx.catalog.entry("faith", out["belief"])
        except NotFound:
            raise Bad("faith %s is not one of the client's faiths" % out["belief"])
    if "SkillExp" in out and not 0 <= out["SkillExp"] < TALENT_EXP_PER_POINT:
        raise Bad("talent EXP must be 0..%d (100 talent EXP becomes one talent point)" % (TALENT_EXP_PER_POINT - 1))
    if "Map" in out:
        if not ctx.rb.pg.json("(SELECT to_jsonb(count(*)) FROM map_templates WHERE map_id = %d)" % out["Map"]):
            raise Bad("map %s is not in map_templates: the server could not load the character there" % out["Map"])
        if out["Map"] in DUNGEON_MAPS and out["Map"] != int(base.get("Map") or -1):
            raise Bad("map %s (%s) is %s" % (out["Map"], DUNGEON_MAPS[out["Map"]], DUNGEON_WHY))
        if "Pos_X" not in out or "Pos_Z" not in out:
            raise Bad("moving to another map needs the X and Z position too (pick a point on the map)")
    if "selected_title_id" in out and out["selected_title_id"] and out["selected_title_id"] not in (extra.get("titles") or []):
        raise Bad("the character does not own title %s; the server only shows owned titles (titles are earned in Medusa, "
                  "Atlantis and Wonderland runs)" % out["selected_title_id"])
    if "pet_shed_capacity" in out and out["pet_shed_capacity"] < int(extra.get("pets") or 0):
        raise Bad("the character owns %s pets; the pet shed needs at least that many cells" % extra.get("pets"))
    for col in ("curHP", "curMP", "MaxHP", "MaxMP"):
        if col in out and not 0 <= out[col] <= I32:
            raise Bad("%s must be 0..%s" % (_label(col), format(I32, ",")))
    # The server clamps the stored HP / MP to the character's real maximum when it loads it
    # (CharacterSnapshotContract.cs: CurrentHp == Clamp(curHP, 0, MaxHp)) but keeps vitals_revision, so a stored value
    # above that maximum makes every logout's vitals flush fail (RevisionConflict, "[checkpoint] final vitals flush
    # failed") until the HP changes in game. The real maximum is (base Max HP + gear) x bonuses
    # (PostgresCharacterRuntimeItemProjectionSql.DonatorBenefits.cs), never below the base, so the base is a safe bound.
    for cur, mx, floor in (("curHP", "MaxHP", 1), ("curMP", "MaxMP", 1)):
        if {cur, mx} & set(out):
            c, m = int(nb[cur] or 0), max(floor, int(nb[mx] or 0))
            if c > m and (cur in out or c > int(base.get(cur) or 0) or m < int(base.get(mx) or 0)):
                raise Bad("%s %s is above the base %s %s: the server would clamp it at login and then fail to save HP / MP at "
                          "every logout. Set %s at most %s (gear and bonuses only raise the maximum)." % (
                              _label(cur), format(c, ","), _label(mx), format(m, ","), _label(cur), format(m, ",")))
    return out


@M.post(r"save/(\d+)")
def save(ctx, cid):
    """POST /api/m/character/save/<cid> {values: {col: v}, expected: {col: shown}, label?, advanced?} - one guarded
    save of character_base values. Online + the round-2 server offers every field -> applied live (the player sees it
    now); offline -> one database transaction (next login); otherwise 423 with what needs a logout."""
    cid = int(cid)
    b = ctx.body or {}
    vals = b.get("values") or {}
    if not isinstance(vals, dict) or not vals:
        raise Bad("nothing to save")
    r = _base(ctx, cid)
    base = r["base"]
    on = common.online_info(ctx.app, cid)
    clean = _validate(ctx, cid, vals, base, {"titles": r.get("titles_owned") or [], "pets": r.get("pets_owned"),
                                             "live": bool(on.get("online"))})
    # drop values equal to what is stored (a no-op write would only bump revision counters)
    same = [c for c, v in clean.items() if base.get(c) == v or (c in TEXT_COLS and str(base.get(c)) == str(v))
            or (c not in TEXT_COLS and str(_num(base.get(c))) == str(_num(v)))]
    for c in same:
        clean.pop(c)
    if not clean:
        return {"mode": "none", "note": "nothing changed (the values are already stored)"}
    if on.get("online") and "fighter_job_lv" in clean and "fighter_job_exp" not in clean and int(base.get("fighter_job_exp") or 0):
        # the live level call resets EXP to 0 (round-2 /admin/progression: "resets EXP unless experience is given"); say
        # so in the change, and give Undo the old EXP to put back (without it Undo restored the level with EXP 0)
        clean["fighter_job_exp"] = 0
    adv = bool(b.get("advanced")) or bool(set(clean) & ADVANCED)
    exp = b.get("expected") or {}
    exp = {c: exp[c] for c in clean if c in exp} or None
    label = b.get("label") or None
    if not label:
        label = "%s: %s" % (base["name"], ", ".join("%s %s -> %s" % (_label(c), _fmt(base.get(c)), _fmt(v)) for c, v in clean.items()))
    return ctx.set_character_fields(cid, clean, expected=exp, advanced=adv, label=label)


def _fmt(v):
    v = _num(v)
    if isinstance(v, bool) or v is None:
        return str(v)
    if isinstance(v, int):
        return format(v, ",")
    return str(v)


# =========================================================================== accounts
def _need_acc():
    if ACC is None:
        raise Bad("the Accounts module did not load (%s)" % ACC_ERROR)
    return ACC


def _account(ctx, aid):
    rb = _rb(ctx)
    a = rb.pg.json("""(SELECT (to_jsonb(a) - 'password' - 'login_presence_token') || jsonb_build_object(
          'presence', a.login_presence_token IS NOT NULL, 'hashed', a.password LIKE 'gws$%%',
          'realms', (SELECT coalesce(json_agg(to_jsonb(r) ORDER BY r.realm_id), '[]') FROM account_realm r WHERE r.account_id = a.id),
          'entitlements', (SELECT coalesce(json_agg(to_jsonb(e) ORDER BY e.id), '[]') FROM account_entitlements e WHERE e.account_id = a.id),
          'characters', (SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name, 'level', c.fighter_job_lv,
                'profession', c.profession, 'camp', c.camp, 'realm', c.server_id, 'state', c.lifecycle_state,
                'deleted_at', c.deleted_at, 'restore_until', c.restore_until, 'purge_after', c.purge_after,
                'restorable', c.lifecycle_state = 'deleted' AND c.restore_until > now(),
                'online', c.checkpoint_owner_id IS NOT NULL, 'last_login', c."LastLogin_time") ORDER BY c.id), '[]')
              FROM character_base c WHERE c.account_id = a.id))
        FROM accounts a WHERE a.id = %d)""" % int(aid))
    if not a:
        raise NotFound("no account #%s" % aid)
    for c in a["characters"]:
        c["class"] = common.CLASS_NAMES.get(c["profession"], str(c["profession"]))
        c["camp_name"] = common.CAMP_NAMES.get(c["camp"], str(c["camp"]))
    a["online"] = bool(a["login_status"] or a["presence"] or any(c["online"] for c in a["characters"]))
    a["banned"] = a["status"] == 1
    a["ban_effective"] = bool(ctx.caps.get("available"))
    a["tier_name"] = TIERS.get(a["donator_tier"], "?")
    a["game_safe_name"] = bool(GAME_USER_RX.match(a["username"]))
    return a


def _online_refuse(a, what):
    if a["online"]:
        raise OnlineRefused("%s: the account %s is logged in (%s). Log it out first." % (
            what, a["username"], ", ".join(c["name"] for c in a["characters"] if c["online"]) or "at the login / character screen"),
            {"name": a["username"], "online": True, "reasons": ["accounts.login_status=%s" % a["login_status"]] +
             (["accounts.login_presence_token is set"] if a["presence"] else []), "live": [], "kick": False})


def _offline_sql(aid):
    """DO-block run inside the write transaction after the scope rows are locked: abort if the account is online."""
    return ("DO $gwc$ BEGIN IF EXISTS (SELECT 1 FROM accounts a WHERE a.id = %d AND (a.login_status <> 0 OR "
            "a.login_presence_token IS NOT NULL)) OR EXISTS (SELECT 1 FROM character_base c WHERE c.account_id = %d AND "
            "c.checkpoint_owner_id IS NOT NULL) THEN RAISE EXCEPTION 'gwadmin: account #%d is online (login_status / presence "
            "token / a character owned by a game session); nothing written'; END IF; END $gwc$" % (int(aid), int(aid), int(aid)))


def _acc_scope(aid, ignore=None):
    return common.scope("accounts", "id = %d" % int(aid), pk=["id"], counters=[],
                        ignore=ignore if ignore is not None else ["last_login_time", "last_logout_time", "last_login_ip",
                                                                  "last_login_mac", "total_online_time", "login_status",
                                                                  "login_presence_token"])


@M.get("accounts")
def accounts_list(ctx):
    """GET /api/m/character/accounts?q= - every account with its characters (the Accounts module's list) plus
    whether each user name can log in from the game client."""
    r = _need_acc().list_accounts(ctx)
    for a in r["accounts"]:
        a["game_safe_name"] = bool(GAME_USER_RX.match(a["username"]))
        a["tier_name"] = TIERS.get(a["donator_tier"], "?")
    r["user_rule"] = GAME_USER_RULE
    return r


@M.get(r"account/(\d+)")
def account(ctx, aid):
    """GET /api/m/character/account/<id> - one account: details, realms, entitlements, characters (also deleted ones),
    and what a permanent delete would remove or what blocks it."""
    a = _account(ctx, aid)
    a["tiers"] = TIERS
    a["entitlement_keys"] = ENTITLEMENTS
    a["user_rule"] = GAME_USER_RULE
    return a


@M.get(r"account/(\d+)/delete-plan")
def account_delete_plan(ctx, aid):
    """GET /api/m/character/account/<id>/delete-plan - what a permanent delete would remove, and what blocks it."""
    _account(ctx, aid)
    return _delete_plan(ctx, int(aid), preview=True)


@M.post("account/create")
def account_create(ctx):
    """POST /api/m/character/account/create {username, password, email?, donator_tier?} - a new account exactly as the
    server's own first-login registration writes it (modules/accounts.py), with the game client's user-name rule."""
    user = str((ctx.body or {}).get("username") or "").strip()
    if not GAME_USER_RX.match(user):
        raise Bad("the user name must be " + GAME_USER_RULE)
    rb = _rb(ctx)
    if rb.pg.json("(SELECT to_jsonb(count(*)) FROM accounts WHERE lower(username) = lower(%s))" % lit(user)):
        raise Bad("the user name %s is taken" % user)
    ctx.body["username"] = user
    r = _need_acc().create(ctx)
    return r


@M.post(r"account/(\d+)/rename")
def account_rename(ctx, aid):
    """POST /api/m/character/account/<id>/rename {username, expected} - a new login name (refused while online; the
    player logs in with the new name next time)."""
    a = _account(ctx, aid)
    new = str((ctx.body or {}).get("username") or "").strip()
    if not GAME_USER_RX.match(new):
        raise Bad("the user name must be " + GAME_USER_RULE)
    if new == a["username"]:
        return {"account": a, "note": "the name is unchanged"}
    rb = _rb(ctx)
    if rb.pg.json("(SELECT to_jsonb(count(*)) FROM accounts WHERE lower(username) = lower(%s) AND id <> %d)" % (lit(new), int(aid))):
        raise Bad("the user name %s is taken" % new)
    _online_refuse(a, "rename")
    exp = (ctx.body or {}).get("expected") or a["username"]
    r = ctx.rows_write([_acc_scope(aid)], "rename account %s -> %s" % (a["username"], new),
                       lambda before: [_offline_sql(aid), "UPDATE accounts SET username = %s WHERE id = %d" % (lit(new), int(aid))],
                       fields=["accounts.username"], advanced=True,
                       expected=[{"table": "accounts", "where": {"id": int(aid)}, "values": {"username": exp}}],
                       applies="next_login", target="account " + a["username"])
    r["account"] = _account(ctx, aid)
    r["note"] = "Log in as %s from now on (the password is unchanged)." % new
    return r


@M.post(r"account/(\d+)/password")
def account_password(ctx, aid):
    """POST /api/m/character/account/<id>/password {password} - the Accounts module's password change."""
    r = _need_acc().set_password(ctx, aid)
    r["account"] = _account(ctx, aid)
    return r


@M.post(r"account/(\d+)/edit")
def account_edit(ctx, aid):
    """POST /api/m/character/account/<id>/edit {email?, donator_tier?, donator_expires_at?, expected} - Accounts module."""
    r = _need_acc().edit(ctx, aid)
    r["account"] = _account(ctx, aid)
    return r


@M.post(r"account/(\d+)/ban")
def account_ban(ctx, aid):
    """POST /api/m/character/account/<id>/ban {banned} - accounts.status 1 / 0 (Accounts module; round-2 honours it)."""
    r = _need_acc().ban(ctx, aid)
    r["account"] = _account(ctx, aid)
    return r


@M.post(r"account/(\d+)/entitlement")
def account_entitlement(ctx, aid):
    """POST /api/m/character/account/<id>/entitlement {op: grant|revoke|expiry, key, id?, expires_at?} - account perks
    (account_entitlements, read by the server at the next use)."""
    a = _account(ctx, aid)
    b = ctx.body or {}
    op = b.get("op")
    exp_at = b.get("expires_at")
    if exp_at in ("", None):
        exp_at = None
    elif not (isinstance(exp_at, str) and re.match(r"^\d{4}-\d{2}-\d{2}([ T]\d{2}:\d{2}(:\d{2})?)?$", exp_at)):
        raise Bad("the end date must be YYYY-MM-DD (empty = never ends)")
    if exp_at and exp_at[:10] <= datetime.date.today().isoformat():
        raise Bad("the end date must be in the future")
    scope = [common.scope("account_entitlements", "account_id = %d" % int(aid), pk=["id"], counters=[], ignore=["updated_at"])]
    if op == "grant":
        key = b.get("key") or "battle_pass"
        if key not in ENTITLEMENTS:
            raise Bad("unknown entitlement %r (the server reads: %s)" % (key, ", ".join(ENTITLEMENTS)))
        ref = "gwadmin-web:%s:%s" % (int(aid), datetime.datetime.now().strftime("%Y%m%d%H%M%S%f"))
        sql = ("INSERT INTO account_entitlements (account_id, entitlement_key, scope_key, starts_at, expires_at, revoked_at, "
               "source, source_reference, metadata) VALUES (%d, %s, 'global', now(), %s, NULL, 'gwadmin-web', %s, "
               "jsonb_build_object('granted_by', 'gwadmin-web character editor'))" % (int(aid), lit(key), lit(exp_at), lit(ref)))
        what = "grant %s to %s%s" % (key, a["username"], " until %s" % exp_at if exp_at else " (never ends)")
    elif op in ("revoke", "expiry"):
        eid = common.to_int(b.get("id"), 1, None, "entitlement id")
        row = next((e for e in a["entitlements"] if e["id"] == eid), None)
        if not row:
            raise NotFound("account %s has no entitlement #%d" % (a["username"], eid))
        if op == "revoke":
            if row.get("revoked_at"):
                return {"account": a, "note": "already revoked"}
            sql = "UPDATE account_entitlements SET revoked_at = now(), updated_at = now() WHERE id = %d AND account_id = %d" % (eid, int(aid))
            what = "revoke %s #%d of %s" % (row["entitlement_key"], eid, a["username"])
        else:
            sql = "UPDATE account_entitlements SET expires_at = %s, updated_at = now() WHERE id = %d AND account_id = %d" % (
                lit(exp_at), eid, int(aid))
            what = "%s #%d of %s ends %s" % (row["entitlement_key"], eid, a["username"], exp_at or "never")
    else:
        raise Bad("op must be grant, revoke or expiry")
    r = ctx.rows_write(scope, what, lambda before: [sql], fields=["account_entitlements.*"], advanced=True,
                       applies="now", target="account " + a["username"])
    r["account"] = _account(ctx, aid)
    return r


# ------------------------------------------------------------------ character delete / restore (the game's rules)
def _lifecycle_scope(aid, cids):
    ids = ", ".join(str(int(x)) for x in cids) or "NULL"
    return [
        common.scope("accounts", "id = %d" % int(aid), pk=["id"], counters=["character_lifecycle_version"],
                     ignore=["last_login_time", "last_logout_time", "last_login_ip", "last_login_mac", "total_online_time",
                             "login_status", "login_presence_token"]),
        common.scope("account_realm", "account_id = %d" % int(aid), pk=["account_id", "realm_id"],
                     counters=["character_lifecycle_version"], ignore=[]),
        # literal ids: a character the player creates after this write is never touched by its Undo
        common.scope("character_base", "id IN (%s)" % ids, pk=["id"], counters=["lifecycle_version"], ignore=[]),
    ]


def _tombstone_sql(aid, cid):
    """The server's compatibility delete (PostgresGameStore.CharacterLifecycle.cs) for one character, as one statement
    block: durable-stream guard, account_realm version + 1, the tombstone, the Tempest mirror. RAISEs if not exact."""
    aid, cid = int(aid), int(cid)
    return ("DO $gwt$ DECLARE v bigint; n int; realm int; BEGIN\n"
            "  SELECT server_id INTO realm FROM character_base WHERE id = %(cid)d AND account_id = %(aid)d;\n"
            "  IF EXISTS (SELECT 1 FROM outbox_consumer_positions WHERE consumer_key = CASE WHEN realm = %(t)d THEN 'character_lifecycle_v1' "
            "ELSE 'character_lifecycle_v2' END AND aggregate_type = CASE WHEN realm = %(t)d THEN 'account_character_slot' ELSE "
            "'account_realm_character_slot' END AND aggregate_key = CASE WHEN realm = %(t)d THEN '%(aid)d:0' ELSE '%(aid)d:' || realm || ':0' END\n"
            "     UNION ALL SELECT 1 FROM outbox_events WHERE consumer_key = CASE WHEN realm = %(t)d THEN 'character_lifecycle_v1' "
            "ELSE 'character_lifecycle_v2' END AND aggregate_type = CASE WHEN realm = %(t)d THEN 'account_character_slot' ELSE "
            "'account_realm_character_slot' END AND aggregate_key = CASE WHEN realm = %(t)d THEN '%(aid)d:0' ELSE '%(aid)d:' || realm || ':0' END) THEN\n"
            "    RAISE EXCEPTION 'gwadmin: the durable character lifecycle stream has started for account #%(aid)d; the server only "
            "deletes through it (CharacterLifecycleDurableStreamActiveException); nothing written';\n"
            "  END IF;\n"
            "  INSERT INTO account_realm (account_id, realm_id) SELECT %(aid)d, realm WHERE realm IS NOT NULL ON CONFLICT DO NOTHING;\n"
            "  UPDATE account_realm SET character_lifecycle_version = character_lifecycle_version + 1 WHERE account_id = %(aid)d "
            "AND realm_id = realm RETURNING character_lifecycle_version INTO v;\n"
            "  UPDATE character_base SET lifecycle_state = 'deleted', lifecycle_version = v, deleted_at = transaction_timestamp(), "
            "restore_until = transaction_timestamp() + interval '%(rd)d days', purge_after = transaction_timestamp() + "
            "interval '%(rd)d days' + interval '%(pd)d days' WHERE id = %(cid)d AND account_id = %(aid)d AND server_id = realm "
            "AND character_slot = 0 AND lifecycle_state = 'active' AND checkpoint_owner_id IS NULL;\n"
            "  GET DIAGNOSTICS n = ROW_COUNT;\n"
            "  IF n <> 1 THEN RAISE EXCEPTION 'gwadmin: character #%(cid)d is not an active, offline character of account #%(aid)d; "
            "nothing written'; END IF;\n"
            "  IF realm = %(t)d THEN UPDATE accounts SET character_lifecycle_version = v WHERE id = %(aid)d; END IF;\n"
            "END $gwt$" % {"aid": aid, "cid": cid, "t": TEMPEST, "rd": RESTORE_DAYS, "pd": PURGE_DAYS})


def _restore_sql(aid, cid):
    """PostgresCharacterLifecycleCommandExecutor RestoreCharacterAsync + AdvanceAccountVersionAsync."""
    aid, cid = int(aid), int(cid)
    return ("DO $gwr$ DECLARE v bigint; n int; realm int; BEGIN\n"
            "  SELECT server_id INTO realm FROM character_base WHERE id = %(cid)d AND account_id = %(aid)d;\n"
            "  IF EXISTS (SELECT 1 FROM character_base WHERE account_id = %(aid)d AND server_id = realm AND character_slot = 0 "
            "AND lifecycle_state = 'active') THEN RAISE EXCEPTION 'gwadmin: the account already has an active character in this "
            "realm (one per account: ux_character_base_active_account_realm_slot); delete it first; nothing written'; END IF;\n"
            "  IF EXISTS (SELECT 1 FROM character_base o, character_base d WHERE d.id = %(cid)d AND o.id <> d.id "
            "AND o.lifecycle_state = 'active' AND lower(o.name) = lower(d.name)) THEN RAISE EXCEPTION 'gwadmin: an active "
            "character already has this name (ux_character_base_active_name_ci); rename one of them first; nothing written'; END IF;\n"
            "  UPDATE account_realm SET character_lifecycle_version = character_lifecycle_version + 1 WHERE account_id = %(aid)d "
            "AND realm_id = realm RETURNING character_lifecycle_version INTO v;\n"
            "  UPDATE character_base SET lifecycle_state = 'active', lifecycle_version = v, deleted_at = NULL, restore_until = NULL, "
            "purge_after = NULL WHERE id = %(cid)d AND account_id = %(aid)d AND server_id = realm AND character_slot = 0 "
            "AND lifecycle_state = 'deleted' AND restore_until > transaction_timestamp();\n"
            "  GET DIAGNOSTICS n = ROW_COUNT;\n"
            "  IF n <> 1 THEN RAISE EXCEPTION 'gwadmin: character #%(cid)d is not a deleted character still inside its %(rd)d-day "
            "restore window; nothing written'; END IF;\n"
            "  IF realm = %(t)d THEN UPDATE accounts SET character_lifecycle_version = v WHERE id = %(aid)d; END IF;\n"
            "END $gwr$" % {"aid": aid, "cid": cid, "t": TEMPEST, "rd": RESTORE_DAYS})


def _char_account(ctx, cid):
    row = _rb(ctx).pg.json("(SELECT json_build_object('id', id, 'account_id', account_id, 'name', name, 'state', lifecycle_state) "
                           "FROM character_base WHERE id = %d)" % int(cid))
    if not row:
        raise NotFound("no character #%s" % cid)
    return row


@M.post(r"lifecycle/(\d+)/(delete|restore)")
def lifecycle(ctx, cid, op):
    """POST /api/m/character/lifecycle/<cid>/delete|restore {confirm: name} - delete a character the game's way
    (restorable for 30 days, then the server purges it) or restore it inside that window. Account must be logged out."""
    ch = _char_account(ctx, cid)
    aid = ch["account_id"]
    a = _account(ctx, aid)
    if op == "delete" and str((ctx.body or {}).get("confirm") or "") != ch["name"]:
        raise Bad("type the character's name to confirm the delete")
    _online_refuse(a, "%s %s" % (op, ch["name"]))
    sql = _tombstone_sql(aid, cid) if op == "delete" else _restore_sql(aid, cid)
    what = ("delete character %s (restorable for %d days)" % (ch["name"], RESTORE_DAYS) if op == "delete"
            else "restore deleted character %s" % ch["name"])
    # character_base.* / accounts.*: the table-level rule. The lifecycle columns are read-only for single edits; this
    # route writes them only with the server's own statements (module docstring).
    r = ctx.rows_write(_lifecycle_scope(aid, [cid]), what, lambda before: [_offline_sql(aid), sql],
                       fields=["character_base.*", "accounts.*"], applies="next_login", target="character " + ch["name"])
    r["account"] = _account(ctx, aid)
    return r


# ------------------------------------------------------------------ account delete
_SCHEMA_CACHE = {}


def _fk_graph(ctx):
    """Every foreign key of the public schema: [(child, [cols], parent, [pcols], deltype)] (read from pg_constraint;
    cached per database for 10 minutes - the schema only changes with a server update)."""
    import time
    key = ("fk", _rb(ctx).db)
    hit = _SCHEMA_CACHE.get(key)
    if hit and time.time() - hit[0] < 600:
        return hit[1]
    rows = _rb(ctx).pg.json("""(SELECT coalesce(json_agg(json_build_object(
        'child', cl.relname, 'parent', pl.relname, 'del', c.confdeltype,
        'cols', (SELECT json_agg(a.attname ORDER BY k.n) FROM unnest(c.conkey) WITH ORDINALITY k(attnum, n)
                 JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum),
        'pcols', (SELECT json_agg(a.attname ORDER BY k.n) FROM unnest(c.confkey) WITH ORDINALITY k(attnum, n)
                  JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.attnum))), '[]')
      FROM pg_constraint c JOIN pg_class cl ON cl.oid = c.conrelid JOIN pg_class pl ON pl.oid = c.confrelid
      JOIN pg_namespace n ON n.oid = cl.relnamespace WHERE c.contype = 'f' AND n.nspname = 'public')""")
    _SCHEMA_CACHE[key] = (time.time(), rows)
    return rows


def _table_meta(ctx, tables):
    """{table: {pk: [cols], always_identity: bool}}"""
    if not tables:
        return {}
    names = ", ".join(lit(t) for t in tables)
    rows = _rb(ctx).pg.json("""(SELECT coalesce(json_object_agg(c.relname, json_build_object(
        'pk', (SELECT json_agg(a.attname ORDER BY array_position(i.indkey::int2[], a.attnum)) FROM pg_index i
               JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) WHERE i.indrelid = c.oid AND i.indisprimary),
        'always_identity', EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND (a.attidentity = 'a' OR a.attgenerated <> '')
                                   AND NOT a.attisdropped))), '{}')
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname IN (%s))""" % names)
    return rows or {}


def _jobj(pairs):
    """jsonb object from 'key, value' pairs, chunked (a PostgreSQL function takes at most 100 arguments)."""
    if not pairs:
        return "'{}'::jsonb"
    return " || ".join("jsonb_build_object(%s)" % ", ".join(pairs[i:i + 40]) for i in range(0, len(pairs), 40))


def _gtable(t):
    try:
        return common.GLOSSARY.table(t) or {}
    except NotFound:
        return {}


def _in(cols, pcols, parent, where):
    lhs = "(%s)" % ", ".join(ident(c) for c in cols) if len(cols) > 1 else ident(cols[0])
    rhs = ", ".join(ident(c) for c in pcols)
    return "%s IN (SELECT %s FROM %s WHERE %s)" % (lhs, rhs, parent, where)


# Evidence the server keeps per player, one row per settled kill, loot pick-up, claim or sealed pet. These are what stop
# a plain delete of any account that has ever killed a monster. A WIPE removes them with the account: nothing else
# reads them once the account is gone (they only stop a kill or a claim being paid twice). Anything NOT on this list
# (guild leadership, other players' rows) still blocks a wipe, so a wipe never touches another player's data.
WIPE_EVIDENCE = {
    "monster_death_reward_settlements", "monster_death_pet_experience", "monster_loot_pickup_claims",
    "online_award_claim_settlements", "faction_crier_daily_claims", "faction_crier_exchange_settlements",
    "faction_crier_weekly_reclaims", "medusa_completion_reward_members", "wonderland_boss_loot_claims",
    "character_title_ownership", "sealed_pet_items",
}
# Undo cannot put these back: a trigger rejects the insert (guard_online_award_claim_insert). They stay in the backup file.
WIPE_NO_UNDO = {"online_award_claim_settlements"}
WIPE_FUNCTION = "public.gwadmin_wipe_account_evidence(integer)"      # sql/wipe-account-evidence.sql; the editor's role cannot delete evidence itself


def _delete_plan(ctx, aid, preview=False):
    """What DELETE FROM accounts WHERE id = <aid> removes (the ON DELETE CASCADE closure, parents first), what keeps a
    row pointing at it (RESTRICT / NO ACTION = evidence: blocks the delete), and what only loses its link (SET NULL)."""
    fks = _fk_graph(ctx)
    where = {"accounts": "id = %d" % int(aid)}
    parents = {"accounts": []}
    order = ["accounts"]
    changed = True
    while changed:                               # closure over ON DELETE CASCADE
        changed = False
        for f in fks:
            if f["del"] != "c" or f["parent"] not in where:
                continue
            cond = _in(f["cols"], f["pcols"], f["parent"], where[f["parent"]])
            if f["child"] not in where:
                where[f["child"]] = cond
                parents[f["child"]] = [f["parent"]]
                changed = True
            elif cond not in where[f["child"]] and f["child"] != f["parent"]:
                where[f["child"]] = "(%s) OR (%s)" % (where[f["child"]], cond)
                parents[f["child"]].append(f["parent"])
                changed = True
            if len(where[f["child"]]) > 6000:
                raise Bad("the cascade from accounts is too deep to plan safely")
    # parents first
    done, seq = set(), []
    pending = [t for t in where]
    for _ in range(len(pending) + 2):
        for t in list(pending):
            if all(p in done or p == t for p in parents.get(t, [])):
                seq.append(t)
                done.add(t)
                pending.remove(t)
    if pending:
        raise Bad("cannot order the cascade tables (%s)" % ", ".join(pending))
    meta = _table_meta(ctx, seq)
    counts = _rb(ctx).pg.json(_jobj(["%s, (SELECT count(*) FROM %s WHERE %s)" % (lit(t), t, where[t]) for t in seq]))
    blockers, setnull = [], []
    for f in fks:
        if f["parent"] in where and f["del"] in ("r", "a") and f["child"] not in where:
            blockers.append(f)
        elif f["parent"] in where and f["del"] == "n":
            setnull.append(f)
    bc = _rb(ctx).pg.json(_jobj(["%s, (SELECT count(*) FROM %s WHERE %s)" % (lit(str(i)), f["child"], _in(f["cols"], f["pcols"], f["parent"], where[f["parent"]]))
                                 for i, f in enumerate(blockers)])) if blockers else {}
    bcounts = [bc.get(str(i), 0) for i in range(len(blockers))]
    block = []
    ev_conds = {}                                # evidence table -> every condition that ties its rows to this account
    for f, n in zip(blockers, bcounts or []):
        if f["child"] in WIPE_EVIDENCE:
            ev_conds.setdefault(f["child"], []).append(_in(f["cols"], f["pcols"], f["parent"], where[f["parent"]]))
        if n:
            g = _gtable(f["child"])
            block.append({"table": f["child"], "rows": n, "references": f["parent"],
                          "what": g.get("description") or g.get("label") or "", "why": "evidence the server keeps (FOREIGN KEY "
                          "... ON DELETE %s)" % ("RESTRICT" if f["del"] == "r" else "NO ACTION")})
    rows = {t: counts.get(t, 0) for t in seq}
    nr = [t for t in seq if meta.get(t, {}).get("always_identity") and rows.get(t)]
    nopk = [t for t in seq if not (meta.get(t) or {}).get("pk")]
    # ---- the wipe: the account plus its evidence rows
    ev_where = {t: " OR ".join("(%s)" % c for c in cs) for t, cs in ev_conds.items()}
    ev_tables = sorted(ev_where)
    ev_meta = _table_meta(ctx, ev_tables)
    ev_counts = _rb(ctx).pg.json(_jobj(["%s, (SELECT count(*) FROM %s WHERE %s)" % (lit(t), t, ev_where[t]) for t in ev_tables])) if ev_tables else {}
    hard = [x for x in block if x["table"] not in WIPE_EVIDENCE]
    # rows in other tables that point at the evidence rows would block deleting them
    for f in fks:
        if f["parent"] in ev_where and f["child"] not in where and f["child"] not in ev_where and f["del"] in ("r", "a", "c"):
            hard.append({"table": f["child"], "rows": 0, "references": f["parent"], "what": "points at evidence rows that a wipe removes",
                         "why": "unexpected foreign key; not wiped"})
    ev_nr = [t for t in ev_tables if ev_counts.get(t) and ((ev_meta.get(t) or {}).get("always_identity") or t in WIPE_NO_UNDO)]
    needs_fn = any(ev_counts.get(t) for t in ev_tables)
    fn_ok = bool(_rb(ctx).pg.json("to_json(to_regprocedure(%s) IS NOT NULL)" % lit(WIPE_FUNCTION))) if needs_fn else True
    ev_nopk = [t for t in ev_tables if ev_counts.get(t) and not (ev_meta.get(t) or {}).get("pk")]
    wipe = {"tables": [{"table": t, "rows": ev_counts.get(t, 0), "label": _gtable(t).get("description") or t, "undo": t not in ev_nr}
                       for t in ev_tables if ev_counts.get(t)],
            "hard_blockers": hard, "not_restorable": ev_nr, "no_pk": ev_nopk,
            "can_wipe": not hard and not nopk and not ev_nopk and fn_ok, "needs_function": needs_fn, "function_installed": fn_ok,
            "total_rows": sum(rows.values()) + sum(ev_counts.get(t, 0) for t in ev_tables)}
    plan = {"tables": [{"table": t, "rows": rows[t], "label": _gtable(t).get("description") or t,
                        "undo": t not in nr} for t in seq if rows[t]],
            "blockers": block, "set_null": [{"table": f["child"], "column": ", ".join(f["cols"])} for f in setnull],
            "not_restorable": nr, "no_pk": nopk, "can_delete": not block and not nopk,
            "total_rows": sum(rows.values()), "wipe": wipe}
    if preview:
        return plan
    plan["_where"], plan["_seq"], plan["_meta"] = where, seq, meta
    plan["_ev_where"], plan["_ev_meta"], plan["_ev_tables"] = ev_where, ev_meta, [t for t in ev_tables if ev_counts.get(t)]
    return plan


@M.post(r"account/(\d+)/delete")
def account_delete(ctx, aid):
    """POST /api/m/character/account/<id>/delete {confirm: username, mode: "delete"|"wipe"|"retire"}
    delete: the account and every row that cascades from it, for good (backup first; Undo re-inserts the rows). Refused
            while online or while evidence rows reference the account.
    wipe:   the same, plus the account's kill / loot / claim evidence rows (WIPE_EVIDENCE). Still refused while online
            or while anything outside that list points at the account (a guild it leads).
    retire: what the game itself allows: delete the active characters the game's way (restorable 30 days) and ban
            the account (the ban needs the round-2 server). Works when evidence blocks a permanent delete."""
    aid = int(aid)
    a = _account(ctx, aid)
    b = ctx.body or {}
    if str(b.get("confirm") or "") != a["username"]:
        raise Bad("type the account's user name to confirm")
    _online_refuse(a, "delete account")
    mode = b.get("mode") or "delete"
    if mode == "retire":
        active = [c["id"] for c in a["characters"] if c["state"] == "active"]
        stmts = [_offline_sql(aid)] + [_tombstone_sql(aid, c) for c in active] + ["UPDATE accounts SET status = 1 WHERE id = %d" % aid]
        r = ctx.rows_write(_lifecycle_scope(aid, [c["id"] for c in a["characters"]]),
                           "retire account %s: delete %d character%s the game's way (restorable %d days) and ban" % (
                               a["username"], len(active), "" if len(active) == 1 else "s", RESTORE_DAYS),
                           lambda before: stmts, fields=["character_base.*", "accounts.status"], applies="next_login",
                           target="account " + a["username"])
        r["account"] = _account(ctx, aid)
        r["note"] = ("" if a["ban_effective"] else "The server running now (stock) ignores the ban flag; the characters are "
                     "deleted, but the player could log in and make a new one until the round-2 update is installed.")
        return r
    if mode not in ("delete", "wipe"):
        raise Bad("mode must be delete, wipe or retire")
    plan = _delete_plan(ctx, aid)
    wipe = mode == "wipe"
    if wipe and not plan["wipe"]["can_wipe"]:
        hb = plan["wipe"]["hard_blockers"]
        if not hb and not plan["wipe"]["function_installed"]:
            raise Refused("Wipe needs one small database function that is not installed yet (sql/wipe-account-evidence.sql; "
                          "deploy-editor-changes.sh installs it). Nothing was changed.")
        raise Refused("account %s cannot be wiped: %s. Take that off the account first (for a guild: transfer or disband it in the "
                      "Guild editor), then wipe again." % (a["username"], "; ".join(
                          "%s: %d row%s" % (x["table"], x["rows"], "" if x["rows"] == 1 else "s") + (" (%s)" % x["what"] if x.get("what") else "")
                          for x in hb) or "tables without a primary key"))
    if not wipe and plan["blockers"]:
        raise Refused("account %s cannot be deleted for good: evidence rows point at it (%s). The editor never deletes "
                      "evidence. Use 'Retire' instead: it deletes the characters the game's way and bans the account." % (
                          a["username"], "; ".join("%s: %d row%s" % (x["table"], x["rows"], "" if x["rows"] == 1 else "s")
                                                    for x in plan["blockers"])))
    if plan["no_pk"]:
        raise Refused("tables without a primary key would cascade (%s): Undo could not restore them" % ", ".join(plan["no_pk"]))
    where, seq, meta = plan["_where"], plan["_seq"], plan["_meta"]
    ev_tables = plan["_ev_tables"] if wipe else []
    where = dict(where)
    meta = dict(meta)
    for t in ev_tables:                          # the evidence goes last in the scope: it points at the account and its characters
        where[t], meta[t] = plan["_ev_where"][t], plan["_ev_meta"][t]
    seq = seq + ev_tables
    not_restorable = plan["not_restorable"] + (plan["wipe"]["not_restorable"] if wipe else [])
    # tables whose identity column is GENERATED ALWAYS cannot be re-inserted by Undo; they are copied to an extra backup
    keep = [t for t in seq if t not in not_restorable]
    scope = []
    for t in keep:
        pk = meta[t]["pk"]
        if t == "accounts":
            scope.append(_acc_scope(aid, ignore=[]))
        else:
            scope.append(common.scope(t, where[t], pk=pk, counters=[], ignore=[]))
    extra = {}
    if not_restorable:
        rb = _rb(ctx)
        extra = rb.pg.json("json_build_object(%s)" % ", ".join(
            "%s, (SELECT coalesce(json_agg(to_jsonb(x)), '[]') FROM %s x WHERE %s)" % (lit(t), t, where[t])
            for t in not_restorable))
        rb._backup("account-%s-extra" % a["username"], extra, datetime.datetime.now())
    chars = ", ".join(c["name"] for c in a["characters"]) or "no characters"
    total = plan["wipe"]["total_rows"] if wipe else plan["total_rows"]
    stmts = ([_offline_sql(aid)] + (["SELECT %s" % WIPE_FUNCTION.replace("(integer)", "(%d)" % aid)] if ev_tables else []) +
             ["DELETE FROM accounts WHERE id = %d" % aid])
    r = ctx.rows_write(scope, "%s account %s for good (%s; %d rows)" % ("WIPE" if wipe else "DELETE", a["username"], chars, total),
                       lambda before: stmts, fields=["accounts.*"], applies="now", target="account " + a["username"])
    r["deleted"] = {"tables": plan["tables"] + (plan["wipe"]["tables"] if wipe else []), "not_restorable": not_restorable,
                    "set_null": plan["set_null"]}
    r["note"] = ("Account %s and its rows are gone%s. Undo in the change log puts them back%s." % (
        a["username"], " (with its kill, loot and claim records)" if wipe else "",
        (" (except %s: kept in the backup folder)" % ", ".join(not_restorable)) if not_restorable else ""))
    return r


@M.post(r"gm/(\d+)")
def set_gm(ctx, cid):
    cid = int(cid)
    if ctx.caps.get("gm_authority") != "character_flag":
        raise Refused("The running server does not yet use character GM flags")
    enabled = ctx.body.get("enabled")
    if not isinstance(enabled, bool):
        raise Bad("enabled must be true or false")
    expected = common.to_int(ctx.body.get("expected"), 0, 1, "expected GM flag")
    base = _base(ctx,cid)["base"]
    if enabled and base.get("lifecycle_state") != "active":
        raise Refused("Restore the character before enabling GM powers")
    return ctx.char_write(cid, "GM powers %s" % ("enabled" if enabled else "disabled"),
        lambda before: [common.update_sql("character_base", {"id":cid}, {"GM":int(enabled)}, bump=False)],
        fields=["character_base.*"], applies="next_login", cross_bumps=False,
        expected=[{"table":"character_base", "where":{"id":cid}, "values":{"GM":expected}}])


@M.get(r"diagnostics/(\d+)")
def diagnostics(ctx, cid):
    """Read-only character validation and bounded recent session-end metadata."""
    from .login_diagnostics import recent_sessions
    cid = int(cid)
    rb = _rb(ctx)
    _base(ctx, cid)  # Preserve the normal not-found handling.
    data = rb.dump(cid)
    findings = rb.check_character(data, validators={})
    ctx.app.refresh_gamelog()
    sessions = recent_sessions(ctx.app.ctx.gamelog, cid)
    recent_edits = []
    for change in sorted(ctx.app.changes, key=lambda c: c.get("time", ""), reverse=True):
        if change.get("database") != rb.db or change.get("cid") != cid:
            continue
        recent_edits.append({"id": change["id"], "time": change.get("time"),
                             "action": str(change.get("action", ""))[:1200],
                             "undone": bool(change.get("undone")),
                             "can_undo": change.get("engine") == "reborn" and not change.get("undone") and not change.get("no_undo")})
        if len(recent_edits) >= 12:
            break
    return {"cid": cid, "findings": findings[:120], "sessions": sessions, "recent_edits": recent_edits,
            "note": "Session records appear after the diagnostics game build is activated. An early exit identifies where login ended; it does not prove which edit caused it.",
            "recovery": "Use History to Undo the specific recent edit. Undo preserves a backup, checks for later changes and requires the character offline. Avoid restoring an entire character unless necessary."}
