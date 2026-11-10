#!/usr/bin/env python3
"""build_origin_catalog.py - English names, attributes and icons for every ID the reborn database uses,
read from the Godswar ORIGIN client (GodsWar-2.51/Reborn-Client/Client) for gwadmin-web.

Run on the Mac (it needs the client and Pillow); the outputs are checked in, so the VPS needs neither:

    python3 -I build_origin_catalog.py [CLIENT_DIR] [--attributes-only] [--no-images]
    python3 -I catalog_dbrefs.py --psql "ssh vps sudo -n -u postgres psql" --db godswar   # optional, read-only:
                                       # refreshes catalog-origin/db-refs.json for the cross-check and coverage

Everything the client files contain is DATA: names and descriptions are copied as text, never interpreted.

Outputs (catalog-origin/, one JSON object per file, keyed by the ID as a string unless noted)
  items.json        every item of ItemBaseAttribute.xml: name, description, kind, category, equipment slot,
                    level range, classes, camp, sex, quality vectors (stats per quality Q1..Q10), allowed append
                    attributes, forge chain, stack, price, skill it casts / teaches, icon + sprite
  appendattrs.json  item append attributes (ItemAppendAttribute.xml ID = item_attribute_templates.id)
  attributes.json   append-attribute stat TYPE -> player-facing label (unchanged format)
  skills.json       player skills (Magic.ini) + SkillInfo.dat text
  talents.json      talents (Skill.ini)
  zodiac.json       {signs, levels, grids, kinds, stones}  (ConstellationConfig.lua, SkillTrainConfig.lua, MagicMini.xml)
  monsters.json     template_key -> name, maps, rank, model       (Monster/<map>/Monster.ini)
  npcs.json         npc_key -> name, description, templates        (NpcName.dat, NPCDescription.dat, NPC.INI)
  maps.json         map id -> scene, English name, regions          (MapIdToNameConfig.ini, QuestRegion.dat, Address.ini)
  quests.json       quest id -> title, level, giver, objectives, rewards  (Quest.xml + Text/Quest/<id>.dat)
  titles.json       title id -> name, description                   (DesigName.dat, DesigInfo.dat)
  pets.json         species (Pet.xml, Pet.ini, Message_Pet.dat)
  petskills.json    pet skills (Pet_Skill.xml + Message_Pet.dat)
  mounts.json       {items: mount + mount gear items, rides: ride statuses -> Ride.ini model}
  statuses.json     buffs / debuffs (Status.ini)
  guild.json        {buildings, altars, levels, defaults, consume, duties}  (Consortia.xml, AltarInfo.xml)
  holystones.json   holy stone effects (EquipStoneInfo.xml + EquipDescription.dat) + the stone items
  suits.json        holy suit codes (EquipSuitInfoIni.xml, EquipEffect.xml, EquipEffectSuit.xml)
  shops.json        client shop whitelist (ItemShop.xml) + the server's compiled NPC shop / mall catalogues
  currencies.json   wallet currencies (codes used by the ledgers) with the client's labels
  labels.json       small lookups: classes, camps, sexes, faiths, item categories, stat names, qualities, grades,
                    equipment slots, pet aptitudes, guild duties
  catalog.json      counts, sources, sprite layout
  items-crosscheck.json + coverage.json + CATALOG.md   (only when db-refs.json exists)
Images (static/)
  icons-origin.png  the four item atlases Icon.gwo .. Icon4.gwo, 2 x 2 (unchanged; reborn.js uses it)
  origin-ui.png     main.gwo (UI skin)
  catalog-icons.png every distinct icon of every catalogue as 36 x 36 tiles, 64 per row; an entry's
                    `sprite` = n means background-position -(n % 64) * 36px -(n // 64) * 36px
"""
import base64
import collections
import gzip
import hashlib
import io
import json
import os
import re
import struct
import sys
import unicodedata
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
ROOT = os.path.dirname(os.path.dirname(TOOLS))          # .../Godswar
sys.path.insert(0, TOOLS)
from build_catalog import read_dat, read_text  # noqa: E402

ATLASES = ["Icon.gwo", "Icon2.gwo", "Icon3.gwo", "Icon4.gwo"]
REBORN_SRC = os.path.join(ROOT, "GodsWar-2.51", "emulators", "src", "godswar-server")
SPRITE_COLS = 64
TILE = 36

# reborn.py KIND_SLOT (S/State/EquipmentSlots.cs): item kind -> equipment slot
KIND_SLOT = {"head": 0, "amulet": 1, "glove": 2, "armor": 3, "cloth": 3, "cuff": 4, "girdle": 5, "shoes": 6,
             "leggins": 7, "ring": 8, "weapon": 10, "shield": 11, "stylish": 12, "mounthead": 15, "mountarmor": 16,
             "mountsoul": 17, "mountornament": 18, "mountamulet": 19, "mount": 20}
SLOT_LABEL = {0: "Head", 1: "Amulet", 2: "Gloves", 3: "Armor", 4: "Cuffs (wrist)", 5: "Girdle", 6: "Shoes",
              7: "Leggings", 8: "Ring", 9: "Ring", 10: "Weapon", 11: "Shield", 12: "Fashion", 15: "Mount Coronet",
              16: "Mount Armor", 17: "Mount Soul", 18: "Mount Saddle", 19: "Mount Tassels", 20: "Mount (riding)"}
# ItemBaseAttribute.xml vector stats (one value per quality Q1..Q10) -> EquipDescription.dat key of the label
VECTOR_STATS = [("Attack", "Attack"), ("MagicAk", "MagicAk"), ("Defence", "Defence"), ("MagicRec", "MagicRec"),
                ("MaxHP", None), ("MaxMP", None), ("Hit", None), ("Miss", None), ("InjureImbibe", "InjureImbibe"),
                ("FuryAddAk", "FuryAddAk"), ("FuryAddRec", "FuryAddRec"), ("Speed", None),
                ("AttackRadius", "AttackRadius"), ("AttackSpeed", "AttackSpeed")]
STAT_FALLBACK = {"MaxHP": "Max HP", "MaxMP": "Max MP", "Hit": "Hit Rating", "Miss": "Dodge Rating",
                 "Speed": "Movement Speed bonus"}
FRACTION_KEYS = ["BaseFraction", "AppFraction", "ArmEff", "ArmEffFraction", "DefendEff", "DefendFraction"]
# Status.ini effect codes: the identifiers are in the file's own comment block; the plain-English text
# is a translation of the Chinese comment next to each (written for this catalogue).
STATUS_EFFECTS = {
    0: "Interrupts casting", 1: "Returns damage (special)", 2: "Cannot move", 3: "Cannot cast spells",
    4: "Cannot use combat skills", 5: "Cannot use normal attacks", 6: "Cannot use items", 7: "Returns damage",
    8: "Casting time changed", 9: "Healing received changed", 10: "Healing done changed",
    11: "Spell damage taken changed", 12: "Spell damage dealt changed", 13: "Physical damage taken changed",
    14: "Physical damage dealt changed", 15: "EXP gain changed", 16: "Max MP changed", 17: "Max HP changed",
    18: "Hit rate changed", 19: "Dodge rate changed", 20: "Defense changed", 21: "Attack changed",
    22: "Crit hit chance", 23: "Crit bonus", 24: "Spell attack changed", 25: "Spell defense changed",
    26: "Restores HP", 27: "Loses HP over time", 28: "Restores MP", 29: "Loses MP over time",
    30: "Status-spell success rate changed", 31: "Status-spell resistance changed", 32: "Talent EXP gain changed",
    33: "Movement speed changed", 34: "Pet EXP gain changed", 35: "Peace state", 36: "Removes debuffs",
    37: "Disables percentage healing"}
# Belief.dat and Consortia_Job.ini ship only Chinese text in this client; these are the standard English names
# of the same entries (translated for this catalogue - source marked "translated").
FAITHS = {0: "Ares", 1: "Athena", 2: "Zeus", 3: "Poseidon", 4: "Hades", 5: "Hera", 6: "Hermes", 7: "Hestia",
          8: "Hephaestus", 9: "Apollo", 10: "Demeter", 11: "Aphrodite", 12: "Artemis", 13: "Dionysus", 255: "No faith"}
GUILD_DUTIES = {0: "None", 1: "Trainee", 2: "Member", 3: "Elite", 4: "Director", 5: "Vice Leader", 6: "Guild Leader"}
GRID_TYPES = {1: "Attack skill (type A)", 2: "Attack skill (type B)", 3: "Defense skill (type A)",
              4: "Defense skill (type B)"}
COLOR_CODE = re.compile(r"\|c[0-9A-Fa-f]{8}")
CJK = re.compile(r"[\u3000-\u303f\u3400-\u9fff\uff00-\uffef]")
# Consumables have no display category in the client (ItemType= is an internal use code), so the catalogue
# derives one from the item's tag prefix in ItemBaseAttribute.xml (category_source "derived").
CONSUMABLE_PREFIX = [
    (("QuestS", "QuestScoll", "questitem", "taskitem", "Syrinx", "Oracle", "Ghost", "Ghose", "Bhose", "Rhose", "Whose",
      "Ahose"), "Quest item"),
    (("Lifing",), "Life-skill material"), (("Earphone", "Sbox", "MoonCake", "Chip", "A"), "Event item"),
    (("Healthbook",), "Manual"), (("Material", "MaterialBase", "MaterialAppend", "MaterialOdds", "MaterialID", "MaterialFlee",
                                   "G", "Smithing", "Rmaterial", "Macadam", "Rmacadam", "Copperstone", "Ironstone"), "Forge material"),
    (("VIPbag", "Novice", "Crystalbag", "Easterbag", "Petgift", "GoldBox", "instancebox", "Congregation", "box", "Free"),
     "Box or gift pack"),
    (("GoldKey", "AddKey", "Storage"), "Key"), (("Steeditem",), "Mount shifter"), (("Ride",), "Mount material"),
    (("Item",), "Skill item"), (("FlyBook",), "Teleport scroll"),
    (("Stone", "Money", "AddEXP", "SkillEXP", "Glory", "GuildStone", "GuildMoney", "GuildGlory", "AddLevel", "WHonor"),
     "Reward item"),
    (("EXPGet",), "EXP potion"), (("AutoItem", "HPPotion", "MPPotion", "SPPotion", "APPotion"), "Potion"),
    (("Firegholiness", "Waterholiness"), "Holy stone"), (("Shenqi",), "Holy suit material"),
    (("Pet", "PetName", "PetQuest", "AddPetNum"), "Pet item"),
]
CONSUMABLE_ITEMTYPE = {0: "Tool", 3: "Pet skill book", 4: "Pet skill book", 6: "Pet egg", 7: "Pet food", 9: "Pet stick",
                       10: "Potion", 11: "Potion", 14: "Potion", 51: "Quest item", 52: "Gathering tool", 53: "Recipe",
                       54: "Coupon"}


# Database IDs this client cannot name, with the reason found in the reborn source / docs.
WHY_MISSING = {
    ("item", 1899): "client entry GMSaff has no EquipName.dat text (GM staff)",
    ("item", 2444): "client entry Cap2444 has no EquipName.dat text",
    ("mount", 14429): "malformed client entry (tag Ride14428, no name); reborn: cannot be generated or ridden",
    ("appendattr", 460): "VampiricPer - reborn-added attribute (stat type 27, life steal), not in ItemAppendAttribute.xml",
    ("appendattr", 470): "ReflectDamagePer - reborn-added attribute (stat type 28), not in ItemAppendAttribute.xml",
    ("title", 5152): "Heir of Perseus - reborn-added Medusa title, not in DesigName.dat",
    ("title", 5153): "Bane of the Three Sisters - reborn-added Medusa title, not in DesigName.dat",
    ("title", 5154): "Gorgon Breaker - reborn-added Medusa title, not in DesigName.dat",
    ("species", 46): "Bloodfang - reborn's own Pet.xml has it, this client does not",
    ("monster", "A_snowman_001"): "Snowman - seasonal monster in reborn's data, no Monster.ini entry in this client",
    ("monster", "A_snowman_002"): "Snowwoman - seasonal monster in reborn's data, no Monster.ini entry in this client",
    ("monster", "C_boss_persiawarrior_001"): "only on the GM test map, whose Monster.ini names it in Chinese",
    ("mount", 14422): "the client's own name for it is Chinese (a joke motorbike mount)",
}
for _i in range(6400, 6406):
    WHY_MISSING[("petskill", _i)] = "Vampiric pet skill - reborn-added (docs/vampiric-pet-skills-20260914.md), not in Pet_Skill.xml"


