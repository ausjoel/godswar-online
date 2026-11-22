"""B22 P1 (the admin d32): teleporter menus - which buttons a teleporter shows, their order, a price per button and the
currency, and new destinations. Routes are registered on the NPC page (modules/npcs.py, register(M)); the page is
static/modules/teleport-menu.js.

The game server (B22 build) reads public.npc_teleport_menu every 15 seconds. Rows for an NPC replace the buttons it
sends for that NPC; an NPC without rows keeps its compiled menu and the 20,000 Silver Mycenae fee. Only the three
ordinary world teleporters (Sparta, Athens, Mycenae) use this table; battlefield and arena teleporters do not.

Button WORDS are client text: a sub id shows a button only if the client's NpcFunTranmit.lua labels it. Sub ids
1094-1105 are kept for new destinations; their words come with a client update (P1/client generator)."""
from . import common, world as W
from .common import Bad

TABLE = "npc_teleport_menu"
CURRENCIES = ("silver", "gold", "bgold")
MAX_ROWS = 40
PRICE_MAX = 2000000000


def _parts(npc):
    return [(TABLE, "npc_key = %s" % common.lit(npc), "position, sub_id")]


def _info(doc):
    tm = doc.get("teleport_menus")
    if not isinstance(tm, dict):
        raise Bad("the running game server does not support teleporter menus yet (it needs a newer game server)")
    return tm


def _labels():
    """sub id -> the client's button words on the teleporter page (NpcFunTranmit.lua, Index 1), from data/npc_menu_text.json."""
    from . import _npc_menu
    doc = _npc_menu.menu_data() or {}
    pages = (((doc.get("types") or {}).get("1") or {}).get("pages") or {}).get("1") or {}
    out = {}
    for k, v in pages.items():
        if k.isdigit() and (v.get("buttons") or []):
            out[int(k)] = v["buttons"][0].get("text", "")
    return out


def _table_exists(ctx):
    return bool(W._v(ctx, "to_regclass('public.%s') IS NOT NULL" % TABLE))


def menu(ctx, npc):
    doc = ctx_content(ctx)
    tm = _info(doc)
    entry = next((n for n in tm.get("npcs") or [] if n.get("npc") == npc), None)
    if not entry:
        raise Bad("%s is not one of the teleporters whose menu can be edited" % npc)
    exists = _table_exists(ctx)
    rows = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(r) ORDER BY position, sub_id), '[]') FROM %s r WHERE npc_key = %s)"
                % (TABLE, common.lit(npc))) if exists else []
    dests = {}
    for t in doc.get("teleports") or []:
        if t.get("npc") in {n["npc"] for n in tm["npcs"]}:
            dests.setdefault(int(t["sub"]), []).append(t)
    fees = {int(f["sub"]): f["fee"] for f in entry.get("compiled_fees") or []}
    labels = _labels()

    def describe(sub):
        own = [t for t in dests.get(sub, []) if t["npc"] == npc]
        any_ = own or dests.get(sub, [])
        t = any_[0] if any_ else None
        return {"sub": sub, "label": labels.get(sub), "compiled_here": bool(own), "borrowed": bool(t and not own),
                "name": t["name"] if t else None, "target_map": t["target_map"] if t else None,
                "level": t["level"] if t else None, "fee": fees.get(sub, 0)}

    known = sorted(set(dests) | set(range(tm["slot_first"], tm["slot_last"] + 1)))
    return {"npc": npc, "source_map": entry["source_map"], "compiled": [describe(s) for s in entry["compiled"]],
            "effective": entry.get("effective"), "rows": rows, "table": exists,
            "version": W._version(ctx, _parts(npc)) if exists else "new",
            "choices": [describe(s) for s in known], "slot_first": tm["slot_first"], "slot_last": tm["slot_last"],
            "maximum_buttons": tm["maximum_buttons"], "currencies": list(CURRENCIES)}


def ctx_content(ctx):
    from . import npcs
    return npcs._content(ctx)


