"""reborn.py - read/write adapter for the 2.51 "reborn" server's PostgreSQL
(godswar-server, /opt/godswar251; source GodsWar-2.51/emulators/src/godswar-server).

Everything goes through the psql command line client (default `sudo -n -u postgres psql`,
i.e. peer authentication on the VPS; nothing stores a password):

  * reads   : one psql run per page, `SET default_transaction_read_only = on`, the result is one
              JSON document (`to_jsonb(row)`) parsed with Decimal so numeric(18,6) stays exact;
  * writes  : one psql run per edit, `-v ON_ERROR_STOP=1`, `BEGIN; ... COMMIT;` - the row lock on
              character_base, an in-transaction online re-check, the statements, the reborn server's
              own pet start-up validators for this character, then COMMIT (deferred constraint
              triggers run there too).  Any error rolls the whole edit back;
  * before every write the character's rows in DUMP_TABLES are dumped to a JSON file, and the
              change record keeps that dump for Undo / Restore dump.

Schema facts used here (file:line in the reborn source, measured on the VPS 2026-10-28):
  item_location 0 = equipment (slots 0..23; real slots EquipmentSlots.cs:7-26), 1 = kit bag
  (0..95 visible: PostgresGameStore.cs:37-38 KitBagProjectionSlots = 96 = 4 pages x 24),
  2 = server temp slots (-32768..-1, PostgresGameStore.Inventory.Projection.cs:44-66),
  3 = warehouse (0..warehouse_capacity-1, docs/warehouse.md; trigger guard_character_warehouse_item_slot).
  Mounts are items: kind 'mount' equipped in slot 20 = the active mount; mount gear kinds
  mounthead/mountarmor/mountsoul/mountornament/mountamulet in slots 15..19 (docs/mount-ride-protocol.md,
  EquipmentEligibility.cs:120-150 "mount gear needs a mount", player lv >= mount lv >= gear lv).
  Online = character_base.checkpoint_owner_id IS NOT NULL (set at world entry, cleared at logout:
  PostgresCharacterCheckpointStore.Ownership.cs:8-58/62-150, called from
  GameClientHandler.CharacterCheckpoints.cs:53/461) OR accounts.login_presence_token IS NOT NULL
  (PostgresAccountStore.cs:262-305) OR accounts.login_status <> 0 (login server / character select,
  PostgresAccountStore.cs:200-245).
"""
import datetime
import decimal
import glob
import json
import os
import re
import shlex
import subprocess
import threading
from decimal import Decimal

try:
    import reborn_validators as V
except ImportError:          # pragma: no cover
    V = None

HERE = os.path.dirname(os.path.abspath(__file__))
D = Decimal

LOC_EQUIP, LOC_BAG, LOC_TEMP, LOC_WAREHOUSE = 0, 1, 2, 3
LOC_NAMES = {0: "equipment", 1: "bag", 2: "temp", 3: "warehouse"}
BAG_SLOTS = 96                     # KitBagProjectionSlots
BAG_PAGE = 24
EQUIP_SLOTS = {0: "Head", 1: "Amulet", 2: "Glove", 3: "Armor", 4: "Cuff", 5: "Girdle", 6: "Shoes", 7: "Leggins",
               8: "Ring1", 9: "Ring2", 10: "MainHand", 11: "OffHand", 12: "Fashion",
               15: "Mount Coronet", 16: "Mount Armor", 17: "Mount Soul", 18: "Mount Saddle", 19: "Mount Tassels",
               20: "Mount"}
MOUNT_GEAR_SLOTS = (15, 16, 17, 18, 19)
MOUNT_SLOT = 20
KIND_SLOT = {"head": 0, "amulet": 1, "glove": 2, "armor": 3, "cloth": 3, "cuff": 4, "girdle": 5, "shoes": 6,
             "leggins": 7, "ring": 8, "weapon": 10, "shield": 11, "stylish": 12, "mounthead": 15, "mountarmor": 16,
             "mountsoul": 17, "mountornament": 18, "mountamulet": 19, "mount": 20}
# Origin client ItemBagsExUI.xml: tab "Equip" (EquipBag0) paperdoll, Rectangle x -> column 42/123/204, y -> row
PAPERDOLL = [[None, 0, 1], [4, 3, 8], [2, 5, 9], [None, 7, None], [10, 6, 11]]
# empty-slot silhouettes (ItemBagsExUI.xml BtnTopPos, in Icon.gwo = atlas 0)
SLOT_ICON = {0: "0,432", 1: "0,684", 2: "0,576", 3: "0,360", 4: "0,504", 5: "0,648", 6: "0,612", 7: "0,504",
             8: "504,648", 9: "504,648", 10: "0,140", 11: "0,140", 12: "0,360",
             15: "0,504", 16: "0,504", 17: "0,504", 18: "0,504", 19: "0,504", 20: "0,504"}
CLASSES = {0: "Warrior", 1: "Champion", 2: "Priest", 3: "Mage"}          # class_templates
CAMPS = {0: "Sparta", 1: "Athens"}
TALENTS = [(1, "Random Event"), (2, "Quest Dispatch"), (4, "Work"), (8, "Healing"), (16, "Merge")]   # PetTalentCatalog.cs:31-52
STAT_NAMES = {1: "Agility", 2: "Strength", 3: "Accuracy", 4: "Technique", 5: "Wisdom", 6: "Luck"}
LEGACY_SAVVY_POLICY = "legacy-high-savvy-range-v1"                      # PetSavvyRuntimeSemantics.cs:9
SAVVY_SOURCE = "basic-plus-scaled-growth-v3"                            # PetSavvyPersistenceContract.cs:10
GROWTH_POLICY = "weak-until-phoenix-v1"

# (table, filter on the character id, primary key, order) - every row the UI can touch
DUMP_TABLES = [
    ("character_base", "id = {cid}", ("id",)),
    ("character_items", "user_id = {cid}", ("id",)),
    ("character_pets", "user_id = {cid}", ("id",)),
    ("character_pet_stat_values", "pet_id IN (SELECT id FROM character_pets WHERE user_id = {cid})", ("pet_id", "stat_code")),
    ("character_pet_skills", "pet_id IN (SELECT id FROM character_pets WHERE user_id = {cid})", ("pet_id", "skill_id")),
    ("character_pet_character_bonuses", "pet_id IN (SELECT id FROM character_pets WHERE user_id = {cid})", ("pet_id", "effect_code")),
    ("character_pet_basic_savvy_previews", "user_id = {cid}", ("user_id",)),
    ("character_pet_growth_previews", "user_id = {cid}", ("user_id",)),
    ("character_skills", "user_id = {cid}", ("user_id", "skill_id")),
    ("character_talents", "user_id = {cid}", ("user_id", "talent_id")),
    ("character_quests", "character_id = {cid}", ("character_id", "quest_id")),
    ("character_title_ownership", "character_id = {cid}", ("character_id", "title")),
    ("character_zodiac_skill_grids", "user_id = {cid}", ("user_id", "grid_index")),
    ("sealed_pet_items", "owner_character_id = {cid}", ("id",)),
]
PARENT_FIRST = ["character_base", "character_pets", "character_items", "sealed_pet_items", "character_pet_stat_values",
                "character_pet_skills", "character_pet_character_bonuses", "character_pet_basic_savvy_previews",
                "character_pet_growth_previews", "character_skills", "character_talents", "character_quests",
                "character_title_ownership", "character_zodiac_skill_grids"]
# More per-character tables the module editors write (schema/WRITE-RULES.md section 7 step 4: quest daily, buffs and
# cooldowns, daily / weekly counters, guild membership). Reborn.enable_extra_dump_tables() appends the ones that exist
# in the connected database to DUMP_TABLES / PARENT_FIRST once, at start-up. A change record written before that has
# no rows for them; Undo / Restore dump leave a table alone when the record's dump does not contain it.
DUMP_TABLES_EXTRA = [
    ("character_title_grants", "character_id = {cid}", ("character_id", "title_id")),
    ("character_quest_daily", "character_id = {cid}", ("character_id", "quest_id")),
    ("character_quest_appraisal", "character_id = {cid}", ("character_id",)),
    ("character_experience_modifiers", "character_id = {cid}", ("character_id", "kind")),
    ("character_bag_consumable_cooldowns", "character_id = {cid}", ("character_id", "cooldown_group")),
    ("character_lucky_gods_wish", "character_id = {cid}", ("character_id",)),
    ("character_wishing_pool_usage", "character_id = {cid}", ("character_id",)),
    ("character_zeus_gift_daily", "character_id = {cid}", ("character_id",)),
    ("character_zeus_gift_weekly", "character_id = {cid}", ("character_id",)),
    ("character_zeus_luck_entries", "character_id = {cid}", ("character_id", "week_start", "item_id")),
    ("lelantine_farm_kill_points", "character_id = {cid}", ("character_id", "faction")),
    ("lelantine_farm_donations", "character_id = {cid}", ("id",)),
    ("legacy_instance_daily_entries", "character_id = {cid}", ("realm_id", "realm_day", "instance_kind", "character_id", "reservation_id")),
    ("medusa_daily_entries", "character_id = {cid}", ("realm_id", "realm_day", "character_id", "reservation_id")),
    ("guild_members", "character_id = {cid}", ("character_id",)),
    ("guild_altar_worship", "character_id = {cid}", ("guild_id", "character_id", "building_type")),
]

# Columns the reborn server owns as bookkeeping (measured: information_schema of the live schema, 2026-10-28).
# Undo / Restore dump never put an old value back into these:
#   SERVER_COUNTERS are monotonic revision counters (fencing / ledger keys: UNIQUE (character_id, wallet_revision,
#     currency_code) on character_currency_ledger, (character_id, inventory_revision, ...) on character_inventory_ledger;
#     the server inserts its next ledger row at the bumped revision). A restore that touches a row moves the
#     counter FORWARD (current + 1) instead of back, so no later server ledger insert can collide.
#   SERVER_FROZEN are session / ownership / online-time state (checkpoint_owner_generation is a fencing token,
#     PostgresCharacterCheckpointStore.Ownership.cs:25-29); they are never written by a restore at all.
SERVER_COUNTERS = {
    "character_base": {"faction_crier_revision", "fighter_level_seal_revision", "inventory_revision", "medusa_reward_revision",
                       "online_award_revision", "pet_shed_revision", "position_revision", "progression_reward_revision",
                       "vitals_revision", "wallet_revision"},
    "character_pets": {"revision"}, "character_pet_stat_values": {"revision"}, "character_pet_skills": {"revision"},
    "character_pet_character_bonuses": {"revision"}, "character_talents": {"outbox_revision"},
}
#   warehouse_revision / warehouse_capacity only move together with a warehouse_expansion_settlements row
#     (triggers guard_character_warehouse_capacity, require_warehouse_capacity_evidence); the editor never writes them,
#     so a difference always comes from the server and a restore must not touch either (schema/WRITE-RULES.md s.6 #7).
SERVER_FROZEN = {
    "character_base": {"checkpoint_owner_id", "checkpoint_owner_generation", "lifecycle_version", "LastLogin_time",
                       "zodiac_last_online_at", "zodiac_online_day", "zodiac_online_duration_ticks",
                       "warehouse_revision", "warehouse_capacity"},
}
# columns ignored when comparing "has the server changed this row since the admin write" (no game data)
NOISE_COLS = {"updated_at"}


def server_cols(t):
    return SERVER_COUNTERS.get(t, set()) | SERVER_FROZEN.get(t, set())


# character_base columns the sheet edits: (label, kind, lo, hi, revision column bumped)
I32 = 2 ** 31 - 1
BASE_EDITABLE = {
    "name": ("name", "name", None, None, None),
    "gender": ("gender", "enum:male,female", None, None, None),
    "fighter_job_lv": ("level", "int", 1, 200, None),
    "fighter_job_exp": ("exp", "int", 0, 2 ** 32 - 1, None),
    "fighter_level_sealed": ("level sealed", "bool", None, None, None),
    "scholar_job_lv": ("scholar level", "int", 0, 200, None),
    "scholar_job_exp": ("scholar exp", "int", 0, I32, None),
    "Money": ("Silver", "int", 0, I32, "wallet_revision"),
    "Stone": ("Gold", "int", 0, I32, "wallet_revision"),
    "BindingGold": ("B-Gold", "int", 0, I32, "wallet_revision"),
    "medusa_honor_points": ("Medusa honor", "int", 0, I32, "wallet_revision"),
    "exchange_point": ("exchange points", "int", 0, I32, "wallet_revision"),
    "exchange_medal": ("exchange medals", "int", 0, I32, "wallet_revision"),
    "SkillPoint": ("talent points", "int", 0, I32, None),
    "SkillExp": ("talent exp", "int", 0, I32, None),
    "curHP": ("HP", "int", 0, I32, "vitals_revision"),
    "curMP": ("MP", "int", 0, I32, "vitals_revision"),
    "MaxHP": ("max HP", "int", 1, I32, None),
    "MaxMP": ("max MP", "int", 1, I32, None),
    "status": ("status", "int", -32768, 32767, None),
    "belief": ("faith", "int", 0, 255, None),
    "prestige": ("prestige", "int", 0, I32, None),
    "earl_rank": ("earl rank", "int", 0, 32767, None),
    "consortia": ("guild id", "int", 0, I32, None),
    "consortia_job": ("guild job", "int", 0, 32767, None),
    "consortia_contribute": ("guild contribution", "int", 0, I32, None),
    "guild_duty": ("guild duty", "int", 0, 32767, None),
    "store_num": ("store_num", "int", 0, 360, None),
    "bag_num": ("bags", "int", 1, 4, None),
    "hair_style": ("hair", "int", 0, 255, None),
    "face_shap": ("face", "int", 0, 255, None),
    "Map": ("map", "int", 0, 32767, "position_revision"),
    "Pos_X": ("X", "float", -2000, 2000, "position_revision"),
    "Pos_Z": ("Z", "float", -2000, 2000, "position_revision"),
    "mutetime": ("mute time", "int", 0, I32, None),
    "pet_shed_capacity": ("pet shed cells", "int", 2, 8, None),
    "selected_title_id": ("selected title", "int", 0, I32, None),
    "quest_current_id": ("current quest", "int", 0, I32, None),
    "zodiac_type": ("zodiac sign", "int", 0, 12, None),
    "zodiac_level": ("zodiac level", "int", 0, 32767, None),
    "zodiac_energy": ("zodiac energy", "int", 0, I32, None),
    "GM": ("GM flag", "int", 0, 1, None),
}
ITEM_FIELDS = ["prop_id", "stack", "bound", "item_quality", "item_grade", "item_exp", "holy_suit_code", "holy_socket_count",
               "attribute1", "attribute2", "attribute3", "attribute4", "attribute5",
               "attribute_level1", "attribute_level2", "attribute_level3", "attribute_level4", "attribute_level5",
               "class_attribute1", "elemental_attribute1", "elemental_attribute2"] + \
              ["holy_socket%d_%s" % (i, k) for i in range(1, 7) for k in ("effect_id", "level")] + \
              ["holy_socket%d_value" % i for i in range(1, 5)]
PET_EDITABLE = {
    "name": ("name", "name"), "sex": ("sex", "int:0:1"), "experience": ("exp", "int:0:%d" % (2 ** 62)),
    "rank": ("rank", "dec2:0:655.35"), "completed_rebirths": ("rebirths done", "int:0:32767"),
    "rebirths_remaining": ("rebirths left", "int:0:32767"), "completed_pet_merges": ("merges", "int:0:%d" % I32),
    "soul_contract_stage": ("soul contract", "int:0:6"), "current_energy": ("energy", "int:0:%d" % I32),
    "maximum_energy": ("max energy", "int:1:%d" % I32), "amity": ("amity", "int:0:%d" % I32),
    "satiety": ("satiety", "int:0:%d" % I32), "remaining_lifetime": ("lifetime", "int:0:65535"),
    "available_stat_points": ("stat points", "int:0:%d" % I32), "bound": ("bound", "bool"),
    "opened_skill_slots": ("opened skill cells", "int:1:12"), "available_skill_slots": ("available skill cells", "int:1:12"),
}


class RebornError(Exception):
    """A client-side error (shown to the admin, HTTP 400)."""