def has_cjk(s):
    return bool(CJK.search(s or ""))


def consumable_category(tag, itype, rec):
    if rec.get("teaches_skill") is not None:
        return "Skill book"
    if rec.get("pet_skill") is not None:
        return "Pet skill book"
    if itype in CONSUMABLE_ITEMTYPE:
        return CONSUMABLE_ITEMTYPE[itype]
    if itype is not None and 1 <= itype <= 22:
        return "Pet item"
    prefix = re.sub(r"[\d_]+$", "", tag)
    for prefixes, label in CONSUMABLE_PREFIX:
        if prefix in prefixes:
            return label
    if tag.startswith("Stone90"):
        return "Holy stone"
    if rec.get("quest"):
        return "Quest item"
    return "Other item"


# ------------------------------------------------------------------ readers
def en(s):
    """NFKC: full-width brackets, ideographic spaces and Roman-numeral signs the client mixes into English text
    become plain characters (（1） -> (1)); Chinese stays Chinese."""
    return unicodedata.normalize("NFKC", s or "").strip()


def clean(s):
    """Strip the client's |cAARRGGBB colour codes and its %Enter% line breaks."""
    s = en(COLOR_CODE.sub("", s or ""))
    return s.replace("%Enter%", "\n").strip()


def decode(path):
    raw = open(path, "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16")
    if raw[:3] == b"\xef\xbb\xbf":
        raw = raw[3:]
    try:
        return raw.decode("utf-8")
    except UnicodeDecodeError:
        return raw.decode("gb18030", "replace")


def parse_xml(path):
    txt = decode(path)
    txt = re.sub(r"<\?xml[^>]*\?>", "", txt, count=1)
    return ET.fromstring(txt)


def read_ini(path, cut_semicolon=True):
    """[section] key=value -> OrderedDict(section -> OrderedDict); keeps every section (also numeric ones).
    A value loses a trailing ';' and, with cut_semicolon, everything from the first ';' on."""
    out = collections.OrderedDict()
    cur = None
    if not os.path.exists(path):
        return out
    for line in decode(path).splitlines():
        s = line.strip()
        if not s or s[0] in "#;" or s.startswith("//"):
            continue
        m = re.match(r"^\[([^\]]*)\]", s)
        if m:
            cur = out.setdefault(m.group(1).strip(), collections.OrderedDict())
            continue
        if cur is None:
            continue
        m = re.match(r"^([^=＝]+)[=＝](.*)$", s)
        if not m:
            continue
        v = m.group(2).strip()
        if cut_semicolon:
            v = v.split(";")[0].split("//")[0].strip()
        else:
            v = v.rstrip(";").strip()
        cur[m.group(1).strip()] = v
    return out


def num(v, d=0):
    try:
        f = float(str(v).strip().rstrip("fF"))
    except (TypeError, ValueError):
        return d
    return int(f) if f == int(f) else f


def nums(v):
    """'1,2,3' -> [1, 2, 3]; empty -> []"""
    v = (v or "").strip().strip(",")
    return [num(x) for x in v.split(",") if x.strip() != ""] if v else []


def tex_name(path):
    return os.path.basename((path or "").replace("\\", "/")).lower()


def atlas_index(texture):
    base = tex_name(texture)
    low = [a.lower() for a in ATLASES]
    return low.index(base) if base in low else 0


# ------------------------------------------------------------------ sprites
class Sprites:
    """Collects icon requests (texture, x, y, w, h); every distinct picture becomes one 36x36 tile."""

    def __init__(self, texdir):
        self.texdir = texdir
        self.req = collections.OrderedDict()      # (tex, x, y, w, h) -> index (provisional)
        self.final = {}

    def add(self, texture, pos, size=(TILE, TILE)):
        if not pos:
            return None
        xy = nums(pos)
        if len(xy) < 2 or xy[0] < 0 or xy[1] < 0:
            return None
        key = (tex_name(texture) or "icon.gwo", int(xy[0]), int(xy[1]), int(size[0]), int(size[1]))
        if key not in self.req:
            self.req[key] = len(self.req)
        return self.req[key]

    def build(self, out_png):
        """Crop, dedupe identical pictures, write the sheet; returns {provisional index -> final index}."""
        try:
            from PIL import Image
        except ImportError:
            print("Pillow is not installed; sprites keep their provisional numbers and no sheet is written")
            return {i: i for i in self.req.values()}
        cache, tiles, by_hash, remap = {}, [], {}, {}
        for (tex, x, y, w, h), idx in self.req.items():
            if tex not in cache:
                p = None
                for fn in os.listdir(self.texdir):
                    if fn.lower() == tex:
                        p = os.path.join(self.texdir, fn)
                cache[tex] = Image.open(io.BytesIO(open(p, "rb").read())).convert("RGBA") if p else None
            im = cache[tex]
            if im is None or x + w > im.width or y + h > im.height:
                remap[idx] = None
                continue
            tile = im.crop((x, y, x + w, y + h))
            if tile.getchannel("A").getbbox() is None:
                remap[idx] = None
                continue
            if (w, h) != (TILE, TILE):
                tile = tile.resize((TILE, TILE), Image.LANCZOS)
            hsh = hashlib.sha1(tile.tobytes()).hexdigest()
            if hsh not in by_hash:
                by_hash[hsh] = len(tiles)
                tiles.append(tile)
            remap[idx] = by_hash[hsh]
        rows = max(1, (len(tiles) + SPRITE_COLS - 1) // SPRITE_COLS)
        sheet = Image.new("RGBA", (SPRITE_COLS * TILE, rows * TILE), (0, 0, 0, 0))
        for i, t in enumerate(tiles):
            sheet.paste(t, ((i % SPRITE_COLS) * TILE, (i // SPRITE_COLS) * TILE))
        sheet.save(out_png, optimize=True)
        print("wrote %s (%d tiles from %d requests, %d x %d, %d bytes)" % (
            out_png, len(tiles), len(self.req), sheet.width, sheet.height, os.path.getsize(out_png)))
        self.tiles = len(tiles)
        return remap


def apply_sprites(obj, remap):
    """Replace every provisional 'sprite' number (also nested) by its final tile index."""
    if isinstance(obj, dict):
        for k, v in list(obj.items()):
            if k in ("sprite", "sprite_male", "sprite_female") and isinstance(v, int):
                obj[k] = remap.get(v)
            else:
                apply_sprites(v, remap)
    elif isinstance(obj, list):
        for v in obj:
            apply_sprites(v, remap)


# ------------------------------------------------------------------ labels
def build_labels(loc):
    ed = read_dat(os.path.join(loc, "Text", "EquipDescription.dat"))
    msg = read_dat(os.path.join(loc, "Text", "Message.dat"))
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "ItemColor.xml"))
    qual, grades, apt = {}, {}, {}
    for el in root.iter():
        a = el.attrib
        if el.tag.startswith("BaseLevel"):
            qual[int(a["BaseLv"])] = {"name": a.get("BaseName", ""), "color": a.get("BaseColor", "")}
        elif el.tag.startswith("AppLevel"):
            grades[int(a["AppendLv"])] = {"stars": num(a.get("AppendStar")), "color": a.get("AppendColor", ""),
                                          "attribute_color": a.get("AppAttributeColor", "")}
        elif el.tag.startswith("Aptitude"):
            apt[int(a["BaseLv"])] = {"name": a.get("BaseName", ""), "color": a.get("BaseColor", "")}
    stats = {}
    for key, dkey in VECTOR_STATS:
        stats[key] = {"label": (ed.get(dkey) if dkey else "") or STAT_FALLBACK.get(key, key),
                      "source": ("EquipDescription.dat:" + dkey) if dkey and ed.get(dkey) else "derived"}
    groups = {}
    for g in ("Swords", "TwoHandsSwords", "Axes", "TwoHandsAxes", "Spears", "Javelins", "Bows", "Wands", "Staffs",
              "Shields", "Armors", "Cloths", "Heads", "Cuffs", "Leggins", "Glove", "Shoes", "Girdle", "Amulet", "Ring",
              "Stylish", "Create", "Pet", "Mount", "Mounthead", "Mountarmor", "Mountsoul", "Mountornament",
              "Mountamulet"):
        if ed.get(g):
            groups[g] = ed[g]
    groups["Stylish"] = "Fashion"          # EquipDescription.dat says "Suit"; the inventory tab says Fashion
    groups["Item"] = "Consumables and other items"
    itemtypes = {int(k): v for k, v in ed.items() if k.isdigit()}
    return {
        "classes": {i: {"name": ed.get("Class%d" % i, ""), "source": "EquipDescription.dat:Class%d" % i} for i in range(4)},
        "camps": {0: ed.get("Camp0", "Sparta"), 1: ed.get("Camp1", "Athens")},
        "sexes": {0: ed.get("SexBind0", "Female"), 1: ed.get("SexBind1", "Male")},
        "faiths": {k: {"name": v, "source": "translated from Text/Belief.dat (Chinese in this client)"} for k, v in FAITHS.items()},
        "guild_duties": {k: {"name": v, "source": "translated from Settings/Sys/Consortia_Job.ini (Chinese in this client)"}
                         for k, v in GUILD_DUTIES.items()},
        "shop_tabs": {"note": "EquipDescription.dat 0..43: captions of the shop / mall tabs (not ItemType=)",
                      "tabs": itemtypes},
        "item_groups": groups,
        "stats": stats,
        "qualities": qual,
        "grades": grades,
        "pet_aptitudes": apt,
        "equipment_slots": SLOT_LABEL,
        "status_effects": STATUS_EFFECTS,
        "currency_words": {k: msg.get(k, "") for k in ("Money", "Bijou", "BindingGold", "SimplePoint", "HardPoint")},
    }


# ------------------------------------------------------------------ items
def build_items(loc, labels, sprites, skills_by_id, petskills):
    names = read_dat(os.path.join(loc, "Text", "EquipName.dat"))
    descs = read_dat(os.path.join(loc, "Text", "EquipDescription.dat"))
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "ItemBaseAttribute.xml"))
    forge = {}
    froot = parse_xml(os.path.join(loc, "Settings", "Sys", "EquipForge.xml"))
    for el in froot:
        a = el.attrib
        if "ID" in a:
            forge[int(a["ID"])] = {"next": num(a.get("NextID"), None), "fail_to": num(a.get("BadID"), None),
                                   "chance": num(a.get("Probability")), "cost_silver": num(a.get("Amoney")),
                                   "cost_quality": nums(a.get("Bmoney")), "cost_grade": nums(a.get("Cmoney")),
                                   "quality_chance_add": nums(a.get("BaseProyAdd")),
                                   "grade_chance_add": nums(a.get("AppendProyAdd")), "related": nums(a.get("RltID"))}
    shop = set()
    sroot = parse_xml(os.path.join(loc, "Settings", "Sys", "ItemShop.xml"))
    for el in sroot.iter():
        if "ID" in el.attrib:
            shop.add(int(el.attrib["ID"]))
    items, dupes = {}, []
    known = {"ID", "Type", "Texture", "Icon", "Overlap", "PlayLv", "Class", "Equip", "Hand", "BindType", "Money",
             "ExpiredTime", "ItemType", "Camp", "SexBind", "MainAttribute", "Random", "Distribution", "Skill", "SkillID",
             "PrevSkillID", "PetSkill", "Use", "SkillFlag", "Quest", "Values", "Lifing", "Mode", "PartId", "PartHair",
             "Hair", "Food", "Fill", "Favor", "SpecialFlag", "GoldBoxIdx", "KEY", "Petlimit", "UndoSkillID",
             "PlaceLimit", "EffChatOpA", "EffChatOpB"} | {k for k, _ in VECTOR_STATS} | set(FRACTION_KEYS)
    groups = labels["item_groups"]

    def walk(el, path):
        for ch in el:
            if "ID" in ch.attrib:
                a = ch.attrib
                iid = int(a["ID"])
                if iid in items:
                    dupes.append(iid)
                kind = a.get("Type", "")
                group = path[-1] if path else ""
                lv = nums(a.get("PlayLv"))
                classes = nums(a.get("Class"))
                itype = num(a.get("ItemType"), None) if a.get("ItemType", "") != "" else None
                if group and group != "Item" and group in groups:
                    category, csrc = groups[group], "EquipDescription.dat:" + group
                else:
                    category, csrc = None, "derived"
                slot = KIND_SLOT.get(kind, -1)
                rec = {
                    # unchanged fields (reborn.py / reborn.js use them)
                    "id": iid, "tag": ch.tag, "group": "/".join(path),
                    "name": en(names.get(ch.tag, "")), "desc": clean(descs.get(ch.tag, "")),
                    "type": kind, "icon": a.get("Icon", ""), "atlas": atlas_index(a.get("Texture", "")),
                    "overlap": max(1, int(num(a.get("Overlap", "1"), 1) or 1)), "playlv": lv[0] if lv else 0,
                    "class": a.get("Class", ""), "equip": a.get("Equip", ""), "hand": a.get("Hand", ""),
                    "bindtype": a.get("BindType", ""), "money": num(a.get("Money", "0")),
                    "expired": a.get("ExpiredTime", ""),
                    # added
                    "kind": kind, "category": category, "category_source": csrc, "item_type": itype,
                    "slot": slot, "slot_label": SLOT_LABEL.get(slot, ""),
                    "minlv": lv[0] if lv else 0, "maxlv": lv[1] if len(lv) > 1 else (lv[0] if lv else 0),
                    "classes": classes, "class_labels": [labels["classes"].get(c, {}).get("name", "class %s" % c) for c in classes],
                    "camp": num(a.get("Camp"), None) if a.get("Camp", "") != "" else None,
                    "sex": num(a.get("SexBind"), None) if a.get("SexBind", "") != "" else None,
                    "stack": max(1, int(num(a.get("Overlap", "1"), 1) or 1)), "price": num(a.get("Money", "0")),
                    "sprite": sprites.add(a.get("Texture", ""), a.get("Icon", "")),
                    "in_shop_list": iid in shop,
                }
                st = {}
                for key, _ in VECTOR_STATS:
                    if a.get(key, "") != "":
                        st[key] = nums(a[key])
                if st:
                    rec["stats"] = st
                fr = {k: nums(a[k]) for k in FRACTION_KEYS if a.get(k, "") != ""}
                if fr:
                    rec["fractions"] = fr
                # highest quality / grade THIS client has numbers for (reborn docs/client-reverse-engineering-notes.md:
                # a quality or grade past the end of these lists can make the client fail stat lookup or close)
                qlen = max([len(v) for v in st.values()] + [len(fr.get("BaseFraction", []))] or [0])
                if qlen:
                    rec["max_quality"] = qlen
                if fr.get("AppFraction"):
                    rec["max_grade"] = len(fr["AppFraction"])
                if a.get("MainAttribute", "") != "":
                    rec["append_attributes"] = nums(a["MainAttribute"])
                if a.get("Random", "") != "":
                    rec["random"] = num(a["Random"])
                if a.get("Distribution", "") != "":
                    rec["distribution"] = nums(a["Distribution"])
                if a.get("Skill", "") != "":
                    sid = num(a["Skill"])
                    rec["use_skill"] = sid
                    rec["use_skill_name"] = (skills_by_id.get(sid) or {}).get("name", "")
                if a.get("SkillID", "") != "":
                    sid = num(a["SkillID"])
                    rec["teaches_skill"] = sid
                    rec["teaches_skill_name"] = (skills_by_id.get(sid) or {}).get("name", "")
                    rec["prev_skill"] = num(a.get("PrevSkillID"), None)
                if a.get("PetSkill", "") != "":
                    ps = num(a["PetSkill"])
                    rec["pet_skill"] = ps
                    rec["pet_skill_name"] = (petskills.get(ps) or {}).get("name", "")
                for src, dst in (("Use", "usable"), ("SkillFlag", "skill_flag"), ("Quest", "quest"), ("Values", "values"),
                                 ("Lifing", "life_skill"), ("Mode", "mode"), ("PartHair", "fashion_hair"),
                                 ("Hair", "hide_hair"), ("Food", "pet_food"), ("Fill", "pet_food_fill"),
                                 ("Favor", "pet_food_favor"), ("GoldBoxIdx", "gold_box"), ("KEY", "key"),
                                 ("Petlimit", "pet_limit"), ("UndoSkillID", "undo_skill"), ("PlaceLimit", "place_limit")):
                    if a.get(src, "") != "":
                        rec[dst] = num(a[src], a[src])
                if a.get("PartId", "") != "":
                    rec["fashion_parts"] = nums(a["PartId"])
                if a.get("SpecialFlag"):
                    rec["special"] = a["SpecialFlag"]
                if iid in forge:
                    rec["forge"] = forge[iid]
                if rec["category"] is None:
                    rec["category"] = consumable_category(ch.tag, itype, rec) if kind in ("consume item", "skillitem") \
                        else (groups.get(kind.title()) or kind.title())
                if has_cjk(rec["name"]):
                    rec["name_cn"] = True
                extra = {k: v for k, v in a.items() if k not in known}
                if extra:
                    rec["extra"] = extra
                items[iid] = rec
            if len(ch):
                walk(ch, path + [ch.tag])

    walk(root, [])
    return items, dupes, shop


