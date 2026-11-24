"""modules/legality.py - B21 item 30 (the admin 2026-11-21): "Item legality check": every item that normal play could not
produce, with the attribute's NAME, whether it stops a login or is impossible but harmless, and Fix / Remove buttons
(one item, or every such item). Page: static/modules/legality.js.

Which findings stop a login (checked 2026-11-21 against the running server, rig copy godswar_b21_e1, W8-RANK build):
  * The server's own dry run of a login (POST /admin/character/entry-check, W6A) is the judge. "blocks" = the
    character cannot log in; "hidden" = the login works and the server leaves that item out of what the client is sent
    (PacketBuilder.ItemEntryGuard). Since W6A only a failed snapshot load blocks; a bad item record is hidden.
  * The item rules below are never checked at login: the record writer copies attribute ids and levels as they are,
    clamps quality / grade / stack to a byte and the socket count to 4 (PacketBuilder.ItemSerialization.cs). On the rig
    an item with attribute 9999 at level 99, attribute 104 at level 50, Q20, G25, 6 sockets and a stack of 250 still
    gave canEnter=true with no finding (docs: Development/batch-2026-11-21/E1/README.md). So every finding the dry run
    does not name is "impossible but harmless".
  * The database itself refuses the worst rows (a foreign key on prop_id, CHECKs on quality 0-20, grade 0-25, sockets
    0-6, class / elemental attributes), so those cannot exist.

Writes: one character at a time through ctx.char_write (offline: backup, lock, audit row, inventory_revision + 1, Undo).
An online character: a kit-bag Remove goes through the live server (ctx.bag_item); a warehouse fix is written like the
inventory page does (the player sees it at the next storage open); anything else answers 423 with Kick.
"""
from . import common, inventory as INV, natural as NAT, _attribute_families as AF
from .common import Bad, Conflict, NotFound
R = common.R

M = common.module(
    "legality", "Item legality",
    icon={"sprite": 684},
    order=27,
    status="ready",
    description="Items that normal play cannot make: which ones stop a login, which are only impossible, "
                "and Fix or Remove for one item or all of them.",
    needs_character=False,
)

CLASS_TEXT = {
    "breaks_login": "Stops the login",
    "hidden": "Hidden from the player",
    "harmless": "Impossible but harmless",
}
MAX_STACK = 99                       # the admin's rule: no stack above 99
SCAN_MAX = 500
ATTR_COLS = [("attribute%d" % i, "attribute_level%d" % i) for i in range(1, 6)]


def attr_name(attr_id):
    try:
        n = common.CATALOG.name("appendattr", int(attr_id))
    except Exception:      # noqa: BLE001 - an id the client does not know
        n = None
    return n if n and not str(n).startswith("#") else "unknown attribute %s" % attr_id


def _where(r):
    return R.slot_label(int(r["item_location"]), int(r["slot_index"]))


def _f(out, r, meta, rule, severity, message, fixes, **extra):
    out.append(dict({"row": int(r["id"]), "location": int(r["item_location"]), "slot": int(r["slot_index"]),
                     "where": _where(r), "item_id": int(r["prop_id"]), "item": meta["name"], "rule": rule,
                     "severity": severity, "message": message, "fixes": fixes, "cls": "harmless"}, **extra))


def class_stone_findings(out, r, meta, prof):
    """B22 d55 (the admin: allowed on ANY item, players can reach it in game): a class stone (class_attribute1) on gear is
    legal whether or not the piece is a class suit, so it is NOT a finding. Still flagged: a stone that is not one of
    the 8 points-exchanger stones, one for the other class group (Warrior / Champion vs Priest / Mage), one on an item
    that is not gear, and the retired second class row."""
    from . import _attribute_families as AF
    ca, ca2 = r.get("class_attribute1"), r.get("class_attribute2")
    if ca2 is not None:
        _f(out, r, meta, "class-stone-2", "impossible", "A second class stone; the game has one per item.", ["clear_class_stone", "remove"],
           field="class_attribute2")
    if ca is None:
        return
    ca, name = int(ca), attr_name(int(ca))
    if ca not in INV.CLASS_ATTRS:
        _f(out, r, meta, "class-stone", "impossible", "%s (id %d) is not a class stone." % (name, ca), ["clear_class_stone", "remove"],
           field="class_attribute1", attribute=ca, attribute_name=name)
    elif meta.get("slot", -1) < 0:
        _f(out, r, meta, "class-stone", "impossible", "%s sits on an item that is not gear." % name, ["clear_class_stone", "remove"],
           field="class_attribute1", attribute=ca, attribute_name=name)
    elif not AF.class_allowed(ca, prof):
        _f(out, r, meta, "class-stone", "unnatural", "%s is a class stone of the other class group." % name, ["clear_class_stone", "remove"],
           field="class_attribute1", attribute=ca, attribute_name=name)


