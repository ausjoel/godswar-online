"""modules/library.py - the Library: a read-only encyclopedia of every game entry, built from the admin's Godswar Origin
client (catalog-origin/*.json) and, where the server holds more, from the reborn database (read-only).

Routes (GET routes only read; the one POST, give/<cid>, goes through the framework's guarded write helpers):
  tabs                  the Library tabs with their entry counts, the catalogue build stamp, and the editors an entry
                        can be handed to ("use in editor")
  detail/<type>/<id>    one entry: the full catalogue record, every attribute as {label, value, help}, cross references
                        resolved to English names (who drops it, which skill applies this buff, which items teach this
                        skill ...), and the server's own rows for it (item_templates, monster_templates, spawns, loot ...)
  search?q=             every entry of every tab whose name / id / key contains the words (grouped by tab)
  zodiac                the zodiac system: 12 signs, 30 levels, 16 training grids, 34 skill kinds, the energy stones
  talents               every talent with its tree position and prerequisite (for the talent-tree view)
  shops                 the server's compiled shop catalogues (read-only: the server builds them into its code)
  suits                 holy suit tiers, levels and point bonuses
  char/<cid>            a character's map and position (what 'Move a character here' shows and sends as expected)
  POST give/<cid>       give an item: online -> ctx.bag_item (live on round2, else 423); offline -> ctx.char_write

"Use in editor" hands the entry to the right editor (Inventory, Skills, Pets, Character, World), or uses the
framework's write helpers (give: ctx.bag_item / ctx.char_write below; move: GW.saveFields), which do the guarded,
live-or-database write with a backup and Undo. The database reads below all run in a read-only psql session
(reborn.Psql.json sets default_transaction_read_only = on).
"""
import re
import threading
import urllib.parse

from . import common
from .common import Bad, NotFound, lit

M = common.module(
    "library", "Library",
    icon={"sprite": 604},
    order=5,
    group="library",
    status="ready",
    description="Every item, buff, skill, pet skill, talent, zodiac skill, monster, NPC, map, quest, title, pet, mount, "
                "holy stone and append attribute from your Origin client, in English, with the game's tooltips. "
                "Read-only; 'Use in editor' hands an entry to the right editor.",
    needs_character=False,
)

CLASS_NAMES = common.CLASS_NAMES
CAMP_NAMES = common.CAMP_NAMES
MOUNT_KINDS = ("mount", "mounthead", "mountarmor", "mountsoul", "mountornament", "mountamulet")

# --------------------------------------------------------------------------------------------- the tabs
# key, title, catalogue type, icon sprite, help, editor it hands entries to, extra grid filter (front end)
TABS = [
    ("items", "Items", "item", 604, "Every item of your client: weapons, armour, jewellery, fashion, consumables, "
     "books, pet items, materials, quest items. 3,473 from the client plus the server-only ones.", "inventory", None),
    ("mounts", "Mounts", "item", 727, "Every mount and mount gear item (coronet, armour, soul, saddle, tassels), with "
     "speed and HP by quality.", "inventory", "mount"),
    ("statuses", "Buffs & statuses", "status", 3, "Every buff, debuff and state (Status.ini): what it does, how long "
     "it lasts, and which skills apply it.", "world", None),
    ("skills", "Skills", "skill", 34, "Every skill (Magic.ini) by class: MP, cooldown, range, the description with "
     "its numbers, the buff it applies, and the book that teaches it.", "skills", None),
    ("petskills", "Pet skills", "petskill", 79, "Every pet skill (Pet_Skill.xml): what it does per rank, which pet "
     "family may learn it, and the book that teaches it.", "pets", None),
    ("talents", "Talents", "talent", 63, "Every talent of the four talent trees (Skill.ini): effect per rank and "
     "the talent it needs first.", "skills", None),
    ("zodiac", "Zodiac", "zodiac_kind", 40, "The zodiac system: the skill kinds you train in the 16 grids, the 12 "
     "signs, the 30 zodiac levels and the energy stones.", "skills", None),
    ("monsters", "Monsters", "monster", None, "Every monster of the client (Monster.ini): rank, where it lives, and "
     "from the server: level, HP, spawns and loot.", "world", None),
    ("npcs", "NPCs", "npc", None, "Every NPC: name, the line it says, where it stands, the quests it gives or "
     "takes.", "world", None),
    ("maps", "Maps", "map", None, "Every map: named places with coordinates, the gates to other maps, the monsters "
     "and NPCs placed on it. You can move a character to a place.", "character", None),
    ("quests", "Quests", "quest", None, "Every quest (Quest.xml + Text/Quest): level, camp, who gives it, what to "
     "do, rewards.", "skills", None),
    ("titles", "Titles", "title", None, "Every title of DesigName.dat, and the Medusa reward titles the server "
     "adds.", "character", None),
    ("pets", "Pets", "pet", 1055, "Every pet species (Pet.xml): icons, models per rebirth, the egg item and starter "
     "skill the server uses.", "pets", None),
    ("holystones", "Holy stones", "holystone", 811, "Every holy stone effect that can sit in a gear socket, and the "
     "stone items that carry it.", "inventory", None),
    ("appendattrs", "Append attributes", "appendattr", None, "Every green attribute an item can roll "
     "(ItemAppendAttribute.xml): value per level, and the items that may roll it.", "inventory", None),
    ("suits", "Holy suits", "suit", 1091, "Holy suit tiers (Bronze ... Adamantium) and levels: suit-effect bonus "
     "and EXP per level.", "inventory", None),
    ("guild", "Guild buildings", "guild_building", None, "Guild buildings and altars: what each level costs and "
     "gives.", "guild", None),
    ("shops", "Shops", None, 664, "The shop and cash-mall catalogues the server compiles into its code. To change "
     "what a shop sells, its prices or slot order, use 'NPCs: shops & teleports' (an edited shop replaces the compiled list).", "npcs", None),
]
TAB = {t[0]: t for t in TABS}

# which editor an entry type is handed to, and what that editor does with it
USE = {
    "item": ("inventory", "Put it in a bag, the equipment, the warehouse or a mount slot, with quality, grade and attributes"),
    "appendattr": ("inventory", "Add it to a piece of gear in the gear editor"),
    "holystone": ("inventory", "Socket it into a piece of gear in the gear editor"),
    "suit": ("inventory", "Set a gear piece's holy suit level"),
    "skill": ("skills", "Teach it to a character (skill book window)"),
    "talent": ("skills", "Set its rank in a character's talent tree"),
    "zodiac_kind": ("skills", "Put it in a zodiac training grid"),
    "quest": ("skills", "Accept it, set its progress or mark it done for a character"),
    "pet": ("pets", "Give a character a pet of this species"),
    "petskill": ("pets", "Teach it to one of a character's pets"),
    "title": ("character", "Give it to a character as the shown title"),
    "map": ("character", "Move a character to this map"),
    "zodiac_sign": ("character", "Set a character's zodiac sign"),
    "monster": ("world", "Edit its loot table or place it on a map"),
    "npc": ("world", "Place it on a map"),
    "status": ("world", "Use it in events and loot rules"),
    "guild_building": ("guild", "Set a guild's building level"),
    "altar": ("guild", "Set a guild's altar level"),
}

