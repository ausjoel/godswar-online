"""modules/npcs.py - edit NPC shops and teleporter buttons. Backend for static/modules/npcs.js.

The game server hands over what is compiled into it (GET /npc-content on its loopback management port) and reads the
editor's rows from npc_shop_overrides / npc_teleport_overrides (hot reload, no restart). No row = the compiled content.
"""
import os
import re
import zlib

from . import buffs, common, world as W
from .common import Bad

M = common.module("npcs", "NPCs: shops & teleports", icon={"sprite": 684}, order=25, status="ready",
                  description="Every NPC. Change what shops sell and their prices, and where teleporter buttons go.")

WALK_DIR = os.path.join(common.CATALOG_DIR, "walk")
SHOP_MAX = 400
ACTIVATOR_PARTS = [("item_activators", "true", "item_id")]
MALL_ADMIN = "MallAdmin"
MALL_ENTRIES = (("MallAthens", "Mall: Athens"), ("MallSparta", "Mall: Sparta"), (MALL_ADMIN, "Mall: admin tab (accounts with editor access only)"))
INT_MAX = 2 ** 31 - 1
_walk = {}

# ---- What each shop row charges. The game server decides this in code, not from npc_shop_overrides (it has no currency
# column), and its management port does not report it, so these tables mirror the server source. They are checked
# against the C# by tests/test_npcs_currency_unit.py; keep them in step with:
#   reborn-src-v2/src/Godswar.Server/Domain/World/Content/CapitalNpcServiceProtocol.cs
#     TryGetShopCurrency(byte shopType)          frame currency byte -> balance
#     TryGetPointExchangerCurrency(uint itemId)  Point Exchanger: balance chosen BY ITEM ID
#   reborn-src-v2/src/Godswar.Server/Packets/PacketBuilder.CapitalNpcShops.cs
#     TryGetCapitalShopCatalogCurrency           Point Exchanger item table first, else the frame byte
#     BuildOverriddenCapitalShopCatalog          edited rows take the byte of their tab's FIRST compiled frame
#   reborn-src-v2/src/Godswar.Server/Packets/PacketBuilder.MallCatalog.cs  mall = Gold, admin tab = Silver
CURRENCIES = {"gold": "Gold", "bgold": "B-Gold", "silver": "Silver", "honour": "Honour", "points": "Points", "medals": "Medals"}
CURRENCY_BYTES = {2: "gold", 3: "silver", 4: "bgold", 5: "honour", 6: "points", 7: "medals"}
POINT_EXCHANGER = "PointExchanger"
POINT_EXCHANGER_ITEMS = dict(
    [(i, "honour") for i in (9940, 9941, 9942, 9943, 9944, 9945, 9946, 9947, 9948, 9949, 9963, 9991, 4223, 4233,
                             9030, 9031, 9040, 9041, 9042, 9050, 9021, 4266, 4267, 4269)] +
    [(i, "points") for i in (4213, 3931, 3962, 9950, 9951, 9952, 9953, 9954, 9955, 9956, 9957,
                             10090, 10100, 10083, 4502, 4524, 3819, 11010)] +
    [(i, "medals") for i in (9958, 9959, 14069, 4193)])


def shop_currency(service, compiled):
    """How the server picks the balance for this shop's rows: {kind, by_category, default, by_item}.
    kind 'fixed' = one balance for the whole shop; 'tab' = the tab's frame byte; 'item' = Point Exchanger item table."""
    if service.startswith("Mall"):
        cur = "silver" if service == MALL_ADMIN else "gold"
        return {"kind": "fixed", "default": cur, "by_category": {}, "by_item": {}}
    by_cat = {}
    for r in compiled:
        by_cat.setdefault(str(r.get("category")), CURRENCY_BYTES.get(r.get("currency")))
    first = CURRENCY_BYTES.get(compiled[0].get("currency")) if compiled else None
    return {"kind": "item" if service == POINT_EXCHANGER else ("fixed" if len(set(by_cat.values())) <= 1 else "tab"),
            "default": first, "by_category": by_cat,
            "by_item": {str(k): v for k, v in POINT_EXCHANGER_ITEMS.items()} if service == POINT_EXCHANGER else {}}


def row_currency(cur, item, category, edited=True, compiled_byte=None):
    """The balance one row charges. edited=False with compiled_byte = the original list's own frame byte."""
    if cur["kind"] == "item" and str(item) in cur["by_item"]:
        return cur["by_item"][str(item)]
    if cur["kind"] == "fixed" and cur["default"] and not cur["by_category"]:
        return cur["default"]
    if not edited and compiled_byte is not None:
        return CURRENCY_BYTES.get(compiled_byte)
    return cur["by_category"].get(str(category)) or cur["default"]