def _clean_rows(ctx, npc, doc, body_rows):
    from . import npcs
    tm = _info(doc)
    if not isinstance(body_rows, list) or not body_rows:
        raise Bad("the menu needs at least one button (use Reset to go back to the original menu)")
    if len(body_rows) > MAX_ROWS:
        raise Bad("at most %d rows" % MAX_ROWS)
    known = {int(t["sub"]) for t in doc.get("teleports") or [] if t.get("npc") in {n["npc"] for n in tm["npcs"]}}
    labels = _labels()
    out, seen, warnings = [], set(), []
    for i, r in enumerate(body_rows):
        if not isinstance(r, dict):
            raise Bad("row %d is not an object" % (i + 1))
        sub = W._int(r.get("sub"), 1, 9999, "button number")
        if sub in seen:
            raise Bad("button %d is listed twice" % sub)
        seen.add(sub)
        hidden = W._bool(r.get("hidden", False))
        price = W._int(r.get("price"), 0, PRICE_MAX, "price", null=True)
        currency = str(r.get("currency") or "silver")
        if currency not in CURRENCIES:
            raise Bad("currency must be one of %s" % ", ".join(CURRENCIES))
        level = W._int(r.get("level"), 1, 255, "minimum level", null=True)
        name = W._text(r.get("name"), "name", 48, empty=True) or None
        row = {"npc_key": npc, "sub_id": sub, "position": i + 1, "hidden": hidden, "price": price, "currency": currency,
               "target_map_id": None, "pos_x": None, "pos_z": None, "minimum_level": level, "display_name": name}
        has_target = r.get("map") not in (None, "")
        if has_target:
            target = W._int(r.get("map"), 0, 255, "map")
            scene = npcs._scene(ctx, target)
            if not scene:
                raise Bad("map %d does not exist" % target)
            try:
                x, z = float(r.get("x")), float(r.get("z"))
            except (TypeError, ValueError):
                raise Bad("button %d: X and Z must be numbers" % sub)
            if not (-256 <= x < 256 and -256 < z <= 256):
                raise Bad("button %d: X must be -256 to 255 and Z -255 to 256" % sub)
            plane = npcs._plane(scene)
            if plane is None:
                warnings.append("no wall data for %s: the point of button %d could not be checked" % (scene, sub))
            elif not npcs.clear_of_walls(plane, x, z):
                near = npcs.nearest_clear(plane, x, z)
                raise Bad("button %d: (%g, %g) on %s is in or against a wall. Nearest open point: %s" % (
                    sub, x, z, scene, "(%g, %g)" % near if near else "none found"))
            row.update(target_map_id=target, pos_x=x, pos_z=z)
        elif sub not in known:
            raise Bad("button %d is a new destination: pick its map and click the point it goes to" % sub)
        if not hidden and sub not in labels:
            warnings.append("button %d has no words in the current client, so players see no button for it until the "
                            "client update that adds them" % sub)
        out.append(row)
    visible = sum(1 for r in out if not r["hidden"])
    if visible > tm["maximum_buttons"]:
        raise Bad("%d buttons are shown but the game window has room for %d: hide or remove some" % (visible, tm["maximum_buttons"]))
    if visible == 0:
        raise Bad("every button is hidden: the teleporter would show an empty menu")
    return out, warnings


def save(ctx):
    b = ctx.body
    npc = W._text(b.get("npc"), "NPC", 96, rx=r"^[A-Za-z0-9_]+$")
    doc = ctx_content(ctx)
    if not any(n.get("npc") == npc for n in _info(doc).get("npcs") or []):
        raise Bad("%s is not one of the teleporters whose menu can be edited" % npc)
    if not _table_exists(ctx):
        raise Bad("the teleporter menu table is missing: start the newer game server once (it creates the table)")
    rows, warnings = _clean_rows(ctx, npc, doc, b.get("rows"))
    where = "npc_key = %s" % common.lit(npc)
    stmts = [W._ver_guard(_parts(npc), b.get("version"), "this teleporter's menu"), "DELETE FROM %s WHERE %s" % (TABLE, where)]
    stmts += [common.insert_sql(TABLE, r) for r in rows]
    shown = [str(r["sub_id"]) for r in rows if not r["hidden"]]
    out = W._write(ctx, [common.scope(TABLE, where, pk=["npc_key", "sub_id"])],
                   "%s teleporter menu: %s" % (npc, ", ".join(shown)), stmts, [TABLE + ".*"], target="teleport menu " + npc)
    out["warnings"] = warnings
    npcs_reload(ctx)
    return out


def reset(ctx):
    npc = W._text(ctx.body.get("npc"), "NPC", 96, rx=r"^[A-Za-z0-9_]+$")
    if not _table_exists(ctx):
        return {"ok": True, "nothing": True}
    where = "npc_key = %s" % common.lit(npc)
    out = W._write(ctx, [common.scope(TABLE, where, pk=["npc_key", "sub_id"])],
                   "%s teleporter menu: back to the original" % npc,
                   [W._ver_guard(_parts(npc), ctx.body.get("version"), "this teleporter's menu"),
                    "DELETE FROM %s WHERE %s" % (TABLE, where)], [TABLE + ".*"], target="teleport menu " + npc)
    npcs_reload(ctx)
    return out


def npcs_reload(ctx):
    from . import npcs
    npcs._reload(ctx)


def register(M):
    """Called once by modules/npcs.py: GET teleport/menu/<npc>, POST teleport/menu/save, POST teleport/menu/reset."""
    M.get(r"teleport/menu/([A-Za-z0-9_]+)")(menu)
    M.post("teleport/menu/save")(save)
    M.post("teleport/menu/reset")(reset)