# --------------------------------------------------------------------------------------------- field help
# (label, help) for every attribute the detail panel lists. Help says what the value is, in plain words, and which
# client file it comes from. Values the client lists without a known meaning say so instead of guessing.
F = {
    "item": {
        "id": ("Item id", "The item's number (ItemBaseAttribute.xml ID; item_templates.id on the server; character_items.prop_id on a character)."),
        "name": ("Name", "English name from Text/EquipName.dat."),
        "kind": ("Slot family", "Which equipment slot family the item belongs to (weapon, armor, ring ...), or 'consume item' for everything that is not worn."),
        "category": ("What it is", "The client's own group for it (One-Hand Sword, Potion, Skill Book ...)."),
        "slot_label": ("Equipment slot", "Where it is worn. Not wearable items have none."),
        "slot": ("Slot number", "The equipment slot number the server uses (-1 = not wearable). Rings fit 8 and 9."),
        "class_labels": ("Classes", "Who may use or wear it. All four = everyone."),
        "minlv": ("Required level", "Lowest character level that may use it (PlayLv)."),
        "maxlv": ("Highest level", "Highest character level that may use it (PlayLv; 200 = no limit)."),
        "sex": ("Sex", "Only one sex may wear it (fashion)."),
        "camp": ("Camp", "Only Sparta or Athens may use it."),
        "stack": ("Stack size", "Most items of this kind in one bag cell (Overlap)."),
        "price": ("Shop sell value", "Silver a shop pays when you sell it (Money)."),
        "bindtype": ("Binding rule", "The client's BindType value (0 / empty = never bound, 1 = bound when picked up, 2 = bound when equipped or used). Whether a copy is bound is stored per copy (character_items.bound)."),
        "random": ("Random-attribute value", "The client's Random value for this item (ItemBaseAttribute.xml). The server decides what really rolls when an item drops."),
        "distribution": ("Attribute roll range", "The client's Distribution pair for this item (ItemBaseAttribute.xml), as listed."),
        "hand": ("Hand", "Which hand the item takes (the client's Hand value): 1 = both hands (two-hand swords, wands, spears), "
                 "2 = the main hand (one-hand swords and wands), 3 = the off hand (shields). Measured on every weapon and shield "
                 "of the client."),
        "group": ("Client group", "Where the item sits in ItemBaseAttribute.xml (its group folders)."),
        "item_type": ("Item type", "The client's item type value, as listed (meaning not documented)."),
        "values": ("Values", "The client's Values field for this item (what a consumable restores or grants), as listed."),
        "prev_skill": ("Needs skill first", "Skill books: the skill rank that must be known before this book can be read (PrevSkillID)."),
        "equip": ("Wearable", "1 = the client treats it as equipment."),
        "usable": ("Usable", "1 = it can be used from the bag (right click)."),
        "skill_flag": ("Skill flag", "The client's SkillFlag value (which skills the weapon allows), as listed."),
        "use_skill_name": ("Use effect", "The skill the item casts when used."),
        "teaches_skill_name": ("Teaches skill", "The skill a skill book teaches."),
        "pet_skill_name": ("Teaches pet skill", "The pet skill a pet skill book teaches."),
        "life_skill": ("Life skill", "The client's Lifing value (life-skill recipe or tool), as listed."),
        "quest": ("Quest item", "The quest this item belongs to (the client's Quest value)."),
        "mode": ("Model", "The client's Mode value (model or appearance number)."),
        "gold_box": ("Gold box", "The client's GoldBoxIdx (which treasure box table it opens)."),
        "pet_food": ("Pet food kind", "Pet food: which pet food kind it is."),
        "pet_food_fill": ("Satiety", "Pet food: how much satiety it gives."),
        "pet_food_favor": ("Amity", "Pet food: how much amity it gives."),
        "pet_limit": ("Pet limit", "The client's Petlimit value."),
        "place_limit": ("Place limit", "The client's PlaceLimit value (where it may be used)."),
        "expired": ("Expires", "The client's ExpiredTime (time-limited items)."),
        "max_quality": ("Highest quality (client)", "The highest quality this client has numbers for. The server allows up to Q20; above this the client may fail to show the item."),
        "max_grade": ("Highest grade (client)", "The highest grade this client has numbers for (gear: G25)."),
        "tag": ("Client key", "The item's tag in ItemBaseAttribute.xml (and item_templates.name_key)."),
        "in_shop_list": ("In the shop list", "Listed in Settings/Sys/ItemShop.xml. Reference only: Origin.exe never loads that file, so it does not limit shops."),
        "desc": ("Description", "Text/EquipDescription.dat."),
    },
    "skill": {
        "id": ("Skill id", "Magic.ini id (skill_templates.skill_id; character_skills.skill_id)."),
        "name": ("Name", "Text/SkillInfo.dat."), "level": ("Skill level", "Rank of this skill in its family."),
        "family_name": ("Skill family", "The skill line this rank belongs to; every rank is its own id."),
        "class": ("Class", "The class that learns it (none = an item or monster skill)."),
        "mp": ("MP cost", "MP used per cast."), "cooldown_s": ("Cooldown", "Seconds before it can be cast again."),
        "cast_s": ("Cast time", "Seconds of casting (0 = instant)."), "range": ("Range", "How far the target may be."),
        "area": ("Area", "Radius of the area it hits (0 = one target)."), "interval_s": ("Tick interval", "Seconds between ticks of a channelled or repeating skill."),
        "power1": ("Power 1 (attack %)", "Extra share of the typed attack the hit adds: -0.5 means an additional 50% (reborn docs/general-player-skill-damage)."),
        "power2": ("Power 2 (flat)", "Flat damage or healing added to the hit."),
        "power3": ("Power 3", "A third skill value the client lists."),
        "status_name": ("Applies status", "The buff or debuff the skill puts on its target."),
        "status_chance": ("Status chance", "The client's chance value for the status, as listed."),
        "target": ("Target type", "The client's target code (who it may be cast on), as listed."),
        "affects": ("Affects", "The client's AffectObj code, as listed."),
        "property": ("Damage type", "0 = physical, 1 = magical (Property)."),
        "priority": ("Priority", "The client's Priority value."), "desc": ("Description", "Text/SkillInfo.dat, with the numbers filled in."),
        "base": ("Skill name", "The skill's name without its rank number."),
        "family": ("Zodiac skill kind", "The skill kind number (Magic.ini Kind); the zodiac grids train skills by kind (MagicMini.xml)."),
        "equip_request": ("Needs weapon", "The weapon kind the skill needs (EquipRequest; -1 or 0 = any), as the client lists it."),
        "type_flag": ("Type flag", "The client's TypeFlag value, as listed (meaning not documented)."),
        "server_classes": ("Classes (server)", "Who may learn it, from the server's skill_templates.class_ids. The client only names the class of "
                           "zodiac-trainable skills, so this is the reliable one."),
    },
    "status": {
        "id": ("Status id", "Status.ini id."), "name": ("Name", "Status.ini name."), "note": ("What it does", "Status.ini note."),
        "effect_label": ("Effect", "The effect code, in words (translated from the Chinese comments in Status.ini)."),
        "duration_s": ("Duration", "Seconds it lasts."), "interval_s": ("Tick interval", "Seconds between ticks (damage or healing over time)."),
        "value": ("Value", "Strength of the effect per tick or in total, as listed."), "family": ("Family", "Statuses of one family replace each other."),
        "priority": ("Priority", "Which status of a family wins."), "ride": ("Ride model", "Mount statuses: the ride model shown."),
        "visual_skill": ("Visual", "The skill effect drawn while it lasts (-1 = none)."), "style": ("Style", "The client's Style value."),
        "effect": ("Effect code", "The status effect number (Status.ini Effect); 'Effect' shows it in words when known."),
    },
    "petskill": {
        "id": ("Pet skill id", "Pet_Skill.xml id (character_pet_skills.skill_id)."), "name": ("Name", "Text/Message_Pet.dat."),
        "info": ("What it does", "Text/Message_Pet.dat with the values filled in."), "type": ("Family / type", "Pet skill type: species-only skills are for one pet family."),
        "values": ("Values by rank", "The skill's value at each rank."), "restrict": ("Rank requirement", "Pet rank needed per skill rank."),
        "priority": ("Tier", "The skill's tier in its line."), "add": ("Add flag", "The client's Add value."), "effect": ("Effect", "The client's Effect value."),
        "trait": ("Traits", "The client's Trait values (six numbers)."),
    },
    "talent": {
        "id": ("Talent id", "Skill.ini id (talent_templates.id; character_talents.talent_id)."), "name": ("Name", "Skill.ini name."),
        "class": ("Class", "Whose talent tree it is in."), "tree_order": ("Tree position", "Order in the talent tree."),
        "requires_talent": ("Needs talent", "This talent must have ranks first."), "requires_rank": ("Needs rank", "Ranks the needed talent must have."),
        "requires_total_rank": ("Needs total ranks", "Talent points spent in the tree first."), "equip_request": ("Needs weapon", "Weapon kind needed (-1 = any)."),
    },
    "monster": {
        "key": ("Monster key", "Monster.ini section name (monster_templates.template_key)."), "name": ("Name", "Monster.ini name."),
        "rank": ("Rank", "normal / elite / boss."), "attack_type": ("Attack type", "1 = melee, 2 = ranged / magic, as the client lists it."),
        "scale": ("Size", "Model scale."), "model": ("Model", "Model file."), "texture": ("Texture", "Texture file."),
    },
    "npc": {"key": ("NPC key", "NPC key (npc_spawn_definitions.npc_key)."), "name": ("Name", "Text/NpcName.dat."),
            "desc": ("What it says", "Text/NPCDescription.dat."), "scene": ("Scene", "The map scene it belongs to.")},
    "map": {"id": ("Map id", "Map id (character_base.Map, map_templates.map_id)."), "name": ("Name", "Text/QuestRegion.dat."),
            "scene": ("Scene", "Client scene folder."), "client_scene_id": ("Client scene id", "The scene number the client loads."),
            "minimap": ("Has a minimap", "UI/Texture/MinMap holds a minimap picture for this scene."),
            "name_source": ("Name from", "Where the English name comes from: QuestRegion.dat, or the scene key when the client has no name.")},
    "quest": {"id": ("Quest id", "Text/Quest/<id>.dat (character_quests.quest_id)."), "title": ("Title", "Quest title."),
              "min_level": ("Level from", "Lowest level that may take it."), "max_level": ("Level to", "Highest level."),
              "camp": ("Camp", "Sparta, Athens or both."), "giver_name": ("Given by", "NPC that gives it."),
              "responder_name": ("Hand in to", "NPC that completes it."), "giver_map_name": ("Where", "Map of the giver."),
              "objectives": ("Objectives", "What to do."), "details": ("Story", "Quest text."), "completion": ("On completion", "What the NPC says."),
              "class": ("Class", "Who may take it (-1 = every class)."), "faith": ("Faith", "The god-faith the quest needs (Quest.xml Belief; -1 = any)."),
              "color": ("Colour", "The colour the quest list draws the title in (Quest.xml Color)."),
              "pet_quest": ("Pet quest", "The client's pet value for the quest (Quest.xml pet), as listed; 0 = not a pet quest."),
              "has_text": ("Has quest text", "Text/Quest/<id>.dat exists, so the quest window can show its story."),
              "in_quest_xml": ("In Quest.xml", "Settings/Sys/Quest.xml lists it (level, camp, giver)."),
              "giver_map_name": ("Where", "Map of the NPC that gives it.")},
    "title": {"id": ("Title id", "DesigName.dat id (character_base.selected_title_id)."), "name": ("Name", "DesigName.dat."),
              "desc": ("What it gives", "DesigInfo.dat.")},
    "pet": {"species": ("Species", "Pet.xml species id (character_pets.species_id)."), "name": ("Name", "Text/Message_Pet.dat.")},
    "appendattr": {"id": ("Attribute id", "ItemAppendAttribute.xml id (item_attribute_templates.id; character_items.attribute1..5)."),
                   "label": ("Stat", "What it raises (Message.dat caption)."), "key": ("Client key", "Its tag in ItemAppendAttribute.xml."),
                   "max_level": ("Highest level", "Highest attribute level."), "stat_type": ("Stat type", "The stat number the server adds it to."),
                   "distribution": ("Roll range", "The client's Distribution pair."), "flag": ("Flag", "The client's Flag value."),
                   "text": ("Tooltip text", "How the item tooltip writes this attribute (EquipDescription.dat).")},
    "holystone": {"id": ("Effect id", "EquipStoneInfo.xml id (character_items.holy_socket1..6_effect_id)."), "name": ("Name", "EquipDescription.dat."),
                  "desc": ("Effect", "What the stone does in a socket."), "key": ("Client key", "The stat it changes."),
                  "percent": ("Percent", "The value is a percentage."),
                  "effect_text": ("Tooltip text", "How the tooltip writes the stone's effect (EquipDescription.dat).")},
    "zodiac_kind": {"kind": ("Kind", "Zodiac skill kind (character_zodiac_skill_grids.selected_skill_id)."), "name": ("Name", "MagicMini.xml."),
                    "class": ("Class", "Whose zodiac it is."), "first_skill": ("First skill", "The skill rank this kind trains first.")},
    "suit": {"code": ("Suit code", "tier x 100 + level (character_items.holy_suit_code)."), "name": ("Name", "EquipDescription.dat."),
             "tier": ("Tier", "Bronze 1 ... Adamantium 7."), "level": ("Level", "Level within the tier."),
             "bonus": ("Suit effect", "Extra suit effect at this level."), "exp_to_next": ("EXP to next", "Suit EXP to reach the next level.")},
    "guild_building": {"id": ("Building id", "Consortia.xml id (guild_building_types.building_type)."), "name": ("Name", "Consortia.xml."),
                       "desc": ("What it does", "Consortia.xml."), "max_level": ("Highest level", "Highest level the building can reach.")},
    "altar": {"id": ("Altar id", "AltarInfo.xml id."), "name": ("Name", "AltarInfo.xml."), "desc": ("What it does", "AltarInfo.xml.")},
}
COMMON_F = {
    "in_client": ("In your client", "Whether your Origin client's files define it. Missing = the game shows an empty or nameless entry."),
    "in_server": ("In the server", "Whether the server's templates define it. Missing = the server cannot create it."),
    "note": ("Note", "What the editor knows about this entry."),
}
# raw client fields that repeat another listed attribute (same value under an English label, or the id of an entry the
# cross references already show by name): not listed twice
HIDE = {
    "item": {"money", "overlap", "playlv", "teaches_skill", "use_skill", "pet_skill"},
    "skill": {"status"},
    "quest": {"giver", "responder", "giver_map"},
}
HAND_WORDS = {"1": "both hands", "2": "main hand", "3": "off hand"}
BIND_WORDS = {"0": "never bound", "": "never bound", "1": "bound when picked up", "2": "bound when equipped or used"}
STAT_HELP = "Value at each quality, Q1 first ."