def build_appendattrs(loc, attributes):
    descs = read_dat(os.path.join(loc, "Text", "EquipDescription.dat"))
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "ItemAppendAttribute.xml"))
    out = {}
    for el in root.iter():
        a = el.attrib
        if el is root or "ID" not in a:
            continue
        aid = int(a["ID"])
        st = num(a.get("Type"), None)
        levels = []
        i = 1
        while ("L%d" % i) in a:
            levels.append(num(a["L%d" % i]))
            i += 1
        lab = (attributes.get(st) or {}).get("label", "")
        out[aid] = {"id": aid, "key": el.tag, "stat_type": st, "label": lab, "text": clean(descs.get(el.tag, "")),
                    "distribution": nums(a.get("Distribution")), "flag": num(a.get("Flag"), None),
                    "levels": levels, "max_level": len(levels)}
    return out


# ------------------------------------------------------------------ skills, talents, zodiac, statuses
def build_statuses(loc, sprites):
    ini = read_ini(os.path.join(loc, "Settings", "Sys", "Status.ini"), cut_semicolon=False)
    out = {}
    for sec, v in ini.items():
        if not sec.isdigit():
            continue
        sid = int(sec)
        eff = num(v.get("Effect"), None)
        size = nums(v.get("IconSize")) or [TILE, TILE]
        out[sid] = {"id": sid, "name": en(v.get("Name", "")), "note": clean(v.get("Note", "")),
                    "style": num(v.get("Style")), "family": num(v.get("Kind")), "priority": num(v.get("Priority")),
                    "effect": eff, "effect_label": STATUS_EFFECTS.get(eff, ""), "value": num(v.get("Values")),
                    "interval_s": num(v.get("Interval")), "duration_s": num(v.get("Time")),
                    "ride": num(v.get("RideId"), -1), "visual_skill": num(v.get("EffectDisplay"), -1),
                    "icon": v.get("IconPos", ""), "atlas": 0,
                    "sprite": sprites.add("icon.gwo", v.get("IconPos"), size[:2])}
        if v.get("AffectMap"):
            out[sid]["maps"] = nums(v["AffectMap"])
    return out


def build_zodiac_kinds(loc, sprites):
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "MagicMini.xml"))
    out = {}
    for el in root:
        a = el.attrib
        if "Kind" not in a:
            continue
        k = int(a["Kind"])
        out[k] = {"kind": k, "name": a.get("Name", ""), "class": num(a.get("Job"), None),
                  "first_skill": (k % 10000) * 10, "icon": a.get("TexturePos", ""), "atlas": atlas_index(a.get("Texture")),
                  "sprite": sprites.add(a.get("Texture"), a.get("TexturePos"))}
    return out


def build_skills(loc, sprites, statuses, kinds):
    ini = read_ini(os.path.join(loc, "Settings", "Sys", "Magic.ini"), cut_semicolon=False)
    info = read_dat(os.path.join(loc, "Text", "SkillInfo.dat"))
    out = {}
    for sec, v in ini.items():
        if not sec.isdigit():
            continue
        sid = int(sec)
        name = en(v.get("Name", ""))
        m = re.search(r"\s(\d+)$", name)
        kind = num(v.get("Kind"), None)
        st = num(v.get("Status"), -1)
        desc = clean(info.get(sec, ""))
        desc = re.sub(r"<m,(\w+)<", lambda mm: "{%s}" % mm.group(1), desc)
        size = nums(v.get("IconSize")) or [TILE, TILE]
        out[sid] = {"id": sid, "name": name, "base": re.sub(r"\s\d+$", "", name), "level": int(m.group(1)) if m else None,
                    "desc": desc, "class": (kinds.get(kind) or {}).get("class") if kind is not None else None,
                    "family": kind, "family_name": (kinds.get(kind) or {}).get("name", ""),
                    "target": num(v.get("Target")), "affects": num(v.get("AffectObj")), "range": num(v.get("Distance")),
                    "area": num(v.get("Range")), "mp": num(v.get("MP")), "cooldown_s": num(v.get("CoolingTime")),
                    "cast_s": num(v.get("IntonateTime")), "property": num(v.get("Property")),
                    "power1": num(v.get("Power1")), "power2": num(v.get("Power2")), "power3": num(v.get("Power3")),
                    "interval_s": num(v.get("TimeInterval")),
                    "status": st if st >= 0 else None, "status_name": (statuses.get(st) or {}).get("name", "") if st >= 0 else "",
                    "status_chance": num(v.get("StatusOdds")), "equip_request": num(v.get("EquipRequest")),
                    "priority": num(v.get("Priority")), "type_flag": num(v.get("TypeFlag")),
                    "icon": v.get("IconPos", ""), "atlas": atlas_index(v.get("FilePathName")),
                    "sprite": sprites.add(v.get("FilePathName"), v.get("IconPos"), size[:2])}
    return out


def build_talents(loc, sprites):
    ini = read_ini(os.path.join(loc, "Settings", "Sys", "Skill.ini"))
    effects = {int(k[6:]): v for k, v in ini.get("Effect", {}).items() if k.startswith("Effect") and k[6:].isdigit()}
    node = {int(k): v.rstrip("+").strip() for k, v in ini.get("NODE", {}).items() if k.isdigit()}
    eff_label = {name: node.get(i, name) for i, name in effects.items()}
    percent = set(nums(ini.get("parameter", {}).get("Percent")))
    cls = {}
    for sec, v in ini.items():
        m = re.match(r"class(\d+)$", sec)
        if m:
            for order, t in enumerate(nums(v.get("Skill"))):
                cls.setdefault(t, (int(m.group(1)), order))
    out = {}
    for sec, v in ini.items():
        if not sec.isdigit():
            continue
        tid = int(sec)
        eff = []
        for k, val in v.items():
            if k in eff_label:
                pr = nums(val)
                idx = [i for i, n in effects.items() if n == k]
                eff.append({"stat": k, "label": eff_label[k], "values": pr,
                            "percent": bool(idx and idx[0] in percent)})
        c = cls.get(tid)
        size = nums(v.get("IconSize")) or [TILE, TILE]
        out[tid] = {"id": tid, "name": en(v.get("Name", "")), "class": c[0] if c else None, "tree_order": c[1] if c else None,
                    "requires_talent": num(v.get("PrefixID")), "requires_rank": num(v.get("RrefixRank")),
                    "requires_total_rank": num(v.get("TotalRank")), "equip_request": num(v.get("EquipRequest")),
                    "effects": eff, "icon": v.get("IconPos", ""), "atlas": 0,
                    "sprite": sprites.add("icon.gwo", v.get("IconPos"), size[:2])}
    return out, eff_label


def lua_tables(text, name):
    """NAME[n] = { ... } blocks of a client Lua config -> {n: {key: value or [values]}} (data only)."""
    out = {}
    for m in re.finditer(r"%s\[(\d+)\]\s*=\s*\{(.*?)\n\}" % re.escape(name), text, re.S):
        body = m.group(2)
        rec = {}
        for km in re.finditer(r"(\w+)\s*=\s*(\{[^}]*\}|\"[^\"]*\"|\[\[.*?\]\]|[-\d.]+)", body, re.S):
            k, val = km.group(1), km.group(2)
            if val.startswith("{"):
                rec[k] = nums(val.strip("{}"))
            elif val.startswith('"'):
                rec[k] = val.strip('"')
            elif val.startswith("[["):
                rec[k] = val[2:-2].strip()
            else:
                rec[k] = num(val)
        out[int(m.group(1))] = rec
    return out


