"""Shared map sections and monster tiers (wave 4 package D).

One place that says which section a map belongs to (regular map, instance, event, battlefield, arena, other) and which
tier a monster template has, so the drop-tier, respawn-tier, monster-progression and stat-curve pages agree.

* Map sections are hand-classified from map_templates (server/database/postgres/008_maps.sql) and the server code that
  runs each map (instances: Medusa, Atlantis, Wonderland, Bay under Attack, Cursed Zone, Pan's Labyrinth, Hera's Labyrinth...;
  events: Treasure Land, Sicily, Lelantine Farm, Troy; battlefields: Pindus, Ni Mini, Mantinea, Parnassus).
  map_templates.map_mode does not separate them (0 or 5 on every kind), so the list is explicit. A map id the list does
  not know is "other" and is still shown.
* The drop/respawn TIER is the server's rule (Application/Npc/NpcContentOverrides.cs MonsterTierOverrides.Tier): the
  template key contains "_boss" -> boss, "_elite" -> elite, anything else -> normal. The RANK (gameplay_monster_templates
  is_boss / is_elite, or the "[Elite]"/"[E]" name prefix) can disagree, e.g. C_boss_greecewarrior_002 is named
  "[Elite]General James"; pages show both when they differ.

Private helper (leading underscore): not a module; imported by drop_tiers/respawn_tiers (via _tier_overrides),
monsters, curves.
"""
import re

SECTIONS = [
    ("regular", "Regular maps", "Open-world fields and towns, ordered by level"),
    ("instance", "Instances", "Party or solo instances: their drops are their own and stronger for the level"),
    ("event", "Events", "Timed or switched event maps: event drops"),
    ("battlefield", "Battlefields", "Faction and guild war maps"),
    ("arena", "Arenas", "Arena and duel copies (no monster loot)"),
    ("other", "Other", "GM, test and unclassified maps"),
]
SECTION_TITLE = {k: t for k, t, _ in SECTIONS}
SECTION_ORDER = {k: i for i, (k, _, _) in enumerate(SECTIONS)}

# Regular maps in the order of their level band (towns first). Level bands from Text/QuestRegion.dat "Lv.. Field"
# labels and the live spawn levels (Development/drops/live-spawns-full-20261102.csv).
REGULAR = [0, 1, 2, 4, 11, 13, 3, 5, 12, 14, 18, 19, 15, 16, 17, 8, 20, 21, 7, 9, 6, 10, 22, 69]
TOWNS = {0, 1}
INSTANCE = {24: "Cursed Zone 1", 28: "Cursed Zone 2", 29: "Cursed Zone 3", 30: "Hera's Labyrinth (Cyclades)",
            31: "Hera's Labyrinth (Dodecanese)", 40: "Pan's Labyrinth", 41: "Hemlock Island", 200: "Medusa's Island (Enhanced / Mythic)",
            204: "Medusa's Island (Normal)", 205: "Atlantis", 207: "Wonderland", 210: "The Trials of Hercules",
            26: "Epimetheus' Home", 201: "Warriors' Training Ground 1", 202: "Warriors' Training Ground 2",
            203: "Warriors' Training Ground 3", 206: "Warriors' Training Ground 4",
            # Bay under Attack is a party instance (Instance Caller, one entry a day, 30 minutes: HarborAttackContentPolicy),
            # so it is listed with the instances (2026-11-19, the admin looked for it there)
            208: "Bay under Attack 1", 209: "Bay under Attack 2"}
EVENT = {25: "Treasure Land 1", 39: "Treasure Land 2", 32: "Sicily 1", 33: "Sicily 2", 42: "Lelantine Farm",
         44: "Troy", 45: "Romance Island"}
BATTLEFIELD = {27: "Mantinea 1", 34: "Mantinea 2", 35: "Mantinea 3", 36: "Mantinea 4", 37: "Mantinea 5",
               38: "Mt. Pindus", 43: "Ni Mini", 56: "Hall of Peace", 68: "Parnassus"}