def _content(ctx):
    port = common.mgmt_port(ctx.app)
    if not port:
        raise Bad("no game server management port is configured for this database")
    st, doc = common._http("GET", port, "/npc-content", timeout=8.0)
    if st != 200 or not isinstance(doc, dict):
        raise Bad("the running game server does not offer NPC editing yet (HTTP %s)" % st)
    return doc


def _reload(ctx):
    """Best effort: the game server also polls every 15 seconds."""
    try:
        common._http("POST", common.mgmt_port(ctx.app), "/npc-content/reload", timeout=4.0)
    except Exception:
        pass


def _scene(ctx, map_id):
    return W._v(ctx, "(SELECT scene_key FROM map_templates WHERE map_id = %d)" % map_id)


def _plane(scene):
    if scene not in _walk:
        path = os.path.join(WALK_DIR, os.path.basename(str(scene)) + ".z")
        _walk[scene] = zlib.decompress(open(path, "rb").read()) if os.path.exists(path) else None
    return _walk[scene]


def _blocked(plane, x, z):
    if not (-256 <= x < 256 and -256 < z <= 256):
        return True
    return plane[int((256 - z) * 4) * 2048 + int((x + 256) * 4)] != 0


def clear_of_walls(plane, x, z, margin=1.5):
    return not any(_blocked(plane, x + dx, z + dz) for dx in (-margin, 0, margin) for dz in (-margin, 0, margin))


def nearest_clear(plane, x, z):
    for r in range(1, 400):
        d = r / 2
        for cx, cz in ((1, 0), (-1, 0), (0, 1), (0, -1), (.7, .7), (-.7, .7), (.7, -.7), (-.7, -.7)):
            if clear_of_walls(plane, x + d * cx, z + d * cz):
                return round(x + d * cx, 1), round(z + d * cz, 1)
    return None


def _shop_parts(service):
    return [("npc_shop_overrides", "service = %s" % common.lit(service), "position")]


def _tele_parts(npc, sub):
    return [("npc_teleport_overrides", "npc_key = %s AND sub_id = %d" % (common.lit(npc), sub), "sub_id")]


@M.get("overview")
def overview(ctx):
    doc = _content(ctx)
    cat = ctx.catalog
    maps = {int(r["map_id"]): r for r in W._q(ctx, "(SELECT coalesce(json_agg(json_build_object('map_id', map_id, 'scene', "
                                                  "scene_key, 'name', display_name) ORDER BY map_id), '[]') FROM map_templates)")}
    for m in maps.values():
        m["name"] = cat.name("map", m["map_id"]) or m["scene"]
    over = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(o) ORDER BY service, position), '[]') FROM npc_shop_overrides o)")
    tele = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(o)), '[]') FROM npc_teleport_overrides o)")
    versions = W._q(ctx, "(SELECT coalesce(json_object_agg(s, %s), '{}') FROM unnest(%s::text[]) s)" % (
        W._ver_expr([("npc_shop_overrides", "service = s", "position")]),
        common.lit("{" + ",".join(doc["shops"]) + "}")))
    port = common.mgmt_port(ctx.app)
    by_service = {}
    for row in over:
        by_service.setdefault(row["service"], []).append(row)
    shops = {}
    for name in doc["shops"]:
        st, rows = common._http("GET", port, "/npc-content/shop/" + name, timeout=8.0)
        if st != 200 or not isinstance(rows, list) or (not rows and name != MALL_ADMIN):
            continue
        cur = shop_currency(name, rows)
        for r in rows:
            r["charges"] = row_currency(cur, r.get("item"), r.get("category"), edited=False, compiled_byte=r.get("currency"))
        shops[name] = {"compiled": rows, "override": by_service.get(name), "version": versions.get(name),
                       "label": re.sub(r"(?<=[a-z])(?=[A-Z])", " ", name), "currency": cur}
    npcs = []
    for n in doc["npcs"]:
        m = maps.get(n["map"]) or {}
        npcs.append(dict(n, map_name=m.get("name") or str(n["map"]), name=cat.name("npc", n["key"]) or n["key"]))
    teles = {(t["npc_key"], t["sub_id"]): t for t in tele}
    teleports = []
    for t in doc["teleports"]:
        o = teles.get((t["npc"], t["sub"]))
        version = W._version(ctx, _tele_parts(t["npc"], t["sub"]))
        teleports.append(dict(t, override=o, version=version, target_name=(maps.get(t["target_map"]) or {}).get("name")))
    # The mall has no NPC. It appears as three entries at the top of the list.
    npcs[:0] = [{"key": key, "name": label, "map": -1, "map_name": "Mall (opened from the client's button)", "x": 0, "z": 0,
                 "shop": True, "service": key, "transporter": False}
                for key, label in MALL_ENTRIES if key in shops]
    npcs.insert(len([n for n in npcs if n["map"] < 0]), {"key": "activators", "name": "Reusable items", "map": -1,
        "map_name": "Items that cast a buff and stay in the bag", "x": 0, "z": 0, "shop": False, "transporter": False, "activators": True})
    activators = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(a) ORDER BY item_id), '[]') FROM item_activators a)")
    return {"activators": activators, "activators_version": W._version(ctx, ACTIVATOR_PARTS), "npcs": npcs, "shops": shops,
            "teleports": teleports, "maps": list(maps.values()), "revision": doc.get("revision"), "currencies": CURRENCIES}