def build_zodiac(loc, kinds):
    cfg = decode(os.path.join(loc, "UI", "XML", "ConstellationConfig.lua"))
    signs = {}
    for n, rec in lua_tables(cfg, "Constellation_Cofig").items():
        t = rec.get("static_text", "")
        m = re.match(r"\s*(\w+):\s*([^,]+),\s*Patron Planet:\s*([^,]+),\s*Patron God:\s*([^.]+)", t)
        signs[n] = {"id": n, "name": m.group(1) if m else t.split(":")[0].strip(), "dates": m.group(2).strip() if m else "",
                    "planet": m.group(3).strip() if m else "", "god": m.group(4).strip() if m else "", "text": t}
    lev = lua_tables(cfg, "Constellationlev_Cofig").get(0, {})
    up = lua_tables(cfg, "Constellationlevup_Cofig").get(0, {})
    levels = {}
    for i in range(1, 31):
        levels[i] = {"level": i, "player_level": num(lev.get("Player_Level%d" % i), None),
                     "energy_to_next": up.get("Constellationlevup_Level%d" % i), "max_energy": up.get("MaxPower%d" % i)}
    st = lua_tables(cfg, "Stoneslev_Cofig").get(0, {})
    stup = lua_tables(cfg, "Stoneslevup_Cofig").get(0, {})
    stones = {}
    for i in range(1, 31):
        stones[i] = {"level": i, "talent_bonus": st.get("Speciality_Level%d" % i), "exp_bonus": st.get("Experience_Level%d" % i),
                     "talent_cap": num(st.get("sk_max%d" % i), None), "exp_cap": num(st.get("exp_max%d" % i), None),
                     "energy_cost": stup.get("Power_Level%d" % i), "gold_cost": stup.get("Gold_Level%d" % i)}
    train = decode(os.path.join(loc, "UI", "XML", "SkillTrainConfig.lua"))
    grids = {}
    for n, rec in lua_tables(train, "SkillTrain_ConfigGird").items():
        g = n - 1                      # character_zodiac_skill_grids.grid_index is 0-based
        lv = []
        for i in range(len(rec.get("SkillEff") or [])):
            def at(k):
                v = rec.get(k) or []
                return v[i] if i < len(v) else None
            lv.append({"level": i + 1, "energy": at("UpdateE"), "talent_points": at("UpdateS"), "effect": at("SkillEff"),
                       "extra_mp": at("MP"), "zodiac_level": at("Starlv")})
        grids[g] = {"grid": g, "row": g // 4, "type": rec.get("Type"), "type_label": GRID_TYPES.get(rec.get("Type"), ""),
                    "unlock_gold": rec.get("UnlockG"), "unlock_bgold": rec.get("UnlockGb"),
                    "accepts_kinds": "10000-19999" if g // 4 in (0, 2) else "20000-29999",
                    "max_level": len(lv), "levels": lv}
    return {"signs": signs, "levels": levels, "stones": stones, "grids": grids, "kinds": kinds}


# ------------------------------------------------------------------ world: monsters, npcs, maps, quests
def map_table(loc):
    ini = read_ini(os.path.join(loc, "Settings", "Sys", "MapIdToNameConfig.ini"))
    region = read_dat(os.path.join(loc, "Text", "QuestRegion.dat"))
    out = {}
    for k, v in ini.get("config", {}).items():
        m = re.match(r"MapID(\d+)$", k)
        if not m:
            continue
        parts = [x.strip() for x in v.split(",")]
        mid = int(m.group(1))
        scene = parts[0]
        cid = num(parts[1], None) if len(parts) > 1 else None
        out[mid] = {"id": mid, "scene": scene, "client_scene_id": cid, "name": en(region.get(str(cid), "")) if cid is not None else ""}
    return out


def build_maps(loc, client):
    maps = map_table(loc)
    by_scene = {m["scene"].lower(): mid for mid, m in maps.items()}
    minimap = {os.path.splitext(f)[0].lower() for f in os.listdir(os.path.join(loc, "UI", "Texture", "MinMap"))}
    for mid, m in maps.items():
        m["minimap"] = m["scene"].lower() in minimap
        m["name_source"] = "QuestRegion.dat" if m["name"] else "scene key"
        if not m["name"]:
            m["name"] = m["scene"].replace("_All", "").replace("_", " ")
        m["regions"] = []
    mon = os.path.join(loc, "Monster")
    for scene in sorted(os.listdir(mon)):
        p = os.path.join(mon, scene, "Address.ini")
        mid = by_scene.get(scene.lower())
        if mid is None or not os.path.exists(p):
            continue
        txt = decode(p)
        for sm in re.finditer(r"^\[(\d+)\]\s*;?([^\r\n]*)(.*?)(?=^\[\d+\]|\Z)", txt, re.S | re.M):
            grp = int(sm.group(1))
            body = sm.group(3)
            names = dict(re.findall(r"AddressName(\d+)\s*=\s*([^;\r\n]*)", body))
            coords = dict(re.findall(r"Coordinate(\d+)\s*=\s*([^;\r\n]*)", body))
            for idx in sorted(names, key=int):
                xz = nums(coords.get(idx, ""))
                maps[mid]["regions"].append({"group": grp, "group_name": sm.group(2).strip(), "point": int(idx),
                                             "name": names[idx].strip(), "x": xz[0] if xz else None,
                                             "z": xz[1] if len(xz) > 1 else None})
    span = parse_xml(os.path.join(loc, "Settings", "Sys", "SpanMapConfig.xml"))
    links = {}
    for el in span.iter():
        if "ID" in el.attrib and el.tag.startswith("Map"):
            src = num(el.attrib["ID"])
            for k, v in el.attrib.items():
                if k.startswith("Map") and k != "ID":
                    t = nums(v)
                    if t:
                        links.setdefault(src, []).append({"to": t[0], "x": t[1] if len(t) > 1 else None,
                                                          "z": t[2] if len(t) > 2 else None})
    for mid, l in links.items():
        if mid in maps:
            maps[mid]["links"] = l
    return maps


def build_monsters(loc, maps):
    by_scene = {m["scene"].lower(): mid for mid, m in maps.items()}
    out = {}
    mon = os.path.join(loc, "Monster")
    files = [("", os.path.join(mon, "Monster.ini"))]
    for scene in sorted(os.listdir(mon)):
        for fn in ("Monster.ini", "Monster_New.ini"):
            p = os.path.join(mon, scene, fn)
            if os.path.isfile(p):
                files.append((scene, p))
    for scene, p in files:
        for key, v in read_ini(p).items():
            if key in ("default",) or "Name" not in v:
                continue
            rec = out.get(key)
            mid = by_scene.get(scene.lower())
            if rec is None:
                low = key.lower()
                rank = "boss" if "_boss" in low else "elite" if "_elite" in low else "normal"
                name = v.get("Name", "").strip()
                rec = out[key] = {"key": key, "name": name, "rank": rank, "model": v.get("FileName", ""),
                                  "texture": v.get("TextureName", ""), "scale": num(v.get("Scale"), 1),
                                  "attack_type": num(v.get("AttackType"), None), "maps": [], "scenes": []}
            if scene and scene not in rec["scenes"]:
                rec["scenes"].append(scene)
            if mid is not None and v.get("Name", "").strip():
                rec.setdefault("names_by_map", {}).setdefault(mid, en(v["Name"]))
            if not scene and v.get("Name", "").strip():
                rec["shared_name"] = en(v["Name"])
            if mid is not None and mid not in rec["maps"]:
                rec["maps"].append(mid)
    for rec in out.values():
        # the shared Monster/Monster.ini name, else the English name of the lowest map id (the GM test map's
        # Monster.ini names its copies in Chinese)
        cands = [rec.pop("shared_name", "")] + [n for _, n in sorted((rec.get("names_by_map") or {}).items())] + [rec["name"]]
        english = [c for c in cands if c and not has_cjk(c)]
        rec["name"] = english[0] if english else en(rec["name"])
        if has_cjk(rec["name"]):
            rec["name_cn"] = True
    return out


def build_npcs(loc):
    names = read_dat(os.path.join(loc, "Text", "NpcName.dat"))
    descs = read_dat(os.path.join(loc, "Text", "NPCDescription.dat"))
    ini = read_ini(os.path.join(loc, "Settings", "Sys", "NPC.INI"))
    out = {}
    for tkey, v in ini.items():
        m = re.match(r"^(.+?_\d{3})(?:_|$)", tkey)
        nkey = m.group(1) if m else tkey
        rec = out.setdefault(nkey, {"key": nkey, "name": en(names.get(nkey, "") or v.get("name", "")),
                                    "desc": clean(descs.get(nkey, "")), "scene": re.sub(r"_\d{3}$", "", nkey),
                                    "templates": {}})
        rec["templates"][tkey] = {"name": v.get("name", "").strip(), "sex": num(v.get("sex"), None),
                                  "model": v.get("MeshName", "") if v.get("MeshName", "null") != "null" else ""}
    for nkey, nm in names.items():
        if nkey not in out and re.match(r"^.+_\d{3}$", nkey):
            out[nkey] = {"key": nkey, "name": en(nm), "desc": clean(descs.get(nkey, "")),
                         "scene": re.sub(r"_\d{3}$", "", nkey), "templates": {}}
    for rec in out.values():
        if has_cjk(rec["name"]):
            rec["name_cn"] = True
    texts = {k: clean(v) for k, v in descs.items() if k not in out}
    return out, texts


def build_quests(loc, npcs, maps):
    xml_q = {}
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "Quest.xml"))
    for el in root:
        a = el.attrib
        if "ID" in a:
            xml_q[int(a["ID"])] = a
    out = {}
    qdir = os.path.join(loc, "Text", "Quest")
    ids = set(xml_q)
    if os.path.isdir(qdir):
        ids |= {int(fn[:-4]) for fn in os.listdir(qdir) if fn.endswith(".dat") and fn[:-4].isdigit()}
    for qid in sorted(ids):
        blocks = {}
        p = os.path.join(qdir, "%d.dat" % qid)
        if os.path.exists(p):
            t = read_text(p).replace("\r", "")
            for bm in re.finditer(r"^(\w+)\s*\n\{(.*?)\n?\}", t, re.S | re.M):
                blocks[bm.group(1)] = bm.group(2).strip()
        title = clean((blocks.get("Title") or "").splitlines()[0] if blocks.get("Title") else "")
        rew = {}
        for line in (blocks.get("Append") or "").splitlines():
            if ":" in line:
                k, v = line.split(":", 1)
                rew[k.strip()] = v.strip()
        a = xml_q.get(qid, {})
        lvm = re.match(r"\[Lv(\d+)\]", title)
        giver = a.get("GiverName", "")
        resp = a.get("ResponderName", "")
        gmap = num(a.get("GiverMapID"), None) if a.get("GiverMapID", "") != "" else None
        out[qid] = {"id": qid, "title": title, "in_quest_xml": qid in xml_q, "has_text": bool(blocks),
                    "min_level": num(a.get("MinLevel"), None) if a else (int(lvm.group(1)) if lvm else None),
                    "max_level": num(a.get("MaxLevel"), None) if a else None,
                    "class": num(a.get("Class"), None) if a else None, "faith": num(a.get("Belief"), None) if a else None,
                    "camp": num(a.get("Faction"), None) if a else None, "color": num(a.get("Color"), None) if a else None,
                    "pet_quest": num(a.get("pet"), 0) if a else 0,
                    "giver": giver, "giver_name": (npcs.get(giver) or {}).get("name", ""),
                    "responder": resp, "responder_name": (npcs.get(resp) or {}).get("name", ""),
                    "giver_map": gmap, "giver_map_name": (maps.get(gmap) or {}).get("name", "") if gmap is not None else "",
                    "details": clean(blocks.get("Details", "")), "objectives": clean(blocks.get("Objectives", "")),
                    "rewards": rew, "completion": clean(blocks.get("CompletionText", ""))}
    return out


def build_titles(loc):
    names = read_dat(os.path.join(loc, "Text", "DesigName.dat"))
    info = read_dat(os.path.join(loc, "Text", "DesigInfo.dat"))
    out = {}
    for k, v in names.items():
        if k.isdigit():
            out[int(k)] = {"id": int(k), "name": clean(v), "desc": clean(info.get(k, ""))}
            if has_cjk(out[int(k)]["name"]):
                out[int(k)]["name_cn"] = True
    return out


