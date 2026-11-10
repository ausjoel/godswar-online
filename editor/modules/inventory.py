"""modules/inventory.py - the "Inventory" editor: the bag window (Equip / Fashion / Mount tabs), bags I-IV, the
warehouse (SB-1..SB-9), Holy Boxes and the gear editor (quality, grade, append attributes, class / elemental
attributes, holy sockets, holy suit, item EXP). Contract: ../MODULES.md. Front end: static/modules/inventory.js.

Where the rules come from (reborn source, commit b85a1244; S/ = src/Godswar.Server/):
  * rows: public.character_items, one row per (user_id, item_location, slot_index) - unique index; location 0 equipment
    (0..12, 15..20), 1 kit bag (0..95 shown), 2 server temp (never written), 3 warehouse (0..warehouse_capacity-1).
  * the writes reuse reborn.py's tested builders (op_item / op_move: slot / kind / mount rules, stack <= Overlap,
    audit row, inventory_revision + 1, holy-suit recompute, row id kept on move / swap) inside ctx.char_write
    (db guard, FOR UPDATE OF c, a, online re-check, economy-baseline guard, backup, change log, Undo).
  * client limits: the Q20 client draws qualities 1-20 and grades 1-25 (items.json max_quality / max_grade),
    4 holy sockets (S/Packets/PacketBuilder.cs:102 NativeClientHolyStoneSocketCount = 4); the database allows
    Q0-20, G0-25 and 6 sockets. Above the client limit needs "override".
  * holy sockets: effect id 1..24, stone level 1..10 (sent as effect*100 + level-1,
    PacketBuilder.ItemSerialization.cs HolyStoneEffectCode), value = the rolled effectiveness, range
    grade-1 min..max x level (S/Domain/Inventory/HolySpiritImplementationPolicy.cs); empty value = the legacy fixed
    value (S/State/HolySpiritLegacyEffectiveness.cs). Drilling rules (S/State/HolyStoneDrillEligibilityPolicy.cs):
    socket 1 needs item level 100, socket 2 level 120, sockets 3-4 level 140 + Socket Spell III / IV.
  * class / elemental attributes: only on the class-suit items in the CHECK of character_items; elemental ids
    480 + element x 3 + family (S/State/ElementalAttributeCatalog.cs), two of different elements, grade 1..25.
  * Holy Boxes 9020-9024 store EXP in item_exp (docs/holy-suit-system.md).
  * live (round 2): only kit-bag add / remove / move exist (MODULES.md s.4). Everything else on a logged-in
    character is refused with the reason; the shared helpers ctx.bag_item / ctx.char_write decide. A live add carries
    no append attribute (id 0 = Physical Attack counts as one), so an add with any attribute needs a logout.
  * mount rules (S/State/EquipmentEligibility.cs), enforced for set, delete and swap alike (verify 2026-10-28): the
    riding slot cannot be emptied while mount gear is equipped, and a mount below the level of the equipped gear is
    refused. Class / level / max-level mismatches of equipment are WARNINGS: the game would refuse the equip, but the
    server loads such rows (verified with a headless login), so an admin may do it on purpose.
  * swap parking cell: an interrupted swap (or a partial Undo of its three steps) can leave a row in kit-bag slot
    32767; the page lists it under the bags and it can be moved to any EMPTY cell (move route, from = 1/32767).
"""
import re

from . import common, _attribute_families as AF
from .common import Bad, Refused, NotFound, Conflict, OnlineRefused, R, RebornError

M = common.module(
    "inventory", "Inventory",
    icon={"sprite": 684},          # catalog-icons.png tile
    order=20,
    status="ready",
    description='The bag window (B): Equip / Fashion / Mount tabs, bags I-IV, the warehouse, Holy Boxes, and the gear '
                'editor (quality, grade, append attributes, holy sockets, holy suit). Drag items between cells; add any item.',
    needs_character=True,
)

LOC_EQUIP, LOC_BAG, LOC_TEMP, LOC_WH = 0, 1, 2, 3
BAG_SLOTS, BAG_PAGE, WH_BOX, WH_BOXES = 96, 24, 40, 9
CLIENT_Q, CLIENT_G, CLIENT_SOCKETS, DB_SOCKETS = 20, 25, 4, 6
CLIENT_ATTR_LEVEL = 25                                      # ItemAppendAttribute.xml L1..L25 (GodsWar-2.51/Q20-Client)
STORAGE_KEY = 4102
SOCKET_SPELLS = {3: 4272, 4: 4273}
SOCKET_LEVEL = {1: 100, 2: 120, 3: 140, 4: 140}           # HolyStoneDrillEligibilityPolicy.cs
HOLY_BOXES = {9020: 100000, 9021: 1000000, 9022: 10000000, 9023: 100000000, 9024: 400000000}
GEAR_EXP_MAX = 2000000000                                   # docs/holy-suit-system.md
I32 = 2 ** 31 - 1

# equipment slots: name, the item kinds that fit (reborn.KIND_SLOT), the bag-window tab and the cell position
# (Origin ItemBagsExUI.xml, UI-STYLE.md s.6.2; x / y inside the tab area)
SLOTS = {
    0: ("Head", ["head"], "equip", 123, 8), 1: ("Amulet", ["amulet"], "equip", 204, 8),
    2: ("Gloves", ["glove"], "equip", 42, 92), 3: ("Armor", ["armor", "cloth"], "equip", 123, 50),
    4: ("Cuffs", ["cuff"], "equip", 42, 50), 5: ("Girdle", ["girdle"], "equip", 123, 92),
    6: ("Shoes", ["shoes"], "equip", 123, 176), 7: ("Leggings", ["leggins"], "equip", 123, 134),
    8: ("Ring", ["ring"], "equip", 204, 50), 9: ("Ring", ["ring"], "equip", 204, 92),
    10: ("Weapon", ["weapon"], "equip", 42, 166), 11: ("Shield", ["shield"], "equip", 205, 166),
    12: ("Fashion", ["stylish"], "fashion", 123, 8),
    20: ("Mount", ["mount"], "mount", 123, 30),
    15: ("Mount Coronet", ["mounthead"], "mount", 19, 140), 16: ("Mount Armor", ["mountarmor"], "mount", 71, 140),
    17: ("Mount Soul", ["mountsoul"], "mount", 123, 140), 18: ("Mount Saddle", ["mountornament"], "mount", 175, 140),
    19: ("Mount Tassels", ["mountamulet"], "mount", 227, 140),
}
# holy spirits: effect -> (value kind, grade-1 minimum, grade-1 maximum, stone family)
# S/Domain/Inventory/HolySpiritImplementationPolicy.cs (9060-9093); "percent" = hundredths of a percent
SPIRITS = {1: ("percent", 32, 80, "Heated"), 2: ("percent", 32, 80, "Heated"), 5: ("flat", 16, 40, "Heated"),
           6: ("flat", 12, 30, "Heated"), 7: ("percent", 24, 60, "Heated"), 8: ("flat", 40, 100, "Heated"),
           3: ("percent", 20, 50, "Heated"), 4: ("percent", 24, 60, "Heated"),
           9: ("percent", 22, 80, "Cooled"), 10: ("percent", 22, 80, "Cooled"), 11: ("flat", 16, 40, "Cooled"),
           12: ("flat", 14, 35, "Cooled"), 19: ("percent", 16, 40, "Cooled"), 20: ("flat", 16, 40, "Cooled"),
           13: ("percent", 28, 70, "Cooled"), 14: ("flat", 40, 100, "Cooled"),
           21: ("percent", 15, 30, "Zephyr"), 22: ("percent", 10, 20, "Zephyr"), 23: ("percent", 100, 200, "Zephyr"),
           24: ("percent", 75, 150, "Zephyr")}