@M.get("activators/buffs")
def activator_buffs(ctx):
    return buffs.runtime_catalogue(ctx)


@M.post("activators/save")
def activators_save(ctx):
    rows = ctx.body.get("rows")
    if not isinstance(rows, list) or len(rows) > SHOP_MAX:
        raise Bad("the list must have 0-%d items" % SHOP_MAX)
    catalogue = buffs.runtime_catalogue(ctx)
    if not catalogue.get("supported"):
        raise Bad(catalogue.get("reason") or "The running buff catalogue is unavailable.")
    known = {r["skillId"] for r in catalogue["buffs"] if r.get("beneficial", True)}
    stmts = [W._ver_guard(ACTIVATOR_PARTS, ctx.body.get("version"), "this list"), "DELETE FROM item_activators"]
    seen = set()
    for i, r in enumerate(rows):
        what = "row %d" % (i + 1)
        pid, _meta = W._item(ctx, r.get("item_id"), what + " item")
        if pid in seen:
            raise Bad("%s: this item is listed twice" % what)
        seen.add(pid)
        skill = W._int(r.get("skill_id"), 1, INT_MAX, what + " buff")
        if skill not in known:
            raise Bad("%s: choose an authored buff from the running server" % what)
        stmts.append(common.insert_sql("item_activators", {"item_id": pid, "skill_id": skill,
                     "cooldown_seconds": W._int(r.get("cooldown_seconds", 0), 0, INT_MAX, what + " cooldown")}))
    out = W._write(ctx, [common.scope("item_activators", "true", pk=["item_id"])],
                   "reusable items: %d" % len(rows), stmts, ["item_activators.*"], target="reusable items")
    _reload(ctx)
    return out


@M.post("shop/save")
def shop_save(ctx):
    b = ctx.body
    service = W._text(b.get("service"), "shop", 64, rx=r"^[A-Za-z0-9]+$")
    if service not in _content(ctx)["shops"]:
        raise Bad("%s is not a shop of this server" % service)
    rows = b.get("rows")
    if not isinstance(rows, list) or not 1 <= len(rows) <= SHOP_MAX:
        raise Bad("a shop lists 1-%d items (use reset to go back to the original list)" % SHOP_MAX)
    stmts = [W._ver_guard(_shop_parts(service), b.get("version"), "this shop"),
             "DELETE FROM npc_shop_overrides WHERE service = %s" % common.lit(service)]
    warnings = []
    st, compiled = common._http("GET", common.mgmt_port(ctx.app), "/npc-content/shop/" + service, timeout=8.0)
    compiled = compiled if st == 200 and isinstance(compiled, list) else []
    cur = shop_currency(service, compiled)
    was = {}
    for c in compiled:
        was.setdefault(c.get("item"), row_currency(cur, c.get("item"), c.get("category"), edited=False, compiled_byte=c.get("currency")))
    for i, r in enumerate(rows):
        what = "row %d" % (i + 1)
        pid, meta = W._item(ctx, r.get("item"), what + " item")
        values = {"service": service, "position": i, "item_id": pid,
                  "price": W._int(r.get("price"), 1, INT_MAX, what + " price"),
                  "category": 7 if service == MALL_ADMIN else W._int(r.get("category", 0), 0, 6 if service.startswith("Mall") else 255, what + " tab"),
                  "quality": W._int(r.get("quality", 1), 1, 20, what + " quality"),
                  "grade": W._int(r.get("grade", 1), 1, 25, what + " grade"),
                  "bound": W._int(r.get("bound", 0), 0, 1, what + " bound"),
                  "stack_marker": W._int(r.get("stack_marker", 1), 1, 255, what + " stack")}
        stmts.append(common.insert_sql("npc_shop_overrides", values))
        now = row_currency(cur, pid, values["category"])
        name = meta.get("name") or "item"
        if cur["kind"] == "item" and str(pid) not in cur["by_item"]:
            warnings.append("%s: %s #%d is not in the server's honour/points/medals list, so it charges %s %s" % (
                what, name, pid, values["price"], CURRENCIES.get(now, "the tab's default balance")))
        elif pid in was and was[pid] and now and was[pid] != now:
            warnings.append("%s: %s #%d charged %s in the original list and now charges %s" % (
                what, name, pid, CURRENCIES[was[pid]], CURRENCIES[now]))
    out = W._write(ctx, [common.scope("npc_shop_overrides", "service = %s" % common.lit(service), pk=["service", "position"])],
                   "shop %s: %d items" % (service, len(rows)), stmts, ["npc_shop_overrides.*"], target="shop " + service)
    out["warnings"] = warnings
    _reload(ctx)
    return out