# ------------------------------------------------------------------ pets, mounts
def build_pets(loc, sprites):
    txt = read_dat(os.path.join(loc, "Text", "Message_Pet.dat"))
    raw = decode(os.path.join(loc, "Settings", "Sys", "Pet.xml"))
    pets = {}
    for name, icon in re.findall(r'<PetInfo Name="Pet(\d+_[01])" IconPos="([^"]+)"', raw):
        sp, sex = name.split("_")
        rec = pets.setdefault(int(sp), {"species": int(sp), "name": "", "icon_male": "", "icon_female": "", "atlas": 1})
        rec["icon_male" if sex == "1" else "icon_female"] = icon
        rec["name"] = rec["name"] or txt.get("Pet" + name, "")
    for block in re.finditer(r"<Pet(\d+)_([01])>(.*?)</Pet\1_\2>", raw, re.S):
        rec = pets.get(int(block.group(1)))
        if rec is None:
            continue
        models = rec.setdefault("models", {})
        for mm in re.finditer(r'<PetModel Samsara="(\d+)" FileName="([^"]*)"', block.group(3)):
            models.setdefault("male" if block.group(2) == "1" else "female", []).append(
                {"rebirths": int(mm.group(1)), "model": mm.group(2)})
    for rec in pets.values():
        rec["icon_male"] = rec["icon_male"] or rec["icon_female"]
        rec["icon_female"] = rec["icon_female"] or rec["icon_male"]
        rec["sprite_male"] = sprites.add("icon2.gwo", rec["icon_male"])
        rec["sprite_female"] = sprites.add("icon2.gwo", rec["icon_female"])
        rec["sprite"] = rec["sprite_male"]
    return pets


def build_petskills(loc, sprites):
    txt = read_dat(os.path.join(loc, "Text", "Message_Pet.dat"))
    raw = decode(os.path.join(loc, "Settings", "Sys", "Pet_Skill.xml"))
    out = {}
    for m in re.finditer(r"<(\w+) ([^>]*?ID=\"(\d+)\"[^>]*)/>", raw):
        attrs = dict(re.findall(r'(\w+)="([^"]*)"', m.group(2)))
        if "Name" not in attrs or not attrs["Name"].startswith("Pet"):
            continue
        sid = int(attrs["ID"])
        info = re.sub(r"\|c[0-9A-Fa-f]{8}", "", txt.get(attrs.get("Info", ""), ""))
        out[sid] = {"id": sid, "name": txt.get(attrs["Name"], attrs["Name"]), "info": info.replace("%Enter%", " "),
                    "icon": attrs.get("IconPos", ""), "atlas": 1, "type": int(attrs.get("Type", "0") or 0),
                    "priority": int(attrs.get("Priority", "0") or 0), "add": attrs.get("Add", ""), "effect": attrs.get("Effect", ""),
                    "trait": attrs.get("Trait", ""), "restrict": attrs.get("Restrict", ""), "values": attrs.get("Values", ""),
                    "sprite": sprites.add("icon2.gwo", attrs.get("IconPos", ""))}
    return out


def build_mounts(loc, items, statuses):
    ride = read_ini(os.path.join(loc, "Settings", "Sys", "Ride.ini"))
    models = {int(k): {"section": int(k), "status": num(v.get("StatuID"), None), "model": v.get("XFile", ""),
                       "texture": v.get("TextureFile", "")} for k, v in ride.items() if k.isdigit()}
    rides = {}
    for sid, s in statuses.items():
        if s.get("ride", -1) >= 0:
            rides[sid] = {"status": sid, "name": s["name"], "ride_section": s["ride"],
                          "model": (models.get(s["ride"]) or {}).get("model", ""), "sprite": s.get("sprite")}
    mitems = {}
    for iid, it in items.items():
        if it["kind"].startswith("mount") or it["kind"] == "pet":
            st = it.get("stats", {})
            mitems[iid] = {"id": iid, "name": it["name"], "kind": it["kind"], "slot": it["slot"],
                           "slot_label": it["slot_label"], "minlv": it["minlv"],
                           "speed_by_quality": st.get("Speed"), "max_hp_by_quality": st.get("MaxHP"),
                           "stats": st, "append_attributes": it.get("append_attributes", []), "icon": it["icon"],
                           "atlas": it["atlas"], "sprite": it["sprite"], "desc": it["desc"]}
    return {"items": mitems, "rides": rides, "ride_models": models}


# ------------------------------------------------------------------ guild, holy stones, suits
def build_guild(loc, sprites):
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "Consortia.xml"))
    out = {"buildings": {}, "levels": {}, "defaults": {}, "consume": [], "duties": GUILD_DUTIES, "altars": {}}
    for el in root.iter():
        a = el.attrib
        if el.tag.startswith("BuildID"):
            bid = int(a["ID"])
            out["buildings"][bid] = {"id": bid, "name": a.get("Name", ""), "desc": clean(a.get("Description", "")),
                                     "icon": a.get("ImagePos", ""), "atlas": 0,
                                     "sprite": sprites.add("icon.gwo", a.get("ImagePos")), "levels": {}}
    for el in root.iter():
        a = el.attrib
        if el.tag.startswith("Default_"):
            out["defaults"][el.tag[8:]] = num(a.get("Value"))
        elif el.tag.startswith("consume"):
            out["consume"].append({"condition": num(a.get("Condition")), "contribution": num(a.get("ContributePoint")),
                                   "cost": num(a.get("ConsumeValue"))})
    lu = root.find("ConsortiaLevelUp")
    for el in (lu if lu is not None else []):
        out["levels"][num(el.attrib.get("Clevel"))] = {"funds": num(el.attrib.get("found")), "gems": num(el.attrib.get("bijou"))}
    imp = root.find("BuildingImpact")
    for b in (imp if imp is not None else []):
        bid = num(b.attrib.get("ID"))
        for lv in b:
            rec = out["buildings"].get(bid)
            if rec is not None:
                rec["levels"][num(lv.attrib.get("Clevel"))] = {k: num(v, v) for k, v in lv.attrib.items() if k not in ("ID", "Clevel")}
    for bid, rec in out["buildings"].items():
        rec["max_level"] = max(rec["levels"]) if rec["levels"] else None
    alt = parse_xml(os.path.join(loc, "Settings", "Sys", "AltarInfo.xml"))
    for el in alt.iter():
        a = el.attrib
        if el.tag.startswith("AltarID") and "ID" in a:
            aid = int(a["ID"])
            out["altars"][aid] = {"id": aid, "name": a.get("Name", ""), "desc": clean(a.get("Description", "")),
                                  "icon": a.get("ImagePos", ""), "atlas": 0, "sprite": sprites.add("icon.gwo", a.get("ImagePos"))}
    return out


def build_holystones(loc, items, sprites):
    ed = read_dat(os.path.join(loc, "Text", "EquipDescription.dat"))
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "EquipStoneInfo.xml"))
    by_name = collections.defaultdict(list)
    for iid, it in items.items():
        by_name[it["name"].strip().lower()].append(iid)
    out = {}
    for el in root:
        a = el.attrib
        if "ID" not in a:
            continue
        sid = int(a["ID"])
        name = ed.get("EquipStoneName%d" % sid, "")
        mats = sorted(by_name.get(name.strip().lower(), [])) if sid else []
        out[sid] = {"id": sid, "key": el.tag, "name": name, "desc": clean(ed.get("EquipStoneDesc%d" % sid, "")),
                    "effect_text": clean(ed.get(el.tag, "")), "percent": num(a.get("Percent")) == 1,
                    "socket_icon": a.get("IconPos", ""), "socket_sprite": sprites.add(a.get("Texture"), a.get("IconPos"), (20, 20)),
                    "stone_items": mats, "sprite": items[mats[0]]["sprite"] if mats else None}
    return out


def build_suits(loc, items, sprites):
    names = read_dat(os.path.join(loc, "Text", "EquipName.dat"))
    ed = read_dat(os.path.join(loc, "Text", "EquipDescription.dat"))
    tiers = {}
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "EquipSuitInfoIni.xml"))
    for el in root:
        a = el.attrib
        if "Type" not in a:
            continue
        t = int(a["Type"])
        tiers.setdefault(t, {"type": t, "stages": []})["stages"].append(
            {"key": el.tag, "name": names.get(el.tag, ""), "desc": clean(ed.get(el.tag, "")),
             "from_level": num(a.get("Conditions")), "icon": a.get("IcoPos", ""),
             "sprite": sprites.add(a.get("MtPath"), a.get("IcoPos"), (30, 30))})
    eff = {}
    for el in parse_xml(os.path.join(loc, "Settings", "Sys", "EquipEffect.xml")):
        a = el.attrib
        if "LvId" in a:
            eff[int(a["LvId"])] = {"bonus": num(a.get("Effect")), "exp": num(a.get("Exp"))}
    codes = {}
    for code in sorted(set(eff) | {int(k[4:]) for k in ed if re.match(r"^Suit\d+$", k)}):
        t, lv = divmod(code, 100)
        codes[code] = {"code": code, "tier": t, "level": lv, "name": clean(ed.get("Suit%d" % code, "")),
                       "bonus": (eff.get(code) or {}).get("bonus"), "exp_to_next": (eff.get(code) or {}).get("exp")}
    points = []
    for el in parse_xml(os.path.join(loc, "Settings", "Sys", "EquipEffectSuit.xml")):
        a = el.attrib
        if "Type" in a:
            points.append({"key": el.tag, "label": clean(ed.get(el.tag, "")) or el.tag, "type": num(a["Type"]),
                           "points": num(a.get("Number")), "value": num(a.get("EffValue"))})
    return {"codes": codes, "tiers": tiers, "point_bonuses": points}


# ------------------------------------------------------------------ shops (client list + server constants)
def decode_server_shops(items):
    """The reborn server ships its NPC shop and cash-mall catalogues as gzip+base64 packet captures inside
    src/Godswar.Server/Packets/*.cs (docs/point-exchanger-shop.md: frame header 16 bytes for 10071, 8 for the
    mall 10178; 88-byte records, item u32 @0, price u32 @68).  Decoded read-only for display."""
    pdir = os.path.join(REBORN_SRC, "src", "Godswar.Server", "Packets")
    out = {}
    if not os.path.isdir(pdir):
        return out
    # CapitalNpcServiceProtocol.TryGetShopCurrency: the currency each shop really charges (the server overrides
    # the captured frame byte for most shops); PropsVendor keeps the per-frame byte.
    cur_names = {1: "silver", 2: "gold", 3: "silver", 4: "binding_gold", 5: "honor", 6: "exchange_point",
                 7: "exchange_medal"}
    fixed = {"BoundGoldVendor": "gold", "BindingGoldShop": "binding_gold", "SkillVendor": "binding_gold",
             "PointExchangerShop": "honor, exchange_point or exchange_medal (per item)", "PetMerchant": "silver",
             "LelantineFarmQuartermaster": "silver", "JewelryMerchant": "gold", "SpartaMall": "gold", "AthensMall": "gold"}
    for k in ("WarriorEquipmentVendor", "ScholarEquipmentVendor", "JewelryEquipmentVendor", "ArmorEquipmentVendor",
              "WarriorSupplier", "SkillMerchant", "ArmorMerchant", "ScholarSupplier", "IngredientsVendor"):
        fixed[k] = "binding_gold"
    for k in ("AlchemyRecipeVendor", "ForgingRecipeVendor", "MythcraftingRecipeVendor", "ScholarshipRecipeVendor",
              "SpartaWarriorEquipmentMerchant", "SpartaScholarEquipmentMerchant", "SpartaSkillMerchant",
              "SpartaArmorMerchant", "SpartaJewelryMerchant", "SpartaPropsMerchant", "SpartaPetMerchant"):
        fixed[k] = "silver"
    # which NPCs open which shop (CapitalNpcServiceProtocol.cs: ("Sparta_052", 5049u) or ... => Kind.X)
    npc_of = collections.defaultdict(list)
    proto = os.path.join(REBORN_SRC, "src", "Godswar.Server", "Domain", "World", "Content", "CapitalNpcServiceProtocol.cs")
    if os.path.exists(proto):
        ptxt = open(proto, encoding="utf-8", errors="replace").read()
        for m in re.finditer(r"((?:\(\s*\"\w+?_\d{3}\"\s*,\s*[\d_]+u\s*\)\s*(?:or\s*)?)+)=>\s*CapitalNpcServiceKind\.(\w+)", ptxt):
            for key in re.findall(r"\"(\w+?_\d{3})\"", m.group(1)):
                if key not in npc_of[m.group(2)]:
                    npc_of[m.group(2)].append(key)
    for fn in sorted(os.listdir(pdir)):
        if not fn.endswith(".cs"):
            continue
        src = open(os.path.join(pdir, fn), encoding="utf-8", errors="replace").read()
        for m in re.finditer(r"const string (\w+?)CatalogGzip\s*=\s*((?:\s*\"[A-Za-z0-9+/=]*\"\s*\+?)+);", src):
            name = m.group(1)
            b64 = "".join(re.findall(r"\"([A-Za-z0-9+/=]*)\"", m.group(2)))
            try:
                data = gzip.decompress(base64.b64decode(b64))
            except (OSError, ValueError):
                continue
            entries, off = [], 0
            while off + 4 <= len(data):
                ln, op = struct.unpack_from("<HH", data, off)
                if ln < 8 or off + ln > len(data):
                    break
                mall = op == 10178
                hdr = 8 if mall else 16
                count = data[off + 6] if mall else data[off + 10]
                cat = data[off + 4] if mall else data[off + 8]
                cur = None if mall else data[off + 9]
                for i in range(count):
                    r = off + hdr + i * 88
                    if r + 72 > off + ln:
                        break
                    iid, = struct.unpack_from("<I", data, r)
                    price, = struct.unpack_from("<I", data, r + 68)
                    entries.append({"item": iid, "name": (items.get(iid) or {}).get("name", ""),
                                    "price": price, "category": cat,
                                    "currency": fixed.get(name) or cur_names.get(cur, str(cur)),
                                    "in_client": iid in items})
                off += ln
            # Listings the server appends to a captured shelf (behind a hot rate switch; not in the gzip capture).
            for iid, price, cat, switch in APPENDED_LISTINGS.get(name, ()):
                last = max((i for i, e in enumerate(entries) if e["category"] == cat), default=len(entries) - 1)
                entries.insert(last + 1, {"item": iid, "name": (items.get(iid) or {}).get("name", ""), "price": price,
                                          "category": cat, "currency": fixed.get(name) or "silver",
                                          "in_client": iid in items, "switch": switch + " = 1"})
            if entries:
                kind_name = {"PointExchangerShop": "PointExchanger"}.get(name, name)
                keys = npc_of.get(kind_name) or npc_of.get("Athens" + kind_name) or []
                out[name] = {"source": "src/Godswar.Server/Packets/" + fn, "kind": "mall" if "Mall" in name else "npc shop",
                             "currency": fixed.get(name, "per frame"), "npcs": keys, "entries": entries}
    return out