# the fixed value a socket without a stored roll shows (S/State/HolySpiritLegacyEffectiveness.cs)
_LEG = {"ph": [110, 170, 240, 320, 410, 500, 650, 850, 1100, 1400], "pm": [80, 120, 170, 230, 300, 370, 500, 700, 950, 1200],
        "fh": [120, 190, 280, 380, 500, 620, 850, 1200, 1650, 2200], "fc": [150, 240, 340, 460, 590, 720, 950, 1300, 1800, 2400],
        "fl": [60, 90, 130, 170, 210, 250, 350, 500, 700, 950]}
LEGACY = {}
for _e in (1, 2, 3, 4):
    LEGACY[_e] = _LEG["ph"]
for _e in (5, 6):
    LEGACY[_e] = _LEG["fh"]
for _e in (7, 9, 10, 13, 15, 17, 19):
    LEGACY[_e] = _LEG["pm"]
LEGACY[8] = _LEG["fc"]
for _e in (11, 12, 14, 16, 18, 20):
    LEGACY[_e] = _LEG["fl"]
LEGACY_KIND = {e: ("percent" if v in (_LEG["ph"], _LEG["pm"]) else "flat") for e, v in LEGACY.items()}
# elemental attributes 480..500 (S/State/ElementalAttributeCatalog.cs): 480 + element * 3 + family
ELEMENTS = [("Fire", "Burn", "Damage over time", "Damage resistance", "On-hit chance"),
            ("Water", "Drench", "Movement slow", "Slow resistance", "Slow chance"),
            ("Lightning", "Shock", "Paralyze duration", "Paralyze resistance", "Paralyze chance"),
            ("Earth", "Fracture", "Defense reduction", "Defense-break resistance", "Defense-break chance"),
            ("Wind", "Gale", "Movement speed", "Slow resistance", "Movement activation chance"),
            ("Light", "Dazzle", "Accuracy reduction", "Accuracy-loss resistance", "Accuracy-reduction chance"),
            ("Dark", "Wither", "Healing reduction", "Healing-reduction resistance", "Healing-suppression chance")]
FAMILIES = ["Power", "Resistance", "Penetration"]
SLOT_FAMILY = {10: 0, 1: 1, 3: 1, 4: 1, 5: 1, 6: 1, 7: 1, 11: 1, 0: 2, 2: 2, 8: 2, 9: 2}
CLASS_ATTRS = [200, 201, 210, 211, 220, 221, 230, 231]
ITEM_FIELDS = list(R.ITEM_FIELDS) if R else []
CMP_FIELDS = ["id"] + ITEM_FIELDS


def elemental_list():
    out = []
    for e, (el, eff, pot, res, chance) in enumerate(ELEMENTS):
        for f in range(3):
            out.append({"id": 480 + e * 3 + f, "element": el, "effect": eff, "family": FAMILIES[f],
                        "name": "[%s] %s" % (eff, (pot, res, chance)[f]), "per_grade_bp": 20 if f == 2 else 40})
    return out


_SUIT_IDS = None


def class_suit_items():
    """Item ids the CHECK of character_items allows class / elemental attributes on (schema/glossary.json)."""
    global _SUIT_IDS
    if _SUIT_IDS is None:
        ids = set()
        for chk in (common.GLOSSARY.table("character_items") or {}).get("table_checks") or []:
            if "class_attribute1 IS NULL) OR (prop_id = ANY" in chk:
                ids |= {int(x) for x in re.findall(r"\d+", chk.split("ARRAY[", 1)[1].split("]", 1)[0])}
        _SUIT_IDS = sorted(ids)
    return _SUIT_IDS


def _suit_codes():
    return {int(k) for k in (common.CATALOG.file("suits").get("codes") or {})}


def _row_fields(row):
    return {k: (row or {}).get(k) for k in CMP_FIELDS}


def _items(d):
    return d.get("character_items") or []


def _find(d, loc, slot):
    for r in _items(d):
        if r["item_location"] == loc and r["slot_index"] == slot:
            return r
    return None


def _cid_check(ctx, cid):
    rb = ctx.need_rb()
    cid = int(cid)
    d = rb.dump(cid)
    if not d.get("character_base"):
        raise NotFound("no character #%d" % cid)
    return rb, cid, d


def _where(cid, loc, slot):
    return {"user_id": cid, "item_location": loc, "slot_index": slot}


def _expected(cid, loc, slot, shown):
    """The row the page showed at (loc, slot): its values, or None = the cell was empty (409 if it is not)."""
    if shown is None:
        return {"table": "character_items", "where": _where(cid, loc, slot), "values": None}
    if not isinstance(shown, dict):
        raise Bad("expected must be the item row the page showed, or null for an empty cell")
    return {"table": "character_items", "where": _where(cid, loc, slot),
            "values": {k: shown.get(k) for k in CMP_FIELDS if k in shown}}


def _place(p, what):
    if not isinstance(p, dict):
        raise Bad("%s must be {location, slot}" % what)
    loc = common.to_int(p.get("location"), 0, 3, what + " location")
    slot = common.to_int(p.get("slot"), -32768, 32767, what + " slot")
    return loc, slot