def _attrs(typ, e, skip=()):
    """[{key, label, value, help}] for every scalar attribute of a catalogue entry, known ones first."""
    known = dict(F.get(typ, {}), **COMMON_F)
    out, seen = [], set(skip) | HIDE.get(typ, set())
    for k, (lab, hlp) in known.items():
        if k in e and k not in seen and e[k] not in (None, "", [], {}):
            v = e[k]
            if typ == "item" and k == "bindtype":
                v = "%s (%s)" % (BIND_WORDS.get(str(v), "value %s" % v), v)
            elif typ == "item" and k == "hand" and str(v) in HAND_WORDS:
                v = "%s (%s)" % (HAND_WORDS[str(v)], v)
            out.append({"key": k, "label": lab, "value": v, "help": hlp})
            seen.add(k)
    for k, v in e.items():
        if k in seen or k in ("icon", "atlas", "sprite", "sprite_male", "sprite_female", "icon_male", "icon_female", "socket_icon",
                              "socket_sprite", "classes", "class", "category_source", "name_cn", "type") or isinstance(v, (dict, list)):
            continue
        if v in (None, "", []):
            continue
        out.append({"key": k, "label": k.replace("_", " ").capitalize(), "value": v,
                    "help": "The client's '%s' value for this entry, as listed in its file." % k})
    return out