def item_findings(b, rows, meta_of, client_items, attributes):
    """Structured findings for one character's item rows (the same rules as natural._item_checks, with names and
    the fix each one offers). Pure: the unit test calls it with hand-made rows."""
    out = []
    level, prof = int(b["fighter_job_lv"]), int(b["profession"])
    for r in rows:
        pid = int(r["prop_id"])
        meta, cli = meta_of(pid), client_items.get(str(pid)) or {}
        if not meta["server"]:
            _f(out, r, meta, "item", "impossible", "The server has no such item.", ["remove"])
            continue
        cap = min(int(meta["overlap"] or 1), MAX_STACK) if int(meta["overlap"] or 1) > 1 else 1
        if int(r["stack"]) > int(meta["overlap"] or 1):
            _f(out, r, meta, "stack", "unnatural", "Stack of %d; this item stacks to %d." % (r["stack"], meta["overlap"]),
               ["clamp", "remove"], field="stack", value=cap)
        if meta["slot"] >= 0:
            q, g = int(r["item_quality"]), int(r["item_grade"])
            mq, mg = int(cli.get("max_quality") or INV.CLIENT_Q), int(cli.get("max_grade") or INV.CLIENT_G)
            if q > mq:
                _f(out, r, meta, "quality", "unnatural", "Quality Q%d; this item forges to Q%d." % (q, mq), ["clamp", "remove"],
                   field="item_quality", value=mq)
            if g > mg:
                _f(out, r, meta, "grade", "unnatural", "Grade G%d; this item forges to G%d." % (g, mg), ["clamp", "remove"],
                   field="item_grade", value=mg)
        if r["item_location"] == INV.LOC_EQUIP:
            if (meta.get("minlv") or 0) > level:
                _f(out, r, meta, "equip-level", "unnatural", "Worn at level %d; it needs level %d." % (level, meta["minlv"]), ["remove"])
            if meta.get("classes") and prof not in meta["classes"]:
                _f(out, r, meta, "equip-class", "unnatural", "Worn by a class that cannot use it.", ["remove"])
        allowed = [int(a) for a in cli.get("append_attributes") or []]
        for i, (col, lcol) in enumerate(ATTR_COLS, 1):
            a = r.get(col)
            if a is None:
                continue
            a = int(a)
            name = attr_name(a)
            lv = r.get(lcol)
            am = attributes.get(str(a))
            if am is None:
                _f(out, r, meta, "attribute-unknown", "impossible", "%s (id %d) is not an attribute the server knows." % (name, a),
                   ["clear_attribute", "remove"], index=i, attribute=a, attribute_name=name)
                continue
            mx = int(am.get("max") or 1)
            if lv is not None and int(lv) > mx:
                _f(out, r, meta, "attribute-level", "impossible", "%s at level %s; its maximum is %d." % (name, lv, mx),
                   ["clamp", "remove"], index=i, attribute=a, attribute_name=name, field=lcol, value=mx)
            # B22: the same family test as the Inventory editor and the worn-gear preset (an enhanced level such as
            # id 14 belongs to the family the item rolls, id 10); an exact-id test flagged every preset stat
            if allowed and not AF.rolls_on(a, allowed):
                _f(out, r, meta, "attribute", "unnatural", "%s cannot roll on this item." % name,
                   ["clear_attribute", "replace_attribute", "remove"], index=i, attribute=a, attribute_name=name, level=lv,
                   choices=[{"id": x, "name": attr_name(x), "max": int((attributes.get(str(x)) or {}).get("max") or 1)}
                            for x in allowed if x not in {int(r[c]) for c, _ in ATTR_COLS if r.get(c) is not None}][:60])
        class_stone_findings(out, r, meta, prof)
        sockets = int(r.get("holy_socket_count") or 0)
        if sockets > INV.CLIENT_SOCKETS:
            _f(out, r, meta, "sockets", "impossible", "%d holy sockets; the client shows %d." % (sockets, INV.CLIENT_SOCKETS),
               ["clamp_sockets", "remove"])
        for i in range(1, min(sockets, 6) + 1):
            hl = r.get("holy_socket%d_level" % i)
            if hl is not None and int(hl) > NAT.HOLY_LEVEL_MAX:
                _f(out, r, meta, "holy-level", "unnatural", "Holy stone %d at level %s; stones stop at %d." % (i, hl, NAT.HOLY_LEVEL_MAX),
                   ["remove"])
    return out