# PROF skillbook source: PacketBuilder.ProfessionSkillbookListing.cs appends the Profession Skillbook to the end of
# tab 1 of the Scholarship Recipe Vendor (GENERATED price; retail sold it nowhere).
APPENDED_LISTINGS = {
    "ScholarshipRecipeVendor": [(4268, 10000, 0, "profession.skillbook_shelf")],
}


def build_shops(shop_ids, items):
    srv = decode_server_shops(items)
    return {"client_shop_list": {"note": "Settings/Sys/ItemShop.xml: reference list only, Origin.exe never loads this file, so it does not limit shop windows",
                                 "items": sorted(shop_ids)},
            "server_catalogues": srv,
            "editable": False,
            "why_read_only": "The reborn server compiles these catalogues into its code (packet captures in "
                             "Packets/*.cs); they are not in the database, so the editor can only show them."}


def build_currencies(loc):
    ed = read_dat(os.path.join(loc, "Text", "EquipDescription.dat"))
    msg = read_dat(os.path.join(loc, "Text", "Message.dat"))
    return {
        "silver": {"label": msg.get("Money") or ed.get("Money", "Silver"), "column": "character_base.\"Money\"",
                   "shop_code": 3, "max": 2147483647, "client_key": "Message.dat:Money"},
        "gold": {"label": msg.get("Bijou") or ed.get("Stone", "Gold"), "column": "character_base.\"Stone\"",
                 "shop_code": 2, "max": 2147483647, "client_key": "Message.dat:Bijou"},
        "binding_gold": {"label": msg.get("BindingGold") or "B-Gold", "column": "character_base.\"BindingGold\"",
                         "shop_code": 4, "max": 2147483647, "client_key": "Message.dat:BindingGold"},
        "honor": {"label": msg.get("SimplePoint") or "Honor", "column": "character_base.medusa_honor_points",
                  "client_key": "Message.dat:SimplePoint"},
        "exchange_point": {"label": msg.get("HardPoint") or "Point", "column": "character_base.exchange_point",
                           "client_key": "Message.dat:HardPoint"},
        "exchange_medal": {"label": ed.get("NeedMedalPoint", "Thearchy Medals").replace(" Required", ""),
                           "column": "character_base.exchange_medal", "client_key": "EquipDescription.dat:NeedMedalPoint"},
    }


# ------------------------------------------------------------------ attributes (unchanged)
ATTR_LABELS = {
    0: ("Attackpro", "Physical Attack"), 1: ("Defendpro", "Physical Defense"), 2: ("Mattackpro", "Spell Attack"),
    3: ("Mdefendpro", "Spell Defense"), 4: ("Hitpro", "Hit Rate"), 5: ("Dodgepro", "Dodge Rate"),
    6: ("CritAppendpro", "Wild Attack Rate"), 7: ("CritDefendpro", "Dodge Wild Attack"),
    8: ("PhyDamageAppendpro", "Physical Damage %"), 9: ("MagDamageAppendpro", "Spell Damage %"),
    10: ("DamageSorbpro", "Damage Absorbed"), 11: (None, "Status Effect"), 12: (None, "Status Immunity"),
    13: ("Hppro", "Max HP"), 14: ("Mppro", "Max MP"), 15: (None, "HP Recovery"), 16: (None, "MP Recovery"),
    17: (None, "Healing Received %"), 18: ("Curepro", "Healing Effect"), 19: (None, "Ignore Physical Defense %"),
    20: (None, "Ignore Spell Defense %"), 23: (None, "Extra Physical Damage"), 24: (None, "Extra Spell Damage"),
    25: (None, "Wild Attack Damage %"), 26: (None, "Wild Attack Damage"), 27: (None, "Life Steal %"),
    28: (None, "Reflect Damage %"),
}


def build_attributes(loc):
    txt = read_dat(os.path.join(loc, "Text", "Message.dat"))
    root = parse_xml(os.path.join(loc, "Settings", "Sys", "ItemAppendAttribute.xml"))
    keys = {}
    for ch in root.iter():
        if ch is not root and "Type" in ch.attrib:
            keys.setdefault(int(ch.attrib["Type"]), [])
            if ch.tag not in keys[int(ch.attrib["Type"])]:
                keys[int(ch.attrib["Type"])].append(ch.tag)
    out = {}
    for t in sorted(set(keys) | set(ATTR_LABELS)):
        mkey, fallback = ATTR_LABELS.get(t, (None, None))
        label = (txt.get(mkey) or "").strip() if mkey else ""
        if label:
            out[t] = {"label": label, "source": "Message.dat:" + mkey, "keys": keys.get(t, [])}
        else:
            el = (keys.get(t) or ["type %d" % t])[0]
            out[t] = {"label": fallback or el, "source": "derived:" + el, "keys": keys.get(t, [])}
    return out