# --------------------------------------------------------------------------------------------- reverse indexes
def _shop_title(key):
    """'SpartaPetMerchant' -> 'Sparta Pet Merchant'."""
    return re.sub(r"(?<=[a-z])(?=[A-Z])", " ", key)


CURRENCY = {"silver": "Silver", "gold": "Gold", "binding_gold": "B-Gold", "honor": "Honor", "exchange_point": "Exchange points",
            "exchange_medal": "Exchange medals"}


class Refs:
    """Cross references computed once from the catalogue (who teaches / applies / drops / gives what)."""

    def __init__(self, cat):
        self.cat = cat
        self.lock = threading.Lock()
        self.ready = False

    def build(self):
        with self.lock:
            if self.ready:
                return self
            f = self.cat.file
            items, skills, statuses = f("items"), f("skills"), f("statuses")
            self.teach, self.use, self.petteach, self.forged_from, self.aa_items = {}, {}, {}, {}, {}
            for k, it in items.items():
                iid = it["id"]
                for fld, dst in (("teaches_skill", self.teach), ("use_skill", self.use), ("pet_skill", self.petteach)):
                    v = it.get(fld)
                    if v not in (None, "", -1):
                        dst.setdefault(str(v), []).append(iid)
                fg = it.get("forge") or {}
                if fg.get("next") not in (None, "", -1, 0):
                    self.forged_from.setdefault(str(fg["next"]), []).append(iid)
                for a in it.get("append_attributes") or []:
                    self.aa_items.setdefault(str(a), []).append(iid)
            self.status_skills, self.family = {}, {}
            for k, s in skills.items():
                if s.get("status") not in (None, "", -1):
                    self.status_skills.setdefault(str(s["status"]), []).append(s["id"])
                self.family.setdefault(str(s.get("family")), []).append(s["id"])
            for v in self.family.values():
                v.sort(key=lambda sid: ((skills.get(str(sid)) or {}).get("level") or 0, sid))
            self.quest_giver, self.quest_resp, self.quests_on_map = {}, {}, {}
            for k, q in f("quests").items():
                if q.get("giver"):
                    self.quest_giver.setdefault(q["giver"], []).append(q["id"])
                if q.get("responder"):
                    self.quest_resp.setdefault(q["responder"], []).append(q["id"])
                if q.get("giver_map") not in (None, ""):
                    self.quests_on_map.setdefault(str(q["giver_map"]), []).append(q["id"])
            self.map_by_scene = {}
            for k, m in f("maps").items():
                if m.get("scene"):
                    self.map_by_scene.setdefault(m["scene"], m["id"])
            self.monsters_on_map, self.npcs_on_scene = {}, {}
            for k, m in f("monsters").items():
                for mid in m.get("maps") or []:
                    self.monsters_on_map.setdefault(str(mid), []).append(k)
            for k, n in f("npcs").items():
                if n.get("scene"):
                    self.npcs_on_scene.setdefault(n["scene"], []).append(k)
            self.shop_items = {}
            for cname, c in (f("shops").get("server_catalogues") or {}).items():
                for e in c.get("entries") or []:
                    self.shop_items.setdefault(str(e.get("item")), []).append(
                        {"shop": cname, "title": _shop_title(cname), "price": e.get("price"),
                         "currency": e.get("currency") or c.get("currency")})
            self.stone_effect = {}
            for k, s in f("holystones").items():
                for it in s.get("stone_items") or []:
                    self.stone_effect[str(it)] = s["id"]
            self.ready = True
            return self


REFS = Refs(common.CATALOG)


_BY = {}       # type -> (index rows, {id: row}); kept here, never inside the shared index dict (it is served as JSON)


def ref(typ, id_, note=None):
    """{type, id, name, sprite, sub} of one entry for a cross-reference chip (unknown ids still listed)."""
    ix = common.CATALOG.index(typ)
    c = _BY.get(typ)
    if c is None or c[0] is not ix["rows"]:            # rebuilt when the catalogue index is rebuilt
        c = _BY[typ] = (ix["rows"], {str(r[0]): r for r in ix["rows"]})
    r = c[1].get(str(id_))
    d = {"type": typ, "id": id_, "name": (r[1] if r else "") or "%s %s" % (typ, id_), "sprite": r[5] if r else None,
         "sub": r[4] if r else "", "known": r is not None}
    if note:
        d["note"] = note
    return d


def refs(typ, ids, note=None):
    return [ref(typ, i, note) for i in ids]


# --------------------------------------------------------------------------------------------- database (read-only)
def _db(ctx, parts):
    """One read-only psql call for several named queries: {name: sql returning rows}. A query whose table is missing
    (stock server, older schema) gives an error string for that name only."""
    rb = ctx.rb
    if not rb or not parts:
        return {}, (None if rb else "no database adapter")
    expr = "json_build_object(%s)" % ", ".join(
        "%s, (SELECT coalesce(json_agg(to_jsonb(t)), '[]'::json) FROM (%s) t)" % (lit(k), sql) for k, sql in parts.items())
    try:
        return rb.pg.json(expr, timeout=30) or {}, None
    except Exception:          # noqa: BLE001 - fall back to one query at a time so one missing table does not hide the rest
        out, errs = {}, []
        for k, sql in parts.items():
            try:
                out[k] = rb.pg.json("(SELECT coalesce(json_agg(to_jsonb(t)), '[]'::json) FROM (%s) t)" % sql, timeout=30)
            except Exception as e:          # noqa: BLE001
                out[k] = None
                errs.append("%s: %s" % (k, str(e).splitlines()[0][:160] if str(e) else type(e).__name__))
        return out, "; ".join(errs) or None


def _num(v):
    try:
        f = float(v)
        return int(f) if f.is_integer() else f
    except (TypeError, ValueError):
        return v


# --------------------------------------------------------------------------------------------- detail builders
def _skill_desc(s, statuses):
    def stat(m):
        st = statuses.get(m.group(1)) or {}
        fld = m.group(2)
        return str({"Name": st.get("name"), "Time": st.get("duration_s"), "Interval": st.get("interval_s"),
                    "Values": st.get("value")}.get(fld, m.group(0)))
    d = re.sub(r"<s(\d+),(\w+)<", stat, s.get("desc") or "")
    p1 = s.get("power1")
    if isinstance(p1, (int, float)) and p1:
        d = d.replace("{Power1}", "%g%%" % round(abs(p1) * 100, 1))
    for k in ("Power1", "Power2", "Power3"):
        d = d.replace("{%s}" % k, "%g" % (s.get(k.lower()) or 0))
    d = d.replace("{TimeInterval}", "%g" % (s.get("interval_s") or 0))
    return d


