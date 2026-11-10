"""modules/gearstats.py - "4 legal stats": put four attributes that the official game allows on a piece of gear, in one click.
Page: static/modules/gearstats.js. the admin 2026-11-05 (triage B4).

Where "legal" comes from:
  * IGG's Enhancement guide, guide.php?acid=229 (2011; Wayback copy in Reference/gw.igg.com-2022-01-30/guide/
    guide.php__acid=229.html, section IV "Notes", table "Equipment Type / Added Attribute"). The table below is that
    table, slot by slot, in the guide's order and spelling. IGG marks some attributes with "+".
  * the admin's rule: "+" attributes have levels 1-5, the others a base value.
  * How the SERVER stores a level (State/GearEnhancementMaterialCatalog.cs + GearEnhancementPlanner.Operations.cs,
    stage2-on-r3 f541eed): an enhanceable Attribute Stone has a CHAIN of five attribute ids (Physical Attack 0-4,
    Physical Defense 10-14, Magical Attack 20-24, Magical Defense 30-34, Damage Absorption 100-104, Max HP 130-134,
    Max MP 140-144, HP regen 150-154, MP regen 160-164). Level N = chain id N-1 AND character_items.attribute_levelK = N;
    a Quartz Plate refuses an attribute whose id and stored level disagree ("not synchronized"). So level N is written
    exactly like that here, and later in-game enhancement keeps working.
  * Where the guide says "+" but the server has ONE id (Hit Rating 40, Dodge Rating 50, Physical / Magical Damage 80 / 90,
    Hit / Dodge Rating Buff 110 / 120), there is nothing to level: no enhancement control, and the page says why.
    Where the guide has no "+" (Attack Power, Crit Bonus, Damage Absorption ...), the first family ID is selected; its amount still scales with grade.
  * The value shown for a level is the client's ItemAppendAttribute.xml table (catalog-origin/appendattrs.json:
    levels[gear_grade-1] of the chosen id). Enhancement rank is independent of this grade table.

Writes go through the Inventory module's own set path (validation, slot rules, audit row, inventory_revision + 1,
holy-suit recompute, backup, change log, Undo). Offline characters only (the live server changes bag items, not
attributes): an online character answers 423 with the Kick hint.
"""
from . import common, inventory as INV
from .common import Bad, NotFound

M = common.module(
    "gearstats", "Gear: 4 legal stats",
    icon={"sprite": 811},
    order=21,
    status="ready",
    description="Pick a piece of gear and give it four attributes the official game allows on that slot (IGG Enhancement "
                "guide), with maximum Quartz strength for supported families and grade-correct values. One write with Undo.",
    needs_character=True,
)

# family -> (label, attribute id chain). One id = no levels on the server.
FAMILIES = {
    "patk": ("Physical Attack", [0, 1, 2, 3, 4]),
    "pdef": ("Physical Defense", [10, 11, 12, 13, 14]),
    "matk": ("Magical Attack", [20, 21, 22, 23, 24]),
    "mdef": ("Magical Defense", [30, 31, 32, 33, 34]),
    "hit": ("Hit Rating", [40]),
    "dodge": ("Dodge Rating", [50]),
    "crit": ("Crit Bonus", [60]),
    "critres": ("Crit Resistance", [70]),
    "pdmg": ("Physical Damage", [80]),
    "mdmg": ("Magical Damage", [90]),
    "absorb": ("Damage Absorption", [100, 101, 102, 103, 104]),
    "hitbuff": ("Hit Rating Buff (status success)", [110]),
    "dodgebuff": ("Dodge Rating Buff (status resistance)", [120]),
    "maxhp": ("Max HP", [130, 131, 132, 133, 134]),
    "maxmp": ("Max MP", [140, 141, 142, 143, 144]),
    "hpregen": ("HP Restoration Speed", [150, 151, 152, 153, 154]),
    "mpregen": ("MP Restoration Speed", [160, 161, 162, 163, 164]),
    "healdone": ("Healing Done (healing received)", [170]),
    "heal": ("Increases Healing", [180]),
}