def classify(findings, server_result, rows=(), meta_of=None, db_findings=()):
    """Mark each finding with the server's verdict. server_result: the dry-run result of this character's account, or
    None when the server could not be asked. A server (or database, _login_check.database_findings) finding on an item
    that no editor rule flags becomes its own row (rule 'server', fix: remove), so nothing the server objects to is
    hidden. Returns (findings, extra) where extra = server findings naming no item at all (e.g. a failed load)."""
    loc_of = {"equipment": 0, "bag": 1}
    by_slot, extra = {}, []
    for f in (server_result or {}).get("findings") or []:
        loc = loc_of.get(f.get("container"))
        if loc is not None and f.get("slot") is not None:
            by_slot[(loc, int(f["slot"]))] = {"severity": f.get("severity"), "message": str(f.get("message") or "")}
        else:
            extra.append(f)
    for d in db_findings or ():                       # e.g. a packed Seal Jade with no pet inside
        fx = d.get("fix") or {}
        if fx.get("location") is not None:
            by_slot.setdefault((int(fx["location"]), int(fx["slot"])),
                               {"severity": "blocks" if d.get("severity") == "blocks" else "hidden",
                                "message": "%s. %s" % (d.get("title") or "", d.get("detail") or "")})
    named = {(f["location"], f["slot"]) for f in findings}
    at = {(int(r["item_location"]), int(r["slot_index"])): r for r in rows or ()}
    for key, s in by_slot.items():
        if key in named or key not in at:
            continue
        r = at[key]
        meta = meta_of(int(r["prop_id"])) if meta_of else {"name": "item %s" % r["prop_id"]}
        _f(findings, r, meta, "server", "impossible", "The game server cannot send this item: %s" % s["message"], ["remove"])
    for f in findings:
        s = by_slot.get((f["location"], f["slot"]))
        if s is not None:
            f["cls"] = "breaks_login" if s.get("severity") == "blocks" else "hidden"
            f["server_message"] = s["message"]
        f["cls_text"] = CLASS_TEXT[f["cls"]]
        f["checked_by_server"] = server_result is not None
    return findings, extra


def _characters(ctx, cid=None):
    where = "lifecycle_state = 'active'" + ("" if cid is None else " AND id = %d" % int(cid))
    return ctx.need_rb().pg.json("(SELECT coalesce(json_agg(json_build_object('id', id, 'account_id', account_id, 'name', name) "
                                 "ORDER BY id), '[]') FROM (SELECT id, account_id, name FROM character_base WHERE %s ORDER BY id "
                                 "LIMIT %d) c)" % (where, SCAN_MAX)) or []