def d_item(ctx, e, sid, out, db):
    r = REFS
    cat = ctx.catalog
    labels = cat.file("labels")
    st = labels.get("stats") or {}
    # stats table: one row per stat, one column per quality
    stats = []
    for k, arr in (e.get("stats") or {}).items():
        lab = (st.get(k) or {}).get("label") if isinstance(st.get(k), dict) else st.get(k)
        stats.append({"key": k, "label": lab or k, "values": arr})
    out["stats"] = stats
    out["stats_help"] = STAT_HELP
    x = {}
    if e.get("teaches_skill") not in (None, "", -1):
        x["Teaches skill"] = [ref("skill", e["teaches_skill"])]
    if e.get("use_skill") not in (None, "", -1):
        x["Casts when used"] = [ref("skill", e["use_skill"])]
    if e.get("pet_skill") not in (None, "", -1):
        x["Teaches pet skill"] = [ref("petskill", e["pet_skill"])]
    if e.get("append_attributes"):
        x["May roll these append attributes"] = refs("appendattr", e["append_attributes"])
    fg = e.get("forge") or {}
    if fg.get("next") not in (None, "", -1, 0):
        x["Forges into"] = [ref("item", fg["next"])]
    if fg.get("fail_to") not in (None, "", -1, 0):
        x["Becomes on a failed forge"] = [ref("item", fg["fail_to"])]
    if r.forged_from.get(sid):
        x["Forged from"] = refs("item", r.forged_from[sid])
    if fg.get("related"):
        x["Same forge family"] = refs("item", [i for i in fg["related"] if str(i) != sid])
    if r.stone_effect.get(sid) is not None:
        x["Holy stone effect"] = [ref("holystone", r.stone_effect[sid])]
    if r.shop_items.get(sid):
        out["shops"] = r.shop_items[sid]
    if e.get("quest") not in (None, "", 0, -1, "0"):
        try:
            x["Quest"] = [ref("quest", int(e["quest"]))]
        except (TypeError, ValueError):
            pass
    out["refs"] = x
    if fg:
        out["forge"] = fg
    if e.get("fractions"):
        out["fractions"] = e["fractions"]
    mi = (cat.file("mounts").get("items") or {}).get(sid)
    if mi:
        out["mount"] = {k: mi.get(k) for k in ("speed_by_quality", "max_hp_by_quality", "stats", "slot_label")}
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db.update({
        "template": "SELECT id, kind, name_key, display_name, equipment_slot, class_ids, min_level, max_level, hand, skill_flag, stats "
                    "FROM item_templates WHERE id = %d" % i,
        "loot": "SELECT template_key, chance_basis_points, minimum_quantity, maximum_quantity, enabled, item_quality, item_grade "
                "FROM monster_loot_rules WHERE item_id = %d ORDER BY template_key" % i,
        "medusa_loot": "SELECT difficulty, template_alias, chance_basis_points, minimum_quantity, maximum_quantity, enabled "
                       "FROM medusa_monster_loot_rules WHERE item_id = %d ORDER BY difficulty, template_alias" % i,
        "skill_book": "SELECT skill_id, skill_level, min_level FROM skill_book_templates WHERE item_id = %d" % i,
        "material": "SELECT policy_kind, stack_cap, material, material_level, is_piece, attribute_name, target_item_id, recipe_kind, "
                    "recipe_quantity FROM item_material_content_definitions WHERE item_id = %d" % i,
        "pet_egg": "SELECT species_id, display_name FROM pet_content_species_definitions WHERE egg_item_id = %d OR magic_jade_item_id = %d" % (i, i),
        "online_award": "SELECT revision, reward_order, quantity, item_quality FROM online_award_balance_entries WHERE item_id = %d" % i,
        "owned": "SELECT count(*) AS copies, count(DISTINCT user_id) AS characters FROM character_items WHERE prop_id = %d" % i,
    })


def p_item(ctx, out, rows):
    x = out.setdefault("refs", {})
    if rows.get("loot"):
        x["Dropped by"] = [ref("monster", r["template_key"], "%g%%" % (float(r["chance_basis_points"] or 0) / 100)) for r in rows["loot"]]
    if rows.get("medusa_loot"):
        x["Medusa drop"] = [{"type": "text", "name": "difficulty %s · %s · %g%%" % (r["difficulty"], r["template_alias"],
                             float(r["chance_basis_points"] or 0) / 100)} for r in rows["medusa_loot"]]
    if rows.get("pet_egg"):
        x["Hatches / belongs to pet"] = [ref("pet", r["species_id"]) for r in rows["pet_egg"]]
    t = (rows.get("template") or [None])[0]
    if t:
        out["server_template"] = t


def d_skill(ctx, e, sid, out, db):
    r, f = REFS, ctx.catalog.file
    out["desc_filled"] = _skill_desc(e, f("statuses"))
    for a in out.get("attributes") or []:          # the attribute list shows the filled text too, not the <s331,Time< codes
        if a["key"] == "desc":
            a["value"] = out["desc_filled"]
    x = {}
    if e.get("status") not in (None, "", -1):
        x["Applies status"] = [ref("status", e["status"], ("chance %s" % e["status_chance"]) if e.get("status_chance") else None)]
    fam = [i for i in r.family.get(str(e.get("family")), []) if e.get("family") not in (None, 0, "")]
    if len(fam) > 1:
        x["Ranks of %s" % (e.get("family_name") or e.get("base") or "this skill")] = refs("skill", fam)
    if r.teach.get(sid):
        x["Taught by"] = refs("item", r.teach[sid])
    if r.use.get(sid):
        x["Cast by using"] = refs("item", r.use[sid])
    kinds = [k for k, z in (f("zodiac").get("kinds") or {}).items() if str(z.get("first_skill")) == sid or
             (fam and z.get("first_skill") in fam)]
    if kinds:
        x["Zodiac skill kind"] = refs("zodiac_kind", [int(k) for k in kinds])
    out["refs"] = x
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db["template"] = ("SELECT skill_id, display_name, base_name, skill_level, class_ids, previous_skill_id, min_level, max_level, "
                      "target, affect_obj, distance, effect_range, property, mp, power1, power2, intonate_time, cooling_time "
                      "FROM skill_templates WHERE skill_id = %d" % i)
    db["known_by"] = "SELECT count(DISTINCT user_id) AS characters FROM character_skills WHERE skill_id = %d" % i


def d_status(ctx, e, sid, out, db):
    x = {}
    if REFS.status_skills.get(sid):
        x["Applied by skills"] = refs("skill", REFS.status_skills[sid])
    rides = ctx.catalog.file("mounts").get("rides") or {}
    if sid in rides:
        x["Ride status"] = [ref("ride", int(sid))]
    if e.get("visual_skill") not in (None, -1, ""):
        x["Drawn with skill effect"] = [ref("skill", e["visual_skill"])]
    out["refs"] = x


def d_petskill(ctx, e, sid, out, db):
    vals = [v for v in str(e.get("values") or "").split(",") if v != ""]
    rest = [v for v in str(e.get("restrict") or "").split(",") if v != ""]
    out["ranks"] = [{"rank": i + 1, "value": _num(v), "pet_rank": _num(rest[i]) if i < len(rest) else None} for i, v in enumerate(vals)]
    info = str(e.get("info") or "").replace("%Values%", "/".join(vals) or "?").replace("%Restrict%", (" " + "/".join(rest)) if rest else "")
    out["desc_filled"] = info
    m = re.search(r"Available to (.+?) only", e.get("info") or "")
    out["family_only"] = m.group(1) if m else None
    x = {}
    if REFS.petteach.get(sid):
        x["Taught by"] = refs("item", REFS.petteach[sid])
    out["refs"] = x
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db["starter_of"] = "SELECT species_id, display_name FROM pet_content_species_definitions WHERE starter_skill_id = %d" % i
    db["known_by"] = "SELECT count(*) AS pets FROM character_pet_skills WHERE skill_id = %d" % i