# IGG acid=229 table, as printed (name as IGG spells it, family). A trailing "+" is IGG's mark.
_N = {"Attack Power": "patk", "Physical Attack+": "patk", "Defense+": "pdef", "Physical Defense+": "pdef",
      "Magical Attack+": "matk", "Magical Attack": "matk", "Magical Defense+": "mdef", "Hit Rating+": "hit", "Hit rating": "hit",
      "Dodge Rating+": "dodge", "Crit Bonus": "crit", "Crit Resistance": "critres", "Physical Damage+": "pdmg",
      "Physical Damage": "pdmg", "Magical Damage+": "mdmg", "Magical Damage": "mdmg", "Damage Absorption": "absorb",
      "Hit Rating Buff+": "hitbuff", "Dodge Rating Buff+": "dodgebuff", "Max HP+": "maxhp", "Max MP+": "maxmp",
      "HP Res Spd+": "hpregen", "HP Restoration Speed+": "hpregen", "MP Res Spd+": "mpregen", "MP Restoration Speed+": "mpregen",
      "Healing Done": "healdone", "Increases Healing": "heal"}
GUIDE = {
    "One-Hand Sword": ["Attack Power", "Hit Rating+", "Crit Bonus", "Hit Rating Buff+", "Max HP+", "HP Res Spd+", "Increases Healing"],
    "Spear": ["Attack Power", "Hit Rating+", "Crit Bonus", "Physical Damage+", "Max HP+", "HP Res Spd+", "Increases Healing"],
    "One-Hand Wand": ["Magical Attack+", "Hit Rating+", "Crit Bonus", "Hit Rating Buff+", "Max MP+", "Increases Healing", "MP Res Spd+"],
    "Two-handed Wand": ["Magical Attack+", "Hit Rating+", "Crit Bonus", "Max HP+", "Max MP+", "MP Res Spd+", "Increases Healing", "Magical Damage+"],
    "Shield": ["Defense+", "Magical Defense+", "Damage Absorption", "Dodge Rating Buff+", "Max HP+", "Max MP+", "Healing Done"],
    "Physical Helmet": ["Attack Power", "Hit Rating+", "Crit Bonus", "Physical Damage", "Hit Rating Buff+", "Max HP+", "Increases Healing"],
    "Magical Helmet": ["Magical Attack", "Hit Rating+", "Crit Bonus", "Magical Damage", "Hit Rating Buff+", "Max MP+", "Increases Healing"],
    "Helmet": ["Hit Rating+", "Crit Bonus", "Physical Damage+", "Hit Rating Buff+", "Max HP+", "Max MP+", "Increases Healing", "Magical Damage+"],
    "Physical Armor": ["Physical Defense+", "Damage Absorption", "Max HP+", "HP Restoration Speed+", "Dodge Rating+", "Dodge Rating Buff+", "Healing Done"],
    "Magical Armor": ["Magical Defense+", "Damage Absorption", "Max MP+", "MP Restoration Speed+", "Dodge Rating+", "Dodge Rating Buff+", "Healing Done"],
    "Leggings": ["Physical Defense+", "Magical Defense+", "HP Restoration Speed+", "Dodge Rating+", "Dodge Rating Buff+", "Healing Done", "Crit Resistance"],
    "Cuffs": ["Physical Defense+", "Magical Defense+", "HP Restoration Speed+", "Hit rating", "Dodge Rating Buff+", "Healing Done", "Crit Resistance"],
    "Gloves": ["Physical Attack+", "Magical Attack+", "Crit Bonus", "Physical Damage", "Magical Damage+", "Hit Rating Buff+", "Increases Healing", "Hit Rating+"],
    "Boots": ["Damage Absorption", "Max HP+", "HP Restoration Speed+", "MP Res Spd+", "Dodge Rating+", "Crit Resistance", "Healing Done"],
    "Girdle": ["Defense+", "Magical Defense+", "Crit Resistance", "Damage Absorption", "Max HP+", "Max MP+", "HP Res Spd+", "MP Res Spd+"],
    "Amulet": ["Dodge Rating+", "Crit Resistance", "Damage Absorption", "Dodge Rating Buff+", "Max MP+", "Healing Done", "Increases Healing"],
    "Ring": ["Attack Power", "Magical Attack+", "Crit Bonus", "Physical Damage+", "Magical Damage", "Hit Rating Buff+", "Max HP+", "Increases Healing"],
}
SOURCE = "IGG Enhancement guide, guide.php?acid=229 (2011), section IV table"
MAX_STATS = 4


