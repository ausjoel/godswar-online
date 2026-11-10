#!/usr/bin/env python3
"""tests/test_framework.py - checks the shared module framework (modules/common.py + reborn.py's write engines)
against a COPY of the reborn database. Run ON THE VPS from a tools copy:

    python3 -I gwadmin-web/tests/test_framework.py --db godswar_ed_<you> [--mgmt <login-port+2>] [--cid <offline character id>]

It refuses the live database 'godswar'. Every write it makes is undone again (the script prints each step and
ends with "ALL OK" or the first failure). What it covers:
  1. catalogue: every ID type builds; the item list has every server template (no "top N")
  2. glossary + write rules: field help, read-only refusals, revision bumps of update_sql
  3. capabilities: a fake round2 management port (both JSON shapes), live_call's 409 "offline" answer
  4. char_write: a guarded character write (expected values, wallet_revision +1), 409 on stale values,
     403 on a read-only field, 423 while the character looks online, then Undo through reborn.undo
  5. the economy-baseline guard SQL (inside a rolled-back transaction)
  6. rows_write + undo_rows on start-up content (revision counter moves forward, after_restart)
"""
import argparse
import http.server
import json
import os
import sys
import tempfile
import threading

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
sys.path.insert(0, HERE)
import reborn as R                # noqa: E402
from modules import common as C   # noqa: E402

OK = []


def step(msg, cond=True, detail=""):
    if not cond:
        print("FAIL: %s %s" % (msg, detail))
        sys.exit(1)
    OK.append(msg)
    print("ok   %s%s" % (msg, (" - " + detail) if detail else ""))


class FakeApp:
    def __init__(self, rb, mgmt):
        self.reborn, self.changes, self.rates = rb, [], None
        self.a = argparse.Namespace(rates_mgmt=mgmt, port=0, allow_force=False, backup_dir=rb.backup_dir)
        self.started = "test"

    def record_reborn_change(self, c):
        self.changes.append(c)