def lit(v):
    """SQL literal (standard_conforming_strings is on: only ' needs doubling)."""
    if v is None:
        return "NULL"
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(int(v))
    if isinstance(v, (float, Decimal)):
        if isinstance(v, float) and (v != v or v in (float("inf"), float("-inf"))):
            raise RebornError("not a finite number: %r" % v)
        return str(v)
    s = str(v)
    if "\x00" in s:
        raise RebornError("NUL byte in a value")
    return "'" + s.replace("'", "''") + "'"


def ident(name):
    if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", name):
        raise RebornError("bad identifier %r" % name)
    return '"%s"' % name


def jdefault(o):
    if isinstance(o, Decimal):
        return str(o)
    return str(o)


def canon(row):
    return json.dumps(row, sort_keys=True, default=jdefault, ensure_ascii=False)


def to_int(v, lo, hi, what):
    try:
        if isinstance(v, str):
            v = v.strip().replace(",", "")
        n = int(D(str(v))) if not isinstance(v, bool) else int(v)
        if D(str(v)) != n:
            raise ValueError
    except (ValueError, decimal.InvalidOperation, TypeError):
        raise RebornError("%s must be a whole number (got %r)" % (what, v))
    if (lo is not None and n < lo) or (hi is not None and n > hi):
        raise RebornError("%s must be %s..%s (got %d)" % (what, lo, hi, n))
    return n


def to_dec(v, lo, hi, what, places=None):
    try:
        d = D(str(v).strip())
    except (decimal.InvalidOperation, TypeError):
        raise RebornError("%s must be a number (got %r)" % (what, v))
    if not d.is_finite() or d < D(str(lo)) or d > D(str(hi)):
        raise RebornError("%s must be %s..%s (got %s)" % (what, lo, hi, v))
    if places is not None and d != d.quantize(D(1).scaleb(-places)):
        raise RebornError("%s has at most %d decimals" % (what, places))
    return d


def ascii_name(v, what="name", maxlen=31):
    s = str(v or "").strip()
    if not s or len(s) > maxlen or not re.match(r"^[A-Za-z0-9_\-\. ]+$", s):
        raise RebornError("%s must be 1..%d ASCII letters/digits/_-. (got %r)" % (what, maxlen, v))
    return s


def item_defaults(slot):
    """Editor grants keep compatible quality/grade defaults and are tradable unless explicitly bound."""
    if slot is not None and int(slot) >= 0:
        return {"item_quality": 1, "item_grade": 1, "bound": 0}
    return {"item_quality": 0, "item_grade": 10, "bound": 0}


def pet_egg_info(cat, pid):
    for sid, species in cat.get("species", {}).items():
        if species.get("egg_item_id") is not None and int(species["egg_item_id"]) == int(pid):
            aptitudes=sorted({int(p["aptitude"]) for p in (cat.get("profiles") or []) if int(p["species"]) == int(sid)})
            return {"species":int(sid),"name":species.get("display_name"),"aptitudes":aptitudes}
    return None


