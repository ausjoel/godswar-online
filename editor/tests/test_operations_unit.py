"""Unit checks for modules/operations.py: the resource monitor against fixture /proc and cgroup trees, the realm validator
(the server's RealmCatalogEntry rules), restore refusals, and the change-log export + archive. No database, no systemd,
no network. Run: python3 -I tests/test_operations_unit.py"""
import json, os, sys, tempfile, types, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import operations as O, world as W, common  # noqa: E402
from modules.common import Bad, Conflict, Refused  # noqa: E402


def write(root, rel, text):
    p = Path(root, rel); p.parent.mkdir(parents=True, exist_ok=True); p.write_text(text)


def proc_tree(d, busy, total_idle, rx, tx, usage):
    write(d, "proc/stat", "cpu  %d 0 0 %d 0 0 0 0 0 0\ncpu0 1 0 0 1 0 0 0 0 0 0\ncpu1 1 0 0 1 0 0 0 0 0 0\nintr 1\n" % (busy, total_idle))
    write(d, "proc/meminfo", "MemTotal:       4000000 kB\nMemFree:         500000 kB\nMemAvailable:   1000000 kB\nSwapTotal:      1000 kB\nSwapFree:       400 kB\n")
    write(d, "proc/loadavg", "0.22 0.17 0.11 1/173 432330\n")
    write(d, "proc/uptime", "12345.67 40000.00\n")
    write(d, "proc/net/dev", "Inter-|   Receive\n face |bytes\n    lo: 999 0 0 0 0 0 0 0 999 0 0 0 0 0 0 0\n  eth0: %d 5 0 0 0 0 0 0 %d 6 0 0 0 0 0 0\n" % (rx, tx))
    for unit, rel in (("godswar251", "system.slice/godswar251.service"), ("postgres", "system.slice/system-postgresql.slice/postgresql@17-main.service"),
                      ("caddy", "system.slice/caddy.service")):
        write(d, "cg/%s/memory.current" % rel, "%d\n" % (100 * 1024 * 1024))
        write(d, "cg/%s/cpu.stat" % rel, "usage_usec %d\nuser_usec 1\n" % usage)
        write(d, "cg/%s/pids.current" % rel, "12\n")
        write(d, "cg/%s/cgroup.events" % rel, "populated 1\nfrozen 0\n")