def fake_mgmt(doc_holder):
    class H(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def _send(self, code, obj):
            b = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            self.wfile.write(b)

        def do_GET(self):
            if self.path == "/admin/capabilities" and doc_holder.get("doc") is not None:
                return self._send(200, doc_holder["doc"])
            return self._send(404, {"error": "not found"})

        def do_POST(self):
            n = int(self.headers.get("Content-Length") or 0)
            body = json.loads(self.rfile.read(n) or b"{}")
            doc_holder["last"] = (self.path, body)
            return self._send(409, {"error": "offline"})
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


def _baseline_case(rb, cid):
    sql = ("BEGIN;\nSET LOCAL session_replication_role = replica;\nDELETE FROM character_inventory_baseline_items WHERE character_id = %d;\n"
           "DELETE FROM character_economy_baseline WHERE character_id = %d;\nSET LOCAL session_replication_role = origin;\n%s\n"
           "SELECT 'GWJSON|' || json_build_object('rev', (SELECT wallet_revision + inventory_revision FROM character_base WHERE id = %d), "
           "'base', (SELECT count(*) FROM character_economy_baseline WHERE character_id = %d), "
           "'items', (SELECT count(*) FROM character_inventory_baseline_items WHERE character_id = %d))::text;\nROLLBACK;\n") % (
        cid, cid, R.Reborn.economy_baseline_sql(cid), cid, cid, cid)
    try:
        out = rb.pg.run(sql)
        res = json.loads([l for l in out.splitlines() if l.startswith("GWJSON|")][0][7:])
        step("economy baseline re-created for #%d (revisions 0)" % cid, res["rev"] == 0 and res["base"] == 1, str(res))
    except R.RebornError as e:
        step("economy-baseline guard refuses #%d (revisions above 0, no baseline)" % cid, "no economy baseline" in str(e), str(e)[:110])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", required=True)
    ap.add_argument("--psql", default="sudo -n -u postgres psql")
    ap.add_argument("--mgmt", type=int, default=None, help="the copy's management port (only for the server-status line)")
    ap.add_argument("--cid", type=int, default=None, help="an offline character to write (default: the first one)")
    a = ap.parse_args()
    if a.db == "godswar":
        sys.exit("refusing the live database 'godswar'")
    rb = R.Reborn(a.psql, a.db, writable=True, live=False, backup_dir=tempfile.mkdtemp(prefix="gwfw-"))
    ident = rb.identity()
    step("psql reaches the copy", ident.get("db") == a.db and a.db != "godswar", str(ident))
    rb.identity_info = ident
    added = rb.enable_extra_dump_tables()
    step("extra dump tables enabled", len(added) >= 10, ", ".join(added))
    app = FakeApp(rb, a.mgmt)
    C.CATALOG.rb = rb

    # 1. catalogue
    n_tpl = len(rb.cat()["items"])
    for typ in C.CATALOG.TYPES:
        ix = C.CATALOG.index(typ)
        step("catalogue %s: %d entries" % (typ, ix["count"]), ix["count"] > 0)
    ids = {r[0] for r in C.CATALOG.index("item")["rows"]}
    missing = [k for k in rb.cat()["items"] if int(k) not in ids]
    step("item list holds every server template", not missing, "%d templates, %d listed, missing %s" % (n_tpl, len(ids), missing[:5]))
    step("weapons are all there", sum(1 for r in C.CATALOG.index("item")["rows"] if r[11] == "weapon") == 145)

    # 2. glossary / rules
    f = C.GLOSSARY.field("character_base", "Money")
    step("field help Silver", f["label"] == "Silver" and f["revision"] == "wallet_revision" and f["editable"], f["applies_text"])
    for bad in ("character_base.store_num", "character_base.checkpoint_owner_id", "item_templates.name", "character_item_audit.action"):
        try:
            C.check_fields([bad])
            step("refuse %s" % bad, False)
        except C.Refused as e:
            step("refuse %s" % bad, True, str(e)[:90])
    try:
        C.check_fields(["guild_members.duty"])
        step("advanced table needs the switch", False)
    except C.Refused:
        step("advanced table needs the switch")
    C.check_fields(["guild_members.duty"], advanced=True)
    sql = C.update_sql("character_base", {"id": 1}, {"Money": 5, "Map": 2})
    step("update_sql bumps wallet + position revisions", "wallet_revision" in sql and "position_revision" in sql, sql)

    # 3. capabilities
    holder = {"doc": None}
    srv = fake_mgmt(holder)
    app.a.rates_mgmt = srv.server_address[1]
    caps = C.capabilities(app, force=True)
    step("no /admin/capabilities -> none", not caps["available"] and "404" in (caps["error"] or ""), caps["error"])
    holder["doc"] = {"version": 1, "endpoints": [{"method": "GET", "path": "/admin/capabilities"},
                                                 {"method": "POST", "path": "/admin/money", "body": {"actor": "s", "character": "s", "silver": "int"}},
                                                 {"method": "POST", "path": "/admin/kick", "body": {"actor": "s", "character": "s"}}]}
    caps = C.capabilities(app, force=True)
    step("round2 shape parsed", caps["available"] and "character.wallet" in caps["operations"] and "character.kick" in caps["operations"],
         ", ".join(sorted(caps["operations"])))
    holder["doc"] = {"version": 1, "operations": {"character.level": {"method": "POST", "path": "/admin/character/level", "online": True}}}
    caps = C.capabilities(app, force=True)
    step("WRITE-RULES shape parsed", list(caps["operations"]) == ["character.level"])
    holder["doc"] = {"version": 1, "endpoints": [{"method": "POST", "path": "/admin/money", "body": {}}]}
    C.capabilities(app, force=True)
    ctx = C.Ctx(app, "POST", "x", {}, {}, C.Module("fwtest", "fw test"))
    r = ctx.live_call("character.wallet", "Nobody", {"silver": 1})
    step("live_call: 409 offline -> write the database instead", r == {"ok": False, "offline": True} and holder["last"][1]["character"] == "Nobody")
    srv.shutdown()
    app.a.rates_mgmt = a.mgmt
    C.capabilities(app, force=True)

    # 4. char_write
    chars = rb.characters()
    cid = a.cid or next(c["id"] for c in chars if not c["online"])
    row = next(c for c in chars if c["id"] == cid)
    before = rb.dump(cid)["character_base"][0]
    money, wrev = int(before["Money"]), int(before["wallet_revision"])
    step("test character", True, "#%d %s Silver %d wallet_revision %d" % (cid, row["name"], money, wrev))
    res = ctx.char_write(cid, "fw test: Silver +1", lambda d: [C.update_sql("character_base", {"id": cid}, {"Money": money + 1})],
                         fields=["character_base.Money"], expected=[{"table": "character_base", "where": {"id": cid}, "values": {"Money": money}}])
    after = rb.dump(cid)["character_base"][0]
    step("char_write saved", int(after["Money"]) == money + 1 and int(after["wallet_revision"]) == wrev + 1,
         "Silver %s, wallet_revision %s, applies %s" % (after["Money"], after["wallet_revision"], res["applies_text"]))
    ch = app.changes[-1]
    step("change record", ch["engine"] == "reborn" and ch["module"] == "fwtest" and ch["applies"] == "next_login" and os.path.exists(ch["backup"]))
    try:
        ctx.char_write(cid, "stale", lambda d: [C.update_sql("character_base", {"id": cid}, {"Money": 1})], fields=["character_base.Money"],
                       expected=[{"table": "character_base", "where": {"id": cid}, "values": {"Money": money}}])
        step("stale expected -> 409", False)
    except C.Conflict as e:
        step("stale expected -> 409", int(rb.dump(cid)["character_base"][0]["Money"]) == money + 1, str(e)[:100])
    try:
        ctx.char_write(cid, "ro", lambda d: ["UPDATE character_base SET store_num = 1 WHERE id = %d" % cid], fields=["character_base.store_num"])
        step("read-only field -> 403", False)
    except C.Refused as e:
        step("read-only field -> 403", True, str(e)[:80])
    acc = rb.pg.json("(SELECT to_jsonb(account_id) FROM character_base WHERE id = %d)" % cid)
    rb.pg.run("UPDATE accounts SET login_status = 1 WHERE id = %d;\n" % int(acc))          # the copy only: pretend online
    try:
        ctx.char_write(cid, "online", lambda d: [C.update_sql("character_base", {"id": cid}, {"Money": 7})], fields=["character_base.Money"])
        step("online character -> 423", False)
    except C.OnlineRefused as e:
        step("online character -> 423", e.info["online"] and "login_status" in " ".join(e.info["reasons"]), str(e)[:100])
    finally:
        rb.pg.run("UPDATE accounts SET login_status = 0 WHERE id = %d;\n" % int(acc))
    u = rb.undo(ch, False, app.record_reborn_change, all_changes=app.changes)
    back = rb.dump(cid)["character_base"][0]
    step("Undo of the char_write", int(back["Money"]) == money and int(back["wallet_revision"]) == wrev + 2 and u["result"]["all_identical"],
         "Silver %s, wallet_revision %s (forward)" % (back["Money"], back["wallet_revision"]))

    # 5. economy-baseline guard (rolled back): delete the baseline inside a transaction, run the guard, count rows.
    #    A character whose wallet / inventory revisions are 0 gets a new baseline; one above 0 is refused.
    zero = rb.pg.json("(SELECT coalesce(json_agg(id ORDER BY id), '[]') FROM character_base WHERE wallet_revision = 0 AND inventory_revision = 0)")
    for bcid in ([zero[0]] if zero else []) + [cid]:
        _baseline_case(rb, bcid)

    # 6. rows_write + undo on start-up content
    sc = [C.scope("monster_reward_settings", {"setting_id": 1})]
    snap = rb.snapshot_rows(sc)["monster_reward_settings"][0]
    gap, rev = int(snap["maximum_lower_level_gap"]), int(snap["revision"])
    new = 199 if gap != 199 else 198
    res6 = ctx.rows_write(sc, "fw test: level gap %d -> %d" % (gap, new),
                          lambda b: [C.update_sql("monster_reward_settings", {"setting_id": 1}, {"maximum_lower_level_gap": new})],
                          fields=["monster_reward_settings.maximum_lower_level_gap"],
                          expected=[{"table": "monster_reward_settings", "where": {"setting_id": 1}, "values": {"maximum_lower_level_gap": gap}}],
                          target="kill reward settings")
    s2 = rb.snapshot_rows(sc)["monster_reward_settings"][0]
    step("rows_write saved", int(s2["maximum_lower_level_gap"]) == new and int(s2["revision"]) == rev + 1 and res6["applies"] == "after_restart",
         "gap %s revision %s applies %s" % (s2["maximum_lower_level_gap"], s2["revision"], res6["applies_text"]))
    cw = app.changes[-1]
    u6 = rb.undo_rows(cw, False, app.record_reborn_change, all_changes=app.changes)
    s3 = rb.snapshot_rows(sc)["monster_reward_settings"][0]
    step("undo_rows", int(s3["maximum_lower_level_gap"]) == gap and int(s3["revision"]) == rev + 2 and u6["result"]["all_identical"],
         "gap %s revision %s (forward)" % (s3["maximum_lower_level_gap"], s3["revision"]))
    try:
        rb.undo_rows(cw, False, app.record_reborn_change, all_changes=app.changes)
        step("second undo refused", False)
    except R.RebornError:
        step("second undo refused")
    sc2 = [C.scope("legacy_instance_settings", {"instance_kind": 3})]
    try:
        ctx.rows_write(sc2, "x", lambda b: ["SELECT 1"], fields=["legacy_instance_settings.free_entry_limit"])
        step("edit_advanced table needs advanced=True", False)
    except C.Refused:
        step("edit_advanced table needs advanced=True")
    print("ALL OK (%d checks)" % len(OK))


if __name__ == "__main__":
    main()