@M.post("shop/reset")
def shop_reset(ctx):
    service = W._text(ctx.body.get("service"), "shop", 64, rx=r"^[A-Za-z0-9]+$")
    out = W._write(ctx, [common.scope("npc_shop_overrides", "service = %s" % common.lit(service), pk=["service", "position"])],
                   "shop %s: back to the original list" % service,
                   [W._ver_guard(_shop_parts(service), ctx.body.get("version"), "this shop"),
                    "DELETE FROM npc_shop_overrides WHERE service = %s" % common.lit(service)],
                   ["npc_shop_overrides.*"], target="shop " + service)
    _reload(ctx)
    return out


def _tele_key(b):
    return W._text(b.get("npc"), "NPC", 96, rx=r"^[A-Za-z0-9_]+$"), W._int(b.get("sub"), -1, INT_MAX, "button")


@M.post("teleport/save")
def teleport_save(ctx):
    b = ctx.body
    npc, sub = _tele_key(b)
    if not any(t["npc"] == npc and t["sub"] == sub for t in _content(ctx)["teleports"]):
        raise Bad("%s has no teleporter button %d" % (npc, sub))
    target = W._int(b.get("map"), 0, 255, "map")
    scene = _scene(ctx, target)
    if not scene:
        raise Bad("map %d does not exist" % target)
    try:
        x, z = float(b.get("x")), float(b.get("z"))
    except (TypeError, ValueError):
        raise Bad("x and z must be numbers")
    if not (-256 <= x < 256 and -256 < z <= 256):
        raise Bad("x must be -256 to 255 and z -255 to 256")
    plane = _plane(scene)
    warnings = []
    if plane is None:
        warnings.append("no wall data for %s: this point could not be checked" % scene)
    elif not clear_of_walls(plane, x, z):
        near = nearest_clear(plane, x, z)
        raise Bad("(%g, %g) on %s is in or against a wall. Nearest open point: %s" % (
            x, z, scene, "(%g, %g)" % near if near else "none found"))
    values = {"npc_key": npc, "sub_id": sub, "target_map_id": target, "pos_x": x, "pos_z": z,
              "minimum_level": W._int(b.get("level", 1), 1, 255, "minimum level")}
    stmts = [W._ver_guard(_tele_parts(npc, sub), b.get("version"), "this button"),
             "DELETE FROM npc_teleport_overrides WHERE npc_key = %s AND sub_id = %d" % (common.lit(npc), sub),
             common.insert_sql("npc_teleport_overrides", values)]
    where = "npc_key = %s AND sub_id = %d" % (common.lit(npc), sub)
    out = W._write(ctx, [common.scope("npc_teleport_overrides", where, pk=["npc_key", "sub_id"])],
                   "%s button %d goes to %s (%g, %g)" % (npc, sub, scene, x, z), stmts, ["npc_teleport_overrides.*"], target="teleport " + npc)
    out["warnings"] = warnings
    _reload(ctx)
    return out


@M.post("teleport/reset")
def teleport_reset(ctx):
    npc, sub = _tele_key(ctx.body)
    where = "npc_key = %s AND sub_id = %d" % (common.lit(npc), sub)
    out = W._write(ctx, [common.scope("npc_teleport_overrides", where, pk=["npc_key", "sub_id"])],
                   "%s button %d: back to the original destination" % (npc, sub),
                   [W._ver_guard(_tele_parts(npc, sub), ctx.body.get("version"), "this button"),
                    "DELETE FROM npc_teleport_overrides WHERE %s" % where],
                   ["npc_teleport_overrides.*"], target="teleport " + npc)
    _reload(ctx)
    return out