# ------------------------------------------------------------------ images
def build_images(loc, static):
    try:
        from PIL import Image
    except ImportError:
        print("Pillow is not installed; keeping the existing icon images")
        return
    tex = os.path.join(loc, "UI", "Texture")
    big = Image.new("RGBA", (2048, 2048), (0, 0, 0, 0))
    for i, name in enumerate(ATLASES):
        p = os.path.join(tex, name)
        if not os.path.exists(p):
            print("missing atlas %s (left transparent)" % p)
            continue
        im = Image.open(io.BytesIO(open(p, "rb").read())).convert("RGBA")
        if im.size != (1024, 1024):
            print("unexpected size %s for %s" % (im.size, name))
        big.paste(im, ((i % 2) * 1024, (i // 2) * 1024))
    out = os.path.join(static, "icons-origin.png")
    big.save(out, optimize=True)
    print("wrote %s (%d bytes)" % (out, os.path.getsize(out)))
    ui = Image.open(io.BytesIO(open(os.path.join(tex, "main.gwo"), "rb").read())).convert("RGBA")
    out = os.path.join(static, "origin-ui.png")
    ui.save(out, optimize=True)
    print("wrote %s (%d bytes)" % (out, os.path.getsize(out)))


# ------------------------------------------------------------------ cross-check + coverage (db-refs.json)
def resolvers(cat):
    def named(table, field="name"):
        return lambda v: ((table.get(int(v)) if str(v).lstrip("-").isdigit() else None) or {}).get(field) or None
    maps, labels = cat["maps"], cat["labels"]
    regions = {}
    for mid, m in maps.items():
        for r in m["regions"]:
            regions["%d:%d:%d" % (mid, r["group"], r["point"])] = r["name"]
    npc_templates = {t: n["name"] for n in cat["npcs"].values() for t in n["templates"]}
    return {
        "item": named(cat["items"]), "mount": named(cat["items"]), "appendattr": named(cat["appendattrs"], "label"),
        "stattype": named(cat["attributes"], "label"), "skill": named(cat["skills"]), "talent": named(cat["talents"]),
        "zodiac_sign": named(cat["zodiac"]["signs"]), "zodiac_grid": named(cat["zodiac"]["grids"], "type_label"),
        "zodiac_kind": named(cat["zodiac"]["kinds"]),
        "monster": lambda v: (cat["monsters"].get(v) or {}).get("name") or None,
        "npc": lambda v: (cat["npcs"].get(v) or {}).get("name") or cat["npctexts"].get(v) or None,
        "npc_template": lambda v: npc_templates.get(v) or None,
        "map": named(maps), "region": lambda v: regions.get(v),
        "quest": named(cat["quests"], "title"), "title": named(cat["titles"]),
        "species": named(cat["pets"]), "petskill": named(cat["petskills"]), "status": named(cat["statuses"]),
        "guild_building": named(cat["guild"]["buildings"]), "holystone": named(cat["holystones"]),
        "suit": named(cat["suits"]["codes"]), "suit_tier": lambda v: ([s["name"] for s in (cat["suits"]["tiers"].get(int(v)) or {}).get("stages", [])] or [None])[0],
        "class": lambda v: (labels["classes"].get(int(v)) or {}).get("name") or None,
        "faith": lambda v: (labels["faiths"].get(int(v)) or {}).get("name") or None,
        "camp": lambda v: labels["camps"].get(int(v)) or None,
        "currency": lambda v: (cat["currencies"].get(v) or {}).get("label") or None,
        "quality": lambda v: (labels["qualities"].get(int(v)) or {}).get("name") or ("No quality (Q0)" if int(v) == 0 else None),
    }


def crosscheck_items(items, db_items, labels):
    db = {r["id"]: r for r in db_items}
    db_only = []
    for iid in sorted(set(db) - set(items)):
        r = db[iid]
        db_only.append({"id": iid, "kind": r["kind"], "name": r["display_name"] or r["name_key"], "name_key": r["name_key"],
                        "min_level": r["min_level"], "texture": tex_name(r["texture"]), "icon": r["icon"],
                        "note": "not in your client"})
    client_only = [{"id": iid, "kind": items[iid]["kind"], "name": items[iid]["name"], "tag": items[iid]["tag"]}
                   for iid in sorted(set(items) - set(db))]
    kind_mismatch, name_diff, vec = [], 0, collections.Counter()
    for iid in sorted(set(db) & set(items)):
        r, c = db[iid], items[iid]
        ck = "consume item" if c["kind"] == "skillitem" else c["kind"]
        if r["kind"] != ck:
            kind_mismatch.append({"id": iid, "db_kind": r["kind"], "client_kind": c["kind"], "name": c["name"]})
        if (r["display_name"] or "").strip() != (c["name"] or "").strip():
            name_diff += 1
        sk = r.get("stat_keys") or {}
        for key in ("Attack", "Defence", "MaxHP", "Speed", "MagicAk", "Hit"):
            if key in sk and key in c.get("stats", {}):
                vec[(len(c["stats"][key]), sk[key])] += 1
    kinds = collections.OrderedDict()
    for r in db_items:
        kinds.setdefault(r["kind"], {"db": 0, "client": 0})["db"] += 1
    for c in items.values():
        k = "consume item" if c["kind"] == "skillitem" else c["kind"]
        kinds.setdefault(k, {"db": 0, "client": 0})["client"] += 1
    weapons = collections.Counter(c["category"] for c in items.values() if c["kind"] == "weapon")
    db_weapon_slots = collections.Counter(r["equipment_slot"] for r in db_items if r["kind"] == "weapon")
    return {"db_items": len(db), "client_items": len(items), "db_only": db_only, "client_only": client_only,
            "kind_mismatch": kind_mismatch, "name_differs": name_diff,
            "quality_vector_lengths": [{"client": a, "db": b, "stats": n} for (a, b), n in sorted(vec.items())],
            "kinds": kinds, "weapon_groups": dict(weapons), "db_weapon_slots": {str(k): v for k, v in db_weapon_slots.items()}}


def measure_coverage(refs, res):
    out = collections.OrderedDict()
    for name, r in refs.items():
        f = res.get(r["type"])
        vals = r["values"]
        rows = sum(n for _, n in vals)
        ok_rows = ok_vals = 0
        missing = []
        for v, n in vals:
            try:
                hit = f(v) if f else None
            except (ValueError, TypeError):
                hit = None
            if hit and not has_cjk(str(hit)):
                ok_vals += 1
                ok_rows += n
            else:
                missing.append(v)
        out[name] = {"type": r["type"], "distinct": len(vals), "rows": rows, "named_distinct": ok_vals,
                     "named_rows": ok_rows, "pct_rows": round(100.0 * ok_rows / rows, 1) if rows else None,
                     "missing": missing[:60], "missing_count": len(missing)}
    return out


def write_catalog_md(path, cat, counts, cross, cov, dbmeta, sprite_info):
    L = []
    w = L.append
    w("# Origin client catalogue (gwadmin-web `catalog-origin/`)")
    w("")
    w("Generated by `build_origin_catalog.py` from the admin's Godswar Origin client (`%s`). Do not edit by hand:" % cat["client"])
    w("re-run the builder. Every name below is copied from the client's own English files; a few lookups the client")
    w("ships only in Chinese are translated and marked `translated` in `labels.json`.")
    w("")
    w("Rebuild (Mac, needs Pillow and the client):")
    w("")
    w("```sh")
    w("cd GodsWar-VPS/tools/gwadmin-web")
    w("python3 -I catalog_dbrefs.py --psql \"ssh vps sudo -n -u postgres psql\" --db godswar   # read-only snapshot of DB ids")
    w("python3 -I build_origin_catalog.py                                                     # catalogues, sprites, this file")
    w("```")
    w("")
    w("## 1. Files")
    w("")
    w("| File | Entries | Key | From (client files) |")
    w("|---|---:|---|---|")
    for fn, n, key, src in counts:
        w("| `%s` | %s | %s | %s |" % (fn, n, key, src))
    w("")
    w("Icons: `static/catalog-icons.png` holds every distinct icon of every catalogue as a 36 x 36 tile, %d per row"
      " (%d tiles). An entry's `sprite` = n is the tile at x = (n %% %d) x 36, y = floor(n / %d) x 36. The old"
      " `icon` + `atlas` fields still point into `static/icons-origin.png` (4 atlases, 2 x 2) for the existing"
      " character sheet." % (SPRITE_COLS, sprite_info.get("tiles", 0), SPRITE_COLS, SPRITE_COLS))
    w("")
    w("Item fields in plain English (for tooltips): `kind` = equipment slot family; `category` = what the item is"
      " (client weapon/armour group, or derived for consumables, see `category_source`); `slot` = equipment slot"
      " number (-1 = not wearable); `minlv`/`maxlv` = level range; `classes` = who may use it (empty = everyone);"
      " `camp` 0 Sparta / 1 Athens; `sex` 0 female / 1 male; `stack` = most per bag cell; `price` = shop sell value in"
      " Silver; `stats` = one value per quality Q1..Q10 (index 0 = Q1); `append_attributes` = the append attributes"
      " (`appendattrs.json` ids) this item may roll, i.e. what can go in its attribute rows; `forge` = the client's"
      " forge chain (next item, failure item, chance, costs); `use_skill` = skill cast when used; `teaches_skill` ="
      " skill a book teaches; `pet_skill` = pet skill a pet book teaches; `fractions` = the client's per-quality /"
      " per-grade tables (BaseFraction / AppFraction: the equipment-rank score an item adds at each quality / grade;"
      " ArmEffFraction + ArmEff and DefendFraction + DefendEff: weapon / armour rank score thresholds and the glow"
      " level each rank shows); `max_quality` / `max_grade` = the highest quality / grade this client has numbers"
      " for; `name_cn` = the client's name is Chinese.")
    w("")
    w("**Quality and grade limits of this client.** the admin's Origin client lists stats for Q1-Q10 and grade scores for"
      " G1-G12 on ordinary gear (POINTS-AUDIT: forge Q10/G12), while the reborn database allows Q0-Q20 / G0-G25 and"
      " its own templates carry 20 / 25 values. Reborn's notes (`docs/client-reverse-engineering-notes.md`) say a"
      " quality or grade past the client's list can make the client fail the stat lookup or close while entering the"
      " scene, so the editor should warn above `max_quality` / `max_grade`.")
    w("")
    if cross:
        w("## 2. Items: database against client")
        w("")
        w("Database `%s`, read %s (read-only snapshot `db-refs.json`)." % (dbmeta.get("database"), dbmeta.get("taken_at", "")[:19]))
        w("")
        w("* `item_templates`: **%d** rows. Client `ItemBaseAttribute.xml`: **%d** distinct ids." % (cross["db_items"], cross["client_items"]))
        w("* In the database but **not in your client: %d** (the editor marks them \"not in your client\"; giving one"
          " to a character makes the client show an empty or broken slot):" % len(cross["db_only"]))
        w("")
        w("  | id | kind | name |")
        w("  |---:|---|---|")
        for r in cross["db_only"]:
            w("  | %d | %s | %s |" % (r["id"], r["kind"], r["name"]))
        w("")
        w("* In the client but not in the database: **%d** (the server cannot create them):" % len(cross["client_only"]))
        if cross["client_only"]:
            w("")
            w("  | id | kind | name |")
            w("  |---:|---|---|")
            for r in cross["client_only"]:
                w("  | %d | %s | %s |" % (r["id"], r["kind"], r["name"]))
        w("* Same id, different kind: %d%s. Same id, different English name: %d (the database copied"
          " the names from an older text file; the editor shows the client's)." % (
              len(cross["kind_mismatch"]),
              (" (" + ", ".join("%d %s/%s" % (k["id"], k["db_kind"], k["client_kind"]) for k in cross["kind_mismatch"][:8]) + ")")
              if cross["kind_mismatch"] else "", cross["name_differs"]))
        if cross["quality_vector_lengths"]:
            w("* Stat vectors (one value per quality): " + "; ".join(
                "client %d values / database %d values: %d stats" % (q["client"], q["db"], q["stats"])
                for q in cross["quality_vector_lengths"]) + ". The client lists Q1-Q10; the reborn server extended"
              " its copy to Q1-Q20 (`docs/mount-quality-grade.md`), so above Q10 the client tooltip has no number of"
              " its own.")
        w("")
        w("### Why only 145 rows are `kind = weapon`")
        w("")
        w("`kind` is the item's equipment **slot family**, not \"things you can fight with\". Of the %d templates:" % cross["db_items"])
        w("")
        w("| kind | database | client | what it is |")
        w("|---|---:|---:|---|")
        what = {"consume item": "potions, scrolls, books, boxes, pet items, materials, gems, quest items, recipes...",
                "skillitem": "client-only kind; the server stores these as `consume item`",
                "mount": "mounts (riding slot 20)", "weapon": "main-hand weapons, every class (slot 10)",
                "stylish": "fashion (slot 12)", "head": "helmets", "cuff": "wrist pieces", "leggins": "leggings",
                "glove": "gloves", "shoes": "shoes", "ring": "rings (slots 8 and 9)", "armor": "warrior armour (slot 3)",
                "cloth": "scholar robes (also slot 3)", "girdle": "girdles", "amulet": "amulets",
                "shield": "off-hand shields (slot 11)", "mounthead": "mount coronet", "mountarmor": "mount armour",
                "mountsoul": "mount soul", "mountornament": "mount saddle", "mountamulet": "mount tassels",
                "create": "starter gear templates", "pet": "pet-shaped mounts"}
        for k, v in cross["kinds"].items():
            w("| %s | %d | %d | %s |" % (k, v["db"], v["client"], what.get(k, "")))
        w("")
        w("The client sorts weapons into these groups (ItemBaseAttribute.xml `<Weapons>` children, labels from"
          " EquipDescription.dat): " + ", ".join("%s %d" % (g, n) for g, n in sorted(cross["weapon_groups"].items())) +
          ". Every weapon uses the one weapon slot (10); class decides who can wield it (Warrior swords, Champion"
          " spears, Priest wands, Mage staffs). 145 weapons is the whole set: the game has about 14 weapon tiers"
          " per class plus event and quest weapons. The editor looked short of items because its picker returned"
          " only the first 40 matches (`Reborn.item_search(limit=40)`) and filtered by slot; the catalogue now"
          " lists every item so the picker grid can show all of them.")
        w("")
    if cov:
        w("## 3. Coverage: how many database IDs get an English name")
        w("")
        w("Each row is one database column that stores an ID. `named` counts rows whose value the catalogue"
          " resolves to a non-empty English name (a name with Chinese characters does not count). Empty tables (no"
          " rows yet on this server) show `-`.")
        w("")
        w("| ID type | Database column | distinct | rows | named rows | % | not named (first few) |")
        w("|---|---|---:|---:|---:|---:|---|")
        for name, c in cov.items():
            pct = "-" if c["pct_rows"] is None else ("%.1f" % c["pct_rows"])
            miss = ", ".join(str(x) for x in c["missing"][:8]) + (" ..." if c["missing_count"] > 8 else "")
            w("| %s | `%s` | %d | %d | %d | %s | %s |" % (c["type"], name, c["distinct"], c["rows"], c["named_rows"], pct, miss))
        w("")
        tot = collections.OrderedDict()
        for c in cov.values():
            t = tot.setdefault(c["type"], [0, 0])
            t[0] += c["rows"]
            t[1] += c["named_rows"]
        w("Per ID type (all columns together):")
        w("")
        w("| ID type | rows | named | % |")
        w("|---|---:|---:|---:|")
        for t, (r, n) in tot.items():
            w("| %s | %d | %d | %s |" % (t, r, n, "-" if not r else "%.1f" % (100.0 * n / r)))
        w("")
    if cov:
        w("### IDs the database uses that this client cannot name")
        w("")
        w("| ID type | value | where | why |")
        w("|---|---|---|---|")
        seen = set()
        for name, c in cov.items():
            if c["type"] in ("item",) and cross:
                continue                      # items: section 2
            for v in c["missing"]:
                if (c["type"], v) in seen:
                    continue
                seen.add((c["type"], v))
                why = WHY_MISSING.get((c["type"], v))
                if why is None and c["type"] == "npc" and isinstance(v, str):
                    why = "dialogue or function text key, not an NPC; no English text for it in NPCDescription.dat"
                if why is None and c["type"] == "mount" and isinstance(v, int) and 16200 <= v <= 16209:
                    why = "Erebus Lion - reborn's locally authored mount (InstallErebusLionMount.py), not in this client"
                w("| %s | %s | `%s` | %s |" % (c["type"], v, name, why or "not in the client files"))
        w("")
    w("## 4. Gaps and notes")
    w("")
    for g in cat["gaps"]:
        w("* " + g)
    w("")
    open(path, "w", encoding="utf-8").write("\n".join(L))
    print("wrote %s" % path)


# ------------------------------------------------------------------ main
def dump(outdir, name, obj):
    def keyed(o):
        if isinstance(o, dict):
            items = list(o.items())
            try:
                items.sort(key=lambda kv: (0, int(kv[0])) if str(kv[0]).lstrip("-").isdigit() else (1, str(kv[0])))
            except TypeError:
                pass
            return {str(k): keyed(v) for k, v in items}
        if isinstance(o, list):
            return [keyed(v) for v in o]
        return o
    p = os.path.join(outdir, name + ".json")
    with open(p, "w", encoding="utf-8") as f:
        json.dump(keyed(obj), f, ensure_ascii=False, separators=(",", ":"))
    return os.path.getsize(p)


def main():
    args = [x for x in sys.argv[1:] if not x.startswith("--")]
    client = args[0] if args else os.path.join(ROOT, "GodsWar-2.51", "Reborn-Client", "Client")
    loc = os.path.join(client, "Localization", "en_us")
    outdir = os.path.join(HERE, "catalog-origin")
    static = os.path.join(HERE, "static")
    os.makedirs(outdir, exist_ok=True)
    attributes = build_attributes(loc)
    if "--attributes-only" in sys.argv:
        dump(outdir, "attributes", attributes)
        print("attributes %d entries" % len(attributes))
        return

    sprites = Sprites(os.path.join(loc, "UI", "Texture"))
    labels = build_labels(loc)
    statuses = build_statuses(loc, sprites)
    kinds = build_zodiac_kinds(loc, sprites)
    skills = build_skills(loc, sprites, statuses, kinds)
    petskills = build_petskills(loc, sprites)
    items, dupes, shop_ids = build_items(loc, labels, sprites, skills, petskills)
    appendattrs = build_appendattrs(loc, attributes)
    talents, talent_stats = build_talents(loc, sprites)
    zodiac = build_zodiac(loc, kinds)
    maps = build_maps(loc, client)
    monsters = build_monsters(loc, maps)
    npcs, npctexts = build_npcs(loc)
    quests = build_quests(loc, npcs, maps)
    titles = build_titles(loc)
    pets = build_pets(loc, sprites)
    mounts = build_mounts(loc, items, statuses)
    guild = build_guild(loc, sprites)
    holystones = build_holystones(loc, items, sprites)
    suits = build_suits(loc, items, sprites)
    shops = build_shops(shop_ids, items)
    currencies = build_currencies(loc)
    labels["talent_stats"] = talent_stats

    sprite_info = {}
    if "--no-images" not in sys.argv:
        remap = sprites.build(os.path.join(static, "catalog-icons.png"))
        sprite_info["tiles"] = getattr(sprites, "tiles", 0)
        for obj in (items, statuses, kinds, skills, petskills, talents, pets, mounts, guild, holystones, suits):
            apply_sprites(obj, remap)
        build_images(loc, static)

    cat = {"client": os.path.relpath(client, ROOT), "items": items, "appendattrs": appendattrs, "attributes": attributes,
           "skills": skills, "talents": talents, "zodiac": zodiac, "monsters": monsters, "npcs": npcs, "maps": maps,
           "quests": quests, "npctexts": npctexts, "titles": titles, "pets": pets, "petskills": petskills, "mounts": mounts,
           "statuses": statuses, "guild": guild, "holystones": holystones, "suits": suits, "shops": shops,
           "currencies": currencies, "labels": labels}
    files = [
        ("items", items, "item id", "Settings/Sys/ItemBaseAttribute.xml, EquipForge.xml, ItemShop.xml; Text/EquipName.dat, EquipDescription.dat"),
        ("appendattrs", appendattrs, "append attribute id (= item_attribute_templates.id)", "Settings/Sys/ItemAppendAttribute.xml; Text/EquipDescription.dat"),
        ("attributes", attributes, "append attribute stat type", "ItemAppendAttribute.xml; Text/Message.dat"),
        ("skills", skills, "skill id", "Settings/Sys/Magic.ini, MagicMini.xml; Text/SkillInfo.dat"),
        ("talents", talents, "talent id", "Settings/Sys/Skill.ini"),
        ("zodiac", zodiac, "signs / levels / grids / kinds / stones", "UI/XML/ConstellationConfig.lua, SkillTrainConfig.lua; Settings/Sys/MagicMini.xml"),
        ("monsters", monsters, "monster template_key", "Monster/*/Monster.ini, Monster_New.ini"),
        ("npcs", npcs, "npc_key (templates inside)", "Text/NpcName.dat, NPCDescription.dat; Settings/Sys/NPC.INI"),
        ("npctexts", npctexts, "dialogue text key (npc_text_templates keys that are not NPCs)", "Text/NPCDescription.dat"),
        ("maps", maps, "map id", "Settings/Sys/MapIdToNameConfig.ini, SpanMapConfig.xml; Text/QuestRegion.dat; Monster/*/Address.ini"),
        ("quests", quests, "quest id", "Settings/Sys/Quest.xml; Text/Quest/<id>.dat"),
        ("titles", titles, "title id", "Text/DesigName.dat, DesigInfo.dat"),
        ("pets", pets, "pet species", "Settings/Sys/Pet.xml; Text/Message_Pet.dat"),
        ("petskills", petskills, "pet skill id", "Settings/Sys/Pet_Skill.xml; Text/Message_Pet.dat"),
        ("mounts", mounts, "items / rides / ride_models", "ItemBaseAttribute.xml (kind mount*), Status.ini RideId, Ride.ini"),
        ("statuses", statuses, "status id", "Settings/Sys/Status.ini"),
        ("guild", guild, "buildings / altars / levels / duties", "Settings/Sys/Consortia.xml, AltarInfo.xml, Consortia_Job.ini"),
        ("holystones", holystones, "holy stone effect id", "Settings/Sys/EquipStoneInfo.xml; Text/EquipDescription.dat"),
        ("suits", suits, "holy suit code (tier x 100 + level)", "Settings/Sys/EquipSuitInfoIni.xml, EquipEffect.xml, EquipEffectSuit.xml"),
        ("shops", shops, "client list + server catalogues", "Settings/Sys/ItemShop.xml; reborn src Packets/*.cs"),
        ("currencies", currencies, "currency code", "Text/Message.dat, EquipDescription.dat"),
        ("labels", labels, "lookup name", "Text/EquipDescription.dat, Message.dat; Settings/Sys/ItemColor.xml, Skill.ini"),
    ]
    counts = []
    for name, obj, key, src in files:
        size = dump(outdir, name, obj)
        n = len(obj)
        if name == "zodiac":
            n = "%d signs, %d levels, %d grids, %d kinds" % (len(zodiac["signs"]), len(zodiac["levels"]), len(zodiac["grids"]), len(kinds))
        elif name == "mounts":
            n = "%d items, %d rides" % (len(mounts["items"]), len(mounts["rides"]))
        elif name == "guild":
            n = "%d buildings, %d altars" % (len(guild["buildings"]), len(guild["altars"]))
        elif name == "suits":
            n = "%d codes, %d tiers" % (len(suits["codes"]), len(suits["tiers"]))
        elif name == "shops":
            n = "%d client ids, %d server catalogues (%d entries)" % (
                len(shop_ids), len(shops["server_catalogues"]), sum(len(c["entries"]) for c in shops["server_catalogues"].values()))
        counts.append(("catalog-origin/%s.json" % name, n, key, src))
        print("%-12s %-50s %8d bytes" % (name, n, size))

    gaps = []
    if dupes:
        gaps.append("ItemBaseAttribute.xml defines %d ids twice (%s); the later definition wins, like in the client."
                    % (len(set(dupes)), ", ".join(str(x) for x in sorted(set(dupes)))))
    nomap = [m["scene"] for m in maps.values() if m["name_source"] != "QuestRegion.dat"]
    gaps.append("Map display names in the database (`map_templates.display_name`) are partly Chinese; the catalogue"
                " names every map from `QuestRegion.dat` by client scene id%s." % (
                    (" (scene key used for %s)" % ", ".join(nomap)) if nomap else ""))
    cn_items = sorted(i for i, it in items.items() if it.get("name_cn"))
    gaps.append("%d item names in `EquipName.dat` are still Chinese in this client (event collectibles and a few quest"
                " boxes, e.g. %s); they are flagged `name_cn` and do not count as English in section 3."
                % (len(cn_items), ", ".join("%d %s" % (i, items[i]["name"]) for i in cn_items[:4])))
    cn_sk = sum(1 for s in skills.values() if has_cjk(s["name"]))
    gaps.append("%d of %d Magic.ini skill names are Chinese (item-use and monster skills the player never sees in a"
                " skill window); every player skill and skill book is English." % (cn_sk, len(skills)))
    gaps.append("Faith (`character_base.belief`) and guild duty names exist only in Chinese in this client"
                " (`Text/Belief.dat`, `Consortia_Job.ini`); `labels.json` gives the standard English names, marked translated.")
    gaps.append("Status effect codes (`statuses.json` `effect_label`) are translated from the Chinese comments in Status.ini.")
    gaps.append("Skill descriptions keep the client's value placeholders as `{Power1}` / `{Power2}`; the numbers are"
                " in the same entry (`power1`, `power2`). The client also scales them by skill level and zodiac grid.")
    gaps.append("Ride.ini mount model names are Chinese (GBK); mounts are named by their item (`mounts.items`) and by"
                " their ride status (`mounts.rides`, from Status.ini).")
    gaps.append("Monster stats (level, HP, damage) are not in the client; the database (`monster_templates.stats`,"
                " `medusa_monster_rules`) holds them. `monsters.json` gives names, maps, rank and model.")
    gaps.append("Shops are not in the database: the reborn server compiles its NPC shop and cash-mall catalogues into"
                " its code. `shops.json` decodes them for display only.")
    gaps.append("`quest_completed_ids` and the quest tables use the ids of `Text/Quest/<id>.dat`; %d quests have text"
                " but no `Quest.xml` entry (no giver / level data)." % sum(1 for q in quests.values() if not q["in_quest_xml"]))
    cat["gaps"] = gaps

    cross = cov = None
    dbmeta = {}
    refs_path = os.path.join(outdir, "db-refs.json")
    if os.path.exists(refs_path):
        db = json.load(open(refs_path, encoding="utf-8"))
        dbmeta = {"database": db.get("database"), "taken_at": db.get("taken_at", "")}
        cross = crosscheck_items(items, db.get("item_templates") or [], labels)
        cov = measure_coverage(db.get("refs") or {}, resolvers(cat))
        hi = [(v, n) for v, n in ((db.get("refs") or {}).get("character_items.item_quality") or {}).get("values", []) if v > 10]
        if hi:
            gaps.append("Live data check: %d `character_items` row(s) already sit above this client's Q10 (%s) -"
                        " see the quality limit note in section 1." % (sum(n for _, n in hi), ", ".join(
                            "Q%d x%d" % (v, n) for v, n in hi)))
        dump(outdir, "items-crosscheck", dict(cross, database=dbmeta))
        dump(outdir, "coverage", {"database": dbmeta, "columns": cov})
        print("cross-check: %d db-only, %d client-only items" % (len(cross["db_only"]), len(cross["client_only"])))
    else:
        print("no catalog-origin/db-refs.json: skipping the database cross-check (run catalog_dbrefs.py)")

    # compact list for the all-items picker grid: one row per item, fields named once (items.json has the rest)
    idx_fields = ["id", "name", "kind", "category", "slot", "minlv", "maxlv", "classes", "camp", "sex", "stack", "price",
                  "sprite", "icon", "atlas", "max_quality", "max_grade", "append_count", "cn", "desc"]
    rows = []
    for iid in sorted(items):
        it = items[iid]
        rows.append([iid, it["name"], it["kind"], it["category"], it["slot"], it["minlv"], it["maxlv"], it["classes"],
                     it["camp"], it["sex"], it["stack"], it["price"], it["sprite"], it["icon"], it["atlas"],
                     it.get("max_quality", 0), it.get("max_grade", 0), len(it.get("append_attributes", [])),
                     1 if it.get("name_cn") else 0, it["desc"][:160]])
    dump(outdir, "items-index", {"fields": idx_fields, "rows": rows,
                                 "note": "max_quality / max_grade = the highest quality / grade this client has numbers"
                                         " for (0 = item has no quality / grade); append_count = how many append"
                                         " attributes the item may roll (items.json append_attributes)"})
    with open(os.path.join(outdir, "catalog.json"), "w", encoding="utf-8") as f:
        json.dump({"client": cat["client"], "items": len(items), "pets": len(pets), "petskills": len(petskills),
                   "quests": len(quests), "skills": len(skills), "talents": len(talents), "monsters": len(monsters),
                   "npcs": len(npcs), "maps": len(maps), "titles": len(titles), "statuses": len(statuses),
                   "appendattrs": len(appendattrs), "atlases": ATLASES,
                   "atlas_layout": "2x2 in static/icons-origin.png: atlas i at ((i%2)*1024, (i//2)*1024)",
                   "sprites": {"file": "static/catalog-icons.png", "tile": TILE, "columns": SPRITE_COLS,
                               "tiles": sprite_info.get("tiles"),
                               "position": "x = (sprite % columns) * tile, y = (sprite // columns) * tile"},
                   "database": dbmeta}, f, indent=1)
    counts.append(("catalog-origin/items-index.json", len(rows), "one row per item, `fields` names the columns",
                   "items.json (compact list for the all-items picker grid)"))
    if cross:
        counts.append(("catalog-origin/items-crosscheck.json", "%d db-only, %d client-only" % (
            len(cross["db_only"]), len(cross["client_only"])), "-", "items.json against db-refs.json"))
        counts.append(("catalog-origin/coverage.json", len(cov), "database column", "all catalogues against db-refs.json"))
        counts.append(("catalog-origin/db-refs.json", len(db.get("refs") or {}), "database column",
                       "catalog_dbrefs.py (read-only snapshot of the IDs the database uses; no player data)"))
    write_catalog_md(os.path.join(outdir, "CATALOG.md"), cat, counts, cross, cov, dbmeta, sprite_info)


if __name__ == "__main__":
    main()