def scan(ctx, cid=None):
    rb = ctx.need_rb()
    chars = _characters(ctx, cid)
    if cid is not None and not chars:
        raise NotFound("no active character #%s" % cid)
    port = common.mgmt_port(ctx.app)
    from . import _login_check as LC
    server = LC.server_results(common._http, port, [c["account_id"] for c in chars], actor="gwadmin-legality") if port else None
    cat, cli = rb.cat(), rb.client()["items"]
    out = []
    for c in chars:
        d = rb.dump(int(c["id"]))
        if not d.get("character_base"):
            continue
        b = d["character_base"][0]
        findings = item_findings(b, d.get("character_items") or [], rb.item_meta, cli, cat["attributes"])
        res = (server or {}).get(int(c["account_id"])) if server is not None else None
        dbf = LC.database_findings(rb.pg.json, [int(c["id"])], self_healing=server is not None).get(int(c["id"]), [])
        findings, extra = classify(findings, res, d.get("character_items") or [], rb.item_meta, dbf)
        online = common.online_info(ctx.app, int(c["id"]))
        out.append({"cid": int(c["id"]), "character": c["name"], "level": b["fighter_job_lv"],
                    "class": common.CLASS_NAMES.get(b["profession"], str(b["profession"])), "gm": str(b.get("GM")) == "1",
                    "online": bool(online.get("online")), "kick": bool(online.get("kick")),
                    "can_enter": None if res is None else bool(res.get("canEnter", True)),
                    "server_findings": [{"severity": x.get("severity"), "stage": x.get("stage"), "message": x.get("message")}
                                        for x in extra],
                    "findings": findings})
    out.sort(key=lambda x: (0 if x["can_enter"] is False else 1, -len(x["findings"]), x["character"].lower()))
    return {"characters": out, "server_checked": server is not None,
            "server_note": None if server is not None else
            "The game server's own login check did not answer, so 'stops the login' cannot be confirmed; by the server's code "
            "none of these item rules is checked at login.",
            "classes": CLASS_TEXT}


@M.get("scan")
def scan_view(ctx):
    cid = ctx.q("character")
    return scan(ctx, int(cid) if cid not in (None, "") else None)


# =========================================================================== fixes
FIX_KINDS = ("clamp", "clamp_sockets", "clear_attribute", "replace_attribute", "remove")
SOCKET_COLS = ["holy_socket%d_%s" % (i, k) for i in range(INV.CLIENT_SOCKETS + 1, 7) for k in ("effect_id", "level")]   # 5-6 have no value column


def _default_fix(f):
    """The fix 'Fix' uses: the first of the finding's own fixes that keeps the item; None = only Remove helps (a Fix
    button never deletes)."""
    return next((k for k in f["fixes"] if k not in ("remove", "replace_attribute")), None)


def _set_values(f, kind, choice=None):
    """character_items column -> new value for one non-remove fix."""
    if kind == "clamp":
        return {f["field"]: int(f["value"])}
    if kind == "clamp_sockets":            # keep stones 1-4 (what the client shows), drop 5 and 6
        return dict({"holy_socket_count": INV.CLIENT_SOCKETS}, **{c: None for c in SOCKET_COLS})
    if kind == "clear_attribute":
        col, lcol = ATTR_COLS[f["index"] - 1]
        return {col: None, lcol: None}
    if kind == "clear_class_stone":          # B22 d55
        return {f["field"]: None}
    if kind == "replace_attribute":
        ok = {int(c["id"]): c for c in f.get("choices") or []}
        if choice is None or int(choice) not in ok:
            raise Bad("choose one of the attributes this item can roll")
        col, lcol = ATTR_COLS[f["index"] - 1]
        out = {col: int(choice)}
        if f.get("level") is not None and int(f["level"]) > ok[int(choice)]["max"]:
            out[lcol] = ok[int(choice)]["max"]          # the new attribute's own top level
        return out
    raise Bad("unknown fix %s" % kind)