def slot_label(loc, slot):
    loc, slot = int(loc), int(slot)
    if loc == LOC_EQUIP:
        return "equipment %s" % EQUIP_SLOTS.get(slot, "slot %d" % slot)
    if loc == LOC_BAG:
        return "bag %s-%d" % (["I", "II", "III", "IV"][slot // BAG_PAGE] if 0 <= slot < BAG_SLOTS else "?", slot % BAG_PAGE + 1)
    if loc == LOC_WAREHOUSE:
        return "warehouse SB-%d #%d" % (slot // 40 + 1, slot % 40 + 1)
    return "%s slot %d" % (LOC_NAMES.get(loc, loc), slot)


def to_bool(v):
    if isinstance(v, bool):
        return v
    return str(v).strip().lower() in ("1", "true", "yes", "on", "t")


# --------------------------------------------------------------------------- psql
class Psql:
    def __init__(self, base_cmd, db):
        self.base = shlex.split(base_cmd) if isinstance(base_cmd, str) else list(base_cmd)
        self.db = db

    def cmd(self):
        return self.base + ["-X", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-d", self.db]

    def run(self, script, timeout=60):
        try:
            p = subprocess.run(self.cmd(), input=script.encode("utf-8"), capture_output=True, timeout=timeout)
        except subprocess.TimeoutExpired:
            raise RebornError("psql did not finish within %ds" % timeout)
        except OSError as e:
            raise RebornError("cannot run psql (%s): %s" % (" ".join(self.base), e))
        out, err = p.stdout.decode("utf-8", "replace"), p.stderr.decode("utf-8", "replace")
        if p.returncode != 0:
            msg = "\n".join(l for l in err.splitlines() if l.strip() and not l.startswith("WARNING:  SET LOCAL")) or "psql failed (%d)" % p.returncode
            raise RebornError(msg.strip())
        return out

    def json(self, expr, timeout=60):
        """Evaluate one SQL expression that yields json/jsonb, read-only."""
        out = self.run("SET default_transaction_read_only = on;\nSELECT 'GWJSON|' || coalesce((%s)::jsonb::text, 'null');\n" % expr, timeout)
        for line in out.splitlines():
            if line.startswith("GWJSON|"):
                return json.loads(line[7:], parse_float=Decimal)
        raise RebornError("no result from psql: %s" % out[:300])


# --------------------------------------------------------------------------- the adapter
class Reborn:
    def __init__(self, psql_cmd="sudo -n -u postgres psql", db="godswar", writable=False, live=False,
                 backup_dir=None, log_path=None, allow_force=False):
        self.db = db
        self.pg = Psql(psql_cmd, db)
        self.writable = writable
        self.live = live
        self.allow_force = allow_force
        self.backup_dir = backup_dir or os.path.join(os.getcwd(), "gwadmin-backups")
        self.log_path = log_path
        self.lock = threading.RLock()
        self._cat = None
        self._client = None
        self._status = None
        self.identity_info = {}

    # ------------------------------------------------------------ catalogues
    def client(self):
        if self._client is None:
            c = {}
            for name in ("items", "pets", "petskills", "quests", "attributes"):
                p = os.path.join(HERE, "catalog-origin", name + ".json")
                try:
                    with open(p, encoding="utf-8") as f:
                        c[name] = json.load(f)
                except (OSError, ValueError):
                    c[name] = {}
            self._client = c
        return self._client

    def cat(self, refresh=False):
        """Server-side templates (what the reborn server accepts), cached per run."""
        if self._cat is None or refresh:
            expr = """json_build_object(
 'items', (SELECT json_object_agg(id, json_build_object('id', id, 'kind', kind, 'name', display_name, 'key', name_key,
     'slot', equipment_slot, 'classes', class_ids, 'minlv', min_level, 'maxlv', max_level, 'icon', icon, 'texture', texture,
     'overlap', COALESCE(NULLIF(stats->>'Overlap', ''), '1'), 'bindtype', stats->>'BindType')) FROM item_templates),
 'attributes', (SELECT json_object_agg(id, json_build_object('id', id, 'key', name_key, 'stat', stat_type, 'max', max_level,
     'percent', percent, 'values', level_values)) FROM item_attribute_templates),
 'qualities', (SELECT json_object_agg(level, name) FROM item_quality_levels),
 'grades', (SELECT json_object_agg(level, stars) FROM item_grade_levels),
 'skills', (SELECT json_object_agg(skill_id, json_build_object('id', skill_id, 'name', display_name, 'base', base_name,
     'level', skill_level, 'classes', class_ids, 'minlv', min_level)) FROM skill_templates),
 'talents', (SELECT json_object_agg(id, json_build_object('id', id, 'name', name, 'class', class_id)) FROM talent_templates),
 'maps', (SELECT json_object_agg(map_id, scene_key) FROM map_templates),
 'pet_revision', (SELECT revision FROM pet_content_publication WHERE family = 'pets'),
 'pet_settings', (SELECT to_jsonb(s) FROM pet_content_settings s WHERE s.revision = (SELECT revision FROM pet_content_publication WHERE family = 'pets')),
 'species', (SELECT json_object_agg(species_id, to_jsonb(s)) FROM pet_content_species_definitions s
     WHERE s.revision = (SELECT revision FROM pet_content_publication WHERE family = 'pets')),
 'aptitudes', (SELECT json_object_agg(aptitude, to_jsonb(a)) FROM pet_content_aptitude_definitions a
     WHERE a.revision = (SELECT revision FROM pet_content_publication WHERE family = 'pets')),
 'hatch_steps', (SELECT json_agg(to_jsonb(h) ORDER BY aptitude, outcome_order) FROM pet_content_hatch_rank_steps h
     WHERE h.revision = (SELECT revision FROM pet_content_publication WHERE family = 'pets')),
 'profiles', (SELECT json_agg(json_build_object('species', species_id, 'aptitude', aptitude, 'lifetime', lifetime,
     'starter', starter_skill_id)) FROM pet_content_native_profiles p
     WHERE p.revision = (SELECT revision FROM pet_content_publication WHERE family = 'pets')),
 'titles', (SELECT json_object_agg(title, display_name) FROM medusa_reward_title_definitions)
)"""
            c = self.pg.json(expr, timeout=120)
            try:       # runtime pet-skill ids of the published learned-skill content (docs/pet-skills-talents.md)
                c["pet_skill_ids"] = self.pg.json("(SELECT json_agg(DISTINCT runtime_skill_id) FROM pet_skill_curve_steps)")
            except RebornError:
                c["pet_skill_ids"] = []
            for k in ("items", "attributes", "qualities", "grades", "skills", "talents", "maps", "species", "aptitudes", "titles"):
                c[k] = c.get(k) or {}
            c["pet_skill_ids"] = set(int(x) for x in (c.get("pet_skill_ids") or []))
            self._cat = c
        return self._cat

    def item_meta(self, pid):
        pid = int(pid)
        srv = self.cat()["items"].get(str(pid))
        cli = self.client()["items"].get(str(pid))
        name = (srv or {}).get("name") or (cli or {}).get("name") or ("item %d" % pid)
        icon, atlas = "", 0
        if cli and cli.get("icon"):
            icon, atlas = cli["icon"], cli.get("atlas", 0)
        elif srv and srv.get("icon"):
            icon = srv["icon"]
            t = os.path.basename((srv.get("texture") or "").replace("\\", "/"))
            atlas = {"Icon.gwo": 0, "Icon2.gwo": 1, "Icon3.gwo": 2, "Icon4.gwo": 3}.get(t, 0)
        try:
            overlap = max(1, int((srv or {}).get("overlap") or (cli or {}).get("overlap") or 1))
        except ValueError:
            overlap = 1
        # item_templates.equipment_slot is 0 for 1,915 'consume item' templates (measured), so the slot only counts
        # for the equipment kinds (KIND_SLOT); everything else is not equipment (-1)
        slot = (srv or {}).get("slot", -1)
        slot = -1 if slot is None or (srv or {}).get("kind") not in KIND_SLOT else slot
        egg=pet_egg_info(self.cat(),pid)
        defaults={"item_quality":1,"item_grade":1,"bound":0} if egg else item_defaults(slot)
        return {"id": pid, "name": name, "icon": icon, "atlas": atlas, "kind": (srv or {}).get("kind") or (cli or {}).get("type", ""),
                "slot": slot, "minlv": (srv or {}).get("minlv"), "classes": (srv or {}).get("classes") or [],
                "overlap": overlap, "desc": (cli or {}).get("desc", ""), "server": bool(srv), "client": bool(cli),
                "defaults": defaults, "pet_egg":egg}

    def item_search(self, q, limit=40, kind=None, slot=None):
        """slot: an equipment slot - only templates whose equipment_slot fits it (rings 8 <-> 9)."""
        from modules.common import CATALOG
        q = (q or "").strip().lower()
        out = []
        if getattr(self, "_metas", None) is None or self._metas[0] is not self.cat():
            self._metas = (self.cat(), [self.item_meta(int(k)) for k in sorted(self.cat()["items"], key=int)])
        for m in self._metas[1]:
            pid = m["id"]
            if kind and m["kind"] != kind and not (kind == "mountgear" and m["kind"].startswith("mount") and m["kind"] != "mount"):
                continue
            if slot is not None and not (m["slot"] == slot or (m["slot"] in (8, 9) and slot in (8, 9))):
                continue
            if not CATALOG.matches("item", pid, q, m["name"], m["kind"], m.get("desc", "")):
                continue
            out.append(m)
        if q and not q.isdigit():
            out.sort(key=lambda m: (0 if m["name"].lower().startswith(q) else 1, m["minlv"] or 0, m["name"]))
        return out[:limit]

    def catalog_search(self, typ, entries, query, limit=80):
        """Compatibility search routes share the same description/effect text as the main picker."""
        from modules.common import CATALOG
        hits = [v for v in entries if CATALOG.matches(typ, v["id"], query,
            v.get("name"), v.get("title"), v.get("key"), v.get("desc"), v.get("description"))]
        return sorted(hits, key=lambda v: v["id"])[:limit]

    def attribute_catalog(self):
        """item_attribute_templates + the player-facing label of the stat type (catalog-origin/attributes.json)."""
        labels = self.client().get("attributes") or {}
        out = {}
        for k, a in self.cat()["attributes"].items():
            lab = labels.get(str(a.get("stat"))) or {}
            vals = [str(v) for v in (a.get("values") or [])]
            out[k] = dict(a, label=lab.get("label") or a.get("key"), label_source=lab.get("source", ""), values=vals)
        return out

    def petskill_species(self, sid):
        """Species ids a pet skill is restricted to, or None.  The Origin client's skill text says
        'Available to <family> only' (Pet_Skill.xml Info -> Message_Pet.dat); the family is the skill Type, and a
        species belongs to the family whose Type its starter skill has (pet_content_species_definitions
        .starter_skill_id; measured: Loyal Dog starter Guard I = Type 101 = 'Royal Dogs')."""
        ps = self.client()["petskills"]
        s = ps.get(str(sid)) or {}
        m = re.search(r"Available to (.+?) only", s.get("info", ""))
        if not m:
            return None
        allowed = set()
        for sp_id, sp in self.cat()["species"].items():
            st = ps.get(str(sp.get("starter_skill_id"))) or {}
            if st and st.get("type") == s.get("type"):
                allowed.add(int(sp_id))
        return {"family": m.group(1), "species": sorted(allowed)} if allowed else None

    # ------------------------------------------------------------ status / lists
    def status(self):
        try:
            r = self.pg.json("json_build_object('characters', (SELECT count(*) FROM character_base), 'pets', "
                             "(SELECT count(*) FROM character_pets), 'items', (SELECT count(*) FROM character_items), "
                             "'templates', (SELECT count(*) FROM item_templates), 'db', current_database(), 'version', current_setting('server_version'))", timeout=20)
            r.update({"available": True, "database": self.db, "writable": self.writable, "live": self.live, "identity": self.identity_info,
                      "psql": " ".join(self.pg.base),
                      "note": ("LIVE reborn database: writes enabled (--live)" if self.live and self.writable else
                               "writes enabled" if self.writable else
                               "read-only: the live reborn DB 'godswar' needs --live --reborn-db godswar to be written")})
            return r
        except RebornError as e:
            return {"available": False, "database": self.db, "psql": " ".join(self.pg.base),
                    "note": "reborn PostgreSQL not reachable: %s" % str(e)[:300]}

    def log_state(self):
        """name -> 'online'/'offline' from the server log's last [world] joined/left line (optional)."""
        if not self.log_path:
            return {}
        try:
            if os.access(self.log_path, os.R_OK):
                with open(self.log_path, "rb") as f:
                    f.seek(0, 2)
                    n = f.tell()
                    f.seek(max(0, n - 2_000_000))
                    data = f.read().decode("utf-8", "replace")
            else:
                p = subprocess.run(["sudo", "-n", "tail", "-c", "2000000", self.log_path], capture_output=True, timeout=20)
                data = p.stdout.decode("utf-8", "replace")
        except (OSError, subprocess.SubprocessError):
            return {}
        st = {}
        for m in re.finditer(r"\[world\] (joined|left) realm=\S+ instance=\S+ map=\S+ character=(\S+)", data):
            st[m.group(2)] = "online" if m.group(1) == "joined" else "offline"
        return st

    def characters(self, q=""):
        where = ""
        if q:
            where = "WHERE c.name ILIKE %s OR a.username ILIKE %s OR CAST(c.id AS text) = %s" % (
                lit("%" + q + "%"), lit("%" + q + "%"), lit(q))
        rows = self.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.username, r.id), '[]') FROM (
            SELECT c.id, c.account_id, a.username, c.name, c.fighter_job_lv AS level, c.profession, c.camp, c."GM" AS gm,
                   c."Map" AS map, c."Money" AS money, c."Stone" AS gold, c.lifecycle_state,
                   c.checkpoint_owner_id IS NOT NULL AS owned, a.login_status, a.login_presence_token IS NOT NULL AS presence,
                   c."LastLogin_time" AS last_login, (SELECT count(*) FROM character_pets p WHERE p.user_id = c.id) AS pets
            FROM character_base c JOIN accounts a ON a.id = c.account_id %s) r)""" % where)
        logst = self.log_state()
        maps = self.cat()["maps"]
        for r in rows:
            r["class"] = CLASSES.get(r["profession"], str(r["profession"]))
            r["camp_name"] = CAMPS.get(r["camp"], str(r["camp"]))
            r["map_name"] = (maps.get(str(r["map"])) or str(r["map"])).replace("_", " ")
            r["log"] = logst.get(r["name"], "unknown")
            r["online"] = bool(r["owned"] or r["presence"] or r["login_status"] or r["log"] == "online")
        return rows

    # ------------------------------------------------------------ dump / sheet
    @staticmethod
    def dump_expr(cid):
        cid = int(cid)
        parts = []
        for t, where, pk in DUMP_TABLES:
            order = ", ".join(ident(c) for c in pk)
            parts.append("%s, (SELECT coalesce(json_agg(to_jsonb(t) ORDER BY %s), '[]') FROM %s t WHERE %s)" % (
                lit(t), order, t, where.format(cid=cid)))
        return "json_build_object(%s)" % ", ".join(parts)

    def dump(self, cid):
        return self.pg.json(self.dump_expr(cid))

    def preview(self, cid, statements):
        """Run the statements in a transaction, dump the character's rows as they would be, ROLLBACK."""
        lines = ["BEGIN;", "SET LOCAL lock_timeout = '5s';", "SET LOCAL statement_timeout = '60s';"]
        lines += [s.rstrip().rstrip(";") + ";" for s in statements]
        lines.append("SELECT 'GWJSON|' || (%s)::jsonb::text;" % self.dump_expr(cid))
        lines.append("ROLLBACK;")
        out = self.pg.run("\n".join(lines) + "\n", timeout=120)
        for line in out.splitlines():
            if line.startswith("GWJSON|"):
                return json.loads(line[7:], parse_float=Decimal)
        raise RebornError("dry run gave no result: %s" % out[:300])

    def identity(self):
        """What the configured psql really reaches (the live gate is decided on this, not on the argument)."""
        r = self.pg.json("json_build_object('db', current_database(), 'port', inet_server_port(), "
                         "'addr', host(inet_server_addr()), 'version', current_setting('server_version'))", timeout=20)
        return r

    def enable_extra_dump_tables(self):
        """Append the DUMP_TABLES_EXTRA tables that exist in this database to DUMP_TABLES / PARENT_FIRST (once).
        Returns the names added. A database without one of them (older schema) simply does not dump it."""
        have = {t for t, _, _ in DUMP_TABLES}
        want = [x for x in DUMP_TABLES_EXTRA if x[0] not in have]
        if not want:
            return []
        exists = self.pg.json("json_build_object(%s)" % ", ".join(
            "%s, to_regclass(%s) IS NOT NULL" % (lit(t), lit("public." + t)) for t, _, _ in want), timeout=20)
        added = []
        for t, where, pk in want:
            if exists.get(t):
                DUMP_TABLES.append((t, where, pk))
                PARENT_FIRST.append(t)
                added.append(t)
        return added

    def online_state(self, cid):
        r = self.pg.json("""(SELECT json_build_object('owner', c.checkpoint_owner_id, 'generation', c.checkpoint_owner_generation,
            'login_status', a.login_status, 'presence', a.login_presence_token IS NOT NULL, 'name', c.name,
            'last_login', a.last_login_time, 'last_logout', a.last_logout_time)
            FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.id = %d)""" % int(cid))
        if not r:
            raise RebornError("no reborn character #%s" % cid)
        r["log"] = self.log_state().get(r["name"], "unknown") if self.log_path else "not configured"
        reasons = []
        if r["owner"]:
            reasons.append("character_base.checkpoint_owner_id is set (a game session owns this character)")
        if r["presence"]:
            reasons.append("accounts.login_presence_token is set (player in the world)")
        if r["login_status"]:
            reasons.append("accounts.login_status=%s (logged in to the login server / character select)" % r["login_status"])
        if r["log"] == "online":
            reasons.append("server log: last [world] event for %s is 'joined'" % r["name"])
        r["online"] = bool(reasons)
        r["reasons"] = reasons
        return r

    def cell(self, row):
        m = self.item_meta(row["prop_id"])
        cat = self.cat()
        attrs = []
        for i in range(1, 6):
            a = row.get("attribute%d" % i)
            if a is not None:
                am = cat["attributes"].get(str(a)) or {}
                attrs.append({"n": i, "id": a, "level": row.get("attribute_level%d" % i), "key": am.get("key", "?"),
                              "max": am.get("max"), "known": bool(am)})
        holy = []
        for i in range(1, 7):
            e = row.get("holy_socket%d_effect_id" % i)
            if e is not None:
                holy.append({"n": i, "effect": e, "level": row.get("holy_socket%d_level" % i),
                             "value": row.get("holy_socket%d_value" % i) if i <= 4 else None})
        q = row.get("item_quality")
        return dict(row, name=m["name"], icon=m["icon"], atlas=m["atlas"], kind=m["kind"], tslot=m["slot"],
                    minlv=m["minlv"], overlap=m["overlap"], desc=m["desc"], known_server=m["server"],
                    known_client=m["client"], attrs=attrs, holy=holy,
                    quality_name=cat["qualities"].get(str(q), "Q%s" % q), sealed=False)

    def sheet(self, cid):
        cid = int(cid)
        d = self.dump(cid)
        if not d["character_base"]:
            raise RebornError("no reborn character #%d" % cid)
        b = d["character_base"][0]
        cat = self.cat()
        cli = self.client()
        acct = self.pg.json("(SELECT json_build_object('id', id, 'username', username, 'login_status', login_status, "
                            "'status', status, 'donator_tier', donator_tier, 'last_login_time', last_login_time, "
                            "'last_logout_time', last_logout_time, 'total_online_time', total_online_time, "
                            "'presence', login_presence_token IS NOT NULL) FROM accounts WHERE id = %d)" % int(b["account_id"]))
        sealed_items = {s["item_instance_id"] for s in d["sealed_pet_items"]}
        equip, bag, wh, temp, other = {}, [None] * BAG_SLOTS, [None] * int(b["warehouse_capacity"]), [], []
        for it in d["character_items"]:
            c = self.cell(it)
            c["sealed"] = it["id"] in sealed_items
            loc, sl = it["item_location"], it["slot_index"]
            if loc == LOC_EQUIP:
                if sl in equip:
                    other.append(c)
                else:
                    equip[sl] = c
            elif loc == LOC_BAG and 0 <= sl < BAG_SLOTS and bag[sl] is None:
                bag[sl] = c
            elif loc == LOC_WAREHOUSE and 0 <= sl < len(wh) and wh[sl] is None:
                wh[sl] = c
            elif loc == LOC_TEMP:
                temp.append(c)
            else:
                other.append(c)
        mounts = [self.cell(it) for it in d["character_items"] if self.item_meta(it["prop_id"])["kind"] == "mount"]
        # pets
        stats, skills, bonuses = {}, {}, {}
        for s in d["character_pet_stat_values"]:
            stats.setdefault(s["pet_id"], []).append(s)
        for s in d["character_pet_skills"]:
            skills.setdefault(s["pet_id"], []).append(s)
        for s in d["character_pet_character_bonuses"]:
            bonuses.setdefault(s["pet_id"], []).append(s)
        pets = []
        for p in d["character_pets"]:
            sp = cli["pets"].get(str(p["species_id"])) or {}
            ssp = cat["species"].get(str(p["species_id"])) or {}
            apt = cat["aptitudes"].get(str(p["aptitude"])) or {}
            st = sorted(stats.get(p["id"], []), key=lambda s: s["stat_code"])
            for s in st:
                s["stat_name"] = STAT_NAMES.get(s["stat_code"], str(s["stat_code"]))
                s["effective_growth"] = str(D(str(s["base_growth_rate"])) + D(str(s["growth_acceleration"])))
            sk = []
            for s in sorted(skills.get(p["id"], []), key=lambda s: s["slot_index"]):
                cs = cli["petskills"].get(str(s["skill_id"])) or {}
                sk.append(dict(s, name=cs.get("name") or "skill %d" % s["skill_id"], icon=cs.get("icon", ""), atlas=1,
                               info=cs.get("info", ""), known_client=bool(cs),
                               known_server=(not cat["pet_skill_ids"]) or s["skill_id"] in cat["pet_skill_ids"]))
            pets.append(dict(p, species_name=sp.get("name") or ssp.get("display_name") or "species %s" % p["species_id"],
                             icon=sp.get("icon_male" if p["sex"] == 1 else "icon_female") or "", atlas=1,
                             aptitude_name=apt.get("display_name") or str(p["aptitude"]),
                             talents=[{"bit": bit, "name": n, "on": bool(int(p["talent_mask"]) & bit)} for bit, n in TALENTS],
                             stats=st, skills=sk, bonuses=bonuses.get(p["id"], []),
                             sealed=any(s["pet_id"] == p["id"] for s in d["sealed_pet_items"]),
                             savvy_total=str(sum((D(str(s["initial_savvy"])) for s in st), D(0))),
                             growth_total=str(sum((D(str(s["base_growth_rate"])) for s in st), D(0)))))
        skills_c = [dict(s, name=(cat["skills"].get(str(s["skill_id"])) or {}).get("name", "skill %d" % s["skill_id"]))
                    for s in d["character_skills"]]
        talents_c = [dict(t, name=(cat["talents"].get(str(t["talent_id"])) or {}).get("name", "talent %d" % t["talent_id"]))
                     for t in d["character_talents"]]
        quests = [dict(q, title=(cli["quests"].get(str(q["quest_id"])) or {}).get("title", "")) for q in d["character_quests"]]
        done = [{"id": q, "title": (cli["quests"].get(str(q)) or {}).get("title", "")} for q in (b.get("quest_completed_ids") or [])]
        titles = [dict(t, name=cat["titles"].get(str(t["title"]), "")) for t in d["character_title_ownership"]]
        return {
            "engine": "reborn", "database": self.db, "writable": self.writable, "live": self.live,
            "base": b, "account": acct, "online": self.online_state(cid),
            "class": CLASSES.get(b["profession"], str(b["profession"])), "camp_name": CAMPS.get(b["camp"], str(b["camp"])),
            "map_name": (cat["maps"].get(str(b["Map"])) or str(b["Map"])).replace("_", " "),
            "equip": {str(k): v for k, v in equip.items()}, "equip_slots": {str(k): v for k, v in EQUIP_SLOTS.items()},
            "paperdoll": PAPERDOLL, "slot_icons": {str(k): v for k, v in SLOT_ICON.items()},
            "bag": bag, "bag_page": BAG_PAGE, "warehouse": wh, "warehouse_capacity": b["warehouse_capacity"],
            "temp": temp, "misplaced": other, "mounts": mounts,
            "mount": equip.get(MOUNT_SLOT), "mount_gear": {str(s): equip.get(s) for s in MOUNT_GEAR_SLOTS},
            "pets": pets, "pet_shed_capacity": b["pet_shed_capacity"],
            "skills": skills_c, "talents": talents_c, "quests": quests, "quests_done": done,
            "titles": titles, "zodiac": d["character_zodiac_skill_grids"],
            "findings": self.check_character(d),
            "base_editable": {k: v[0] for k, v in BASE_EDITABLE.items()},
            "raw": d,
        }

    # ------------------------------------------------------------ validation
    def validator_ids(self, pet_filter_sql=None):
        """Run the reborn server's own pet start-up validators; {validator: [pet ids]}."""
        if V is None:
            return {}
        cat = self.cat()
        params = {"petContentRevision": lit(cat.get("pet_revision")),
                  "initialPolicyVersion": lit((cat.get("pet_settings") or {}).get("initial_savvy_policy_version")),
                  "migratedLegacySavvyPolicyVersion": lit(LEGACY_SAVVY_POLICY),
                  "initialSourceVersion": lit(SAVVY_SOURCE)}
        parts = []
        for name in ("GROWTH_STATE", "INITIAL_SAVVY_STATE", "SAVVY_BASELINE_STATE"):
            sql = getattr(V, name).format(**params)
            flt = (" WHERE v.id IN (%s)" % pet_filter_sql) if pet_filter_sql else ""
            parts.append("%s, (SELECT coalesce(json_agg(v.id), '[]') FROM (%s) v%s)" % (lit(name), sql, flt))
        return self.pg.json("json_build_object(%s)" % ", ".join(parts), timeout=120)

    def validator_guard_sql(self, cid):
        """DO-block for a write transaction: abort if this character's pets fail a server start-up validator."""
        if V is None:
            return ""
        cat = self.cat()
        params = {"petContentRevision": lit(cat.get("pet_revision")),
                  "initialPolicyVersion": lit((cat.get("pet_settings") or {}).get("initial_savvy_policy_version")),
                  "migratedLegacySavvyPolicyVersion": lit(LEGACY_SAVVY_POLICY),
                  "initialSourceVersion": lit(SAVVY_SOURCE)}
        checks = []
        for name in ("GROWTH_STATE", "INITIAL_SAVVY_STATE", "SAVVY_BASELINE_STATE"):
            sql = getattr(V, name).format(**params)
            checks.append("    SELECT min(v.id) INTO bad FROM (%s) v WHERE v.id IN (SELECT id FROM character_pets WHERE user_id = %d);\n"
                          "    IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: pet %% would fail the reborn server start-up check %s', bad; END IF;"
                          % (sql.replace("$", ""), int(cid), name))
        return "DO $gwv$\nDECLARE bad bigint;\nBEGIN\n%s\nEND\n$gwv$;\n" % "\n".join(checks)

    def check_character(self, d, validators=None):
        """Cross-table checks the database does not enforce (what makes a login or the client fail)."""
        cat, cli = self.cat(), self.client()
        out = []

        def f(sev, what, msg):
            out.append({"severity": sev, "where": what, "msg": msg})
        if not d["character_base"]:
            return out
        b = d["character_base"][0]
        lv = int(b["fighter_job_lv"])
        seen = {}
        equip = {}
        for it in d["character_items"]:
            where = "item #%s %s %s" % (it["id"], LOC_NAMES.get(it["item_location"], it["item_location"]), it["slot_index"])
            key = (it["item_location"], it["slot_index"])
            if key in seen:
                f("error", where, "slot also holds item #%s (two rows in one slot; the client shows only one)" % seen[key])
            seen[key] = it["id"]
            m = self.item_meta(it["prop_id"])
            if not m["server"]:
                f("error", where, "prop_id %s is not in item_templates (the server cannot load it)" % it["prop_id"])
            elif not m["client"]:
                f("warn", where, "prop_id %s (%s) has no definition in the Origin client (missing icon / client error)" % (it["prop_id"], m["name"]))
            if it["stack"] > m["overlap"]:
                f("warn", where, "stack %s exceeds Overlap %s of %s" % (it["stack"], m["overlap"], m["name"]))
            if it["item_location"] == LOC_BAG and it["slot_index"] >= BAG_SLOTS:
                f("warn", where, "bag slot %s is beyond the 96 slots the server projects (invisible)" % it["slot_index"])
            if it["item_location"] == LOC_WAREHOUSE and it["slot_index"] >= int(b["warehouse_capacity"]):
                f("error", where, "warehouse slot beyond warehouse_capacity %s" % b["warehouse_capacity"])
            if it["item_location"] == LOC_EQUIP:
                equip[it["slot_index"]] = (it, m)
                ts = m["slot"]
                ok = ts == it["slot_index"] or (ts in (8, 9) and it["slot_index"] in (8, 9))
                if m["server"] and not ok:
                    f("error", where, "%s (%s, template slot %s) is equipped in slot %s" % (m["name"], m["kind"], ts, it["slot_index"]))
                if it["slot_index"] not in EQUIP_SLOTS:
                    f("warn", where, "equipment slot %s is not one the server uses" % it["slot_index"])
            for i in range(1, 6):
                a = it.get("attribute%d" % i)
                if a is not None:
                    am = cat["attributes"].get(str(a))
                    if not am:
                        f("warn", where, "attribute%d=%s is not in item_attribute_templates" % (i, a))
                    else:
                        al = it.get("attribute_level%d" % i)
                        if al is not None and am.get("max") and al > am["max"]:
                            f("warn", where, "attribute_level%d=%s exceeds max %s of %s" % (i, al, am["max"], am["key"]))
        mount = equip.get(MOUNT_SLOT)
        for s in MOUNT_GEAR_SLOTS:
            if s in equip and not mount:
                f("error", "equipment %s" % EQUIP_SLOTS[s], "mount gear equipped without a mount in slot 20 (EquipmentEligibility)")
            if s in equip and mount and (equip[s][1]["minlv"] or 0) > (mount[1]["minlv"] or 0):
                f("warn", "equipment %s" % EQUIP_SLOTS[s], "gear level %s above mount level %s" % (equip[s][1]["minlv"], mount[1]["minlv"]))
        if mount and (mount[1]["minlv"] or 0) > lv:
            f("warn", "equipment Mount", "mount level %s above character level %s (the server refuses to equip/ride it)" % (mount[1]["minlv"], lv))
        # pets
        settings = cat.get("pet_settings") or {}
        owned = [p for p in d["character_pets"] if p["activity_state"] == "owned"]
        if len(owned) > int(b["pet_shed_capacity"]):
            f("error", "pets", "%d owned pets but pet_shed_capacity %s: the 10237 pet list throws at login (PacketBuilder.Pets.cs:174-180)"
              % (len(owned), b["pet_shed_capacity"]))
        if len(owned) > int(settings.get("maximum_owned_pet_count") or 8):
            f("error", "pets", "more than %s owned pets" % settings.get("maximum_owned_pet_count"))
        stats, skills = {}, {}
        for s in d["character_pet_stat_values"]:
            stats.setdefault(s["pet_id"], []).append(s)
        for s in d["character_pet_skills"]:
            skills.setdefault(s["pet_id"], []).append(s)
        for p in d["character_pets"]:
            where = "pet #%s %s" % (p["id"], p["name"])
            if str(p["species_id"]) not in cat["species"]:
                f("error", where, "species %s is not published (pet list throws)" % p["species_id"])
            if str(p["species_id"]) not in cli["pets"]:
                f("warn", where, "species %s has no Pet.xml entry in the Origin client" % p["species_id"])
            if not (int(settings.get("minimum_level") or 1) <= p["level"] <= int(settings.get("maximum_level") or 120)):
                f("error", where, "level %s outside %s..%s" % (p["level"], settings.get("minimum_level"), settings.get("maximum_level")))
            if not re.match(r"^[\x20-\x7e]{1,31}$", p["name"] or ""):
                f("warn", where, "name is not 1..31 printable ASCII characters (client field is 32 bytes ASCII)")
            act = [s for s in skills.get(p["id"], []) if s["is_active"]]
            if len(act) > p["opened_skill_slots"] or any(s["slot_index"] >= p["opened_skill_slots"] for s in act):
                f("error", where, "an active skill sits outside the %s opened skill cells (ValidatePetSkillCells throws)" % p["opened_skill_slots"])
            slots = [s["slot_index"] for s in skills.get(p["id"], [])]
            if len(slots) != len(set(slots)):
                f("error", where, "two skills share a skill cell")
            for s in skills.get(p["id"], []):
                if s["skill_id"] > 65535:
                    f("error", where, "skill %s does not fit the u16 wire field" % s["skill_id"])
                if str(s["skill_id"]) not in cli["petskills"]:
                    f("warn", where, "skill %s has no Pet_Skill.xml entry in the client" % s["skill_id"])
                if cat["pet_skill_ids"] and s["skill_id"] not in cat["pet_skill_ids"]:
                    f("warn", where, "skill %s is not a published runtime pet skill (no effect)" % s["skill_id"])
                rs = self.petskill_species(s["skill_id"])
                if rs and int(p["species_id"]) not in rs["species"]:
                    f("warn", where, "skill %s is for %s only (species %s), this pet is species %s" % (
                        s["skill_id"], rs["family"], ",".join(map(str, rs["species"])), p["species_id"]))
            st = stats.get(p["id"], [])
            for s in st:
                exp = (D(str(s["base_growth_rate"])) + D(str(s["growth_acceleration"]))) * p["level"]
                if s.get("birth_initial_savvy") is not None and D(str(s["added_savvy"])) != exp:
                    f("error", where, "%s added savvy %s != (base growth + acceleration) x level = %s" % (
                        STAT_NAMES.get(s["stat_code"]), s["added_savvy"], exp))
        if sum(1 for p in d["character_pets"] if p["is_carried"]) > 1:
            f("error", "pets", "more than one carried pet")
        if validators is None and d["character_pets"]:
            validators = self.validator_ids("SELECT id FROM character_pets WHERE user_id = %d" % int(b["id"]))
        for name, ids in (validators or {}).items():
            for pid in ids:
                f("error", "pet #%s" % pid, "fails the reborn server start-up check %s: the server would refuse to start" % name)
        return out

    def selftest(self):
        """Read every character and validate every row; writes nothing."""
        chars = self.pg.json("(SELECT coalesce(json_agg(id ORDER BY id), '[]') FROM character_base)")
        rep = {"engine": "reborn", "database": self.db, "characters": len(chars), "rows": 0, "findings": [],
               "validators": {}, "counts": {}}
        allv = self.validator_ids()
        rep["validators"] = allv
        for cid in chars:
            d = self.dump(cid)
            for t, rows in d.items():
                rep["counts"][t] = rep["counts"].get(t, 0) + len(rows)
                rep["rows"] += len(rows)
            pet_ids = {p["id"] for p in d["character_pets"]}
            mine = {k: [i for i in v if i in pet_ids] for k, v in allv.items()}
            for fnd in self.check_character(d, validators=mine):
                fnd.update({"id": cid, "name": d["character_base"][0]["name"]})
                rep["findings"].append(fnd)
        known = {p for v in allv.values() for p in v}
        orphan = [p for p in known if not any(f.get("where") == "pet #%s" % p for f in rep["findings"])]
        for p in orphan:
            rep["findings"].append({"severity": "error", "id": None, "name": "", "where": "pet #%s" % p,
                                    "msg": "fails a reborn start-up check (pet not owned by any listed character)"})
        rep["errors"] = sum(1 for f in rep["findings"] if f["severity"] == "error")
        rep["warnings"] = sum(1 for f in rep["findings"] if f["severity"] == "warn")
        rep["ok"] = rep["errors"] == 0
        rep["notes"] = [
            "Checked per character: every character_items row against item_templates (server) and the Origin client "
            "catalogue, slot occupancy (the database has no unique slot index), equipment slot vs template slot, "
            "stack vs Overlap, warehouse capacity, append attributes, mount / mount-gear rules; every pet against the "
            "published species/levels, skill cells (PacketBuilder.Pets.cs ValidatePetSkillCells), shed capacity, "
            "and the server's own three start-up validators (reborn_validators.py, run over ALL pets).",
            "CHECK constraints, foreign keys and triggers are enforced by PostgreSQL itself, so a stored row always "
            "satisfies them; the findings here are what the database does NOT enforce.",
            "Nothing is written."]
        return rep

    # ------------------------------------------------------------ write machinery
    def _guard_writable(self):
        if not self.writable:
            raise RebornError("this server is read-only for reborn database %s (live writes need --live --reborn-db godswar)" % self.db)

    def _backup(self, name, d, stamp):
        os.makedirs(self.backup_dir, exist_ok=True)
        path = os.path.join(self.backup_dir, "reborn-%s-%s-%s.json" % (self.db, re.sub(r"[^A-Za-z0-9_-]", "_", name),
                                                                       stamp.strftime("%Y%m%d-%H%M%S-%f")))
        with open(path, "w", encoding="utf-8") as f:
            json.dump({"gwadmin_web": "reborn", "database": self.db, "dumped": stamp.isoformat(timespec="seconds"),
                       "character": name, "tables": d}, f, ensure_ascii=False, indent=1, default=jdefault)
        return path

    def structure_guard_sql(self, cid):
        """DO-block for a write transaction: abort if the character's rows break a rule the database does not enforce
        but the server's login does (checked after the statements, so a write that FIXES such a state passes)."""
        cid = int(cid)
        return ("DO $gws$\nDECLARE bad text;\nBEGIN\n"
                # PacketBuilder.Pets.cs:396-410 ValidatePetSkillCells: an active skill outside the opened cells throws,
                # the login is rejected ('[character-snapshot] rejected phase=login reason=invalid_data')
                "  SELECT format('pet #%%s %%s: active skill %%s sits in cell %%s but only %%s cells are opened', p.id, p.name, s.skill_id, "
                "s.slot_index + 1, p.opened_skill_slots) INTO bad FROM character_pets p JOIN character_pet_skills s ON s.pet_id = p.id "
                "WHERE p.user_id = %d AND s.is_active AND s.slot_index >= p.opened_skill_slots LIMIT 1;\n"
                "  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %% (ValidatePetSkillCells would reject the login); nothing written', bad; END IF;\n"
                # PacketBuilder.Pets.cs:160-185: more owned pets than shed cells -> the 10237 pet list throws
                "  SELECT format('%%s owned pets but pet_shed_capacity %%s', (SELECT count(*) FROM character_pets WHERE user_id = b.id "
                "AND activity_state = 'owned'), b.pet_shed_capacity) INTO bad FROM character_base b WHERE b.id = %d AND "
                "(SELECT count(*) FROM character_pets WHERE user_id = b.id AND activity_state = 'owned') > b.pet_shed_capacity;\n"
                "  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %% (the pet list throws at login); nothing written', bad; END IF;\n"
                "END\n$gws$;\n") % (cid, cid)

    def db_guard_sql(self):
        """The transaction must run in the database this server verified at start-up, and the live 'godswar' only
        when the server was started with --live --reborn-db godswar."""
        return ("DO $gwd$ BEGIN IF current_database() <> %s THEN RAISE EXCEPTION 'gwadmin: connected to %%, expected %s; nothing written', "
                "current_database(); END IF; IF current_database() = 'godswar' AND NOT %s THEN RAISE EXCEPTION "
                "'gwadmin: the live reborn database needs --live --reborn-db godswar; nothing written'; END IF; END $gwd$;"
                % (lit(self.db), self.db.replace("'", ""), "true" if self.live and self.writable else "false"))

    @staticmethod
    def economy_baseline_sql(cid):
        """DO-block run before statements that move wallet_revision / inventory_revision or touch character_items.
        PostgresCharacterEconomyBaseline.EnsureAsync (S/Infrastructure/Inventory/) can create a missing baseline only
        while both revisions are 0; a revision bump on a character without one breaks every later shop / forge / bag
        command. So: a missing baseline is created here exactly as EnsureAsync does (same SQL, 'runtime_cutover')
        when both are 0, and the write is refused otherwise (schema/WRITE-RULES.md section 4)."""
        cid = int(cid)
        return ("DO $gwe$\nDECLARE wr bigint; ir bigint; acc integer;\nBEGIN\n"
                "  IF EXISTS (SELECT 1 FROM public.character_economy_baseline WHERE character_id = %d) THEN RETURN; END IF;\n"
                "  SELECT wallet_revision, inventory_revision, account_id INTO wr, ir, acc FROM public.character_base WHERE id = %d;\n"
                "  IF wr IS NULL THEN RETURN; END IF;\n"
                "  IF wr <> 0 OR ir <> 0 THEN RAISE EXCEPTION 'gwadmin: character %d has no economy baseline row and its "
                "wallet/inventory revision is already above 0; the server could not create one either, so this edit is "
                "refused (every later shop / forge / bag command would fail); nothing written'; END IF;\n"
                "  LOCK TABLE public.character_items IN SHARE MODE;\n"
                "  WITH inserted_baseline AS (\n"
                "    INSERT INTO public.character_economy_baseline (character_id, account_id, wallet_revision, inventory_revision, "
                "silver, gold, binding_gold, item_count, baseline_source)\n"
                "    SELECT c.id, c.account_id, c.wallet_revision, c.inventory_revision, c.\"Money\"::bigint, c.\"Stone\"::bigint, "
                "c.\"BindingGold\"::bigint, (SELECT count(*)::integer FROM public.character_items i WHERE i.user_id = c.id), 'runtime_cutover'\n"
                "    FROM public.character_base c WHERE c.id = %d AND c.wallet_revision = 0 AND c.inventory_revision = 0\n"
                "    ON CONFLICT (character_id) DO NOTHING RETURNING character_id, account_id)\n"
                "  INSERT INTO public.character_inventory_baseline_items (character_id, account_id, item_instance_id, item_location, "
                "slot_index, prop_id, state_contract_version, item_state)\n"
                "  SELECT i.user_id, b.account_id, i.id, i.item_location, i.slot_index, i.prop_id, 1, to_jsonb(i)\n"
                "  FROM inserted_baseline b JOIN public.character_items i ON i.user_id = b.character_id ORDER BY i.id;\n"
                "END\n$gwe$;" % (cid, cid, cid, cid))

    @staticmethod
    def needs_economy_baseline(statements):
        return any(re.search(r"wallet_revision|inventory_revision|character_items", s) for s in statements)

    def execute(self, cid, statements, replica=False, validate=True, online_ok=False):
        """One transaction: lock character + account rows, online re-check, statements, guards, validators, COMMIT.
        online_ok skips only the online re-check (warehouse cells, F-87); the rows are still locked."""
        cid = int(cid)
        lines = ["BEGIN;", "SET LOCAL lock_timeout = '5s';", "SET LOCAL statement_timeout = '60s';", self.db_guard_sql()]
        if replica:
            lines.append("SET LOCAL session_replication_role = replica;")
        # both rows: a login changes accounts.login_status / login_presence_token (PostgresAccountStore) and
        # character_base.checkpoint_owner_id (Ownership.cs AcquireAsync); with both locked, a racing login waits
        # for the COMMIT or the re-check below sees it
        lines.append("SELECT 1 FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.id = %d FOR UPDATE OF c, a;" % cid)
        if not online_ok:
            lines.append("DO $gwo$ BEGIN IF EXISTS (SELECT 1 FROM character_base c JOIN accounts a ON a.id = c.account_id "
                         "WHERE c.id = %d AND (c.checkpoint_owner_id IS NOT NULL OR a.login_presence_token IS NOT NULL OR a.login_status <> 0)) "
                         "THEN RAISE EXCEPTION 'gwadmin: character %d is online (checkpoint owner / presence token / login_status); nothing written'; "
                         "END IF; END $gwo$;" % (cid, cid))
        if self.needs_economy_baseline(statements):
            lines.append(self.economy_baseline_sql(cid))
        lines += [s.rstrip().rstrip(";") + ";" for s in statements]
        if validate:
            lines.append(self.structure_guard_sql(cid))
            lines.append(self.validator_guard_sql(cid))
        lines.append("COMMIT;")
        script = "\n".join(lines) + "\n"
        self.pg.run(script, timeout=120)
        return script

    def write(self, cid, action, build, changes, online_ok=False):
        """build(d) -> list of SQL statements (d = the fresh dump); action: text or action(d) -> text.
        online_ok: the caller writes rows the game server reads fresh from the database while the player is online
        (warehouse cells: the server reads them on every open and refuses a transfer whose item changed).
        Returns the change record."""
        self._guard_writable()
        with self.lock:
            cid = int(cid)
            on = self.online_state(cid)
            if on["online"] and not online_ok:
                raise RebornError("refused: %s looks online - %s. Log the player out first (the server keeps the "
                                  "character in memory and would overwrite or ignore the edit)." % (on["name"], "; ".join(on["reasons"])))
            before = self.dump(cid)
            if not before["character_base"]:
                raise RebornError("no reborn character #%d" % cid)
            name = before["character_base"][0]["name"]
            stmts = build(before)
            if not stmts:
                raise RebornError("nothing to change")
            if callable(action):
                action = action(before)
            stamp = datetime.datetime.now()
            backup = self._backup(name, before, stamp)
            try:
                self.execute(cid, stmts, online_ok=online_ok)
            except RebornError:
                try:
                    os.remove(backup)       # rolled back: nothing changed, no orphan dump
                except OSError:
                    pass
                raise
            after = self.dump(cid)
            diff = row_diff(before, after)
            verify = self.check_character(after)
            c = {"engine": "reborn", "id": "rb-" + stamp.strftime("%Y%m%d-%H%M%S-%f"), "time": stamp.isoformat(timespec="seconds"),
                 "database": self.db, "character": after["character_base"][0]["name"], "cid": cid, "action": action,
                 "sql": [{"db": self.db, "sql": s} for s in stmts], "backup": backup, "diff": diff,
                 "verify": {"errors": [f for f in verify if f["severity"] == "error"],
                            "warnings": len([f for f in verify if f["severity"] == "warn"])},
                 "output": diff_summary(diff), "before": before, "after": after, "undone": False}
            changes(c)
            return c

    def restore_statements(self, before, now, scope=None, colscope=None, tables=None, counters_map=None, ignore_map=None):
        """SQL that puts `now` back to `before` (rows keyed by primary key). scope: {table: set(pk-json)} or None=all.
        tables: [(table, pk-tuple)] in parent-first order (default: the character DUMP_TABLES / PARENT_FIRST);
        counters_map: {table: set(revision columns)} for those tables (default SERVER_COUNTERS). A table the `before`
        dump does not contain (a record written before that table was dumped) is left alone.
        colscope: {table: {pk-json: [columns]}} = Undo (only the columns that write changed).
        Server bookkeeping (SERVER_FROZEN) is never written; revision counters (SERVER_COUNTERS) of a restored row
        move forward to current + 1 instead of back - for Undo the counters the write bumped, for Restore dump the
        counters that differ between the dump and now.
        Normal triggers stay on; only three guarded cases run inside a short session_replication_role=replica window
        (restoring a lower warehouse_capacity, a pet's aptitude/hatch evidence, re-inserting a pet without hatch
        evidence) because those triggers forbid exactly the reversal an undo needs."""
        dels, ups, ins = [], [], []
        if tables is None:
            pkmap = {t: pk for t, _, pk in DUMP_TABLES}
            order_list = list(PARENT_FIRST)
        else:
            pkmap = {t: tuple(pk) for t, pk in tables}
            order_list = [t for t, _ in tables]
        cmap = SERVER_COUNTERS if counters_map is None else counters_map
        replica_pets = []
        for t in order_list:
            if t not in before:
                continue
            pk = pkmap[t]
            counters = set(cmap.get(t, set()))
            skip = counters | SERVER_FROZEN.get(t, set()) | set((ignore_map or {}).get(t, set()))
            b = {canon([r[c] for c in pk]): r for r in before.get(t, [])}
            n = {canon([r[c] for c in pk]): r for r in now.get(t, [])}
            keys = set(b) | set(n)
            if scope is not None:
                keys &= scope.get(t, set())
            for k in sorted(keys):
                br, nr = b.get(k), n.get(k)
                src = nr if nr is not None else br
                cond = " AND ".join("tt.%s = %s" % (ident(c), lit(src[c])) for c in pk)
                rec = "jsonb_populate_record(NULL::%s, %s::jsonb)" % (t, lit(canon(br))) if br is not None else None
                if br is None:
                    dels.append((t, "DELETE FROM %s AS tt WHERE %s" % (t, cond), False))
                elif nr is None:
                    need = t == "character_pets" and br.get("birth_rank") is None
                    if need:
                        replica_pets.append(br["id"])
                    ins.append((t, "INSERT INTO %s SELECT * FROM %s" % (t, rec), need))
                elif canon(br) != canon(nr):
                    differ = [c for c in br if canon(br.get(c)) != canon(nr.get(c))]
                    cols = [c for c in differ if c not in skip]
                    if colscope is not None and k in colscope.get(t, {}):
                        touched = set(colscope[t][k])
                        cols = [c for c in cols if c in touched]     # Undo: only the columns that write changed
                        bump = sorted(c for c in counters if c in touched)
                    else:
                        bump = sorted(c for c in counters if c in differ)
                    if not cols:
                        continue
                    need = ((t == "character_base" and "warehouse_capacity" in cols) or
                            (t == "character_pets" and any(c in cols for c in ("aptitude", "birth_rank", "hatch_rank_roll",
                                                                               "hatch_rank_outcome_order", "hatch_rank_content_revision"))))
                    if need and t == "character_pets":
                        replica_pets.append(br["id"])
                    sets = ["%s = r.%s" % (ident(c), ident(c)) for c in cols] + ["%s = tt.%s + 1" % (ident(c), ident(c)) for c in bump]
                    ups.append((t, "UPDATE %s AS tt SET %s FROM %s AS r WHERE %s" % (t, ", ".join(sets), rec, cond), need))
        order = {t: i for i, t in enumerate(order_list)}
        dels.sort(key=lambda x: -order[x[0]])          # children first
        ins.sort(key=lambda x: order[x[0]])            # parents first
        stmts = []
        for _, sql, need in dels + ups + ins:
            if need:
                stmts += ["SET LOCAL session_replication_role = replica", sql, "SET LOCAL session_replication_role = origin"]
            else:
                stmts.append(sql)
        for pid in sorted(set(replica_pets)):
            stmts.append("SELECT public.assert_character_pet_basic_savvy_aggregate(%d)" % int(pid))
        return stmts

    @staticmethod
    def _scope(c):
        scope = {}
        for t, dd in c["diff"].items():
            scope[t] = set(dd.get("added", [])) | set(dd.get("removed", [])) | set(dd.get("changed", {}).keys())
        return scope

    @classmethod
    def _touches(cls, c):
        """(table, key) of every row a change touched, plus ("pet", id) for every pet whose row or child rows it
        touched - deleting / re-inserting a pet cascades to its skills / stats, so those count as the same rows."""
        out = set()
        for t, keys in cls._scope(c).items():
            for k in keys:
                out.add((t, k))
                if t == "character_pets" or t in ("character_pet_stat_values", "character_pet_skills", "character_pet_character_bonuses"):
                    try:
                        out.add(("pet", json.loads(k)[0]))
                    except (ValueError, IndexError, TypeError):
                        pass
        return out

    @staticmethod
    def server_changes(c, now, full, tables=None, counters_map=None, ignore_map=None):
        """What changed in the character's rows since the admin write (the change's after-image vs now), ignoring
        server bookkeeping columns. [] = nothing; a conflict means an Undo / Restore would erase it.
        tables / counters_map: as in restore_statements (default: the character DUMP_TABLES)."""
        after = c.get("after")
        if after is None:
            return [{"table": "*", "row": "*", "what": "this change was recorded without an after-image (older gwadmin-web); "
                                                       "cannot tell whether the server changed the rows since"}]
        out = []
        tl = [(t, pk) for t, _, pk in DUMP_TABLES] if tables is None else [(t, tuple(pk)) for t, pk in tables]
        pkmap = dict(tl)
        cmap = SERVER_COUNTERS if counters_map is None else counters_map
        for t, pk in tl:
            if not full and t not in c["diff"]:
                continue
            if t not in after:          # a record from before this table was dumped
                continue
            ign = set(cmap.get(t, set())) | SERVER_FROZEN.get(t, set()) | NOISE_COLS | set((ignore_map or {}).get(t, set()))
            a = {canon([r[x] for x in pkmap[t]]): r for r in after.get(t, [])}
            n = {canon([r[x] for x in pkmap[t]]): r for r in now.get(t, [])}
            if full:
                keys = set(a) | set(n)
                colmap = None
            else:
                dd = c["diff"][t]
                keys = set(dd.get("added", [])) | set(dd.get("removed", [])) | set(dd.get("changed", {}))
                colmap = dd.get("changed", {})
            for k in sorted(keys):
                ar, nr = a.get(k), n.get(k)
                if ar is None and nr is None:
                    continue
                if ar is None:
                    out.append({"table": t, "row": k, "what": "row was added since"})
                    continue
                if nr is None:
                    out.append({"table": t, "row": k, "what": "row was deleted since"})
                    continue
                cols = colmap[k] if colmap is not None and k in colmap else list(set(ar) | set(nr))
                for col in sorted(cols):
                    if col in ign:
                        continue
                    if canon(ar.get(col)) != canon(nr.get(col)):
                        out.append({"table": t, "row": k, "what": "%s: %s -> %s" % (col, canon(ar.get(col))[:60], canon(nr.get(col))[:60])})
        return out

    def undo(self, c, full, changes, all_changes=(), overwrite=False):
        """Undo (full=False: the rows/columns that write changed) or Restore dump (full=True: every row of the
        dump).  Refused when: a later, not-undone admin change touched the same rows (undo that one first); the
        server changed those rows since the write (unless overwrite=True); the result would break a login rule
        (dry run + check_character, then the same in-transaction guards and validators as every forward write)."""
        self._guard_writable()
        if c["database"] != self.db:
            raise RebornError("change %s belongs to reborn database %s, this server uses %s" % (c["id"], c["database"], self.db))
        if c.get("undone") and not full:
            raise RebornError("change %s is already undone ('Restore dump' can still put its pre-write dump back)" % c["id"])
        with self.lock:
            cid = int(c["cid"])
            # 1. reverse order: a later admin change on the same rows must be undone first
            mine = self._scope(c)
            mine_t = self._touches(c)
            later = []
            for x in all_changes:
                # a later "rows" change (module editors: guild membership ...) on the same rows also comes first
                if (x.get("engine") == "rows" and x.get("database") == self.db and not x.get("undone") and not x.get("undo_of")
                        and x["id"][3:] > c["id"][3:] and (mine_t & self._touches(x))):
                    later.append(x)
                    continue
                if (x is c or x.get("engine") != "reborn" or x.get("database") != self.db or int(x.get("cid", -1)) != cid
                        or x.get("undone") or x.get("undo_of") or x["id"] <= c["id"]):
                    continue
                if full or (mine_t & self._touches(x)):
                    later.append(x)
            if later:
                raise RebornError("undo the later change%s first (newest first): %s" % (
                    "s" if len(later) > 1 else "", "; ".join("%s %s" % (x["id"], x.get("action", "")) for x in sorted(later, key=lambda x: x["id"], reverse=True))))
            on = self.online_state(cid)
            if on["online"]:
                raise RebornError("refused: %s looks online - %s" % (on["name"], "; ".join(on["reasons"])))
            before = c["before"]
            now = self.dump(cid)
            # 2. has the server changed these rows since the admin write?
            conflicts = self.server_changes(c, now, full)
            if conflicts and not overwrite:
                raise RebornConflict("the rows changed since this write (the game server or another edit). %s would erase "
                                     "that:\n%s" % ("Restore dump" if full else "Undo", "\n".join(
                                         "  %s %s: %s" % (x["table"], x["row"], x["what"]) for x in conflicts[:30])), conflicts)
            scope = None if full else mine
            colscope = None if full else {t: dd.get("changed", {}) for t, dd in c["diff"].items()}
            stmts = self.restore_statements(before, now, scope, colscope)
            # 3. dry run: what would the rows look like, and does that break anything the login checks?
            if stmts:
                trial = self.preview(cid, stmts)
                key = lambda f: (f["where"], f["msg"])     # noqa: E731
                had = {key(f) for f in self.check_character(now, validators={}) if f["severity"] == "error"}
                new = [f for f in self.check_character(trial, validators={}) if f["severity"] == "error" and key(f) not in had]
                if new:
                    raise RebornError("refused: %s would leave %s in a state the server rejects - %s. Nothing written. "
                                      "Undo the later related changes first, or use 'Restore dump' of the earliest change." % (
                                          "restore dump" if full else "undo", c["character"],
                                          "; ".join("%s: %s" % (f["where"], f["msg"]) for f in new)))
            stamp = datetime.datetime.now()
            backup = self._backup(c["character"], now, stamp)
            if stmts:
                try:
                    self.execute(cid, stmts, validate=True)
                except RebornError:
                    try:
                        os.remove(backup)
                    except OSError:
                        pass
                    raise
            after = self.dump(cid)
            tables = [t for t, _, _ in DUMP_TABLES if t in before] if full else list(c["diff"].keys())
            identical = {}
            for t in tables:
                pk = dict((x, y) for x, _, y in DUMP_TABLES)[t]
                ign = server_cols(t)
                keys = None if full else mine.get(t, set())
                chg = {} if full else c["diff"][t].get("changed", {})

                def part(rows):
                    out = {}
                    for r in rows:
                        k = canon([r[cc] for cc in pk])
                        if keys is None or k in keys:
                            cols = chg[k] if k in chg else list(r)
                            out[k] = {x: r.get(x) for x in cols if x not in ign}
                    return out
                identical[t] = canon(part(after.get(t, []))) == canon(part(before.get(t, [])))
            findings = self.check_character(after)
            ok = all(identical.values())
            r = {"engine": "reborn", "id": "rb-" + stamp.strftime("%Y%m%d-%H%M%S-%f"), "time": stamp.isoformat(timespec="seconds"),
                 "database": self.db, "character": after["character_base"][0]["name"] if after["character_base"] else c["character"],
                 "cid": cid, "action": "%s of %s (%s)" % ("restore dump" if full else "undo", c["id"], c["action"]),
                 "sql": [{"db": self.db, "sql": s} for s in stmts], "backup": backup, "diff": row_diff(now, after),
                 "verify": {"errors": [f for f in findings if f["severity"] == "error"],
                            "warnings": len([f for f in findings if f["severity"] == "warn"])},
                 "result": {"identical": identical, "all_identical": ok, "full": full, "overwrote": conflicts if overwrite else []},
                 "output": "rows %s the pre-write dump (server bookkeeping columns excluded: revision counters moved "
                           "forward, checkpoint owner / online counters untouched)" % ("identical to" if ok else "DIFFER from"),
                 "before": now, "after": after, "undone": False, "undo_of": c["id"]}
            changes(r)
            c["undone"] = True
            return r


    # ------------------------------------------------------------ the "rows" engine (non-character tables)
    # Guild rows, start-up content, per-use settings ... are not keyed by one character. The module editors write
    # them through write_rows(): the same psql transaction model, backup, after-image, change record and Undo /
    # Restore as the character engine above (restore_statements / server_changes / row_diff with an explicit table
    # list). A scope entry is {"table", "where" (SQL over that table's columns), "pk" [cols], "counters" [cols]}:
    # every row the write may touch must match `where`. Characters whose rows the write also changes (guild members)
    # go in lock_cids: they are locked FOR UPDATE OF c, a and must be offline, like a character write.
    @staticmethod
    def _scope_tables(scope):
        seen, out = set(), []
        for e in scope:
            t = e["table"]
            if not re.match(r"^[a-z_][a-z0-9_]*$", t) or t in seen:
                raise RebornError("bad or repeated scope table %r" % t)
            seen.add(t)
            out.append((t, tuple(e["pk"])))
        return out

    def snapshot_rows(self, scope):
        self._scope_tables(scope)
        parts = []
        for e in scope:
            order = ", ".join(ident(c) for c in e["pk"])
            parts.append("%s, (SELECT coalesce(json_agg(to_jsonb(t) ORDER BY %s), '[]') FROM %s t WHERE %s)" % (
                lit(e["table"]), order, e["table"], e.get("where") or "true"))
        return self.pg.json("json_build_object(%s)" % ", ".join(parts), timeout=120)

    def execute_rows(self, scope, statements, lock_cids=(), guard_sql=(), validate=True):
        """One transaction: db guard, lock + offline re-check of lock_cids, lock the scope rows, statements, the
        module's guard SQL (RAISE on a broken invariant), the pet guards / validators of lock_cids, COMMIT."""
        cids = sorted({int(x) for x in lock_cids})
        lines = ["BEGIN;", "SET LOCAL lock_timeout = '5s';", "SET LOCAL statement_timeout = '60s';", self.db_guard_sql()]
        if cids:
            ids = ", ".join(str(x) for x in cids)
            lines.append("SELECT 1 FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.id IN (%s) "
                         "ORDER BY c.id FOR UPDATE OF c, a;" % ids)
            lines.append("DO $gwo$ DECLARE bad text; BEGIN SELECT string_agg(c.name, ', ') INTO bad FROM character_base c "
                         "JOIN accounts a ON a.id = c.account_id WHERE c.id IN (%s) AND (c.checkpoint_owner_id IS NOT NULL OR "
                         "a.login_presence_token IS NOT NULL OR a.login_status <> 0); IF bad IS NOT NULL THEN RAISE EXCEPTION "
                         "'gwadmin: %% is online (checkpoint owner / presence token / login_status); nothing written', bad; "
                         "END IF; END $gwo$;" % ids)
        for e in scope:
            lines.append("SELECT 1 FROM %s t WHERE %s FOR UPDATE;" % (e["table"], e.get("where") or "true"))
        lines += [x.rstrip().rstrip(";") + ";" for x in statements]
        lines += [g for g in guard_sql if g]
        if validate:
            for cid in cids:
                lines.append(self.structure_guard_sql(cid))
                lines.append(self.validator_guard_sql(cid))
        lines.append("COMMIT;")
        script = "\n".join(lines) + "\n"
        self.pg.run(script, timeout=120)
        return script

    def write_rows(self, scope, action, build, changes, target="", lock_cids=(), guard_sql=(), meta=None):
        """build(before) -> SQL statements (before = snapshot of the scope). Returns the change record."""
        self._guard_writable()
        tables = self._scope_tables(scope)
        with self.lock:
            for cid in lock_cids:
                on = self.online_state(cid)
                if on["online"]:
                    raise RebornError("refused: %s looks online - %s. Log the player out first." % (on["name"], "; ".join(on["reasons"])))
            before = self.snapshot_rows(scope)
            stmts = build(before)
            if not stmts:
                raise RebornError("nothing to change")
            if callable(action):
                action = action(before)
            stamp = datetime.datetime.now()
            backup = self._backup(target or "rows", before, stamp)
            try:
                self.execute_rows(scope, stmts, lock_cids, guard_sql)
            except RebornError:
                try:
                    os.remove(backup)
                except OSError:
                    pass
                raise
            after = self.snapshot_rows(scope)
            diff = row_diff(before, after, tables)
            c = {"engine": "rows", "id": "rw-" + stamp.strftime("%Y%m%d-%H%M%S-%f"), "time": stamp.isoformat(timespec="seconds"),
                 "database": self.db, "character": target or ", ".join(t for t, _ in tables), "cid": None,
                 "lock_cids": sorted({int(x) for x in lock_cids}), "scope": scope, "guard_sql": [g for g in guard_sql if g],
                 "action": action, "sql": [{"db": self.db, "sql": x} for x in stmts], "backup": backup, "diff": diff,
                 "verify": {"errors": [], "warnings": 0}, "output": diff_summary(diff), "before": before, "after": after,
                 "undone": False}
            if meta:
                c.update(meta)
            changes(c)
            return c

    def undo_rows(self, c, full, changes, all_changes=(), overwrite=False):
        """Undo / Restore dump of a write_rows change: newest first, 409 on changes since (unless overwrite), revision
        counters of the scope move forward, the same locks and guards as the forward write."""
        self._guard_writable()
        if c["database"] != self.db:
            raise RebornError("change %s belongs to reborn database %s, this server uses %s" % (c["id"], c["database"], self.db))
        if c.get("undone") and not full:
            raise RebornError("change %s is already undone ('Restore dump' can still put its pre-write snapshot back)" % c["id"])
        scope = c["scope"]
        tables = self._scope_tables(scope)
        cmap = {e["table"]: set(e.get("counters") or []) for e in scope}
        imap = {e["table"]: set(e.get("ignore") or []) for e in scope}   # derived columns (updated_at, stamps)
        with self.lock:
            mine = self._scope(c)
            pairs = {(t, k) for t, ks in mine.items() for k in ks}
            later = []
            for x in all_changes:
                # ids are "<engine>-YYYYmmdd-HHMMSS-ffffff": the part after the prefix orders them in time
                if (x is c or x.get("engine") not in ("rows", "reborn") or x.get("database") != self.db or x.get("undone")
                        or x.get("undo_of") or x["id"][3:] <= c["id"][3:]):
                    continue
                xp = {(t, k) for t, ks in self._scope(x).items() for k in ks}
                if (pairs & xp) or (full and {t for t, _ in tables} & {t for t, _ in xp}):
                    later.append(x)
            if later:
                raise RebornError("undo the later change%s first (newest first): %s" % (
                    "s" if len(later) > 1 else "", "; ".join("%s %s" % (x["id"], x.get("action", "")) for x in later)))
            for cid in c.get("lock_cids") or []:
                on = self.online_state(cid)
                if on["online"]:
                    raise RebornError("refused: %s looks online - %s" % (on["name"], "; ".join(on["reasons"])))
            before = c["before"]
            now = self.snapshot_rows(scope)
            conflicts = self.server_changes(c, now, full, tables=tables, counters_map=cmap, ignore_map=imap)
            if conflicts and not overwrite:
                raise RebornConflict("the rows changed since this write (the game server or another edit). %s would erase "
                                     "that:\n%s" % ("Restore" if full else "Undo", "\n".join(
                                         "  %s %s: %s" % (x["table"], x["row"], x["what"]) for x in conflicts[:30])), conflicts)
            colscope = None if full else {t: dd.get("changed", {}) for t, dd in c["diff"].items()}
            stmts = self.restore_statements(before, now, None if full else mine, colscope, tables=tables, counters_map=cmap, ignore_map=imap)
            stamp = datetime.datetime.now()
            backup = self._backup(c.get("character") or "rows", now, stamp)
            if stmts:
                try:
                    self.execute_rows(scope, stmts, c.get("lock_cids") or [], c.get("guard_sql") or [])
                except RebornError:
                    try:
                        os.remove(backup)
                    except OSError:
                        pass
                    raise
            after = self.snapshot_rows(scope)
            identical = {}
            for t, pk in tables:
                if t not in before:
                    continue
                if not full and t not in c["diff"]:
                    continue
                ign = cmap.get(t, set()) | imap.get(t, set()) | NOISE_COLS
                keys = None if full else mine.get(t, set())
                chg = {} if full else c["diff"][t].get("changed", {})

                def part(rows, pk=pk, keys=keys, chg=chg, ign=ign):
                    out = {}
                    for r in rows:
                        k = canon([r[cc] for cc in pk])
                        if keys is None or k in keys:
                            cols = chg[k] if k in chg else list(r)
                            out[k] = {x: r.get(x) for x in cols if x not in ign}
                    return out
                identical[t] = canon(part(after.get(t, []))) == canon(part(before.get(t, [])))
            ok = all(identical.values())
            r = {"engine": "rows", "id": "rw-" + stamp.strftime("%Y%m%d-%H%M%S-%f"), "time": stamp.isoformat(timespec="seconds"),
                 "database": self.db, "character": c.get("character"), "cid": None, "lock_cids": c.get("lock_cids") or [],
                 "scope": scope, "guard_sql": c.get("guard_sql") or [],
                 "action": "%s of %s (%s)" % ("restore" if full else "undo", c["id"], c["action"]),
                 "sql": [{"db": self.db, "sql": x} for x in stmts], "backup": backup, "diff": row_diff(now, after, tables),
                 "verify": {"errors": [], "warnings": 0},
                 "result": {"identical": identical, "all_identical": ok, "full": full, "overwrote": conflicts if overwrite else []},
                 "output": "rows %s the pre-write snapshot (revision counters moved forward)" % ("identical to" if ok else "DIFFER from"),
                 "before": now, "after": after, "undone": False, "undo_of": c["id"]}
            for k in ("module", "applies", "applies_text", "tables"):
                if k in c:
                    r[k] = c[k]
            changes(r)
            c["undone"] = True
            return r


class RebornConflict(RebornError):
    """Undo / Restore would erase changes made since the admin write; carries the list for the UI."""

    def __init__(self, msg, conflicts):
        RebornError.__init__(self, msg)
        self.conflicts = conflicts


def row_diff(before, after, tables=None):
    """{table: {added, removed, changed{pk-json: [columns]}}}; tables = [(table, pk)] (default: DUMP_TABLES)."""
    out = {}
    for t, pk in ([(t, pk) for t, _, pk in DUMP_TABLES] if tables is None else tables):
        if t not in before and t not in after:
            continue
        b = {canon([r[c] for c in pk]): r for r in before.get(t, [])}
        a = {canon([r[c] for c in pk]): r for r in after.get(t, [])}
        added = sorted(set(a) - set(b))
        removed = sorted(set(b) - set(a))
        changed = {}
        for k in set(a) & set(b):
            if canon(a[k]) != canon(b[k]):
                changed[k] = sorted(c for c in set(a[k]) | set(b[k]) if canon(a[k].get(c)) != canon(b[k].get(c)))
        if added or removed or changed:
            out[t] = {"added": added, "removed": removed, "changed": changed}
    return out


def diff_summary(diff):
    parts = []
    for t, dd in diff.items():
        bits = []
        if dd["added"]:
            bits.append("+%d" % len(dd["added"]))
        if dd["removed"]:
            bits.append("-%d" % len(dd["removed"]))
        if dd["changed"]:
            cols = sorted({c for v in dd["changed"].values() for c in v})
            bits.append("~%d (%s)" % (len(dd["changed"]), ",".join(cols[:8]) + ("..." if len(cols) > 8 else "")))
        parts.append("%s %s" % (t, " ".join(bits)))
    return "; ".join(parts) or "no row changed"


# --------------------------------------------------------------------------- operations (build SQL)
def _audit(cid, action, loc, slot, row_new, old_row):
    return ("INSERT INTO character_item_audit (source, action, user_id, item_location, slot_index, prop_id, item_quality, "
            "item_grade, item_exp, old_item) VALUES ('gwadmin-web', %s, %d, %d, %d, %s, %s, %s, %s, %s)" % (
                lit(action), cid, loc, slot, lit((row_new or {}).get("prop_id")), lit((row_new or {}).get("item_quality")),
                lit((row_new or {}).get("item_grade")), lit((row_new or {}).get("item_exp")),
                ("%s::jsonb" % lit(canon(old_row))) if old_row else "NULL"))


def find_item(d, loc, slot):
    rows = [r for r in d["character_items"] if r["item_location"] == loc and r["slot_index"] == slot]
    return rows[0] if rows else None


def check_place(rb, d, loc, slot, meta, force, moving_row=None):
    b = d["character_base"][0]
    if loc == LOC_EQUIP:
        if slot not in EQUIP_SLOTS and not force:
            raise RebornError("equipment slot %d is not one the server uses (0-12, 15-20)" % slot)
        ts = meta["slot"]
        ok = ts == slot or (ts in (8, 9) and slot in (8, 9))
        if not ok and not force:
            raise RebornError("%s is a '%s' (template slot %s: %s); it cannot go in %s" % (
                meta["name"], meta["kind"], ts, EQUIP_SLOTS.get(ts, "not equipment"), EQUIP_SLOTS.get(slot, slot)))
        if slot in MOUNT_GEAR_SLOTS and not force:
            m = find_item(d, LOC_EQUIP, MOUNT_SLOT)
            if not m or (moving_row and m["id"] == moving_row["id"]):
                raise RebornError("mount gear needs a mount equipped in the Mount slot first (EquipmentEligibility.cs)")
            mm = rb.item_meta(m["prop_id"])
            if (meta["minlv"] or 0) > (mm["minlv"] or 0):
                raise RebornError("gear level %s is above the mount's level %s (player lv >= mount lv >= gear lv)" % (meta["minlv"], mm["minlv"]))
        if slot == MOUNT_SLOT and not force and (meta["minlv"] or 0) > int(b["fighter_job_lv"]):
            raise RebornError("%s needs level %s; the character is level %s" % (meta["name"], meta["minlv"], b["fighter_job_lv"]))
    elif loc == LOC_BAG:
        if not (0 <= slot < BAG_SLOTS):
            raise RebornError("bag slot must be 1..96")
    elif loc == LOC_WAREHOUSE:
        if not (0 <= slot < int(b["warehouse_capacity"])):
            raise RebornError("warehouse slot must be 1..%s (warehouse_capacity)" % b["warehouse_capacity"])
    else:
        raise RebornError("location %s cannot be edited here" % loc)


def item_values(rb, p, old, force):
    pid = to_int(p.get("prop_id"), 1, I32, "item id")
    meta = rb.item_meta(pid)
    if not meta["server"] and not force:
        raise RebornError("item %d is not in item_templates (the server would not load it); tick force (needs --allow-force)" % pid)
    vals = {"prop_id": pid}
    vals["stack"] = to_int(p.get("stack", 1), 1, 32767, "stack")
    if vals["stack"] > meta["overlap"] and not force:
        raise RebornError("%s stacks to %d (Overlap); tick force to exceed it" % (meta["name"], meta["overlap"]))
    dflt = old if old is not None else meta["defaults"]
    vals["bound"] = to_int(p.get("bound", dflt["bound"]), 0, 1, "bound")
    vals["item_quality"] = to_int(p.get("item_quality", dflt["item_quality"]), 0, 20, "quality")
    vals["item_grade"] = to_int(p.get("item_grade", dflt["item_grade"]), 0, 25, "grade")
    vals["item_exp"] = to_int(p.get("item_exp", 0), 0, I32, "item exp")
    vals["holy_suit_code"] = to_int(p.get("holy_suit_code", 0), 0, I32, "holy suit code")
    vals["holy_socket_count"] = to_int(p.get("holy_socket_count", 0), 0, 6, "holy socket count")
    cat = rb.cat()
    for i in range(1, 6):
        a = p.get("attribute%d" % i)
        if a in (None, "", -1, "-1"):
            vals["attribute%d" % i] = None
            vals["attribute_level%d" % i] = None
            continue
        a = to_int(a, 1, 32767, "attribute %d" % i)
        if str(a) not in cat["attributes"] and not force:
            raise RebornError("attribute %d = %d is not in item_attribute_templates" % (i, a))
        vals["attribute%d" % i] = a
        al = p.get("attribute_level%d" % i)
        mx = (cat["attributes"].get(str(a)) or {}).get("max") or 32767
        vals["attribute_level%d" % i] = to_int(al if al not in (None, "") else 1, 0, 32767 if force else mx, "attribute %d level" % i)
    for k in ("class_attribute1", "elemental_attribute1", "elemental_attribute2"):
        v = p.get(k)
        vals[k] = None if v in (None, "", "-1", -1) else to_int(v, 1, 32767, k)
    for i in range(1, 7):
        e, lv = p.get("holy_socket%d_effect_id" % i), p.get("holy_socket%d_level" % i)
        vals["holy_socket%d_effect_id" % i] = None if e in (None, "") else to_int(e, 0, 32767, "socket %d effect" % i)
        vals["holy_socket%d_level" % i] = None if lv in (None, "") else to_int(lv, 0, 32767, "socket %d level" % i)
        if i <= 4:
            v = p.get("holy_socket%d_value" % i)
            vals["holy_socket%d_value" % i] = None if v in (None, "") else to_int(v, 1, 32767, "socket %d value" % i)
    return vals, meta


def op_item(rb, cid, p):
    """Set / add / delete the item at (location, slot)."""
    loc = to_int(p.get("location"), 0, 3, "location")
    slot = to_int(p.get("slot"), -32768, 32767, "slot")
    force = bool(p.get("force"))
    if force and not rb.allow_force:
        raise RebornError("'force' is disabled; start the server with --allow-force")

    def build(d):
        old = find_item(d, loc, slot)
        sealed = {s["item_instance_id"] for s in d["sealed_pet_items"]}
        if old and old["id"] in sealed:
            raise RebornError("that item is a packed (sealed) pet; it is managed through the pet, not edited here")
        stm = []
        if p.get("item") is None:
            if not old:
                raise RebornError("%s slot %d is empty" % (LOC_NAMES[loc], slot + 1))
            stm.append("DELETE FROM character_items WHERE id = %d" % old["id"])
            stm.append(_audit(cid, "delete", loc, slot, None, old))
        else:
            vals, meta = item_values(rb, p["item"], old, force)
            check_place(rb, d, loc, slot, meta, force)
            if old:
                sets = ", ".join("%s = %s" % (ident(k), lit(v)) for k, v in vals.items())
                stm.append("UPDATE character_items SET %s, updated_at = now() WHERE id = %d" % (sets, old["id"]))
                stm.append(_audit(cid, "update", loc, slot, vals, old))
            else:
                cols = ["user_id", "item_location", "slot_index"] + list(vals)
                stm.append("INSERT INTO character_items (%s) VALUES (%s)" % (
                    ", ".join(ident(c) for c in cols), ", ".join(lit(v) for v in [cid, loc, slot] + list(vals.values()))))
                stm.append(_audit(cid, "create", loc, slot, vals, None))
        stm.append("UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid)
        if loc == LOC_EQUIP:
            stm.append("SELECT public.recompute_character_holy_suit_points(%d)" % cid)
        return stm
    return build


def op_move(rb, cid, p):
    fl, fs = to_int(p["from"]["location"], 0, 3, "from location"), to_int(p["from"]["slot"], -32768, 32767, "from slot")
    tl, ts = to_int(p["to"]["location"], 0, 3, "to location"), to_int(p["to"]["slot"], -32768, 32767, "to slot")
    force = bool(p.get("force"))
    if force and not rb.allow_force:
        raise RebornError("'force' is disabled; start the server with --allow-force")

    def build(d):
        src = find_item(d, fl, fs)
        if not src:
            raise RebornError("%s slot %d is empty" % (LOC_NAMES[fl], fs + 1))
        if (fl, fs) == (tl, ts):
            raise RebornError("source and destination are the same slot")
        dst = find_item(d, tl, ts)
        sealed = {s["item_instance_id"] for s in d["sealed_pet_items"]}
        if (src["id"] in sealed or (dst and dst["id"] in sealed)) and LOC_WAREHOUSE in (fl, tl):
            raise RebornError("packed sealed-pet items cannot enter the warehouse")
        check_place(rb, d, tl, ts, rb.item_meta(src["prop_id"]), force, moving_row=src)
        if dst:
            check_place(rb, d, fl, fs, rb.item_meta(dst["prop_id"]), force, moving_row=dst)
        if fl == LOC_EQUIP and fs == MOUNT_SLOT and not force and not (dst and rb.item_meta(dst["prop_id"])["kind"] == "mount"):
            if any(find_item(d, LOC_EQUIP, s) for s in MOUNT_GEAR_SLOTS):
                raise RebornError("remove every piece of mount gear before removing the mount (EquipmentEligibility.cs)")
        stm = ["UPDATE character_items SET item_location = %d, slot_index = %d, updated_at = now() WHERE id = %d" % (tl, ts, src["id"])]
        stm.append(_audit(cid, "move", tl, ts, src, src))
        if dst:
            stm.append("UPDATE character_items SET item_location = %d, slot_index = %d, updated_at = now() WHERE id = %d" % (fl, fs, dst["id"]))
            stm.append(_audit(cid, "move", fl, fs, dst, dst))
        stm.append("UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid)
        if LOC_EQUIP in (fl, tl):
            stm.append("SELECT public.recompute_character_holy_suit_points(%d)" % cid)
        return stm
    return build


def op_base(rb, cid, p):
    fields = p.get("fields") or {}
    if not fields:
        raise RebornError("no fields")

    def build(d):
        b = d["character_base"][0]
        sets, bumps = [], set()
        for col, val in fields.items():
            if col not in BASE_EDITABLE:
                raise RebornError("%s is not editable here" % col)
            label, kind, lo, hi, rev = BASE_EDITABLE[col]
            if kind == "name":
                v = ascii_name(val, "name", 32)
                if v != b["name"]:
                    # Psql.json casts to jsonb, and bigint has no cast: to_jsonb.  Inside the write transaction the
                    # unique index ux_character_base_name is the authoritative check (a racing create fails the COMMIT).
                    taken = rb.pg.json("(SELECT to_jsonb(count(*)) FROM character_base WHERE name = %s AND id <> %d)" % (lit(v), cid))
                    if taken:
                        raise RebornError("the name %s is taken" % v)
            elif kind.startswith("enum:"):
                v = str(val).strip()
                if v not in kind[5:].split(","):
                    raise RebornError("%s must be one of %s" % (label, kind[5:]))
            elif kind == "bool":
                v = to_bool(val)
            elif kind == "float":
                v = float(to_dec(val, lo, hi, label))
            else:
                v = to_int(val, lo, hi, label)
            if col == "Map" and str(v) not in rb.cat()["maps"]:
                raise RebornError("map %s is not in map_templates" % v)
            if canon(v) == canon(b.get(col)):
                continue
            sets.append("%s = %s" % (ident(col), lit(v)))
            if rev:
                bumps.add(rev)
        if not sets:
            return []
        for r in sorted(bumps):
            sets.append("%s = %s + 1" % (r, r))
        return ["UPDATE character_base SET %s WHERE id = %d" % (", ".join(sets), cid)]
    return build


# ------------------------------------------------------------ pets
def _pet(d, pet_id):
    for p in d["character_pets"]:
        if p["id"] == pet_id:
            return p
    raise RebornError("pet #%s does not belong to this character" % pet_id)


def _savvy_vector(total, n=6):
    """total (int) split into n hundredth-precision values that sum exactly to total."""
    cents = int(total) * 100
    base, rem = divmod(cents, n)
    return [D(base + (1 if i < rem else 0)) / 100 for i in range(n)]


def _growth_vector(total):
    cents = int((D(str(total)) * 100).to_integral_value())
    base, rem = divmod(cents, 6)
    v = [D(base + (1 if i < rem else 0)) / 100 for i in range(6)]
    if any(x <= 0 for x in v):
        raise RebornError("growth total %s is too small to give six positive rates" % total)
    return v


def pet_new_values(rb, d, species, aptitude, sex, level, name, lifetime=None):
    cat = rb.cat()
    sp = cat["species"].get(str(species))
    if not sp:
        raise RebornError("species %s is not published in pet_content_species_definitions" % species)
    apt = cat["aptitudes"].get(str(aptitude))
    if not apt:
        raise RebornError("aptitude %s is not published" % aptitude)
    settings = cat.get("pet_settings") or {}
    level = to_int(level, int(settings.get("minimum_level") or 1), int(settings.get("maximum_level") or 120), "pet level")
    steps = [h for h in (cat.get("hatch_steps") or []) if int(h["aptitude"]) == int(aptitude)]
    if not steps:
        raise RebornError("no published hatch-rank step for aptitude %s" % aptitude)
    step = sorted(steps, key=lambda h: h["outcome_order"])[0]
    lo, hi = int(apt["minimum_initial_savvy"]), int(apt["maximum_initial_savvy"])
    total = (lo + hi) // 2
    mask = 31 if aptitude >= 14 else 26 if aptitude >= 10 else 26 if aptitude == 8 else 31 if aptitude == 7 else 0
    weak = cat["aptitudes"].get("1") or {}
    gtotal = (D(str(weak.get("minimum_total_growth", "0.01"))) + D(str(weak.get("maximum_total_growth", "0.10")))) / 2
    gtotal = gtotal.quantize(D("0.01"))
    if lifetime is None:
        prof = [x for x in (cat.get("profiles") or []) if x["species"] == int(species) and x["aptitude"] == int(aptitude)]
        lifetime = prof[0]["lifetime"] if prof else max(sp["lifetime_values"])
    slots = 2 if aptitude >= 10 else 1
    pet = {"species_id": int(species), "name": ascii_name(name or sp["display_name"], "pet name"), "sex": int(sex), "level": level,
           "experience": 0, "aptitude": int(aptitude), "initial_savvy_baseline_total": total,
           "initial_savvy_policy_version": settings.get("initial_savvy_policy_version"),
           "rarity_added_savvy_baseline_total": total, "rarity_added_savvy_policy_version": settings.get("initial_savvy_policy_version"),
           "initial_savvy_source_version": SAVVY_SOURCE, "rank": D(str(step["rank"])), "birth_rank": D(str(step["rank"])),
           "hatch_rank_roll": 0, "hatch_rank_outcome_order": int(step["outcome_order"]), "hatch_rank_content_revision": step["revision"],
           "current_energy": 100, "maximum_energy": 100, "amity": 100, "satiety": 100, "remaining_lifetime": int(lifetime),
           "growth_revealed": False, "bound": False, "activity_state": "owned", "talent_mask": mask,
           "has_owner_merge_talent": bool(mask & 16), "opened_skill_slots": slots, "available_skill_slots": slots,
           "is_carried": False, "is_summoned": False}
    sv = _savvy_vector(total)
    gv = _growth_vector(gtotal)
    stats = [{"stat_code": i + 1, "initial_savvy": sv[i], "birth_initial_savvy": sv[i], "rarity_added_savvy": sv[i],
              "base_growth_rate": gv[i], "growth_acceleration": D(0), "added_savvy": gv[i] * level} for i in range(6)]
    return pet, stats, sp


def op_pet_add(rb, cid, p):
    def build(d):
        b = d["character_base"][0]
        owned = [x for x in d["character_pets"] if x["activity_state"] == "owned"]
        settings = rb.cat().get("pet_settings") or {}
        if len(owned) >= int(b["pet_shed_capacity"]):
            raise RebornError("the pet shed has %s cells and %d owned pets; raise 'pet shed cells' (2..8) first" % (b["pet_shed_capacity"], len(owned)))
        if len(owned) >= int(settings.get("maximum_owned_pet_count") or 8):
            raise RebornError("at most %s owned pets" % settings.get("maximum_owned_pet_count"))
        pet, stats, sp = pet_new_values(rb, d, to_int(p.get("species"), 1, 255, "species"), to_int(p.get("aptitude", 1), 1, 16, "aptitude"),
                                        to_int(p.get("sex", 1), 0, 1, "sex"), p.get("level", 1), p.get("name"))
        cols = ["user_id"] + list(pet)
        ins = "INSERT INTO character_pets (%s) VALUES (%s) RETURNING id" % (
            ", ".join(ident(c) for c in cols), ", ".join(lit(v) for v in [cid] + list(pet.values())))
        # one statement: the pet, its six stat rows and the starter skill (CTE keeps the new id)
        st_vals = ", ".join("((SELECT id FROM np), %d, %s, %s, %s, %s, %s, %s)" % (
            s["stat_code"], lit(s["initial_savvy"]), lit(s["added_savvy"]), lit(s["base_growth_rate"]), lit(s["growth_acceleration"]),
            lit(s["birth_initial_savvy"]), lit(s["rarity_added_savvy"])) for s in stats)
        sql = ("WITH np AS (%s), st AS (INSERT INTO character_pet_stat_values (pet_id, stat_code, initial_savvy, added_savvy, "
               "base_growth_rate, growth_acceleration, birth_initial_savvy, rarity_added_savvy) VALUES %s RETURNING pet_id) "
               "INSERT INTO character_pet_skills (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active) "
               "SELECT id, %d, 0, 1, 0, true FROM np") % (ins, st_vals, int(sp["starter_skill_id"]))
        return [sql]
    return build


def op_pet_delete(rb, cid, p):
    pid = to_int(p.get("pet"), 1, 2 ** 62, "pet id")

    def build(d):
        pet = _pet(d, pid)
        if pet["activity_state"] != "owned" or any(s["pet_id"] == pid for s in d["sealed_pet_items"]):
            raise RebornError("pet #%d is %s (sealed/away); only owned pets can be deleted here" % (pid, pet["activity_state"]))
        return ["DELETE FROM character_pets WHERE id = %d AND user_id = %d" % (pid, cid)]
    return build


def op_pet_edit(rb, cid, p):
    pid = to_int(p.get("pet"), 1, 2 ** 62, "pet id")
    fields = p.get("fields") or {}

    def build(d):
        pet = _pet(d, pid)
        stats = sorted([s for s in d["character_pet_stat_values"] if s["pet_id"] == pid], key=lambda s: s["stat_code"])
        sets, stm = [], []
        for col, val in fields.items():
            if col in PET_EDITABLE:
                label, kind = PET_EDITABLE[col]
                if kind == "name":
                    v = ascii_name(val, "pet name")
                elif kind == "bool":
                    v = to_bool(val)
                elif kind.startswith("int:"):
                    _, lo, hi = kind.split(":")
                    v = to_int(val, int(lo), int(hi), label)
                elif kind.startswith("dec2:"):
                    _, lo, hi = kind.split(":")
                    v = to_dec(val, lo, hi, label, places=2)
                if canon(v) != canon(pet.get(col)):
                    sets.append("%s = %s" % (ident(col), lit(v)))
                    pet = dict(pet, **{col: v})
            elif col not in ("level", "presence", "aptitude", "savvy", "growth"):
                raise RebornError("pet field %s is not editable" % col)
        if int(pet["opened_skill_slots"]) > int(pet["available_skill_slots"]):
            raise RebornError("opened skill cells cannot exceed available skill cells")
        used = [s["slot_index"] for s in d["character_pet_skills"] if s["pet_id"] == pid and s["is_active"]]
        if used and max(used) >= int(pet["opened_skill_slots"]):
            raise RebornError("a skill sits in cell %d; open at least %d cells" % (max(used) + 1, max(used) + 1))
        if int(pet["current_energy"]) > int(pet["maximum_energy"]):
            raise RebornError("energy cannot exceed max energy")
        level = int(pet["level"])
        if "level" in fields:
            settings = rb.cat().get("pet_settings") or {}
            level = to_int(fields["level"], int(settings.get("minimum_level") or 1), int(settings.get("maximum_level") or 120), "pet level")
            if level != pet["level"]:
                sets.append("level = %d" % level)
        new_stats = {s["stat_code"]: dict(s) for s in stats}
        if "savvy" in fields:        # basic savvy per stat: sum must stay >= the birth baseline (server validators)
            sv = fields["savvy"]
            for code in range(1, 7):
                if str(code) in sv:
                    new_stats[code]["initial_savvy"] = to_dec(sv[str(code)], "0.01", "100000", STAT_NAMES[code] + " savvy", places=2)
            tot = sum(D(str(s["initial_savvy"])) for s in new_stats.values())
            base_total = pet.get("initial_savvy_baseline_total")
            if base_total is not None and tot < D(base_total):
                raise RebornError("basic savvy total %s is below the pet's birth baseline %s (server start-up check)" % (tot, base_total))
        if "growth" in fields:       # base growth rate per stat: total inside the active bracket
            gv = fields["growth"]
            for code in range(1, 7):
                if str(code) in gv:
                    new_stats[code]["base_growth_rate"] = to_dec(gv[str(code)], "0.000001", "1000", STAT_NAMES[code] + " growth", places=6)
            cat = rb.cat()
            apt = cat["aptitudes"].get("1" if not pet["growth_revealed"] else str(pet["aptitude"])) or {}
            tot = sum(D(str(s["base_growth_rate"])) for s in new_stats.values())
            lo, hi = D(str(apt.get("minimum_total_growth", 0))), D(str(apt.get("maximum_total_growth", 1000)))
            if not (lo <= tot <= hi):
                raise RebornError("growth total %s must be %s..%s (%s bracket; the server refuses to start otherwise)" % (
                    tot, lo, hi, "revealed aptitude" if pet["growth_revealed"] else "Weak, growth not revealed"))
        level_changed = level != int(pet["level"])
        for code, s in new_stats.items():
            old = next(x for x in stats if x["stat_code"] == code)
            ch = []
            if D(str(s["initial_savvy"])) != D(str(old["initial_savvy"])):
                ch.append("initial_savvy = %s" % lit(D(str(s["initial_savvy"]))))
            grow_changed = D(str(s["base_growth_rate"])) != D(str(old["base_growth_rate"]))
            if grow_changed:
                ch.append("base_growth_rate = %s" % lit(D(str(s["base_growth_rate"]))))
            if level_changed or grow_changed:
                added = (D(str(s["base_growth_rate"])) + D(str(s["growth_acceleration"]))) * level
                if added != D(str(old["added_savvy"])):
                    ch.append("added_savvy = %s" % lit(added))
            if ch:
                stm.append("UPDATE character_pet_stat_values SET %s, revision = revision + 1 WHERE pet_id = %d AND stat_code = %d" % (", ".join(ch), pid, code))
        if "presence" in fields:     # carried / summoned / merged: one per character (unique indexes)
            pr = fields["presence"]
            carried, summoned, contrib = to_bool(pr.get("carried")), to_bool(pr.get("summoned")), to_bool(pr.get("contributes"))
            if summoned and not carried:
                raise RebornError("a summoned pet must also be carried")
            if contrib and not (summoned and pet["has_owner_merge_talent"]):
                raise RebornError("only a summoned pet with the Merge talent can contribute (owner merge)")
            if (carried or summoned) and pet["activity_state"] != "owned":
                raise RebornError("only an owned pet can be carried")
            if carried:
                stm.insert(0, "UPDATE character_pets SET is_summoned = false, contributes_to_character = false, is_carried = false, "
                              "revision = revision + 1, updated_at = now() WHERE user_id = %d AND id <> %d AND (is_carried OR is_summoned OR contributes_to_character)" % (cid, pid))
            sets += ["is_carried = %s" % lit(carried), "is_summoned = %s" % lit(summoned), "contributes_to_character = %s" % lit(contrib)]
        if "aptitude" in fields:
            stm += pet_aptitude_statements(rb, d, pet, to_int(fields["aptitude"], 1, 16, "aptitude"), level)
        if sets:
            stm.append("UPDATE character_pets SET %s, revision = revision + 1, updated_at = now() WHERE id = %d AND user_id = %d" % (
                ", ".join(sets), pid, cid))
        return stm
    return build


def pet_aptitude_statements(rb, d, pet, aptitude, level):
    """Changing aptitude changes the savvy bracket, the innate talents (ck_character_pets_quality_innate_talents) and
    the hatch-rank evidence, which trg_character_pets_hatch_rank_evidence_guard makes immutable once set.  The admin
    tool rewrites all of them consistently in one statement with that one trigger bypassed for the transaction, and
    the server's start-up validators then check the result before COMMIT."""
    if aptitude == pet["aptitude"]:
        return []
    new, stats, _ = pet_new_values(rb, d, pet["species_id"], aptitude, pet["sex"], level, pet["name"], pet["remaining_lifetime"])
    old_stats = sorted([s for s in d["character_pet_stat_values"] if s["pet_id"] == pet["id"]], key=lambda s: s["stat_code"])
    cat = rb.cat()
    stm = ["SET LOCAL session_replication_role = replica"]
    cols = ["aptitude", "talent_mask", "has_owner_merge_talent", "initial_savvy_baseline_total", "initial_savvy_policy_version",
            "rarity_added_savvy_baseline_total", "rarity_added_savvy_policy_version", "initial_savvy_source_version",
            "birth_rank", "hatch_rank_roll", "hatch_rank_outcome_order", "hatch_rank_content_revision"]
    sets = ["%s = %s" % (c, lit(new[c])) for c in cols]
    if not new["has_owner_merge_talent"]:
        sets.append("contributes_to_character = false")
    if pet["growth_revealed"]:
        apt = cat["aptitudes"][str(aptitude)]
        g = ((D(str(apt["minimum_total_growth"])) + D(str(apt["maximum_total_growth"]))) / 2).quantize(D("0.01"))
        gv = _growth_vector(g)
    else:
        gv = [D(str(s["base_growth_rate"])) for s in old_stats]
    stm.append("UPDATE character_pets SET %s, revision = revision + 1, updated_at = now() WHERE id = %d" % (", ".join(sets), pet["id"]))
    for i, s in enumerate(stats):
        acc = D(str(old_stats[i]["growth_acceleration"])) if i < len(old_stats) else D(0)
        stm.append("UPDATE character_pet_stat_values SET initial_savvy = %s, birth_initial_savvy = %s, rarity_added_savvy = %s, "
                   "base_growth_rate = %s, added_savvy = %s, revision = revision + 1 WHERE pet_id = %d AND stat_code = %d" % (
                       lit(s["initial_savvy"]), lit(s["birth_initial_savvy"]), lit(s["rarity_added_savvy"]), lit(gv[i]),
                       lit((gv[i] + acc) * level), pet["id"], s["stat_code"]))
    stm.append("SET LOCAL session_replication_role = origin")
    stm.append("SELECT public.assert_character_pet_basic_savvy_aggregate(%d)" % pet["id"])
    return stm


def op_pet_skill(rb, cid, p):
    pid = to_int(p.get("pet"), 1, 2 ** 62, "pet id")
    action = p.get("action")

    def build(d):
        pet = _pet(d, pid)
        cur = [s for s in d["character_pet_skills"] if s["pet_id"] == pid]
        if action == "remove":
            sid = to_int(p.get("skill"), 1, I32, "skill")
            if not any(s["skill_id"] == sid for s in cur):
                raise RebornError("the pet does not have skill %d" % sid)
            return ["DELETE FROM character_pet_skills WHERE pet_id = %d AND skill_id = %d" % (pid, sid)]
        sid = to_int(p.get("skill"), 1, 65535, "skill id")
        cli = rb.client()["petskills"]
        if str(sid) not in cli and not p.get("force"):
            raise RebornError("pet skill %d is not in the client's Pet_Skill.xml" % sid)
        slot = to_int(p.get("slot", 0), 0, 11, "skill cell") if p.get("slot") not in (None, "") else None
        rank = to_int(p.get("rank", 1), 1, 32767, "skill rank")
        exp = to_int(p.get("experience", 0), 0, I32, "skill exp")
        active = to_bool(p.get("active", True))
        old = next((s for s in cur if s["skill_id"] == sid), None)
        if slot is None:
            slot = old["slot_index"] if old else next((i for i in range(int(pet["opened_skill_slots"])) if i not in [s["slot_index"] for s in cur]), None)
            if slot is None:
                raise RebornError("no free opened skill cell (opened %s); raise 'opened skill cells' first" % pet["opened_skill_slots"])
        if active and slot >= int(pet["opened_skill_slots"]):
            raise RebornError("cell %d is not opened (the pet has %s opened cells)" % (slot + 1, pet["opened_skill_slots"]))
        rs = rb.petskill_species(sid)
        if rs and int(pet["species_id"]) not in rs["species"] and not p.get("force"):
            names = ", ".join((rb.cat()["species"].get(str(x)) or {}).get("display_name", str(x)) for x in rs["species"])
            raise RebornError("%s is for %s only (species %s); this pet is species %s. Tick force (needs --allow-force) to add it anyway" % (
                (rb.client()["petskills"].get(str(sid)) or {}).get("name", sid), rs["family"], names, pet["species_id"]))
        other = next((s for s in cur if s["slot_index"] == slot and s["skill_id"] != sid), None)
        if other:
            raise RebornError("cell %d already holds skill %d; remove it first" % (slot + 1, other["skill_id"]))
        if old:
            return ["UPDATE character_pet_skills SET slot_index = %d, skill_rank = %d, skill_experience = %d, is_active = %s, "
                    "revision = revision + 1 WHERE pet_id = %d AND skill_id = %d" % (slot, rank, exp, lit(active), pid, sid)]
        return ["INSERT INTO character_pet_skills (pet_id, skill_id, slot_index, skill_rank, skill_experience, is_active) "
                "VALUES (%d, %d, %d, %d, %d, %s)" % (pid, sid, slot, rank, exp, lit(active))]
    return build


# ------------------------------------------------------------ skills / talents / quests / zodiac
def op_skill(rb, cid, p):
    kind, action = p.get("kind", "skill"), p.get("action", "add")

    def build(d):
        if kind == "skill":
            sid = to_int(p.get("id"), 0, I32, "skill id")
            if str(sid) not in rb.cat()["skills"]:
                raise RebornError("skill %d is not in skill_templates" % sid)
            if action == "remove":
                return ["DELETE FROM character_skills WHERE user_id = %d AND skill_id = %d" % (cid, sid)]
            lv = to_int(p.get("level", 1), 0, 32767, "skill level")
            return ["INSERT INTO character_skills (user_id, skill_id, skill_level, source) VALUES (%d, %d, %d, 'gwadmin-web') "
                    "ON CONFLICT (user_id, skill_id) DO UPDATE SET skill_level = EXCLUDED.skill_level" % (cid, sid, lv)]
        if kind == "talent":
            tid = to_int(p.get("id"), 0, I32, "talent id")
            if str(tid) not in rb.cat()["talents"]:
                raise RebornError("talent %d is not in talent_templates" % tid)
            if action == "remove":
                return ["DELETE FROM character_talents WHERE user_id = %d AND talent_id = %d" % (cid, tid)]
            rk = to_int(p.get("level", 1), 0, 32767, "talent rank")
            return ["INSERT INTO character_talents (user_id, talent_id, rank) VALUES (%d, %d, %d) "
                    "ON CONFLICT (user_id, talent_id) DO UPDATE SET rank = EXCLUDED.rank, updated_at = now()" % (cid, tid, rk)]
        raise RebornError("unknown kind %s" % kind)
    return build


def op_quest(rb, cid, p):
    action = p.get("action")

    def build(d):
        qid = to_int(p.get("quest"), 0, I32, "quest id")
        if action == "done":
            return ["UPDATE character_base SET quest_completed_ids = (SELECT array_agg(DISTINCT x ORDER BY x) FROM "
                    "unnest(quest_completed_ids || ARRAY[%d]) x) WHERE id = %d" % (qid, cid)]
        if action == "undone":
            return ["UPDATE character_base SET quest_completed_ids = array_remove(quest_completed_ids, %d) WHERE id = %d" % (qid, cid)]
        if action == "drop":
            return ["DELETE FROM character_quests WHERE character_id = %d AND quest_id = %d" % (cid, qid)]
        if action == "set":
            st = to_int(p.get("state", 0), 0, 32767, "state")
            pr = to_int(p.get("progress", 0), 0, 2 ** 62, "progress")
            return ["INSERT INTO character_quests (character_id, quest_id, state, progress) VALUES (%d, %d, %d, %d) "
                    "ON CONFLICT (character_id, quest_id) DO UPDATE SET state = EXCLUDED.state, progress = EXCLUDED.progress" % (cid, qid, st, pr)]
        raise RebornError("unknown quest action %s" % action)
    return build


def op_zodiac(rb, cid, p):
    def build(d):
        g = to_int(p.get("grid"), 0, 15, "grid")
        if p.get("action") == "remove":
            return ["DELETE FROM character_zodiac_skill_grids WHERE user_id = %d AND grid_index = %d" % (cid, g)]
        lv = to_int(p.get("level", 0), 0, 50, "grid level")
        sk = to_int(p.get("skill", -1), -1, I32, "selected skill")
        return ["INSERT INTO character_zodiac_skill_grids (user_id, grid_index, level, selected_skill_id) VALUES (%d, %d, %d, %d) "
                "ON CONFLICT (user_id, grid_index) DO UPDATE SET level = EXCLUDED.level, selected_skill_id = EXCLUDED.selected_skill_id, "
                "updated_at = now()" % (cid, g, lv, sk)]
    return build


OPS = {"item": op_item, "move": op_move, "base": op_base, "pet_add": op_pet_add, "pet_delete": op_pet_delete,
       "pet": op_pet_edit, "pet_skill": op_pet_skill, "skill": op_skill, "quest": op_quest, "zodiac": op_zodiac}


def describe(op, p, rb, d=None):
    """Change-log label in plain words (d = the character's rows before the write, for names and old values)."""
    cat = rb.cat()

    def iname(pid):
        try:
            return "%s (#%d)" % (rb.item_meta(int(pid))["name"], int(pid))
        except (TypeError, ValueError):
            return str(pid)

    def petname(pid):
        for x in (d or {}).get("character_pets", []):
            if str(x["id"]) == str(pid):
                return "%s (#%s)" % (x["name"], pid)
        return "#%s" % pid

    def apt(a):
        return (cat["aptitudes"].get(str(a)) or {}).get("display_name") or str(a)

    def species(s):
        return (cat["species"].get(str(s)) or {}).get("display_name") or "species %s" % s

    try:
        if op == "item":
            loc, slot = int(p["location"]), int(p["slot"])
            old = find_item(d, loc, slot) if d else None
            if p.get("item") is None:
                return "delete %s from %s" % (iname(old["prop_id"]) if old else "item", slot_label(loc, slot))
            it = p["item"]
            return "%s %s x%s in %s" % ("set" if old else "add", iname(it.get("prop_id")), it.get("stack", 1), slot_label(loc, slot))
        if op == "move":
            f, t = p["from"], p["to"]
            src = find_item(d, int(f["location"]), int(f["slot"])) if d else None
            dst = find_item(d, int(t["location"]), int(t["slot"])) if d else None
            return "move %s: %s -> %s%s" % (iname(src["prop_id"]) if src else "item", slot_label(f["location"], f["slot"]),
                                            slot_label(t["location"], t["slot"]), " (swapped with %s)" % iname(dst["prop_id"]) if dst else "")
        if op == "base":
            b = (d or {}).get("character_base", [{}])[0]
            parts = []
            for k, v in (p.get("fields") or {}).items():
                label = BASE_EDITABLE.get(k, (k,))[0]
                parts.append("%s %s -> %s" % (label, b.get(k, "?"), v) if b else "%s = %s" % (label, v))
            return "set " + ", ".join(parts)
        if op == "pet_add":
            return "add pet %s (%s), aptitude %s, level %s%s" % (p.get("name") or species(p.get("species")), species(p.get("species")),
                                                                apt(p.get("aptitude", 1)), p.get("level", 1), ", sex %s" % p.get("sex") if "sex" in p else "")
        if op == "pet_delete":
            return "delete pet %s" % petname(p.get("pet"))
        if op == "pet":
            parts = []
            for k, v in (p.get("fields") or {}).items():
                if k == "aptitude":
                    parts.append("aptitude %s" % apt(v))
                elif k == "presence":
                    flags = [n for n, key in (("carried", "carried"), ("summoned", "summoned"), ("merged into owner", "contributes")) if to_bool(v.get(key))]
                    parts.append(", ".join(flags) if flags else "not carried")
                elif k == "savvy":
                    parts.append("basic savvy " + " ".join("%s %s" % (STAT_NAMES.get(int(c), c), x) for c, x in sorted(v.items())))
                elif k == "growth":
                    parts.append("growth " + " ".join("%s %s" % (STAT_NAMES.get(int(c), c), x) for c, x in sorted(v.items())))
                else:
                    parts.append("%s %s" % (PET_EDITABLE.get(k, (k,))[0], v))
            return "pet %s: %s" % (petname(p.get("pet")), ", ".join(parts))
        if op == "pet_skill":
            sk = rb.client()["petskills"].get(str(p.get("skill"))) or {}
            nm = "%s (#%s)" % (sk.get("name", "skill"), p.get("skill"))
            if p.get("action") == "remove":
                return "pet %s: remove skill %s" % (petname(p.get("pet")), nm)
            cellp = " in cell %d" % (int(p["slot"]) + 1) if p.get("slot") not in (None, "") else ""
            return "pet %s: set skill %s%s, rank %s%s" % (petname(p.get("pet")), nm, cellp, p.get("rank", 1),
                                                          "" if to_bool(p.get("active", True)) else ", inactive")
        if op == "skill":
            kind = p.get("kind", "skill")
            tbl = cat["skills"] if kind == "skill" else cat["talents"]
            nm = "%s (#%s)" % ((tbl.get(str(p.get("id"))) or {}).get("name", kind), p.get("id"))
            if p.get("action") == "remove":
                return "remove %s %s" % (kind, nm)
            return "%s %s %s" % (kind, nm, "level %s" % p.get("level", 1) if kind == "skill" else "rank %s" % p.get("level", 1))
        if op == "quest":
            q = rb.client()["quests"].get(str(p.get("quest"))) or {}
            what = {"done": "mark done", "undone": "mark not done", "drop": "drop",
                    "set": "set state %s progress %s" % (p.get("state", 0), p.get("progress", 0))}.get(p.get("action"), p.get("action"))
            return "quest %s%s: %s" % (p.get("quest"), " '%s'" % q["title"] if q.get("title") else "", what)
        if op == "zodiac":
            if p.get("action") == "remove":
                return "zodiac grid %s: remove" % p.get("grid")
            return "zodiac grid %s: level %s, selected skill %s" % (p.get("grid"), p.get("level", 0), p.get("skill", -1))
    except (KeyError, TypeError, ValueError, IndexError):
        pass
    return "%s %s" % (op, json.dumps(p, default=str)[:200])


def load_change_files(changes_dir):
    out = []
    for f in sorted(glob.glob(os.path.join(changes_dir, "change-rb-*.json")))[-300:]:
        try:
            with open(f, encoding="utf-8") as fh:
                out.append(json.load(fh, parse_float=Decimal))
        except (OSError, ValueError):
            pass
    return out