def p_petskill(ctx, out, rows):
    if rows.get("starter_of"):
        out.setdefault("refs", {})["Starter skill of"] = [ref("pet", r["species_id"]) for r in rows["starter_of"]]


def d_talent(ctx, e, sid, out, db):
    x = {}
    if e.get("requires_talent") not in (None, -1, "") and str(e.get("requires_talent")) != sid:
        x["Needs talent first"] = [ref("talent", e["requires_talent"], "rank %s" % e.get("requires_rank") if e.get("requires_rank") else None)]
    nxt = [t["id"] for t in ctx.catalog.file("talents").values() if str(t.get("requires_talent")) == sid and str(t["id"]) != sid]
    if nxt:
        x["Opens"] = refs("talent", nxt)
    out["refs"] = x
    out["effects"] = e.get("effects") or []


def d_monster(ctx, e, sid, out, db):
    f = ctx.catalog.file
    x = {}
    if e.get("maps"):
        x["Lives on"] = refs("map", e["maps"])
    out["refs"] = x
    k = lit(sid)
    db.update({
        "templates": "SELECT source_map_id, scene_key, display_name, rank, is_boss, is_elite, attack_type, scale, stats "
                     "FROM monster_templates WHERE template_key = %s ORDER BY source_map_id" % k,
        "spawns": "SELECT map_id, count(*) AS n, round(avg(pos_x)::numeric, 0) AS x, round(avg(pos_z)::numeric, 0) AS z "
                  "FROM monster_spawn_definitions WHERE template_key = %s GROUP BY map_id ORDER BY map_id" % k,
        "loot": "SELECT item_id, chance_basis_points, minimum_quantity, maximum_quantity, enabled, item_quality, item_grade "
                "FROM monster_loot_rules WHERE template_key = %s ORDER BY loot_index" % k,
        "boss_area": "SELECT map_id, bonus_basis_points, respawn_interval_seconds, enabled FROM world_boss_areas WHERE boss_template_key = %s" % k,
        "quests": "SELECT DISTINCT quest_id FROM quest_monster_references WHERE monster_name = %s ORDER BY quest_id" % lit(e.get("name") or sid),
    })


def p_monster(ctx, out, rows):
    x = out.setdefault("refs", {})
    if rows.get("spawns"):
        x["Placed on (server spawns)"] = [ref("map", r["map_id"], "%s placed" % r["n"]) for r in rows["spawns"]]
    if rows.get("loot"):
        x["Drops"] = [ref("item", r["item_id"], "%g%%" % (float(r["chance_basis_points"] or 0) / 100)) for r in rows["loot"]]
    if rows.get("quests"):
        x["Quests that hunt it"] = refs("quest", [r["quest_id"] for r in rows["quests"]])
    st = {}
    for t in rows.get("templates") or []:
        for kk, vv in (t.get("stats") or {}).items():
            st.setdefault(kk, vv)
    if st:
        out["server_stats"] = st


def d_npc(ctx, e, sid, out, db):
    r = REFS
    x = {}
    if e.get("scene") and r.map_by_scene.get(e["scene"]) is not None:
        x["Map"] = [ref("map", r.map_by_scene[e["scene"]])]
    if r.quest_giver.get(sid):
        x["Gives quests"] = refs("quest", r.quest_giver[sid])
    if r.quest_resp.get(sid):
        x["Takes quests"] = refs("quest", r.quest_resp[sid])
    out["refs"] = x
    out["templates"] = e.get("templates") or {}
    db["spawns"] = "SELECT map_id, pos_x, pos_z, template_key FROM npc_spawn_definitions WHERE npc_key = %s ORDER BY map_id" % lit(sid)


def p_npc(ctx, out, rows):
    if rows.get("spawns"):
        out.setdefault("refs", {})["Stands on (server spawns)"] = [
            ref("map", r["map_id"], "x %s, z %s" % (round(r["pos_x"]), round(r["pos_z"]))) for r in rows["spawns"]]
        out["spawn_points"] = rows["spawns"]


def d_map(ctx, e, sid, out, db):
    r = REFS
    x = {}
    if e.get("links"):
        x["Gates to"] = [ref("map", l["to"], "gate at x %s, z %s" % (l.get("x"), l.get("z"))) for l in e["links"]]
    if r.quests_on_map.get(sid):
        x["Quests given here"] = refs("quest", r.quests_on_map[sid])
    if r.npcs_on_scene.get(e.get("scene")):
        x["NPCs of this scene"] = refs("npc", r.npcs_on_scene[e["scene"]])
    if r.monsters_on_map.get(sid):
        x["Monsters (client)"] = refs("monster", r.monsters_on_map[sid])
    out["refs"] = x
    # named places: the client's region list (group 0 = the world-map captions, 1 = the minimap list), deduplicated
    seen, places = set(), []
    for g in e.get("regions") or []:
        key = (g.get("name"), g.get("x"), g.get("z"))
        if key in seen:
            continue
        seen.add(key)
        places.append({"name": g.get("name") or "point %s" % g.get("point"), "x": g.get("x"), "z": g.get("z"),
                       "group": g.get("group"), "group_name": g.get("group_name") if not re.search(r"[一-鿿]", g.get("group_name") or "") else ""})
    out["places"] = places
    out["gates"] = e.get("links") or []
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db.update({
        "template": "SELECT map_id, scene_key, display_name, client_scene_id, map_mode, music_name FROM map_templates WHERE map_id = %d" % i,
        "monsters": "SELECT template_key, count(*) AS n, round(avg(pos_x)::numeric, 0) AS x, round(avg(pos_z)::numeric, 0) AS z "
                    "FROM monster_spawn_definitions WHERE map_id = %d GROUP BY template_key ORDER BY count(*) DESC" % i,
        "monster_points": "SELECT template_key AS k, round(pos_x::numeric, 0) AS x, round(pos_z::numeric, 0) AS z "
                          "FROM monster_spawn_definitions WHERE map_id = %d" % i,
        "npcs": "SELECT npc_key, round(pos_x::numeric, 0) AS x, round(pos_z::numeric, 0) AS z FROM npc_spawn_definitions WHERE map_id = %d" % i,
        "address": "SELECT group_index, point_index, name, round(pos_x::numeric, 0) AS x, round(pos_z::numeric, 0) AS z "
                   "FROM map_address_points WHERE map_id = %d ORDER BY group_index, point_index" % i,
        "characters": "SELECT count(*) AS n FROM character_base WHERE \"Map\" = %d" % i,
    })


def p_map(ctx, out, rows):
    x = out.setdefault("refs", {})
    if rows.get("monsters"):
        x["Monsters placed (server)"] = [ref("monster", r["template_key"], "x%s" % r["n"]) for r in rows["monsters"]]
    if rows.get("npcs"):
        x["NPCs placed (server)"] = [ref("npc", r["npc_key"]) for r in rows["npcs"]]
    out["plot"] = {"monsters": rows.get("monster_points") or [], "npcs": rows.get("npcs") or []}
    if not out.get("places") and rows.get("address"):
        out["places"] = [{"name": a["name"] or "point %s" % a["point_index"], "x": a["x"], "z": a["z"], "group": a["group_index"]}
                         for a in rows["address"]]