ARENA = set(range(46, 56)) | set(range(57, 68))
OTHER = {23: "Sealed Spot (GM)"}   # 201-203/206 are the Warriors' Training Ground instance (WarriorTrainingGroundPolicy.cs)


# Typical monster level of maps whose monsters the server spawns in code (no spawn rows to read a level from). Gate bands where
# the server has them (Training Grounds WarriorTrainingGroundPolicy.cs 40-50/60-70/80-90/91-120, Atlantis 90+, Pan 70+,
# Treasure Land 60+, Sicily 25+, Hera 50+), Development/drops/INSTANCE-BOSSES.csv (Wonderland 120), a GENERATED guess elsewhere.
TYPICAL_LEVEL = {24: 30, 28: 60, 29: 100, 30: 60, 31: 60, 40: 100, 41: 90, 205: 100, 207: 120, 210: 150, 26: 80,
                 25: 60, 39: 80, 32: 30, 33: 40, 42: 100, 44: 100, 45: 50, 208: 90, 209: 110,
                 201: 45, 202: 65, 203: 85, 206: 105}


def section_of(map_id):
    """'regular' | 'instance' | 'event' | 'battlefield' | 'arena' | 'other' for a map id (int or str)."""
    try:
        m = int(map_id)
    except (TypeError, ValueError):
        return "other"
    if m in REGULAR:
        return "regular"
    if m in INSTANCE:
        return "instance"
    if m in EVENT:
        return "event"
    if m in BATTLEFIELD:
        return "battlefield"
    if m in ARENA:
        return "arena"
    return "other"


def map_order(map_id):
    """Sort key: section, then level order for regular maps, then id."""
    m = int(map_id)
    s = section_of(m)
    return (SECTION_ORDER[s], REGULAR.index(m) if s == "regular" else 0, m)


def section_name(map_id):
    """The name this file gives a non-regular map (instances, events, battlefields, other), or None. These names tell apart
    maps the client catalogue names alike (208/209 "Bay under Attack", 201-203/206 "Training Ground ...")."""
    try:
        m = int(map_id)
    except (TypeError, ValueError):
        return None
    return INSTANCE.get(m) or EVENT.get(m) or BATTLEFIELD.get(m) or OTHER.get(m)


def map_info(map_id, name=None):
    m = int(map_id)
    s = section_of(m)
    fallback = (INSTANCE.get(m) or EVENT.get(m) or BATTLEFIELD.get(m) or OTHER.get(m) or
                ("Arena" if m in ARENA else None) or "Map #%d" % m)
    return {"id": m, "name": name or fallback, "section": s, "section_title": SECTION_TITLE[s],
            "town": m in TOWNS, "order": list(map_order(m))}


def server_tier(template_key):
    """The tier the server's drop/respawn overrides use (MonsterTierOverrides.Tier)."""
    k = (template_key or "").lower()
    return "boss" if "_boss" in k else "elite" if "_elite" in k else "normal"


_ELITE_NAME = re.compile(r"^\s*\[(elite|e)\]", re.I)


def rank_of(template_key, is_boss=None, is_elite=None, name=None):
    """The monster's rank as the game data marks it (DB flags first, then the name prefix, then the key)."""
    if is_boss:
        return "boss"
    if is_elite:
        return "elite"
    if is_boss is None and is_elite is None:
        if name and _ELITE_NAME.match(name):
            return "elite"
        return server_tier(template_key)
    return "normal"


def tier_note(template_key, rank):
    """Text when the server tier and the data rank disagree, else None."""
    t = server_tier(template_key)
    if t == rank:
        return None
    return "The drop/respawn tier pages treat this monster as %s (its key), the game data ranks it %s." % (t, rank)


def grouped(maps):
    """[{section, title, maps:[...]}] in section order; maps = iterable of dicts with 'id'."""
    out = {k: [] for k, _, _ in SECTIONS}
    for m in maps:
        out[section_of(m["id"])].append(m)
    res = []
    for k, title, why in SECTIONS:
        rows = sorted(out[k], key=lambda r: map_order(r["id"]))
        if rows:
            res.append({"section": k, "title": title, "why": why, "maps": rows})
    return res