def _item(ctx, item_id):
    """The catalogue entry, or {} for an id the client catalogue does not have (never raises)."""
    try:
        return ctx.catalog.entry("item", int(item_id)) or {}
    except Exception:          # noqa: BLE001 - server-only or unknown ids
        return {}


def guide_type(entry):
    """The guide's equipment type for a catalogue item entry, or (None, reason)."""
    kind, cat = entry.get("kind"), entry.get("category")
    stats = entry.get("stats") or {}
    if kind == "weapon":
        t = {"One-Hand Sword": "One-Hand Sword", "Spear": "Spear", "One-Hand Wand": "One-Hand Wand", "Two-Hand Wand": "Two-handed Wand"}.get(cat)
        return (t, None) if t else (None, "IGG's table has no %s (only one-hand sword, spear, one-hand wand and two-handed wand)" % (cat or "weapon"))
    if kind == "head":
        if "Attack" in stats:
            return "Physical Helmet", None
        if "MagicAk" in stats:
            return "Magical Helmet", None
        return "Helmet", None
    simple = {"shield": "Shield", "armor": "Physical Armor", "cloth": "Magical Armor", "leggins": "Leggings", "cuff": "Cuffs",
              "glove": "Gloves", "shoes": "Boots", "girdle": "Girdle", "amulet": "Amulet", "ring": "Ring"}
    if kind in simple:
        return simple[kind], None
    return None, "IGG's enhancement table does not cover %s (fashion, mounts and other items cannot be enhanced)" % (cat or kind or "this item")


def options(ctx, entry, grade=1):
    """The legal stats for this item: [{key, name, family, plus, levels: [{level, id, value}], note, in_client}]."""
    t, why = guide_type(entry)
    if not t:
        return t, why, []
    grade = common.to_int(grade, 0, 25, "gear grade")
    attrs = ctx.catalog.file("appendattrs")
    client = set(entry.get("append_attributes") or [])
    out = []
    for name in GUIDE[t]:
        fam = _N[name]
        label, chain = FAMILIES[fam]
        plus = name.endswith("+")
        if plus and len(chain) == 5:
            lv = [(n, chain[n - 1]) for n in range(1, 6)]
            note = "Levels 1-5: level N is attribute #%d+N-1 with stored level N, exactly what Quartz Plates make in game." % chain[0]
        else:
            lv = [(1, chain[0])]
            note = ("IGG marks it '+', but the server has one attribute id for it (#%d) and Quartz Plates cannot raise it: "
                    "no enhancement level." % chain[0]) if plus else "No Quartz enhancement chosen; amount still scales with gear grade."
        levels = []
        for n, aid in lv:
            a = attrs.get(str(aid)) or {}
            vals = a.get("levels") or []
            levels.append({"level": n, "id": aid, "value": vals[min(max(1, grade),len(vals)) - 1] if vals else None, "text": a.get("text"),
                           "percent": bool(a.get("flag"))})
        out.append({"key": fam, "name": name, "label": label, "plus": plus, "levels": levels, "note": note,
                    "in_client": bool(client & {x[1] for x in lv}) if client else None, "grade": grade})
    return t, None, out


def _wearables(ctx, d):
    rows = []
    for r in INV._items(d):
        if r["item_location"] not in (INV.LOC_EQUIP, INV.LOC_BAG, INV.LOC_WH):
            continue
        e = _item(ctx, r["prop_id"])
        if e.get("slot") in (None, -1):
            continue
        t, why = guide_type(e)
        rows.append({"location": r["item_location"], "slot": r["slot_index"], "item": r["prop_id"], "name": e.get("name") or "item %s" % r["prop_id"],
                     "sprite": e.get("sprite"), "guide_type": t, "why_not": why, "row": {k: r.get(k) for k in INV.ITEM_FIELDS + ["id"]}})
    return rows