def d_quest(ctx, e, sid, out, db):
    x = {}
    f = ctx.catalog.file
    for fld, lab in (("giver", "Given by"), ("responder", "Hand in to")):
        if e.get(fld) and e[fld] in f("npcs"):
            x[lab] = [ref("npc", e[fld])]
    if e.get("giver_map") not in (None, ""):
        x["Where"] = [ref("map", e["giver_map"])]
    out["refs"] = x
    rw = []
    for k, v in (e.get("rewards") or {}).items():
        rw.append({"key": k, "label": {"Exp": "EXP", "TP": "Talent points", "Silver": "Silver", "Gold": "Gold",
                                       "Contribution": "Guild contribution", "Guild Fund": "Guild funds",
                                       "Guild Gold": "Guild gold"}.get(k, k), "value": _num(v)})
    out["rewards"] = rw
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db.update({
        "monsters": "SELECT monster_name, map_id, round(pos_x::numeric, 0) AS x, round(pos_z::numeric, 0) AS z, min_level, max_level "
                    "FROM quest_monster_references WHERE quest_id = %d" % i,
        "npcs": "SELECT role, npc_key, map_id FROM npc_spawn_references WHERE quest_id = %d ORDER BY role" % i,
        "reward_items": "SELECT * FROM quest_reward_slots WHERE quest_id = %d" % i,
        "taken_by": "SELECT count(*) AS characters FROM character_quests WHERE quest_id = %d" % i,
    })


def p_quest(ctx, out, rows):
    x = out.setdefault("refs", {})
    if rows.get("monsters"):
        out["hunt"] = rows["monsters"]
        x["Hunt"] = [{"type": "text", "name": "%s on %s (x %s, z %s)" % (r["monster_name"], common.CATALOG.name("map", r["map_id"]) or
                      "map %s" % r["map_id"], r["x"], r["z"])} for r in rows["monsters"]]
    if rows.get("reward_items"):
        x["Reward items (server)"] = [ref("item", r.get("item_id"), "x%s" % (r.get("quantity") or 1)) for r in rows["reward_items"] if r.get("item_id")]


def d_title(ctx, e, sid, out, db):
    out["refs"] = {}
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db["medusa"] = ("SELECT title, semantic_key, display_name, physical_attack_basis_points, magic_attack_basis_points, "
                    "physical_defense_basis_points, magic_defense_basis_points FROM medusa_reward_title_definitions WHERE client_title_id = %d" % i)
    db["shown_by"] = "SELECT count(*) AS characters FROM character_base WHERE selected_title_id = %d" % i


def d_pet(ctx, e, sid, out, db):
    out["refs"] = {}
    out["models"] = e.get("models") or {}
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db["species"] = ("SELECT display_name, food_kind, starter_skill_id, starter_skill_name, lifetime_values, egg_item_id, magic_jade_item_id "
                     "FROM pet_content_species_definitions WHERE species_id = %d AND revision = (SELECT revision FROM "
                     "pet_content_publication WHERE family = 'pets')" % i)
    db["owned"] = "SELECT count(*) AS pets FROM character_pets WHERE species_id = %d" % i


def p_pet(ctx, out, rows):
    s = (rows.get("species") or [None])[0]
    if s:
        x = out.setdefault("refs", {})
        if s.get("egg_item_id"):
            x["Egg item"] = [ref("item", s["egg_item_id"])]
        if s.get("magic_jade_item_id"):
            x["Magic jade"] = [ref("item", s["magic_jade_item_id"])]
        if s.get("starter_skill_id") is not None:
            x["Starter skill"] = [ref("petskill", s["starter_skill_id"])]
        out["species_def"] = s


def d_appendattr(ctx, e, sid, out, db):
    st = ctx.catalog.file("attributes").get(str(e.get("stat_type"))) or {}
    out["levels"] = [{"level": i + 1, "value": v} for i, v in enumerate(e.get("levels") or [])]
    out["stat_label"] = st.get("label")
    x = {}
    if REFS.aa_items.get(sid):
        x["Items that may roll it"] = refs("item", REFS.aa_items[sid])
    out["refs"] = x
    i = int(sid) if sid.lstrip("-").isdigit() else -1
    db["template"] = "SELECT id, name_key, stat_type, max_level, percent, level_values FROM item_attribute_templates WHERE id = %d" % i


def d_holystone(ctx, e, sid, out, db):
    out["refs"] = {"Stone items": refs("item", e.get("stone_items") or [])} if e.get("stone_items") else {}


def d_zodiac_kind(ctx, e, sid, out, db):
    x = {}
    if e.get("first_skill") not in (None, ""):
        s = ctx.catalog.file("skills").get(str(e["first_skill"])) or {}
        fam = REFS.family.get(str(s.get("family")), [e["first_skill"]])
        x["Skill ranks"] = refs("skill", fam)
    out["refs"] = x


def d_suit(ctx, e, sid, out, db):
    t = (ctx.catalog.file("suits").get("tiers") or {}).get(str(e.get("tier"))) or {}
    out["stages"] = t.get("stages") or []
    out["refs"] = {}


def d_building(ctx, e, sid, out, db):
    lv = e.get("levels") or {}
    out["levels"] = [dict(level=int(k), **v) for k, v in sorted(lv.items(), key=lambda kv: int(kv[0]))]
    out["refs"] = {}


def p_skill(ctx, out, rows):
    """The client names a class only for zodiac-trainable skills (50 player skills such as Stun or Holy Ward have none);
    the server's skill_templates.class_ids has it for every learnable skill."""
    t = (rows.get("template") or [None])[0]
    ids = (t or {}).get("class_ids")
    if ids:
        names = [CLASS_NAMES.get(int(c), str(c)) for c in ids]
        out["server_classes"] = names
        out["attributes"] = [a for a in out.get("attributes") or [] if a["key"] != "server_classes"] + [
            {"key": "server_classes", "label": F["skill"]["server_classes"][0], "value": ", ".join(names) if len(names) < 4 else "every class",
             "help": F["skill"]["server_classes"][1]}]


DETAIL = {"item": (d_item, p_item), "skill": (d_skill, p_skill), "status": (d_status, None), "petskill": (d_petskill, p_petskill),
          "talent": (d_talent, None), "monster": (d_monster, p_monster), "npc": (d_npc, p_npc), "map": (d_map, p_map),
          "quest": (d_quest, p_quest), "title": (d_title, None), "pet": (d_pet, p_pet), "appendattr": (d_appendattr, None),
          "holystone": (d_holystone, None), "zodiac_kind": (d_zodiac_kind, None), "suit": (d_suit, None),
          "guild_building": (d_building, None), "altar": (d_building, None)}


# --------------------------------------------------------------------------------------------- routes
@M.get("tabs")
def tabs(ctx):
    cat = ctx.catalog
    out = []
    for key, title, typ, sprite, hlp, mod, filt in TABS:
        n = None
        if typ:
            try:
                ix = cat.index(typ)
                n = ix["count"] if not filt else sum(1 for r in ix["rows"] if r[11] in MOUNT_KINDS)
            except Exception:          # noqa: BLE001
                n = None
        else:
            n = sum(len(c.get("entries") or []) for c in (cat.file("shops").get("server_catalogues") or {}).values())
        out.append({"key": key, "title": title, "type": typ, "sprite": sprite, "help": hlp, "editor": mod, "filter": filt, "count": n})
    mods = {m.name: {"title": m.title, "status": m.status, "needs_character": m.needs_character}
            for m in common.REGISTRY.values()}
    rb = ctx.rb
    return {"tabs": out, "catalog": cat.file("catalog"), "editors": mods,
            "use": {t: {"module": m, "what": w, "title": (mods.get(m) or {}).get("title"), "status": (mods.get(m) or {}).get("status")}
                    for t, (m, w) in USE.items()},
            "database": rb.db if rb else None, "writable": bool(rb and rb.writable)}