def _place_rules(d, loc, slot):
    """Refuse cells the editor never writes, with the reason (403)."""
    b = d["character_base"][0]
    if loc == LOC_TEMP:
        raise Refused("server temp slots (location 2) belong to the game server's own bookkeeping (items in transit, "
                      "S/State/PostgresGameStore.Inventory.Projection.cs); they are never written by the editor")
    if loc == LOC_EQUIP and slot not in SLOTS:
        raise Bad("equipment slot %d is not one the server uses (0-12, 15-20)" % slot)
    if loc == LOC_BAG and not 0 <= slot < BAG_SLOTS:
        raise Bad("bag slots are 0-95 (bags I-IV, 24 each)")
    if loc == LOC_WH:
        cap = int(b.get("warehouse_capacity") or WH_BOX)
        if not 0 <= slot < WH_BOX * WH_BOXES:
            raise Bad("warehouse slots are 0-359 (SB-1..SB-9, 40 each)")
        if slot >= cap:
            raise Refused("SB-%d is locked: this character's warehouse holds %d cells (%d boxes). warehouse_capacity is "
                          "read-only - the game only raises it with Storage Box Keys at the Warehouse Manager, which "
                          "writes a settlement row (trigger require_warehouse_capacity_evidence)" % (slot // WH_BOX + 1, cap, cap // WH_BOX))


def _sealed(d, row):
    return bool(row) and row["id"] in {s["item_instance_id"] for s in d.get("sealed_pet_items") or []}


def _int_or_none(v, lo, hi, what):
    if v in (None, "", "-1", -1):
        return None
    return common.to_int(v, lo, hi, what)


def _check_item(rb, d, loc, slot, item, override, force=False):
    """Checks reborn.py does not do (client limits, sockets, class / elemental attributes). Hard errors raise Bad;
    limits the client cannot show raise Bad unless override. Returns (clean item, warnings)."""
    pid = common.to_int(item.get("prop_id"), 1, I32, "item")
    meta = rb.item_meta(pid)
    cli = common.CATALOG.file("items").get(str(pid)) or {}
    warn, over = [], []
    q = common.to_int(item.get("item_quality", meta["defaults"]["item_quality"]), 0, 20, "quality")
    g = common.to_int(item.get("item_grade", meta["defaults"]["item_grade"]), 0, 25, "grade")
    egg=meta.get("pet_egg")
    if egg and q not in egg["aptitudes"]:
        raise Bad("Pet egg quality stores native aptitude, not equipment colour. Choose a supported pet aptitude (Brave = 10 in this client).")
    mq, mg = int(cli.get("max_quality") or CLIENT_Q), int(cli.get("max_grade") or CLIENT_G)
    if meta["slot"] >= 0 or meta["kind"] in R.KIND_SLOT:
        if q > max(mq, CLIENT_Q):
            over.append("quality Q%d is above what your client shows (Q%d)" % (q, max(mq, CLIENT_Q)))
        if g > max(mg, CLIENT_G):
            over.append("grade %d is above what your client shows (G%d)" % (g, max(mg, CLIENT_G)))
    stack = common.to_int(item.get("stack", 1), 1, 32767, "count")
    if stack > 255:
        over.append("count %d: the client shows a stack count only up to 255 (one byte on the wire)" % stack)
    exp = common.to_int(item.get("item_exp", 0), 0, I32, "item EXP")
    if pid in HOLY_BOXES and exp > HOLY_BOXES[pid]:
        warn.append("%s normally holds %s EXP at most (more only when the Holy Box capacity rate is raised)" % (meta["name"], format(HOLY_BOXES[pid], ",")))
    if pid not in HOLY_BOXES and exp > GEAR_EXP_MAX:
        warn.append("gear EXP above 2,000,000,000 is refused by the Vestment Forger's transfer")
    code = common.to_int(item.get("holy_suit_code", 0), 0, 32767, "holy suit")
    if code and code not in _suit_codes():
        over.append("holy suit code %d is not a tier/level the client knows (tier x 100 + level)" % code)
    # append attributes: never the dedicated class (200-231) / elemental (480-500) ids
    cat = rb.cat()["attributes"]
    seen = set()
    for i in range(1, 6):
        a = _int_or_none(item.get("attribute%d" % i), 0, 32767, "attribute %d" % i)
        if a is None:
            continue
        if a in CLASS_ATTRS or 480 <= a <= 500:
            raise Bad("attribute %d is a dedicated class / elemental attribute: put it in the class / elemental rows "
                      "(the database CHECK refuses it in append rows)" % a)
        if str(a) not in cat:
            raise Bad("append attribute %d is not in the server's item_attribute_templates" % a)
        if a in seen:
            warn.append("append attribute %d appears twice (the game adds both)" % a)
        seen.add(a)
        lv = item.get("attribute_level%d" % i)
        mx = int((cat.get(str(a)) or {}).get("max") or 1)
        if lv not in (None, "") and common.to_int(lv, 1, 32767, "attribute %d level" % i) > mx:
            raise Bad("attribute %d level %s is above its max level %d" % (a, lv, mx))
        if lv not in (None, "") and int(lv) > CLIENT_ATTR_LEVEL:
            over.append("attribute %d level %s is above what your client has (L%d)" % (a, lv, CLIENT_ATTR_LEVEL))
        allowed = cli.get("append_attributes") or []
        if allowed and not set(AF.policy(a)["chain"]) & set(allowed):
            warn.append("%s cannot roll append attribute %d in game (the client lists %d it can roll)" % (meta["name"], a, len(allowed)))
    # class / elemental attributes (CHECK: only the class-suit items, grade 1..25)
    ca = _int_or_none(item.get("class_attribute1"), 1, 32767, "class attribute")
    e1 = _int_or_none(item.get("elemental_attribute1"), 1, 32767, "elemental attribute 1")
    e2 = _int_or_none(item.get("elemental_attribute2"), 1, 32767, "elemental attribute 2")
    if ca is not None or e1 is not None or e2 is not None:
        if pid not in class_suit_items():
            raise Bad("%s is not a class-suit item: only the %d class-suit pieces may carry class / elemental attributes "
                      "(database CHECK)" % (meta["name"], len(class_suit_items())))
        if not 1 <= g <= 25:
            raise Bad("class / elemental attributes need grade 1-25 (database CHECK)")
    if ca is not None and ca not in CLASS_ATTRS:
        raise Bad("class attribute must be one of %s" % ", ".join(map(str, CLASS_ATTRS)))
    if ca is not None and not AF.class_allowed(ca, d["character_base"][0].get("profession")):
        raise Bad("that class-suit stone does not match the character profession")
    for e in (e1, e2):
        if e is not None and not 480 <= e <= 500:
            raise Bad("elemental attributes are 480-500")
    if e2 is not None and e1 is None:
        raise Bad("set elemental attribute 1 before 2 (database CHECK)")
    if e1 is not None and e2 is not None and (e1 - 480) // 3 == (e2 - 480) // 3:
        raise Bad("the two elemental attributes must be of different elements (database CHECK)")
    if loc == LOC_EQUIP and slot in SLOT_FAMILY:
        for e in (e1, e2):
            if e is not None and (e - 480) % 3 != SLOT_FAMILY[slot]:
                warn.append("elemental attribute %d is a %s attribute; the %s slot counts only %s ones" % (
                    e, FAMILIES[(e - 480) % 3], SLOTS[slot][0], FAMILIES[SLOT_FAMILY[slot]]))
    # holy sockets
    n = common.to_int(item.get("holy_socket_count", 0), 0, DB_SOCKETS, "socket count")
    if n > CLIENT_SOCKETS:
        over.append("%d sockets: the client shows 4 (sockets 5-6 are stored but invisible and the game never drills them)" % n)
    lvl = meta.get("minlv") or 0
    for i in range(1, n + 1):
        if lvl < SOCKET_LEVEL.get(i, 140):
            warn.append("socket %d: in game only items of level %d+ can be drilled (this one is level %s)" % (i, SOCKET_LEVEL.get(i, 140), lvl or 0))
            break
    bal = _balance(rb)
    for i in range(1, DB_SOCKETS + 1):
        eff = _int_or_none(item.get("holy_socket%d_effect_id" % i), 0, 32767, "socket %d effect" % i)
        if eff == 0:
            eff = None
            item["holy_socket%d_effect_id" % i] = None
        lv = _int_or_none(item.get("holy_socket%d_level" % i), 0, 32767, "socket %d level" % i)
        val = _int_or_none(item.get("holy_socket%d_value" % i), 1, 32767, "socket %d value" % i) if i <= 4 else None
        if i > 4 and item.get("holy_socket%d_value" % i) not in (None, ""):
            raise Bad("sockets 5 and 6 have no value column")
        if eff is None:
            if val is not None:
                raise Bad("socket %d has a value but no stone (database CHECK: a value needs an effect and a level)" % i)
            continue
        if i > n:
            raise Bad("socket %d holds a stone but only %d sockets are open; open it first" % (i, n))
        if eff not in SPIRITS and eff not in LEGACY:
            raise Bad("holy stone effect %d is not one the server knows (1-24)" % eff)
        if lv is None or not 1 <= lv <= 10:
            raise Bad("socket %d: holy stone level must be 1-10" % i)
        if not common.CATALOG.file("holystones").get(str(eff)):
            warn.append("socket %d: effect %d is not in your client (it shows no name)" % (i, eff))
        if val is not None:
            lo, hi = value_range(eff, lv, bal)
            if lo is not None and not lo <= val <= hi:
                over.append("socket %d: value %d is outside what the game rolls for this stone at level %d (%d-%d)" % (i, val, lv, lo, hi))
        elif eff not in LEGACY:
            warn.append("socket %d: effect %d has no fixed default value; the client shows 0 until a value is set" % (i, eff))
    if loc == LOC_EQUIP:
        warn += equip_warnings(d, meta, cli)
        if slot == R.MOUNT_SLOT and meta["kind"] == "mount":
            gear = mount_gear_too_high(rb, d, meta)
            if gear and not force:
                raise Bad("%s (level %s) is below the equipped mount gear %s: the game needs mount level >= gear level "
                          "(EquipmentEligibility.cs). Remove or lower that gear first." % (meta["name"], meta["minlv"] or 1, gear))
    if over and not override:
        raise Bad("above the game client's limits: %s. Tick 'Allow above the client limits' to save anyway." % "; ".join(over))
    return pid, meta, warn + (["override: " + x for x in over] if over else [])


def equip_warnings(d, meta, cli=None):
    """What the game's own equip check (S/State/EquipmentEligibility.cs ValidateEquip) would refuse. The server still
    LOADS such equipment from the database (verified: a Mage staff and a level-128 helmet on a level-80 Warrior log in
    and are sent to the client), so these are notes, not refusals."""
    b = d["character_base"][0]
    out = []
    prof, lvl = b.get("profession"), int(b.get("fighter_job_lv") or 1)
    classes = meta.get("classes") or []
    if classes and prof is not None and int(prof) not in [int(c) for c in classes]:
        out.append("%s is for %s; this character is a %s - the game would not let it be equipped" % (
            meta["name"], " / ".join(common.CLASS_NAMES.get(int(c), str(c)) for c in classes), common.CLASS_NAMES.get(int(prof), prof)))
    if meta.get("minlv") and int(meta["minlv"]) > lvl:
        out.append("%s needs character level %s; this character is level %d - the game would not let it be equipped" % (meta["name"], meta["minlv"], lvl))
    mx = (cli or common.CATALOG.file("items").get(str(meta["id"])) or {}).get("maxlv")
    try:
        if mx and int(mx) < lvl:
            out.append("%s supports characters only up to level %s (this one is level %d)" % (meta["name"], mx, lvl))
    except (TypeError, ValueError):
        pass
    return out


def mount_gear_too_high(rb, d, mount_meta, ignore_ids=()):
    """The first equipped mount-gear piece whose level is above this mount's level, as 'Name (level N)', else None."""
    ml = int(mount_meta.get("minlv") or 1)
    for s in R.MOUNT_GEAR_SLOTS:
        g = R.find_item(d, LOC_EQUIP, s)
        if g and g["id"] not in ignore_ids:
            gm = rb.item_meta(g["prop_id"])
            if int(gm.get("minlv") or 1) > ml:
                return "%s (level %s)" % (gm["name"], gm["minlv"])
    return None


def _balance(rb):
    try:
        return rb.pg.json("(SELECT to_jsonb(s) FROM holy_spirit_balance_settings s ORDER BY setting_id LIMIT 1)") or {}
    except Exception:          # noqa: BLE001 - older databases lack the table
        return {}


def value_range(eff, level, bal=None):
    s = SPIRITS.get(eff)
    if not s:
        return None, None
    hi1 = s[2]
    b = bal or {}
    hi1 = {9: b.get("cooled_physical_reduction_grade_one_maximum"), 10: b.get("cooled_magic_reduction_grade_one_maximum"),
           13: b.get("cooled_critical_reduction_grade_one_maximum")}.get(eff) or hi1
    return s[1] * level, hi1 * level


def _label(rb, pid):
    return "%s (#%d)" % (rb.item_meta(pid)["name"], pid)


def _online_refuse(ctx, info, what):
    caps = ctx.caps
    if caps.get("available"):
        tail = ("The game server changes only bag items live (add, move to an empty bag cell, remove); %s needs the "
                "character logged out." % what)
    else:
        tail = "Live edits arrive with the round-2 server update; this character must be logged out."
    raise OnlineRefused("%s is online. %s" % (info["name"], tail), dict(info, missing=[what]))


def _live_ok(info, op):
    return info.get("online") and op in (info.get("live_bag") or [])


def _values(rb, p, old, force):
    """reborn.item_values with one difference: append attribute id 0 (AttackA, "Physical Attack") is a real attribute
    (item_attribute_templates.id 0; the wire uses -1 for "none"), which reborn.item_values refuses (lower bound 1)."""
    pid = common.to_int(p.get("prop_id"), 1, I32, "item id")
    meta = rb.item_meta(pid)
    if not meta["server"] and not force:
        raise Bad("item %d is not in item_templates (the server would not load it)" % pid)
    v = {"prop_id": pid, "stack": common.to_int(p.get("stack", 1), 1, 32767, "count")}
    if v["stack"] > meta["overlap"] and not force:
        raise Bad("%s stacks to %d (Overlap); a bigger count needs force" % (meta["name"], meta["overlap"]))
    dflt = old if old is not None else meta["defaults"]
    v["bound"] = common.to_int(p.get("bound", dflt["bound"]), 0, 1, "bound")
    v["item_quality"] = common.to_int(p.get("item_quality", dflt["item_quality"]), 0, 20, "quality")
    v["item_grade"] = common.to_int(p.get("item_grade", dflt["item_grade"]), 0, 25, "grade")
    v["item_exp"] = common.to_int(p.get("item_exp", 0), 0, I32, "item EXP")
    v["holy_suit_code"] = common.to_int(p.get("holy_suit_code", 0), 0, I32, "holy suit code")
    v["holy_socket_count"] = common.to_int(p.get("holy_socket_count", 0), 0, DB_SOCKETS, "socket count")
    cat = rb.cat()["attributes"]
    for i in range(1, 6):
        a = _int_or_none(p.get("attribute%d" % i), 0, 32767, "attribute %d" % i)
        if a is None:
            v["attribute%d" % i] = v["attribute_level%d" % i] = None
            continue
        if str(a) not in cat and not force:
            raise Bad("attribute %d = %d is not in item_attribute_templates" % (i, a))
        mx = int((cat.get(str(a)) or {}).get("max") or 32767)
        al = p.get("attribute_level%d" % i)
        v["attribute%d" % i] = a
        v["attribute_level%d" % i] = None if al in (None, "") else common.to_int(al, 1, 32767 if force else mx, "attribute %d level" % i)
    for k in ("class_attribute1", "elemental_attribute1", "elemental_attribute2"):
        v[k] = _int_or_none(p.get(k), 1, 32767, k)
    for i in range(1, DB_SOCKETS + 1):
        v["holy_socket%d_effect_id" % i] = _int_or_none(p.get("holy_socket%d_effect_id" % i), 0, 32767, "socket %d effect" % i)
        lv = p.get("holy_socket%d_level" % i)
        v["holy_socket%d_level" % i] = None if lv in (None, "") else common.to_int(lv, 0, 32767, "socket %d level" % i)
        if i <= 4:
            v["holy_socket%d_value" % i] = _int_or_none(p.get("holy_socket%d_value" % i), 1, 32767, "socket %d value" % i)
    return v, meta


def _build_set(rb, cid, loc, slot, item, force):
    """reborn.op_item's set path (same statements: slot / kind / mount rules via reborn.check_place, audit row,
    inventory_revision + 1, holy-suit recompute for equipment) with _values instead of reborn.item_values."""
    if force and not rb.allow_force:
        raise Bad("'force' is disabled; start the editor with --allow-force")

    def build(d):
        old = R.find_item(d, loc, slot)
        if _sealed(d, old):
            raise RebornError("that item is a packed (sealed) pet; it is managed through the pet")
        vals, meta_ = _values(rb, item, old, force)
        R.check_place(rb, d, loc, slot, meta_, force)
        stm = []
        if old:
            sets = ", ".join("%s = %s" % (R.ident(k), R.lit(x)) for k, x in vals.items())
            stm.append("UPDATE character_items SET %s, updated_at = now() WHERE id = %d" % (sets, old["id"]))
            stm.append(R._audit(cid, "update", loc, slot, vals, old))
        else:
            cols = ["user_id", "item_location", "slot_index"] + list(vals)
            stm.append("INSERT INTO character_items (%s) VALUES (%s)" % (
                ", ".join(R.ident(c) for c in cols), ", ".join(R.lit(x) for x in [cid, loc, slot] + list(vals.values()))))
            stm.append(R._audit(cid, "create", loc, slot, vals, None))
        stm.append("UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid)
        if loc == LOC_EQUIP:
            stm.append("SELECT public.recompute_character_holy_suit_points(%d)" % cid)
        return stm
    return build


PARK_SLOT = 32767        # kit-bag slot far outside the 0..95 projection: a swap parks one row there inside its transaction


def _build_move(rb, cid, fl, fs, tl, ts, force, swapstep=False):
    """reborn.op_move's checks and statements for a move into an EMPTY cell (row id kept, audit row,
    inventory_revision + 1, holy-suit recompute). reborn.op_move's own swap fails on the unique index
    ux_character_items_location_slot (it is checked per statement), so a swap is three of these (see move_item)."""
    unpark = force == "unpark"
    force = force is True
    if force and not rb.allow_force:
        raise Bad("'force' is disabled; start the editor with --allow-force")
    if (fl, fs) == (LOC_BAG, PARK_SLOT) and not unpark and not force:
        raise Bad("kit-bag slot %d is the editor's swap parking cell" % PARK_SLOT)

    def build(d):
        src, dst = R.find_item(d, fl, fs), R.find_item(d, tl, ts)
        if not src:
            raise RebornError("%s is empty" % R.slot_label(fl, fs))
        sealed = {x["item_instance_id"] for x in d.get("sealed_pet_items") or []}
        if (src["id"] in sealed or (dst and dst["id"] in sealed)) and LOC_WH in (fl, tl):
            raise RebornError("packed sealed-pet items cannot enter the warehouse")
        R.check_place(rb, d, tl, ts, rb.item_meta(src["prop_id"]), force, moving_row=src)
        if dst:
            R.check_place(rb, d, fl, fs, rb.item_meta(dst["prop_id"]), force, moving_row=dst)
        if fl == LOC_EQUIP and fs == R.MOUNT_SLOT and not force and not swapstep:
            if any(R.find_item(d, LOC_EQUIP, x) for x in R.MOUNT_GEAR_SLOTS):
                raise RebornError("remove every piece of mount gear before removing the mount (EquipmentEligibility.cs)")
        mv = "UPDATE character_items SET item_location = %d, slot_index = %d, updated_at = now() WHERE id = %d"
        stm = []
        if dst:
            raise RebornError("%s is not empty any more (it changed since the page read it)" % R.slot_label(tl, ts))
        stm.append(mv % (tl, ts, src["id"]))
        stm.append(R._audit(cid, "move", tl, ts, src, src))
        stm.append("UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid)
        if LOC_EQUIP in (fl, tl):
            stm.append("SELECT public.recompute_character_holy_suit_points(%d)" % cid)
        return stm
    return build


def _build_park(rb, cid, fl, fs, tl, ts, force):
    """Swap step 1: check the whole swap (both cells, kinds, mount rules, sealed pets), then move the source row to the
    parking cell (kit bag 32767, outside the 0..95 projection the client sees)."""
    def build(d):
        src, dst = R.find_item(d, fl, fs), R.find_item(d, tl, ts)
        if not src or not dst:
            raise RebornError("the two cells changed since the page read them")
        if R.find_item(d, LOC_BAG, PARK_SLOT):
            raise RebornError("kit-bag slot %d (the swap parking cell) is in use; move that item out first" % PARK_SLOT)
        R.check_place(rb, d, tl, ts, rb.item_meta(src["prop_id"]), force, moving_row=src)
        R.check_place(rb, d, fl, fs, rb.item_meta(dst["prop_id"]), force, moving_row=dst)
        for cell_, other in (((fl, fs), dst), ((tl, ts), src)):
            if cell_ == (LOC_EQUIP, R.MOUNT_SLOT) and not force and rb.item_meta(other["prop_id"])["kind"] == "mount":
                low = mount_gear_too_high(rb, d, rb.item_meta(other["prop_id"]))
                if low:
                    raise RebornError("%s is below the equipped mount gear %s: the game needs mount level >= gear level "
                                      "(EquipmentEligibility.cs)" % (_label(rb, other["prop_id"]), low))
            if cell_ == (LOC_EQUIP, R.MOUNT_SLOT) and not force and rb.item_meta(other["prop_id"])["kind"] != "mount" \
                    and any(R.find_item(d, LOC_EQUIP, x) for x in R.MOUNT_GEAR_SLOTS):
                raise RebornError("remove every piece of mount gear before removing the mount (EquipmentEligibility.cs)")
        return ["UPDATE character_items SET item_location = %d, slot_index = %d, updated_at = now() WHERE id = %d" % (LOC_BAG, PARK_SLOT, src["id"]),
                R._audit(cid, "move", LOC_BAG, PARK_SLOT, src, src),
                "UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid] + \
            (["SELECT public.recompute_character_holy_suit_points(%d)" % cid] if fl == LOC_EQUIP else [])
    return build


def _write(ctx, cid, action, build, exp, online_ok=False):
    r = ctx.char_write(cid, action, build, fields=["character_items.*"], expected=[e for e in exp if e] or None,
                       cross_bumps=False, online_ok=online_ok)
    r["mode"] = "database"
    return r


# the admin F-87 (2026-11-08): gear edits while the player is online, through the warehouse. The game server reads the
# warehouse from the database on every open (PostgresWarehouseSnapshotReader) and refuses a transfer whose item state
# or warehouse revision changed (PostgresWarehouseTransferCommandExecutor.State), so a warehouse cell can be written
# while the player is online: the player sees it the next time they open storage. No revision counter is bumped (the
# server holds those in memory). Equipment and bag details still need a logout.
ONLINE_WAREHOUSE_NOTE = ("written to the warehouse while the player is online: they see it the next time they open storage "
                         "(if their storage window is open now, close and reopen it)")


# =========================================================================== routes
@M.get("meta")
def meta(ctx):
    """GET /api/m/inventory/meta - slots, limits, holy spirits, attributes (server values), elemental / class lists."""
    rb = ctx.need_rb()
    attrs = {}
    try:
        for k, a in rb.attribute_catalog().items():
            attrs[k] = {"id": int(k), "key": a.get("key"), "label": a.get("label"), "stat": a.get("stat"), "max": a.get("max"),
                        "percent": a.get("percent"), "values": a.get("values"),
                        "dedicated": int(k) in CLASS_ATTRS or 480 <= int(k) <= 500,
                        "family_policy": AF.policy(int(k))}
    except RebornError:
        attrs = {}
    bal = _balance(rb)
    spirits = {}
    for e in sorted(set(SPIRITS) | set(LEGACY)):
        s = SPIRITS.get(e)
        hs = common.CATALOG.file("holystones").get(str(e)) or {}
        spirits[e] = {"effect": e, "name": hs.get("name") or "Holy Spirit effect %d" % e, "desc": hs.get("desc") or "",
                      "sprite": hs.get("sprite"), "socket_sprite": hs.get("socket_sprite"), "in_client": bool(hs),
                      "kind": s[0] if s else LEGACY_KIND.get(e), "family": s[3] if s else "legacy",
                      "ranges": [list(value_range(e, lv, bal)) for lv in range(1, 11)] if s else None,
                      "legacy": LEGACY.get(e), "spirit_item": (hs.get("stone_items") or [None])[0]}
    return {
        "slots": {k: {"slot": k, "name": v[0], "kinds": v[1], "tab": v[2], "x": v[3], "y": v[4]} for k, v in SLOTS.items()},
        "client": {"quality": CLIENT_Q, "grade": CLIENT_G, "sockets": CLIENT_SOCKETS, "attribute_level": CLIENT_ATTR_LEVEL},
        "database": {"quality": 20, "grade": 25, "sockets": DB_SOCKETS, "stack": 32767},
        "bag_slots": BAG_SLOTS, "bag_page": BAG_PAGE, "warehouse_box": WH_BOX, "warehouse_boxes": WH_BOXES,
        "storage_key": STORAGE_KEY, "socket_spells": SOCKET_SPELLS, "socket_levels": SOCKET_LEVEL,
        "holy_boxes": HOLY_BOXES, "gear_exp_max": GEAR_EXP_MAX,
        "spirits": spirits, "balance": bal, "attributes": attrs,
        "class_attributes": [dict(id=a, professions=[0,1] if a in (200,201,210,211) else [2,3], **({"label": (attrs.get(str(a)) or {}).get("label")} if attrs.get(str(a)) else {})) for a in CLASS_ATTRS],
        "elementals": elemental_list(), "slot_family": SLOT_FAMILY, "families": FAMILIES,
        "class_suit_items": class_suit_items(),
        "allow_force": bool(getattr(rb, "allow_force", False)), "writable": bool(rb.writable),
        "kind_slot": dict(R.KIND_SLOT),
        "pet_eggs": {str(sp["egg_item_id"]):R.pet_egg_info(rb.cat(),sp["egg_item_id"]) for sp in rb.cat()["species"].values() if sp.get("egg_item_id")},
        "pet_aptitudes":common.CATALOG.file("labels").get("pet_aptitudes") or {},
    }


@M.get(r"state/(\d+)")
def state(ctx, cid):
    """GET /api/m/inventory/state/<cid> - the character's items in every location, its online state and what works live."""
    rb, cid, d = _cid_check(ctx, cid)
    b = d["character_base"][0]
    sealed = {s["item_instance_id"] for s in d.get("sealed_pet_items") or []}
    items, metas = [], {}
    for r in sorted(_items(d), key=lambda r: (r["item_location"], r["slot_index"])):
        x = {k: r.get(k) for k in ["id", "item_location", "slot_index"] + ITEM_FIELDS}
        x["sealed"] = r["id"] in sealed
        items.append(x)
        if str(r["prop_id"]) not in metas:
            m = rb.item_meta(r["prop_id"])
            metas[str(r["prop_id"])] = {k: m.get(k) for k in ("name", "kind", "slot", "overlap", "minlv", "classes", "server", "client", "defaults")}
    info = common.online_info(ctx.app, cid)
    keys = sum(int(r["stack"]) for r in _items(d) if r["prop_id"] == STORAGE_KEY and r["item_location"] == LOC_BAG)
    return {
        "character": {"id": cid, "name": b["name"], "level": b["fighter_job_lv"], "profession": b["profession"],
                      "class": common.CLASS_NAMES.get(b["profession"], str(b["profession"])), "camp": b.get("camp"),
                      "warehouse_capacity": int(b.get("warehouse_capacity") or WH_BOX), "Money": b.get("Money"),
                      "Stone": b.get("Stone"), "BindingGold": b.get("BindingGold"), "inventory_revision": b.get("inventory_revision")},
        "items": items, "metas": metas, "online": info, "storage_keys": keys,
        "writable": bool(rb.writable), "database": rb.db,
    }


@M.post(r"set/(\d+)")
def set_item(ctx, cid):
    """POST /api/m/inventory/set/<cid> {location, slot, item: {prop_id, stack, ... every character_items column},
    expected: <row the page showed> | null (empty cell), override: bool, force: bool}
    Offline: one guarded write (add or change the row in that cell). Online: only a plain new bag item is given live
    (the server picks the first free bag cell); anything else is refused (423) with the reason."""
    rb, cid, d = _cid_check(ctx, cid)
    body = ctx.body
    loc, slot = _place(body, "cell")
    if body.get("item") is not None and not isinstance(body.get("item"), dict):
        raise Bad("item must be an object of character_items columns ({prop_id, stack, ...})")
    item = dict(body.get("item") or {})
    if not item:
        raise Bad("item is missing (use delete to empty a cell)")
    _place_rules(d, loc, slot)
    old = _find(d, loc, slot)
    if _sealed(d, old):
        raise Refused("that item is a packed (sealed) pet: it is managed through the pet, not edited here (sealed_pet_items)")
    force = bool(body.get("force"))
    pid, meta_, warns = _check_item(rb, d, loc, slot, item, bool(body.get("override")), force)
    item["prop_id"] = pid
    exp = _expected(cid, loc, slot, body.get("expected")) if "expected" in body else None
    info = common.online_info(ctx.app, cid)
    what = "%s %s in %s" % ("change" if old else "add", _label(rb, pid), R.slot_label(loc, slot))
    if info["online"]:
        plain = all(item.get(k) in ((None, "", -1, "-1") if k.startswith("attribute") and not k.startswith("attribute_level")
                                    else (None, "", 0)) for k in ITEM_FIELDS if k not in ("prop_id", "stack", "bound", "item_quality", "item_grade"))
        if loc == LOC_BAG and old is None and plain and not force and _live_ok(info, "character.give_item"):
            r = ctx.bag_item(cid, "add", {"item": pid, "quantity": item.get("stack", 1), "quality": item.get("item_quality"),
                                          "grade": item.get("item_grade"), "bound": item.get("bound")})
            r["warnings"] = warns + ["the game server put it in the first free bag cell (live adds cannot choose the cell)"]
            return r
        if loc == LOC_WH:
            build = _build_set(rb, cid, loc, slot, item, force)
            r = _write(ctx, cid, what + " (player online)", build, [exp], online_ok=True)
            r["warnings"] = warns + [ONLINE_WAREHOUSE_NOTE]
            r["mode"] = "warehouse-online"
            return r
        _online_refuse(ctx, info, "this edit (%s) - tip: move the item into the warehouse in game, then edit it there" % (
            "a bag item's attributes, sockets or EXP" if loc == LOC_BAG and not plain else "changing an existing item" if old
            else "equipment cells" if loc != LOC_BAG else "it"))
    build = _build_set(rb, cid, loc, slot, item, force)
    r = _write(ctx, cid, what, build, [exp])
    r["warnings"] = warns
    return r


@M.post(r"delete/(\d+)")
def delete_item(ctx, cid):
    """POST /api/m/inventory/delete/<cid> {location, slot, expected: <row the page showed>}. Online: a bag item is
    removed live (round 2); anything else needs the character logged out."""
    rb, cid, d = _cid_check(ctx, cid)
    loc, slot = _place(ctx.body, "cell")
    if loc == LOC_TEMP:
        _place_rules(d, loc, slot)          # 403: never written
    old = _find(d, loc, slot)
    if not old:
        raise Conflict("%s is empty now (it changed since the page read it)" % R.slot_label(loc, slot))
    if _sealed(d, old):
        raise Refused("that item is a packed (sealed) pet: delete the pet instead (sealed_pet_items)")
    if (loc, slot) == (LOC_EQUIP, R.MOUNT_SLOT) and any(_find(d, LOC_EQUIP, x) for x in R.MOUNT_GEAR_SLOTS):
        raise Bad("remove every piece of mount gear before removing the mount (EquipmentEligibility.cs ValidateUnequip): "
                  "gear without a mount would stay equipped")
    shown = ctx.body.get("expected")
    exp = _expected(cid, loc, slot, shown) if shown is not None else None
    info = common.online_info(ctx.app, cid)
    if info["online"]:
        if loc == LOC_BAG and _live_ok(info, "character.item_delete"):
            if exp and exp["values"] and any(str(old.get(k)) != str(v) for k, v in exp["values"].items()):
                raise Conflict("%s changed since you opened it" % R.slot_label(loc, slot))
            r = ctx.bag_item(cid, "remove", {"slot": slot})
            return r
        if loc == LOC_WH:
            build = R.op_item(rb, cid, {"location": loc, "slot": slot, "item": None})
            r = _write(ctx, cid, "delete %s from %s (player online)" % (_label(rb, old["prop_id"]), R.slot_label(loc, slot)),
                       build, [exp], online_ok=True)
            r["warnings"] = [ONLINE_WAREHOUSE_NOTE]
            r["mode"] = "warehouse-online"
            return r
        _online_refuse(ctx, info, "removing equipment")
    build = R.op_item(rb, cid, {"location": loc, "slot": slot, "item": None})
    return _write(ctx, cid, "delete %s from %s" % (_label(rb, old["prop_id"]), R.slot_label(loc, slot)), build, [exp])


@M.post(r"move/(\d+)")
def move_item(ctx, cid):
    """POST /api/m/inventory/move/<cid> {from: {location, slot}, to: {location, slot}, expected_from, expected_to,
    force}. Drag and drop: an occupied target swaps (offline). Online: bag -> empty bag cell moves live (round 2)."""
    rb, cid, d = _cid_check(ctx, cid)
    fl, fs = _place(ctx.body.get("from"), "from")
    tl, ts = _place(ctx.body.get("to"), "to")
    if (fl, fs) == (tl, ts):
        raise Bad("source and target are the same cell")
    parked = (fl, fs) == (LOC_BAG, PARK_SLOT)
    for loc, slot in (((tl, ts),) if parked else ((fl, fs), (tl, ts))):
        _place_rules(d, loc, slot)
    src, dst = _find(d, fl, fs), _find(d, tl, ts)
    if not src:
        raise Conflict("%s is empty now (it changed since the page read it)" % R.slot_label(fl, fs))
    if _sealed(d, src) and tl == LOC_WH or _sealed(d, dst) and fl == LOC_WH:
        raise Refused("packed sealed-pet items cannot enter the warehouse (trigger validate_active_sealed_pet_link)")
    exps = []
    if "expected_from" in ctx.body:
        exps.append(_expected(cid, fl, fs, ctx.body.get("expected_from")))
    if "expected_to" in ctx.body:
        exps.append(_expected(cid, tl, ts, ctx.body.get("expected_to")))
    info = common.online_info(ctx.app, cid)
    what = "move %s: %s -> %s%s" % (_label(rb, src["prop_id"]), R.slot_label(fl, fs), R.slot_label(tl, ts),
                                     (" (swap with %s)" % _label(rb, dst["prop_id"])) if dst else "")
    if info["online"]:
        if fl == tl == LOC_BAG and dst is None and _live_ok(info, "character.item_move"):
            for e in exps:
                row = _find(d, e["where"]["item_location"], e["where"]["slot_index"])
                if (e["values"] is None) != (row is None) or (row and any(str(row.get(k)) != str(v) for k, v in (e["values"] or {}).items())):
                    raise Conflict("%s changed since you opened it" % R.slot_label(e["where"]["item_location"], e["where"]["slot_index"]))
            return ctx.bag_item(cid, "move", {"from": fs, "to": ts})
        _online_refuse(ctx, info, "swaps and moves into equipment or the warehouse" if (fl, tl) != (LOC_BAG, LOC_BAG) or dst else "this move")
    force = bool(ctx.body.get("force"))
    notes = []
    if tl == LOC_EQUIP:
        notes += equip_warnings(d, rb.item_meta(src["prop_id"]))
    if dst and fl == LOC_EQUIP:
        notes += equip_warnings(d, rb.item_meta(dst["prop_id"]))
    if parked:
        if dst:
            raise Bad("move the parked item (left by an interrupted swap) into an EMPTY cell")
        r = _write(ctx, cid, what + " (out of the swap parking cell)", _build_move(rb, cid, fl, fs, tl, ts, True if force else "unpark"), exps)
        r["warnings"] = notes
        return r
    if not dst:
        r = _write(ctx, cid, what, _build_move(rb, cid, fl, fs, tl, ts, force), exps)
        r["warnings"] = notes
        return r
    # a swap = three one-row moves (park, move, unpark), each its own change: the shared Undo restores rows one
    # UPDATE at a time, and two rows trading cells collide on ux_character_items_location_slot in one Undo.
    n1, n2 = _label(rb, src["prop_id"]), _label(rb, dst["prop_id"])
    r1 = _write(ctx, cid, "swap 1/3: park %s from %s (swap with %s)" % (n1, R.slot_label(fl, fs), n2),
                _build_park(rb, cid, fl, fs, tl, ts, force), exps)
    try:
        r2 = _write(ctx, cid, "swap 2/3: move %s %s -> %s" % (n2, R.slot_label(tl, ts), R.slot_label(fl, fs)),
                    _build_move(rb, cid, tl, ts, fl, fs, force, swapstep=True), [])
        r3 = _write(ctx, cid, "swap 3/3: move %s -> %s" % (n1, R.slot_label(tl, ts)),
                    _build_move(rb, cid, LOC_BAG, PARK_SLOT, tl, ts, True if force else "unpark"), [])
    except Exception as e:      # put the parked item back so nothing is left in the parking cell
        try:
            ctx.app.reborn and _write(ctx, cid, "swap aborted: put %s back in %s" % (n1, R.slot_label(fl, fs)),
                                      _build_move(rb, cid, LOC_BAG, PARK_SLOT, fl, fs, "unpark"), [])
        except Exception:      # noqa: BLE001 - the original error is the one to show
            pass
        raise e
    r3["changes"] = [r1["change"]["id"], r2["change"]["id"], r3["change"]["id"]]
    r3["note"] = "A swap is saved as three moves (park, move, unpark); Undo them newest first."
    r3["warnings"] = notes
    return r3