def _plan(d, findings, picks):
    """[(finding, kind, values|None)] for the picks [{row, rule, index, kind, choice}], checked against the rows NOW."""
    rows = {int(r["id"]): r for r in d.get("character_items") or []}
    plan, removed = [], set()
    for p in picks:
        f = next((x for x in findings if x["row"] == int(p["row"]) and x["rule"] == p.get("rule")
                  and x.get("index") == p.get("index")), None)
        if f is None:
            raise Conflict("item row %s no longer has that problem (it changed since the page read it)" % p["row"])
        if f["row"] not in rows:
            raise Conflict("item row %s is gone" % f["row"])
        kind = p.get("kind") or _default_fix(f)
        if kind is None:
            raise Bad("%s (%s): only Remove fixes this; choose Remove for it" % (f["item"], f["message"]))
        if kind not in f["fixes"]:
            raise Bad("%s cannot be fixed with %s" % (f["item"], kind))
        if f["row"] in removed:
            continue
        if kind == "remove":
            removed.add(f["row"])
            plan = [x for x in plan if x[0]["row"] != f["row"]]
            plan.append((f, "remove", None))
        else:
            plan.append((f, kind, _set_values(f, kind, p.get("choice"))))
    return plan, rows


def _statements(cid, plan, rows):
    st, touched_equip = [], False
    merged = {}
    for f, kind, vals in plan:
        if kind == "remove":
            continue
        merged.setdefault(f["row"], {}).update(vals)
    for f, kind, _ in plan:
        r = rows[f["row"]]
        if kind == "remove":
            st.append("DELETE FROM character_items WHERE id = %d" % f["row"])
            st.append(R._audit(cid, "delete", r["item_location"], r["slot_index"], None, r))
            touched_equip |= r["item_location"] == INV.LOC_EQUIP
    for row_id, vals in merged.items():
        r = rows[row_id]
        sets = ", ".join("%s = %s" % (R.ident(k), R.lit(v)) for k, v in vals.items())
        st.append("UPDATE character_items SET %s, updated_at = now() WHERE id = %d" % (sets, row_id))
        new = dict(r, **vals)
        st.append(R._audit(cid, "update", r["item_location"], r["slot_index"], new, r))
        touched_equip |= r["item_location"] == INV.LOC_EQUIP
    if st:
        st.append("UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid)
        if touched_equip:
            st.append("SELECT public.recompute_character_holy_suit_points(%d)" % cid)
    return st


def apply_fixes(ctx, cid, picks):
    """One transaction for one character. Online: kit-bag removals go live, warehouse rows are written like the
    inventory page does, everything else is refused (423 with Kick)."""
    rb = ctx.need_rb()
    if not rb.writable:
        raise Bad("this editor connection is read-only")
    cid = int(cid)
    d = rb.dump(cid)
    if not d.get("character_base"):
        raise NotFound("no character #%d" % cid)
    findings = item_findings(d["character_base"][0], d.get("character_items") or [], rb.item_meta, rb.client()["items"],
                             rb.cat()["attributes"])
    plan, rows = _plan(d, findings, picks)
    if not plan:
        raise Bad("nothing to fix")
    sealed = {int(x["item_instance_id"]) for x in d.get("sealed_pet_items") or [] if x.get("item_instance_id") is not None}
    if any(f["row"] in sealed for f, _, _ in plan):
        raise Bad("a packed (sealed) pet is managed through the Pets page")
    if any(k == "remove" and rows[f["row"]]["item_location"] == INV.LOC_EQUIP and rows[f["row"]]["slot_index"] == R.MOUNT_SLOT
           for f, k, _ in plan):
        raise Bad("remove the mount on the Inventory page (its mount gear must come off first)")
    name = d["character_base"][0]["name"]
    what = "; ".join("%s %s: %s" % ("remove" if k == "remove" else "fix", f["item"], f["message"]) for f, k, _ in plan)[:400]
    info = common.online_info(ctx.app, cid)
    if info["online"]:
        live = [(f, k) for f, k, _ in plan if k == "remove" and f["location"] == INV.LOC_BAG and f["slot"] < common.BAG_SLOTS]
        wh = [(f, k, v) for f, k, v in plan if f["location"] == INV.LOC_WH]
        other = [x for x in plan if x[0]["location"] not in (INV.LOC_BAG, INV.LOC_WH) or
                 (x[0]["location"] == INV.LOC_BAG and (x[1] != "remove" or x[0]["slot"] >= common.BAG_SLOTS))]
        if other:
            raise common.OnlineRefused("%s is online: fixing worn or bag gear needs the character logged out (only bag "
                                       "items can be removed live, and the warehouse written)." % name, dict(info))
        results = []
        for f, _ in live:
            results.append(ctx.bag_item(cid, "remove", {"slot": f["slot"]}))
        if wh:
            st = _statements(cid, wh, rows)
            st = [s for s in st if not s.startswith("UPDATE character_base SET inventory_revision")]   # live session owns it
            results.append(ctx.char_write(cid, "legality (online, warehouse): " + what, lambda _d: st,
                                          fields=["character_items.*"], cross_bumps=False, online_ok=True))
        return {"mode": "live", "results": results, "fixed": len(plan), "character": name}
    st = _statements(cid, plan, rows)
    expected = [{"table": "character_items", "where": {"id": f["row"]},
                 "values": {k: rows[f["row"]].get(k) for k in ("prop_id", "item_location", "slot_index")}} for f, _, _ in plan]
    r = ctx.char_write(cid, "legality: " + what, lambda _d: st, fields=["character_items.*"], expected=expected, cross_bumps=False)
    r.update(mode="database", fixed=len(plan), character=name)
    return r


@M.post(r"fix/(\d+)")
def fix(ctx, cid):
    """POST /api/m/legality/fix/<cid> {picks: [{row, rule, index?, kind?, choice?}]} - one or more findings of one
    character (kind omitted = the default fix: clamp / clear the attribute; 'remove' deletes the item)."""
    picks = ctx.body.get("picks")
    if not isinstance(picks, list) or not 1 <= len(picks) <= 400:
        raise Bad("picks must be a list of 1-400 findings")
    for p in picks:
        if not isinstance(p, dict) or p.get("kind") not in (None,) + FIX_KINDS:
            raise Bad("each pick is {row, rule, index?, kind?: %s}" % "|".join(FIX_KINDS))
        p["row"] = common.to_int(p.get("row"), 1, 2 ** 63 - 1, "item row")
        if p.get("index") is not None:
            p["index"] = common.to_int(p["index"], 1, 5, "attribute index")
    return apply_fixes(ctx, cid, picks)


@M.post("fix-all")
def fix_all(ctx):
    """POST /api/m/legality/fix-all {action: fix|remove, classes: [harmless, hidden, breaks_login], cids?: [...]}
    - every matching finding, one transaction per character; online characters that need a logout are skipped and
    listed with the reason."""
    action = ctx.body.get("action")
    if action not in ("fix", "remove"):
        raise Bad("action is 'fix' (keep the item, make it legal) or 'remove' (delete the item)")
    classes = ctx.body.get("classes") or list(CLASS_TEXT)
    if not isinstance(classes, list) or any(c not in CLASS_TEXT for c in classes):
        raise Bad("classes must be some of: %s" % ", ".join(CLASS_TEXT))
    cids = ctx.body.get("cids")
    report = scan(ctx)
    done, skipped, needs_remove = [], [], []
    for c in report["characters"]:
        if cids and c["cid"] not in {int(x) for x in cids}:
            continue
        mine = [f for f in c["findings"] if f["cls"] in classes]
        picks = [{"row": f["row"], "rule": f["rule"], "index": f.get("index"),
                  "kind": "remove" if action == "remove" else _default_fix(f)} for f in mine]
        if action == "fix":
            left = [f for f, p in zip(mine, picks) if p["kind"] is None]
            if left:
                needs_remove.append({"cid": c["cid"], "character": c["character"],
                                     "items": ["%s (%s): %s" % (f["item"], f["where"], f["message"]) for f in left]})
            picks = [p for p in picks if p["kind"] is not None]
        if not picks:
            continue
        try:
            r = apply_fixes(ctx, c["cid"], picks)
            done.append({"cid": c["cid"], "character": c["character"], "fixed": r.get("fixed"), "mode": r.get("mode"),
                         "change": (r.get("change") or {}).get("id")})
        except (common.OnlineRefused, Bad, Conflict, common.Refused) as e:
            skipped.append({"cid": c["cid"], "character": c["character"], "reason": str(e)})
    return {"done": done, "skipped": skipped, "needs_remove": needs_remove}