@M.get(r"gear/(\d+)")
def gear(ctx, cid):
    rb, cid, d = INV._cid_check(ctx, cid)
    b = d["character_base"][0]
    return {"character": {"id": cid, "name": b["name"], "level": b["fighter_job_lv"], "class": common.CLASS_NAMES.get(b["profession"])},
            "items": _wearables(ctx, d), "online": common.online_info(ctx.app, cid), "source": SOURCE, "writable": bool(rb.writable)}


@M.get(r"options/(\d+)")
def opts(ctx, item):
    e = _item(ctx, item)
    if not e:
        raise NotFound("no item %s in the catalogue" % item)
    t, why, out = options(ctx, e, ctx.qint("grade", 1, 0, 25))
    return {"item": int(item), "name": e.get("name"), "guide_type": t, "why_not": why, "options": out, "source": SOURCE, "max": MAX_STATS}


@M.post(r"apply/(\d+)")
def apply(ctx, cid):
    """POST {location, slot, stats: [{key, level}] (1-4, different families), expected: <the row the page showed>}"""
    rb, cid, d = INV._cid_check(ctx, cid)
    body = ctx.body or {}
    loc, slot = INV._place(body, "cell")
    INV._place_rules(d, loc, slot)
    old = INV._find(d, loc, slot)
    if not old:
        raise Bad("%s is empty" % INV.R.slot_label(loc, slot))
    if INV._sealed(d, old):
        raise Bad("that item is a packed (sealed) pet")
    e = _item(ctx, old["prop_id"])
    t, why, legal = options(ctx, e, old["item_grade"])
    if not t:
        raise Bad("%s: %s" % (e.get("name") or old["prop_id"], why))
    picks = body.get("stats")
    if not isinstance(picks, list) or not 1 <= len(picks) <= MAX_STATS:
        raise Bad("choose 1-%d stats" % MAX_STATS)
    by = {o["key"]: o for o in legal}
    item = {k: old.get(k) for k in INV.ITEM_FIELDS}
    seen, desc = set(), []
    for i in range(5):
        item["attribute%d" % (i + 1)] = None
        item["attribute_level%d" % (i + 1)] = None
    for i, p in enumerate(picks):
        k = str((p or {}).get("key"))
        if k not in by:
            raise Bad("stat %d (%s) is not allowed on %s (%s)" % (i + 1, k, t, SOURCE))
        if k in seen:
            raise Bad("%s is chosen twice (the game refuses two attributes of one stone family)" % by[k]["label"])
        seen.add(k)
        lvls = {x["level"]: x for x in by[k]["levels"]}
        n = common.to_int(p.get("level", 1), 1, 5, "level of " + by[k]["label"])
        if n not in lvls:
            raise Bad("%s on %s has level 1 only: %s" % (by[k]["name"], t, by[k]["note"]))
        item["attribute%d" % (i + 1)] = lvls[n]["id"]
        item["attribute_level%d" % (i + 1)] = n if len(by[k]["levels"]) > 1 else None
        desc.append(by[k]["label"] + (" Lv%d" % n if len(by[k]["levels"]) > 1 else ""))
    if online := common.online_info(ctx.app, cid):
        if online.get("online"):
            INV._online_refuse(ctx, online, "changing a gear's attributes")
    pid, meta_, warns = INV._check_item(rb, d, loc, slot, item, False, False)
    exp = INV._expected(cid, loc, slot, body.get("expected")) if "expected" in body else None
    what = "4 legal stats on %s in %s: %s" % (INV._label(rb, pid), INV.R.slot_label(loc, slot), ", ".join(desc))
    r = INV._write(ctx, cid, what, INV._build_set(rb, cid, loc, slot, item, False), [exp])
    r["warnings"] = warns
    return r