@M.get(r"detail/([a-z_]+)/(.+)")
def detail(ctx, typ, id_):
    cat = ctx.catalog
    if typ not in cat.TYPES:
        raise NotFound("no catalogue type %r" % typ)
    REFS.build()
    id_ = urllib.parse.unquote(str(id_))          # the page sends encodeURIComponent(id); the router passes the raw path
    sid = str(id_)
    try:
        e = cat.entry(typ, id_)
    except NotFound:
        # listed by the index from the server's own templates but absent from the client files (e.g. append attributes
        # 460 / 470 that reborn added): show what the index and the database know
        r = ref(typ, id_)
        if not r["known"]:
            raise
        e = {"id": int(sid) if sid.lstrip("-").isdigit() else sid, "name": r["name"], "in_client": False,
             "note": "The server has this entry; your Origin client's files do not, so the game shows no name or icon for it."}
    out = {"type": typ, "id": e.get("id", e.get("species", e.get("kind", e.get("code", id_)))), "entry": e,
           "attributes": _attrs(typ, e), "row": ref(typ, id_)}
    u = USE.get(typ)
    if u:
        m = common.REGISTRY.get(u[0])
        out["use"] = {"module": u[0], "what": u[1], "title": m.title if m else u[0], "status": m.status if m else "missing",
                      "needs_character": bool(m and m.needs_character)}
    fn = DETAIL.get(typ)
    db = {}
    if fn:
        fn[0](ctx, e, sid, out, db)
    rows, err = _db(ctx, db) if db and ctx.rb else ({}, None if ctx.rb else "no database")
    if fn and fn[1] and rows:
        fn[1](ctx, out, rows)
    out["server"] = rows
    if err:
        out["server_error"] = err
    out["refs"] = {k: v for k, v in (out.get("refs") or {}).items() if v}
    return out


@M.get("search")
def search(ctx):
    q = (ctx.q("q") or "").strip().lower()
    if len(q) < 2:
        raise Bad("type at least 2 letters")
    toks = q.split()
    out = []
    for key, title, typ, sprite, hlp, mod, filt in TABS:
        if not typ or key == "mounts":
            continue
        ix = ctx.catalog.index(typ)
        hits = []
        for r in ix["rows"]:
            s = ("%s %s %s %s %s" % (r[1], r[0], r[11] or "", r[4] or "", r[3] or "")).lower()
            if all(t in s for t in toks):
                hits.append({"id": r[0], "name": r[1], "sprite": r[5], "sub": r[4]})
        if hits:
            out.append({"tab": key, "title": title, "type": typ, "count": len(hits), "hits": hits})
    return {"q": q, "groups": out, "total": sum(g["count"] for g in out)}


@M.get("zodiac")
def zodiac(ctx):
    z = ctx.catalog.file("zodiac")
    return {k: z.get(k) for k in ("signs", "levels", "grids", "stones")}


@M.get("talents")
def talents(ctx):
    return {"talents": list(ctx.catalog.file("talents").values()), "classes": CLASS_NAMES}


@M.get("shops")
def shops(ctx):
    s = ctx.catalog.file("shops")
    cats = []
    for name, c in (s.get("server_catalogues") or {}).items():
        cur = c.get("currency") or ""
        cats.append({"key": name, "title": _shop_title(name), "kind": c.get("kind"), "currency": cur,
                     "currency_label": CURRENCY.get(cur, cur), "npcs": c.get("npcs"), "entries": c.get("entries") or [],
                     "source": c.get("source")})
    return {"catalogues": cats, "why_read_only": s.get("why_read_only"), "currencies": CURRENCY,
            "tabs": ((ctx.catalog.file("labels").get("shop_tabs") or {}).get("tabs") or {})}


@M.get("suits")
def suits(ctx):
    s = ctx.catalog.file("suits")
    return {"codes": list((s.get("codes") or {}).values()), "tiers": s.get("tiers") or {}, "point_bonuses": s.get("point_bonuses") or []}


@M.get(r"char/(\d+)")
def char_pos(ctx, cid):
    """What the 'Move a character here' window shows and sends as `expected` (read-only)."""
    rb = ctx.need_rb()
    rows = rb.pg.json("(SELECT coalesce(json_agg(to_jsonb(t)), '[]') FROM (SELECT id, name, fighter_job_lv AS level, \"Map\" AS map, "
                      "\"Pos_X\" AS x, \"Pos_Z\" AS z FROM character_base WHERE id = %d) t)" % int(cid))
    if not rows:
        raise NotFound("no character %s" % cid)
    r = rows[0]
    r["map_name"] = ctx.catalog.name("map", r["map"]) or "map %s" % r["map"]
    return r


# --------------------------------------------------------------------------------------------- give an item
# Framework workaround (reported as "framework change needed"): the shared ctx.bag_item("add") marks the slots it
# fills by APPENDING placeholder rows (id -1) to the dump it is given. Reborn.write passes the "before" dump itself,
# so the backup and the change record get a phantom character_items row and Undo of that give fails with
# 'null value in column "user_id"' (nothing is written, but the give cannot be undone). This route does the same
# offline add without touching the dump, and leaves online characters to the shared path (live on round2, else 423).
@M.post(r"give/(\d+)")
def give(ctx, cid):
    rb = ctx.need_rb()
    cid = int(cid)
    p = ctx.body or {}
    if not isinstance(p, dict):
        raise Bad("the request body must be a JSON object")
    if common.online_info(ctx.app, cid)["online"]:
        return ctx.bag_item(cid, "add", p)
    R = common.R
    pid = common.to_int(p.get("item"), 1, 2 ** 31 - 1, "item")
    qty = common.to_int(p.get("quantity", 1), 1, 9999, "quantity")
    meta = rb.item_meta(pid)
    if not meta["server"]:
        raise Bad("item %d is not an item template of this server" % pid)
    dflt = meta["defaults"]
    q = common.to_int(p.get("quality", dflt["item_quality"]), 0, 20, "quality")
    g = common.to_int(p.get("grade", dflt["item_grade"]), 0, 25, "grade")
    bound = common.to_int(p.get("bound", dflt["bound"]), 0, 1, "bound")
    ov = max(1, int(meta.get("overlap") or 1))
    stacks = [min(ov, qty - i) for i in range(0, qty, ov)]

    def build(d):
        used = {r["slot_index"] for r in d["character_items"] if r["item_location"] == R.LOC_BAG}
        free = [s for s in range(common.BAG_SLOTS) if s not in used]
        if len(stacks) > len(free):
            raise common.RebornError("the bag has %d free slots; %d needed" % (len(free), len(stacks)))
        out = []
        for slot, n in zip(free, stacks):
            b = R.op_item(rb, cid, {"location": R.LOC_BAG, "slot": slot, "item": {
                "prop_id": pid, "stack": n, "item_quality": q, "item_grade": g, "bound": bound}})
            out += [x for x in b(d) if "inventory_revision" not in x]
        return out + ["UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid]
    what = "add %s (#%d) x%d Q%d G%d%s to the bag" % (meta["name"], pid, qty, q, g, " bound" if bound else "")
    r = ctx.char_write(cid, what, build, fields=["character_items.*"], cross_bumps=False)
    r["mode"] = "database"
    return r