class Monitor(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp()
        self.old = O.PROC, O.CGROUP
        O.PROC, O.CGROUP = os.path.join(self.d, "proc"), os.path.join(self.d, "cg")

    def tearDown(self):
        O.PROC, O.CGROUP = self.old

    def test_readers(self):
        proc_tree(self.d, 100, 900, 1000, 2000, 5_000_000)
        self.assertEqual(O.cpu_times(), (100, 1000)); self.assertEqual(O.ncpu(), 2)
        m = O.meminfo(); self.assertEqual(m["total"], 4000000 * 1024); self.assertEqual(m["used_pct"], 75.0); self.assertEqual(m["swap_used"], 600 * 1024)
        self.assertEqual(O.loadavg(), [0.22, 0.17, 0.11]); self.assertAlmostEqual(O.uptime(), 12345.67)
        self.assertEqual(O.net_bytes(), {"eth0": (1000, 2000)})
        s = O.service_raw()
        self.assertEqual(s["postgres"]["memory"], 100 * 1024 * 1024)          # found under system-postgresql.slice
        self.assertTrue(s["godswar251"]["running"]); self.assertIsNone(s["gwsignup"]); self.assertIsNone(s["editor"])

    def test_rates(self):
        proc_tree(self.d, 100, 900, 1000, 2000, 5_000_000)
        a = O.raw_sample(); a["t"] = 1000.0
        proc_tree(self.d, 150, 1850, 11000, 4000, 7_000_000)                  # +50 busy of +1000 total; +2 s of CPU in 10 s
        b = O.raw_sample(); b["t"] = 1010.0
        r = O.derive(a, b, 2)
        self.assertEqual(r["cpu_pct"], 5.0); self.assertEqual(r["net_rx"], 1000); self.assertEqual(r["net_tx"], 200)
        self.assertEqual(r["services"]["godswar251"]["cpu_pct"], 20.0); self.assertIsNone(r["services"]["editor"])
        first = O.derive(None, a, 2); self.assertIsNone(first["cpu_pct"]); self.assertIsNone(first["services"]["godswar251"]["cpu_pct"])

    def test_missing_proc_does_not_raise(self):
        self.assertIsNone(O.cpu_times()); self.assertIsNone(O.meminfo()); self.assertIsNone(O.loadavg()); self.assertEqual(O.net_bytes(), {})

    def test_sampler_history_bounded(self):
        proc_tree(self.d, 100, 900, 1000, 2000, 5_000_000)
        s = O.Sampler()
        for i in range(O.HISTORY + 5):
            proc_tree(self.d, 100 + i, 900 + i * 10, 1000, 2000, 5_000_000 + i)
            s.tick()
        self.assertEqual(len(s.history), O.HISTORY)
        self.assertIn("mem_pct", s.history[-1]); self.assertIn("load1", s.history[-1])


GOOD = dict(name="Dwargon", identifier="DWG3jcIzqGgKvOf1dbYZKC8cS", ip_address="YOUR_SERVER_IP", game_port=7002, server_limit=250,
            display_order=2, enabled=False, recommended=False)


class Realms(unittest.TestCase):
    def test_valid(self):
        self.assertEqual(O.validate_realm(GOOD), GOOD)
        self.assertEqual(O.validate_realm(dict(GOOD, ip_address="play.example.com"))["ip_address"], "play.example.com")
        self.assertEqual(len(O.new_identifier()), 25)
        O.validate_realm(dict(GOOD, identifier=O.new_identifier()))

    def test_rules_of_the_server(self):
        bad = [dict(GOOD, name=""), dict(GOOD, name=" Lead"), dict(GOOD, name="x" * 36), dict(GOOD, name="Zeusé"),
               dict(GOOD, identifier="short"), dict(GOOD, identifier="DWG3jcIzqGgKvOf1dbYZKC8c "), dict(GOOD, identifier="x" * 26),
               dict(GOOD, ip_address="999.1.1.1"), dict(GOOD, ip_address="a" * 24), dict(GOOD, ip_address="bad host"),
               dict(GOOD, ip_address="-x.com"), dict(GOOD, game_port=0), dict(GOOD, game_port=65536), dict(GOOD, game_port="7000"),
               dict(GOOD, server_limit=0), dict(GOOD, display_order=-1), dict(GOOD, enabled="yes"), dict(GOOD, recommended=True),
               dict(GOOD, game_port=True)]
        for v in bad:
            with self.subTest(v=v), self.assertRaises(Bad):
                O.validate_realm(v)

    def test_guard_names_the_login_limits(self):
        self.assertIn("> 16", O.REALM_GUARD); self.assertIn("recommended) > 1", O.REALM_GUARD); self.assertIn("= 0", O.REALM_GUARD)
        self.assertIn("confrelid = 'public.server'::regclass", O.FK_TABLES_SQL)
        self.assertTrue(O.REALM_GUARD.rstrip().endswith(";"), "guards are appended to the transaction as they are")
        self.assertTrue(O._references_sql({"schema": "public", "table": "t", "column": "c"}, 1).startswith("to_jsonb("), "pg.json needs json")

    def test_reference_counts_survive_a_denied_table(self):
        fks = [{"schema": "public", "table": "character_base", "column": "server_id"}, {"schema": "public", "table": "secret", "column": "realm_id"},
               {"schema": "public", "table": "account_realm", "column": "realm_id"}]

        def pg_json(sql):
            if "secret" in sql:
                raise RuntimeError("permission denied for table secret")
            return 40 if "character_base" in sql else 0
        rb = types.SimpleNamespace(pg=types.SimpleNamespace(json=pg_json))
        refs = O.references(rb, 1, fks)
        self.assertEqual(refs[0], {"table": "public.character_base", "rows": 40})
        self.assertIsNone(refs[1]["rows"]); self.assertEqual(len(refs), 2)              # account_realm: 0 rows, not listed
        self.assertIn('"character_base"', O._references_sql(fks[0], 1)); self.assertIn("= 1)", O._references_sql(fks[0], 1))

    def test_moving_an_enabled_realm_needs_an_answer_or_acknowledgement(self):
        row = dict(GOOD, id=3, enabled=True, ip_address="10.0.0.1")
        saved = W._need_writable, O._realm_rows, O._port_open
        writes = []
        try:
            W._need_writable = lambda ctx: ctx.rb
            O._realm_rows = lambda ctx: [dict(row)]
            O._port_open = lambda port, host="127.0.0.1", timeout=2.0: False
            ctx = ctx_for(None, dict(row, ip_address="10.0.0.2", expected=dict(row)))
            ctx.rows_write = lambda *a, **k: writes.append((a, k)) or {"change": {}}
            with self.assertRaises(Conflict):
                O.realm_save(ctx)
            self.assertEqual(writes, [])
            ctx.body["acknowledge_unreachable"] = True
            O.realm_save(ctx); self.assertEqual(len(writes), 1)
            self.assertEqual(writes[0][1]["fields"], ["server.ip_address"])
            ctx.body = dict(row, name="Renamed", expected=dict(row))               # a rename does not probe
            O.realm_save(ctx); self.assertEqual(len(writes), 2)
            ctx.body = dict(row, identifier="Z" * 25, expected=dict(row))
            with self.assertRaises(Refused):
                O.realm_save(ctx)
        finally:
            W._need_writable, O._realm_rows, O._port_open = saved


class Features(unittest.TestCase):
    def test_parse_and_hide(self):
        env = 'GODSWAR_GOLD_BOX_DIAL=3,8 "GODSWAR_SECURE_CERTIFICATE_PASSWORD=hunter2" DOTNET_NOLOGO=1 GODSWAR_PARNASSUS=1 lower=x'
        self.assertEqual(O.parse_environment(env)[0], ("GODSWAR_GOLD_BOX_DIAL", "3,8"))
        self.assertEqual(len(O.parse_environment(env)), 4)
        with tempfile.TemporaryDirectory() as d:
            stub = os.path.join(d, "systemctl")
            Path(stub).write_text("#!/bin/sh\necho 'Environment=%s'\necho 'DropInPaths=/etc/systemd/system/godswar251.service.d/gm.conf /x/goldbox.conf'\n" % env.replace("'", ""))
            os.chmod(stub, 0o755)
            old = O.SYSTEMCTL; O.SYSTEMCTL = stub
            try:
                f = O.features(None)
            finally:
                O.SYSTEMCTL = old
        names = {r["name"]: r["value"] for r in f["switches"]}
        self.assertEqual(names["GODSWAR_GOLD_BOX_DIAL"], "3,8"); self.assertEqual(names["GODSWAR_SECURE_CERTIFICATE_PASSWORD"], "[hidden]")
        self.assertNotIn("DOTNET_NOLOGO", names); self.assertEqual(f["dropins"], ["gm.conf", "goldbox.conf"])


class RealmWriteRules(unittest.TestCase):
    """Today's write_rules refuse server.ip_address etc. (403); ops/write_rules-server.proposed.json (the lead's
    registration line) makes exactly the realm columns editable, and keeps identifier / id read-only."""
    COLS = ["server.ip_address", "server.game_port", "server.server_limit", "server.enabled", "server.display_order", "server.recommended"]

    def test_current_rules_refuse_and_proposed_rules_allow(self):
        # merged (W4): the proposed rules are now the live write_rules, so the realm columns are editable
        common.check_fields(["server.ip_address"], advanced=True)
        common.check_fields(["server.name", "server.*"], advanced=True)     # name edit and whole-row add/remove work today
        g, w = common.GLOSSARY._load()
        old = w["tables"]["server"]
        prop = json.loads((Path(__file__).resolve().parents[1] / "ops" / "write_rules-server.proposed.json").read_text())["tables.server"]
        try:
            w["tables"]["server"] = prop
            common.check_fields(self.COLS + ["server.name"], advanced=True)
            for col in ("server.identifier", "server.id", "server.time_zone_revision"):
                with self.subTest(col=col), self.assertRaises(Refused):
                    common.check_fields([col], advanced=True)
            with self.assertRaises(Refused):
                common.check_fields(["server.ip_address"], advanced=False)
        finally:
            w["tables"]["server"] = old


class FakeApp:
    def __init__(self, d):
        self.changes_dir = d; self.changes = []

    def _save_change(self, c):
        with open(os.path.join(self.changes_dir, "change-%s.json" % c["id"]), "w") as f:
            json.dump(c, f)


def ctx_for(app, body=None):
    return types.SimpleNamespace(app=app, body=body or {}, rb=types.SimpleNamespace(db="godswar_test", writable=True))


class ChangeLog(unittest.TestCase):
    def setUp(self):
        self.d = tempfile.mkdtemp(); self.app = FakeApp(self.d)
        for i, applies in enumerate(["next_login", "after_restart", "now", "after_restart"]):
            c = {"id": "rw-%d" % i, "time": "2026-11-1%dT00:00:00Z" % i, "action": "edit %d" % i, "applies": applies,
                 "undone": i == 3, "before": {"x": i}}
            self.app.changes.append(c); self.app._save_change(c)

    def test_summary_export_and_clear(self):
        s = O.changes_summary(ctx_for(self.app))
        self.assertEqual((s["records"], s["keep"]), (4, 1))                    # rw-1 waits for a restart; rw-3 was undone
        self.assertEqual(len(O.changes_export(ctx_for(self.app))["changes"]), 4)
        with self.assertRaises(Bad):
            O.changes_clear(ctx_for(self.app, {"confirm": "clear", "records_seen": 4}))
        with self.assertRaises(Conflict):
            O.changes_clear(ctx_for(self.app, {"confirm": "CLEAR", "records_seen": 3}))
        r = O.changes_clear(ctx_for(self.app, {"confirm": "CLEAR", "records_seen": 4}))
        self.assertEqual((r["archived"], r["kept"]), (3, 1))
        export = json.loads(Path(r["export"]).read_text())
        self.assertEqual(len(export["changes"]), 4); self.assertEqual(export["changes"][0]["before"], {"x": 0})
        left = sorted(os.path.basename(p) for p in Path(self.d).glob("change-*.json"))
        self.assertEqual(left[0], "change-op-clear-" + os.path.basename(r["folder"])[len("cleared-"):] + ".json")
        self.assertIn("change-rw-1.json", left); self.assertEqual(len(left), 2)
        self.assertEqual(sorted(c["id"] for c in self.app.changes), sorted(["rw-1", r["change"]["id"]]))
        self.assertEqual(len(list(Path(r["folder"]).glob("change-*.json"))), 3)   # moved, not deleted


class Restore(unittest.TestCase):
    def setUp(self):
        self.saved = W._need_writable, W._control_ok, W._who, O._helper, O._record
        W._need_writable = lambda ctx: ctx.rb
        W._control_ok = lambda ctx: True
        self.players = 0
        W._who = lambda ctx: (self.players, {"accounts": self.players, "connections": 0})
        self.sent = []
        O._helper = lambda req: self.sent.append(req) or {"job": req["job"], "state": "running"}
        O._record = lambda *a, **k: {"id": "op-x"}

    def tearDown(self):
        W._need_writable, W._control_ok, W._who, O._helper, O._record = self.saved

    def call(self, body):
        return O.control(ctx_for(FakeApp(tempfile.mkdtemp()), body))

    def test_refusals(self):
        f = "godswar251-2026-11-12-173000.sql.gz"
        for body in ({"action": "restore", "file": "../etc/passwd", "confirm": "../etc/passwd"},
                     {"action": "restore", "file": f, "confirm": f + " "}, {"action": "restore", "file": f}):
            with self.subTest(body=body), self.assertRaises(Bad):
                self.call(body)
        self.players = 2
        with self.assertRaises(Refused):
            self.call({"action": "restore", "file": f, "confirm": f})
        self.assertEqual(self.sent, [])
        with self.assertRaises(Bad):
            self.call({"action": "rm -rf"})

    def test_restore_at_zero_players(self):
        f = "godswar251-pre-restore-2026-11-12-173000.sql.gz"
        r = self.call({"action": "restore", "file": f, "confirm": f})
        self.assertEqual(self.sent[0]["action"], "restore"); self.assertEqual(self.sent[0]["file"], f)
        self.assertEqual(r["players_online"], 0)
        self.call({"action": "backups"}); self.assertEqual(self.sent[-1]["action"], "backups")


class GuidedServers(unittest.TestCase):
    """Wave 5 B: the guided Add / Close / Delete server flow. The root helper, the database and the network are stubs."""
    ROWS = [dict(GOOD, id=1, name="Tempest", identifier="K" * 25, game_port=7000, display_order=1, enabled=True, recommended=True,
                 time_zone_id="Australia/Brisbane"),
            dict(GOOD, id=2, name="Dwargon", ip_address="0.0.0.0", game_port=7000, display_order=2)]
    NAMES = ("_realm_rows", "realm_processes", "_port_open", "_local_ipv4", "_helper", "_record", "_handoff", "realm_players",
             "realm_characters", "references")

    def setUp(self):
        self.saved = {n: getattr(O, n) for n in self.NAMES}
        self.wsaved = W._need_writable, W._control_ok, W.server_job
        self.rows = [dict(r) for r in self.ROWS]
        self.procs, self.sent, self.writes, self.open_ports = {}, [], [], set()
        self.players, self.chars, self.refs, self.handoff, self.jobs = 0, 0, [], "postgres", {}
        O._realm_rows = lambda ctx: [dict(r) for r in self.rows]
        O.realm_processes = lambda: self.procs
        O._port_open = lambda port, host="127.0.0.1", timeout=2.0: (host, port) in self.open_ports or port in self.open_ports
        O._local_ipv4 = lambda: "YOUR_SERVER_IP"
        O._helper = lambda req: self.sent.append(req) or {"job": req["job"], "state": "running"}
        O._record = lambda *a, **k: {"id": "op-x"}
        O._handoff = lambda ctx: {"store": self.handoff, "shared": self.handoff == "postgres", "known": True, "text": "handoff text"}
        O.realm_players = lambda ctx, rid, port: (self.players, {"characters_in_world": self.players, "connections": 0})
        O.realm_characters = lambda ctx, rid: self.chars
        O.references = lambda rb, rid, fks=None: self.refs
        W._need_writable = lambda ctx: ctx.rb
        W._control_ok = lambda ctx: True
        W.server_job = lambda ctx, job: self.jobs[job]

    def tearDown(self):
        for n, v in self.saved.items():
            setattr(O, n, v)
        W._need_writable, W._control_ok, W.server_job = self.wsaved

    def ctx(self, body):
        c = ctx_for(FakeApp(tempfile.mkdtemp()), body)
        c.rb.pg = types.SimpleNamespace(json=lambda sql: 3)                          # next free realm id
        c.need_rb = lambda: c.rb

        def rows_write(scope, action, build, **k):
            self.writes.append({"action": action, "sql": build(None), "k": k}); return {"change": {"action": action}}
        c.rows_write = rows_write
        return c

    # ---- address and port
    def test_address_is_detected_not_typed(self):
        a = O.detect_address(self.rows)
        self.assertEqual((a["address"], a["source"]), ("YOUR_SERVER_IP", "this machine's network interface")); self.assertTrue(a["agrees_with_realm1"])
        O._local_ipv4 = lambda: "10.0.0.5"                                           # behind a NAT: realm 1's address, and say why
        a = O.detect_address(self.rows); self.assertEqual((a["address"], a["source"]), ("YOUR_SERVER_IP", "realm 1's row")); self.assertIn("private", a["note"])
        O._local_ipv4 = lambda: None
        self.assertEqual(O.detect_address([dict(self.rows[0], ip_address="0.0.0.0")])["address"], None)
        O._local_ipv4 = lambda: "23.111.15.200"
        self.assertIn("YOUR_SERVER_IP", O.detect_address(self.rows)["note"])          # disagreement with realm 1 is shown

    def test_port_suggestion_skips_rows_processes_and_listeners(self):
        self.assertEqual(O.suggest_port(self.rows, {}), 7001)
        self.open_ports.add(7001)
        self.assertEqual(O.suggest_port(self.rows, {}), 7002)
        self.assertEqual(O.suggest_port(self.rows + [dict(GOOD, id=3, game_port=7002)], {5: {"set_up": True, "port": 7003}}), 7004)
        self.assertEqual(O.suggest_port(self.rows + [dict(GOOD, id=3, game_port=7002)], {}, own=3), 7002)   # its own port again
        self.open_ports.update(range(7001, 7100)); self.assertIsNone(O.suggest_port(self.rows, {}))

    def test_setup_offers_dwargon_first_and_says_it_is_not_a_template(self):
        s = O.realm_setup(self.ctx(None))
        self.assertEqual([r["id"] for r in s["reuse"]], [2]); self.assertEqual(s["reuse"][0]["suggested_port"], 7001)
        self.assertEqual(s["next_id"], 3); self.assertEqual(s["address"]["address"], "YOUR_SERVER_IP")
        self.assertIn("not a template", s["template_note"]); self.assertIn("Hosting by Design", s["provider_rule"])
        self.assertIn("never deleted", s["character_rule"])

    # ---- provision
    def test_new_server_row_is_added_disabled_at_the_detected_address_then_the_helper_gets_two_numbers(self):
        r = O.realm_provision(self.ctx({"mode": "new", "id": 3, "name": "Olympus", "port": 7001, "ip_address": "6.6.6.6", "server_limit": 100}))
        sql = self.writes[0]["sql"][0]
        self.assertIn("'YOUR_SERVER_IP'", sql); self.assertNotIn("6.6.6.6", sql)       # a typed address is ignored
        self.assertIn("false", sql.lower()); self.assertIn("'Olympus'", sql)
        self.assertIn("'Australia/Brisbane'", sql)                                   # realm 1's clock, not the UTC column default
        self.assertEqual({k: v for k, v in self.sent[0].items() if k != "job"}, {"action": "realm_provision", "realm": 3, "port": 7001})
        self.assertEqual(r["knock"], {"host": "YOUR_SERVER_IP", "port": 7001, "url": "http://YOUR_SERVER_IP:7001/"}); self.assertEqual(r["realm_id"], 3)

    def test_existing_row_is_moved_to_the_detected_address_and_port(self):
        O.realm_provision(self.ctx({"mode": "existing", "id": 2, "name": "Dwargon", "port": 7001}))
        self.assertIn("ip_address", self.writes[0]["sql"][0]); self.assertIn("7001", self.writes[0]["sql"][0])
        self.assertEqual(self.writes[0]["k"]["expected"][0]["values"], {"ip_address": "0.0.0.0", "game_port": 7000})
        self.assertEqual(self.sent[0]["realm"], 2)

    def test_provision_refusals_send_nothing(self):
        cases = [({"mode": "new", "id": 9, "name": "X", "port": 7001}, Conflict),         # stale next id
                 ({"mode": "new", "id": 3, "name": "X", "port": 7000}, Bad),              # the main game's port
                 ({"mode": "new", "id": 3, "name": "X", "port": 22}, Bad),
                 ({"mode": "new", "id": 3, "name": "X", "port": "7001"}, Bad),
                 ({"mode": "new", "id": 3, "name": "", "port": 7001}, Bad),
                 ({"mode": "existing", "id": 1, "port": 7001}, Bad),                      # realm 1 is the main process
                 ({"mode": "existing", "id": 7, "port": 7001}, common.NotFound),
                 ({"mode": "other", "id": 3, "port": 7001}, Bad)]
        for body, err in cases:
            with self.subTest(body=body), self.assertRaises(err):
                O.realm_provision(self.ctx(body))
        self.open_ports.add(7005)
        with self.assertRaises(Conflict):
            O.realm_provision(self.ctx({"mode": "new", "id": 3, "name": "X", "port": 7005}))     # something listens there
        self.rows.append(dict(GOOD, id=4, name="Busy", game_port=7006, enabled=True))
        with self.assertRaises(Conflict):
            O.realm_provision(self.ctx({"mode": "existing", "id": 2, "port": 7006}))             # another row's port
        with self.assertRaises(Refused):
            O.realm_provision(self.ctx({"mode": "existing", "id": 4, "port": 7006}))             # enabled: close it first
        self.players = 1
        with self.assertRaises(Refused):
            O.realm_provision(self.ctx({"mode": "existing", "id": 2, "port": 7001}))             # someone is in it
        self.players = 0
        O._local_ipv4 = lambda: None; self.rows[0]["ip_address"] = "0.0.0.0"
        with self.assertRaises(Refused):
            O.realm_provision(self.ctx({"mode": "new", "id": 3, "name": "X", "port": 7001}))     # no address detected
        self.assertEqual(self.sent, []); self.assertEqual(self.writes, [])

    def test_without_the_helper_nothing_is_written(self):
        W._control_ok = lambda ctx: False
        with self.assertRaisesRegex(Refused, "root helper is not there"):
            O.realm_provision(self.ctx({"mode": "new", "id": 3, "name": "Olympus", "port": 7001}))
        self.assertEqual(self.writes, []); self.assertEqual(self.sent, [])

    def test_address_override_for_nat_and_rigs(self):
        os.environ["GWED_PUBLIC_ADDRESS"] = "play.example.com"
        try:
            self.assertEqual(O.detect_address(self.rows)["address"], "play.example.com")
            os.environ["GWED_PUBLIC_ADDRESS"] = "not a host"
            self.assertEqual(O.detect_address(self.rows)["address"], "YOUR_SERVER_IP")       # a bad override is ignored
        finally:
            del os.environ["GWED_PUBLIC_ADDRESS"]

    def test_old_helper_is_named(self):
        def old(req):
            raise Refused("invalid action or job")
        O._helper = old
        with self.assertRaisesRegex(Refused, "older than v3"):
            O.realm_provision(self.ctx({"mode": "existing", "id": 2, "name": "Dwargon", "port": 7001}))

    # ---- finish (enable)
    def job(self, outside="seen", state="succeeded", port=7001, action="realm_provision", realm=2):
        j = "%032x" % (len(self.jobs) + 1)
        self.jobs[j] = {"job": j, "action": action, "realm_id": realm, "port": port, "state": state,
                        "error": None if state == "succeeded" else "boom", "realm": {"outside": {"result": outside}, "log": "/x/realm-2.log"}}
        return j

    def test_finish_enables_only_after_everything_passed(self):
        self.rows[1].update(ip_address="YOUR_SERVER_IP", game_port=7001); self.open_ports.add(7001)
        r = O.realm_provision_finish(self.ctx({"id": 2, "job": self.job()}))
        self.assertEqual(self.writes[0]["k"]["fields"], ["server.enabled"]); self.assertEqual(r["outside"], "seen")
        self.assertEqual(self.writes[0]["k"]["expected"][0]["values"], {"enabled": False})

    def test_finish_refusals(self):
        self.rows[1].update(ip_address="YOUR_SERVER_IP", game_port=7001); self.open_ports.add(7001)
        with self.assertRaisesRegex(Conflict, "Hosting by Design"):                               # not seen from outside
            O.realm_provision_finish(self.ctx({"id": 2, "job": self.job("not_seen")}))
        with self.assertRaises(Conflict):                                                         # "skipped" is not "seen" either
            O.realm_provision_finish(self.ctx({"id": 2, "job": self.job("skipped")}))
        self.assertEqual(self.writes, [])
        O.realm_provision_finish(self.ctx({"id": 2, "job": self.job("not_seen"), "acknowledge_unreachable": True}))
        self.assertIn("NOT passed", self.writes[0]["action"]); self.writes.clear()                # the audit says so
        for body, err in (({"id": 2, "job": self.job(state="failed")}, Refused), ({"id": 2, "job": self.job(state="running")}, Conflict),
                          ({"id": 2, "job": self.job(port=7003)}, Conflict), ({"id": 2, "job": self.job(action="restart")}, Bad),
                          ({"id": 2, "job": self.job(realm=5)}, Bad), ({"id": 2, "job": "nope"}, Bad), ({"id": 1, "job": self.job()}, Bad)):
            with self.subTest(body=body), self.assertRaises(err):
                O.realm_provision_finish(self.ctx(body))
        self.handoff = "memory"
        with self.assertRaisesRegex(Refused, "handoff text"):
            O.realm_provision_finish(self.ctx({"id": 2, "job": self.job()}))
        self.handoff = "postgres"; self.open_ports.clear()
        with self.assertRaisesRegex(Refused, "Nothing answers"):
            O.realm_provision_finish(self.ctx({"id": 2, "job": self.job()}))
        self.assertEqual(self.writes, [])

    # ---- close / delete
    def test_close_disables_then_takes_the_process_down_and_keeps_characters(self):
        self.rows[1].update(enabled=True, game_port=7001); self.chars = 12
        r = O.realm_close(self.ctx({"id": 2, "confirm": "Dwargon", "delete": False}))
        self.assertIn("enabled", self.writes[0]["sql"][0]); self.assertNotIn("DELETE", " ".join(self.writes[0]["sql"]).upper())
        self.assertEqual({k: v for k, v in self.sent[0].items() if k != "job"}, {"action": "realm_remove", "realm": 2})
        self.assertEqual(r["characters"], 12); self.assertIn("never deleted", r["character_rule"])

    def test_close_and_delete_refusals(self):
        self.rows.append(dict(GOOD, id=3, name="Olympus", game_port=7002, enabled=True))
        for body, err in (({"id": 1, "confirm": "Tempest"}, Refused),                             # the main realm
                          ({"id": 3, "confirm": "olympus"}, Bad),                                 # wrong name
                          ({"id": 9, "confirm": "x"}, common.NotFound),
                          ):
            with self.subTest(body=body), self.assertRaises(err):
                O.realm_close(self.ctx(body))
        self.players = 2
        with self.assertRaisesRegex(Refused, "2 players in server #3"):
            O.realm_close(self.ctx({"id": 3, "confirm": "Olympus"}))
        self.players = 0; self.refs = [{"table": "public.character_base", "rows": 5}]
        with self.assertRaisesRegex(Refused, "5 character_base.*never deleted"):                  # delete with characters: explained
            O.realm_close(self.ctx({"id": 3, "confirm": "Olympus", "delete": True}))
        self.assertEqual(self.sent, []); self.assertEqual(self.writes, [])
        O.realm_close(self.ctx({"id": 3, "confirm": "Olympus", "delete": False}))                 # closing WITH characters is allowed
        self.assertEqual(self.sent[0]["action"], "realm_remove")

    def test_row_cannot_be_removed_while_its_process_is_set_up(self):
        self.rows.append(dict(GOOD, id=3, name="Olympus", game_port=7002))
        self.procs = {3: {"set_up": True, "port": 7002, "unit": "godswar251-realm@3.service"}}
        with self.assertRaisesRegex(Refused, "still has a game process"):
            O.realm_delete(self.ctx({"id": 3, "confirm": "Olympus"}))
        self.procs = {3: {"set_up": False, "state": "removed"}}
        O.realm_delete(self.ctx({"id": 3, "confirm": "Olympus"})); self.assertIn("DELETE", self.writes[0]["sql"][0].upper())

    def test_placeholder_realm_2_can_be_deleted_when_unused(self):                                     # B21 item 16
        with self.assertRaisesRegex(Refused, r"used by public.character_base \(4\)"):
            self.refs = [{"table": "public.character_base", "rows": 4}]
            O.realm_delete(self.ctx({"id": 2, "confirm": "Dwargon"}))
        self.refs = []
        self.rows[1]["enabled"] = True
        with self.assertRaisesRegex(Refused, "Disable realm #2 first"):
            O.realm_delete(self.ctx({"id": 2, "confirm": "Dwargon"}))
        self.rows[1]["enabled"] = False
        with self.assertRaises(Bad):
            O.realm_delete(self.ctx({"id": 2, "confirm": "dwargon"}))
        O.realm_delete(self.ctx({"id": 2, "confirm": "Dwargon"}))
        self.assertIn("DELETE", self.writes[0]["sql"][0].upper()); self.assertIn('"id" = 2', self.writes[0]["sql"][0])
        with self.assertRaises(Refused):                                                        # realm 1 stays protected
            O.realm_delete(self.ctx({"id": 1, "confirm": "Tempest"}))
        # the page's Delete button: realm/close with delete = true, then realm/delete; no helper job for a row that
        # never had a game process
        self.writes.clear()
        r = O.realm_close(self.ctx({"id": 2, "confirm": "Dwargon", "delete": True}))
        self.assertIsNone(r["job"]); self.assertEqual(self.sent, []); self.assertEqual(self.writes, [])

    def test_process_records_and_connections_come_from_files(self):
        with tempfile.TemporaryDirectory() as d:
            write(d, "state/realm-2.json", json.dumps({"realm": 2, "action": "provision", "state": "succeeded", "port": 7001, "steps": [],
                                                       "outside": {"result": "seen"}, "secret": "x"}))
            write(d, "state/realm-3.json", "not json"); write(d, "state/other.json", "{}")
            write(d, "proc/net/tcp", "sl local rem st\n 0: 6D0F6F17:1B59 0A000001:C000 01 x\n 1: 6D0F6F17:1B59 0A000002:C001 01 x\n"
                                     " 2: 00000000:1B59 00000000:0000 0A x\n 3: 6D0F6F17:1B58 0A000001:C002 01 x\n")
            stub = os.path.join(d, "systemctl"); Path(stub).write_text("#!/bin/sh\necho ActiveState=inactive\n"); os.chmod(stub, 0o755)
            old = O.REALM_STATE, O.PROC, O.SYSTEMCTL
            O.REALM_STATE, O.PROC, O.SYSTEMCTL = os.path.join(d, "state"), os.path.join(d, "proc"), stub
            try:
                p = self.saved["realm_processes"]()
                self.assertEqual(list(p), [2]); self.assertTrue(p[2]["set_up"]); self.assertNotIn("secret", p[2])
                self.assertIn("not running", p[2]["problem"])
                self.assertEqual(O._realm_connections(7001), 2)                          # 0x1B59; the listener and port 7000 are not counted
            finally:
                O.REALM_STATE, O.PROC, O.SYSTEMCTL = old


if __name__ == "__main__":
    unittest.main(verbosity=2)
