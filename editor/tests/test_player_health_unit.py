"""Unit checks for wave 6 package A: modules/_session_health.py (login records -> plain answers),
modules/_login_check.py (login check merge, the packed Seal Jade finding), modules/player_health.py (overview, fix)
and the editor's refusal to hand out a packed Seal Jade. No database, no game server: SQL and HTTP are stubs.
Run: python3 -I tests/test_player_health_unit.py"""
import datetime, sys, types, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from modules import _login_check as LC, _session_health as SH, common, player_health as P  # noqa: E402
from modules.common import Bad, Conflict  # noqa: E402

T0 = datetime.datetime(2026, 11, 13, 0, 0, tzinfo=datetime.timezone.utc)


def row(endpoint, minute, outcome, stages=(), account=44, **kw):
    """One session_health row; stages = names reached (their time grows by a second each)."""
    r = {"id": minute * 10 + (1 if endpoint == "game" else 0), "endpoint": endpoint, "account_id": account,
         "started_at": (T0 + datetime.timedelta(minutes=minute, seconds=5 if endpoint == "game" else 0)).isoformat(),
         "duration_ms": 6000, "outcome": outcome, "reason": None, "error_type": None, "error_text": None,
         "transport_error": None, "client_build": 4102004 if endpoint == "login" else None, "network": "203.0.113.0",
         "character_id": 10 if endpoint == "game" else None, "quarantined": None,
         "stages": {name: 1000 * (i + 1) for i, name in enumerate(stages)}}
    r.update(kw)
    return r


LOGIN_OK = ("login", "accepted", "server_list", "server_selected", "redirect")
WORLD = ("game_login", "character_screen", "enter_requested", "entered", "client_ready", "ui_ready", "bootstrap")


def one(*rows, now=None):
    attempts = SH.pair(list(rows))
    return [SH.explain(a, now or T0 + datetime.timedelta(days=1)) for a in attempts]


class Explain(unittest.TestCase):
    def test_a_normal_session_is_one_green_attempt(self):
        e = one(row("login", 0, "accepted", LOGIN_OK), row("game", 0, "peer_eof", WORLD, duration_ms=30 * 60000))
        self.assertEqual(len(e), 1); self.assertEqual(e[0]["level"], "ok"); self.assertIn("29 minutes", e[0]["detail"])

    def test_the_live_case_reads_in_plain_words(self):
        # seen live 2026-11-13: [game-fault] opcode=10006 name=EnterGame InvalidDataException: A packed Seal Jade requires ...
        e = one(row("login", 0, "accepted", LOGIN_OK),
                row("game", 0, "fault", ("game_login", "character_screen", "enter_requested"), reason="opcode 10006",
                    error_type="InvalidDataException", error_text="A packed Seal Jade requires a native-width linked pet ID."))[0]
        self.assertEqual(e["level"], "bad"); self.assertEqual(e["title"], "Server error while entering the world")
        self.assertEqual(e["detail"], "A packed Seal Jade in the bag has no pet inside it"); self.assertIn("Login check", e["fix"])
        self.assertEqual(SH.status([e]), "red")

    def test_refused_logins(self):
        bad = one(row("login", 0, "refused", ("login",), reason="bad_credentials"))[0]
        self.assertEqual((bad["level"], bad["title"]), ("warn", "Wrong password"))
        self.assertEqual(one(row("login", 0, "refused", ("login",), reason="banned"))[0]["level"], "bad")
        self.assertEqual(one(row("login", 0, "refused", ("login",), reason="old_client"))[0]["title"], "The game client is too old")
        self.assertEqual(one(row("login", 0, "refused", ("login",), reason="something_new"))[0]["title"], "Login refused")

    def test_never_reached_the_game_server(self):
        login = row("login", 0, "accepted", LOGIN_OK)
        self.assertEqual(one(login)[0]["code"], "never_reached_game")
        self.assertEqual(one(login, now=T0 + datetime.timedelta(seconds=30))[0]["code"], "in_progress")
        self.assertEqual(one(row("login", 0, "accepted", ("login", "accepted", "server_list")))[0]["code"], "stopped_at_server_list")

    def test_game_side_problems(self):
        ok = row("login", 0, "accepted", LOGIN_OK)
        self.assertEqual(one(ok, row("game", 0, "refused", ("game_login",), reason="no_password_login"))[0]["code"], "handover_refused")
        load = one(ok, row("game", 0, "refused", ("game_login",), reason="character_load:login:invalid_data",
                           error_text="Owned titles contain invalid or duplicate IDs."))[0]
        self.assertEqual(load["level"], "bad"); self.assertIn("title twice", load["detail"])
        self.assertEqual(one(ok, row("game", 0, "peer_eof", ("game_login", "character_screen")))[0]["code"], "left_at_character_screen")
        self.assertEqual(one(ok, row("game", 0, "peer_eof", WORLD[:4]))[0]["code"], "loading_never_finished")
        self.assertEqual(one(ok, row("game", 0, "peer_eof", WORLD, duration_ms=20000))[0]["code"], "early_drop")
        self.assertEqual(one(ok, row("game", 0, "fault", WORLD, duration_ms=600000, reason="opcode 10035", error_type="NullReferenceException",
                                     error_text="boom"))[0]["code"], "play_fault")
        self.assertEqual(one(ok, row("game", 0, "fault", WORLD, duration_ms=600000, error_type="IOException"))[0]["level"], "ok",
                         "a broken connection is not a server error")
        self.assertEqual(one(ok, row("game", 0, "replaced", WORLD, duration_ms=600000))[0]["level"], "info")
        self.assertEqual(one(ok, row("game", 0, "kicked", WORLD, duration_ms=600000))[0]["title"], "Disconnected by staff")
        self.assertEqual(one(ok, row("game", 0, "server_cancelled", WORLD, duration_ms=600000))[0]["code"], "server_stopped")
        self.assertEqual(one(ok, row("game", 0, "fault", WORLD, duration_ms=600000, error_type="TimeoutException", transport_error="TimeoutException"))[0]["code"], "timeout")
        self.assertEqual(one(ok, row("game", 0, "peer_eof", ("game_login", "character_screen", "enter_requested"), character_id=None))[0]["code"], "no_character")

    def test_quarantine_is_said(self):
        e = one(row("login", 0, "accepted", LOGIN_OK), row("game", 0, "peer_eof", WORLD, duration_ms=30 * 60000,
                quarantined="bag slot 17 item 10109: A packed Seal Jade requires a native-width linked pet ID."))[0]
        self.assertEqual(e["level"], "warn"); self.assertEqual(e["code"], "quarantined")
        self.assertIn("A packed Seal Jade in the bag has no pet inside it (bag page 1, slot 18)", e["detail"])

    def test_pairing(self):
        rows = [row("game", 0, "peer_eof", WORLD, duration_ms=10 * 60000), row("login", 0, "accepted", LOGIN_OK),
                row("login", 30, "refused", ("login",), reason="bad_credentials"), row("login", 31, "accepted", LOGIN_OK),
                row("game", 40, "peer_eof", WORLD, duration_ms=10 * 60000)]       # a reconnect with no login row near it
        a = SH.pair(rows)
        self.assertEqual([(bool(x["login"]), bool(x["game"])) for x in a], [(True, True), (True, False), (True, False), (False, True)])

    def test_status_lights(self):
        ok = {"level": "ok", "code": "ok"}; warn = {"level": "warn", "code": "early_drop"}; bad = {"level": "bad", "code": "enter_fault"}
        screen = {"level": "info", "code": "left_at_character_screen"}
        self.assertEqual(SH.status([]), "grey"); self.assertEqual(SH.status([bad, ok]), "green"); self.assertEqual(SH.status([ok, bad]), "red")
        self.assertEqual(SH.status([ok, warn]), "amber"); self.assertEqual(SH.status([warn, bad, ok]), "amber", "two problems in the last five")
        self.assertEqual(SH.status([ok, screen, screen, screen]), "amber", "stuck at the character screen")
        self.assertEqual(SH.status([ok], [{"severity": "blocks"}]), "red"); self.assertEqual(SH.status([], [{"severity": "hidden"}]), "amber")

    def test_times_and_text(self):
        self.assertEqual(SH.local_text(SH.parse_time("2026-11-13T00:05:00.123456+00:00")), "2026-11-13 10:05")
        self.assertEqual(SH.local_text(SH.parse_time("2026-11-13 10:05:00.12+10")), "2026-11-13 10:05")
        self.assertEqual(SH.duration_text(45000), "45 seconds"); self.assertEqual(SH.duration_text(125 * 60000), "2 h 05 min")
        self.assertEqual(SH.build_text(4102004), "2.51.002 (4102004)"); self.assertEqual(SH.build_text(7), "7"); self.assertEqual(SH.build_text(None), "")
        self.assertEqual(SH.plain_error("something else"), "something else")


class Summary(unittest.TestCase):
    def attempts(self, rows):
        out = SH.pair(rows)
        for a in out:
            a["explained"] = SH.explain(a, T0 + datetime.timedelta(days=3)); a["account_id"] = 44
        return out

    def test_counts_and_top_reasons(self):
        fault = dict(reason="opcode 10006", error_type="InvalidDataException", error_text="x")
        rows = [row("login", 0, "accepted", LOGIN_OK), row("game", 0, "peer_eof", WORLD, duration_ms=600000),
                row("login", 60, "accepted", LOGIN_OK), row("game", 60, "fault", WORLD[:3], **fault),
                row("login", 61, "refused", ("login",), reason="bad_credentials")]
        s = SH.summary({44: self.attempts(rows)}, T0 + datetime.timedelta(hours=2))
        self.assertEqual((s["logins_today"], s["failed_today"], s["failed_percent"]), (3, 1, 33))
        self.assertEqual([r["code"] for r in s["top_reasons"]], ["enter_fault", "login_bad_credentials"])
        self.assertEqual(s["top_reasons"][0]["accounts"], 1)

    def test_what_changed_points_at_the_restart(self):
        fault = dict(reason="opcode 10006", error_type="InvalidDataException", error_text="x")
        rows = []
        for m in range(0, 300, 30):                                            # ten good logins
            rows += [row("login", m, "accepted", LOGIN_OK), row("game", m, "peer_eof", WORLD, duration_ms=600000)]
        rows.append(row("game", 330, "server_cancelled", WORLD, duration_ms=60000))     # the restart at 05:31
        for m in range(340, 440, 20):                                          # then every login fails
            rows += [row("login", m, "accepted", LOGIN_OK), row("game", m, "fault", WORLD[:3], **fault)]
        marks = SH.markers(rows)
        self.assertEqual([m["what"] for m in marks], ["the server restart"])
        hint = SH.what_changed(self.attempts(rows), marks)
        self.assertEqual((hint["before_percent"], hint["after_percent"], hint["failed_after"]), (0, 100, 5))
        self.assertIn("after the server restart", hint["text"]); self.assertIn("Server error while entering the world", hint["text"])
        self.assertIsNone(SH.what_changed(self.attempts(rows[:20]), marks), "no failures, no hint")

    def test_a_new_client_version_is_a_marker(self):
        rows = [row("login", 0, "accepted", LOGIN_OK), row("login", 60, "accepted", LOGIN_OK, client_build=4102005)]
        self.assertEqual([m["what"] for m in SH.markers(rows)], ["the first login with client version 4102005"])


CHARS = [{"id": 10, "account_id": 44, "name": "Tester", "username": "tester"}, {"id": 14, "account_id": 45, "name": "Other", "username": "other"}]
JADE = {"row": 478, "cid": 10, "location": 1, "slot": 17, "item": 10109}


class LoginCheck(unittest.TestCase):
    def test_database_finding_without_the_new_server_blocks(self):
        asked = []
        found = LC.database_findings(lambda sql: asked.append(sql) or [JADE], [10, 14], self_healing=False)
        self.assertIn("prop_id = 10109", asked[0]); self.assertIn("sealed_pet_items", asked[0]); self.assertIn("ARRAY[10,14]", asked[0])
        f = found[10][0]
        self.assertEqual((f["severity"], f["where"], f["fix"]["row"]), ("blocks", "Bag page 1, slot 18", 478))
        self.assertEqual(f["fix"]["label"], "Remove this Seal Jade (it has no pet inside)")
        self.assertEqual(LC.database_findings(lambda sql: [JADE], [10], self_healing=True)[10][0]["severity"], "hidden")
        self.assertEqual(LC.database_findings(lambda sql: self.fail("no query for no characters"), []), {})

    def test_merge_red_first_and_no_double_report(self):
        server = {44: {"accountId": 44, "canEnter": True, "findings": [
                      {"severity": "hidden", "stage": "item", "code": "unsendable_item", "message": "A packed Seal Jade requires a native-width linked pet ID.",
                       "container": "bag", "slot": 17, "itemId": 10109}]},
                  45: {"accountId": 45, "canEnter": False, "findings": [
                      {"severity": "blocks", "stage": "load", "code": "invalid_data", "message": "Owned titles contain invalid or duplicate IDs."},
                      {"severity": "hidden", "stage": "item", "code": "unsendable_item", "message": "weird", "container": "equipment", "slot": 3, "itemId": 2001}]}}
        db = LC.database_findings(lambda sql: [JADE], [10, 14], self_healing=True)
        out = LC.merge(CHARS, server, db, {(14, 0, 3): 900}, {2001: "Sword"})
        self.assertEqual([(r["character"], r["light"], r["can_enter"]) for r in out], [("Other", "red", False), ("Tester", "amber", True)])
        self.assertEqual(len(out[1]["findings"]), 1, "the jade is reported once, by the row that has the Fix button")
        titles = [f["title"] for f in out[0]["findings"]]
        self.assertEqual(titles[0], "The title list holds a title twice or an unknown title")
        self.assertEqual(out[0]["findings"][0]["fix"], None)
        self.assertEqual(out[0]["findings"][1]["fix"]["row"], 900); self.assertEqual(out[0]["findings"][1]["fix"]["label"], "Remove this Sword")
        self.assertEqual(LC.sentence(out[0])[0], "This would stop Other logging in: The title list holds a title twice or an unknown title.")
        self.assertIn("The server will hide this from Tester", LC.sentence(out[1])[0])
        self.assertEqual([r["light"] for r in LC.merge(CHARS, None, {}, {}, {})], ["green", "green"])

    def test_server_results(self):
        calls = []

        def http(method, port, path, body, timeout):
            calls.append((method, port, path, body)); return 200, {"ok": True, "results": [{"accountId": a, "canEnter": True, "findings": []} for a in body["accountIds"]]}
        got = LC.server_results(http, 9090, list(range(1, 451)), actor="owner")
        self.assertEqual(len(got), 450); self.assertEqual([len(c[3]["accountIds"]) for c in calls], [200, 200, 50])
        self.assertEqual(calls[0][2], "/admin/character/entry-check"); self.assertEqual(calls[0][3]["actor"], "owner")
        self.assertIsNone(LC.server_results(lambda *a, **k: (404, {"error": "unknown admin route"}), 9090, [1]))
        self.assertIsNone(LC.server_results(lambda *a, **k: (_ for _ in ()).throw(OSError("down")), 9090, [1]))
        self.assertIsNone(LC.server_results(http, None, [1]))
        self.assertTrue(LC.server_offers_check({"operations": {"path:/admin/character/entry-check": {}}}))
        self.assertFalse(LC.server_offers_check({"operations": {"character.kick": {}}}))


class Ctx:
    """A fake request context: canned SQL answers, canned capabilities, recorded writes."""
    def __init__(self, body=None, offered=True, records=True, jade=True, online=False):
        self.body, self.query, self.writes, self.jade, self.records = body or {}, {}, [], jade, records
        self.caps = {"port": 9090, "operations": {"path:/admin/character/entry-check": {}} if offered else {}}
        self.rb = types.SimpleNamespace(writable=True)
        self.app = types.SimpleNamespace()
        self.catalog = types.SimpleNamespace(name=lambda t, i: {10109: "Seal Jade(Packed)"}.get(i))

    def need_rb(self): return self.rb
    def qint(self, key, default=None, lo=None, hi=None): return int(self.query.get(key, default))

    def bag_item(self, cid, op, p):
        self.writes.append(("bag_item", cid, op, p)); self.jade = False
        return {"mode": "database", "change": {"id": "rb-1", "action": "remove"}}


def sql_for(ctx, rows=()):
    def q(c, sql):
        if "FROM character_base c JOIN accounts" in sql:
            return [dict(x, level=200, account_status=0, online=False, last_login=None) for x in CHARS if ("c.id = %d" % x["id"]) in sql or "c.id =" not in sql]
        if "sealed_pet_items" in sql:
            return [JADE] if ctx.jade else []
        if "FROM session_health" in sql.replace("public.", "") or "FROM (SELECT * FROM session_health" in sql:
            return list(rows)
        if "FROM character_items WHERE user_id" in sql:
            return [JADE]
        raise AssertionError("unexpected query: " + sql)

    def v(c, expr):
        if "to_regclass" in expr: return ctx.records
        if "has_table_privilege" in expr: return True
        raise AssertionError("unexpected scalar: " + expr)
    return q, v


class Module(unittest.TestCase):
    def run_with(self, ctx, fn, rows=(), http=None):
        q, v = sql_for(ctx, rows)
        old = (P.W._q, P.W._v, common._http)
        P.W._q, P.W._v = q, v
        common._http = http or (lambda method, port, path, body, timeout=0: (200, {"ok": True, "results": [
            {"accountId": a, "canEnter": True, "findings": ([{"severity": "hidden", "stage": "item", "code": "unsendable_item", "container": "bag",
              "slot": 17, "itemId": 10109, "message": "A packed Seal Jade requires a native-width linked pet ID."}] if a == 44 and ctx.jade else [])}
            for a in body["accountIds"]]}))
        try:
            return fn(ctx)
        finally:
            P.W._q, P.W._v, common._http = old

    def test_today_on_the_old_server_tester_is_red_with_the_reason(self):
        ctx = Ctx(offered=False, records=False)
        d = self.run_with(ctx, P.overview)
        self.assertFalse(d["records_ready"]); self.assertIn("next game server update", d["records_note"])
        first = d["players"][0]
        self.assertEqual(([c["name"] for c in first["characters"]], first["light"]), (["Tester"], "red"))
        self.assertEqual(first["headline"], "A packed Seal Jade has no pet inside it")
        self.assertEqual(first["fix"], "Remove this Seal Jade (it has no pet inside)")
        self.assertEqual((d["check"]["blocked"], d["check"]["checked"], d["check"]["server"]), (1, 2, False))
        self.assertEqual(d["counts"], {"red": 1, "amber": 0, "green": 0, "grey": 1})

    def test_with_the_new_server_and_records(self):
        fault = dict(reason="opcode 10006", error_type="InvalidDataException", error_text="A packed Seal Jade requires a native-width linked pet ID.")
        now = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(hours=1)

        def at(r, minutes):
            r["started_at"] = (now + datetime.timedelta(minutes=minutes)).isoformat(); return r
        rows = [at(row("login", 0, "accepted", LOGIN_OK), 0), at(row("game", 0, "fault", WORLD[:3], **fault), 0.1),
                at(row("login", 5, "accepted", LOGIN_OK, account=45), 5), at(row("game", 5, "peer_eof", WORLD, account=45, duration_ms=1800000, character_id=14), 5.1),
                at(row("login", 6, "refused", ("login",), reason="unknown_account", account=None), 6)]
        ctx = Ctx(offered=True, records=True)
        d = self.run_with(ctx, P.overview, rows)
        self.assertTrue(d["records_ready"]); self.assertTrue(d["check"]["server"])
        by = {p["characters"][0]["name"]: p for p in d["players"]}
        self.assertEqual(by["Tester"]["light"], "red", "the last recorded login failed")
        self.assertEqual(by["Tester"]["timeline"][0]["title"], "Server error while entering the world")
        self.assertEqual(by["Tester"]["timeline"][0]["detail"], "A packed Seal Jade in the bag has no pet inside it")
        self.assertEqual(by["Tester"]["timeline"][0]["network"], "203.0.113.0")
        self.assertEqual(by["Other"]["light"], "green"); self.assertEqual(by["Other"]["timeline"][0]["reached"], "in the world")
        self.assertEqual(d["stats"]["unknown_name_attempts"], 1)
        self.assertEqual(d["builds"], [{"build": 4102004, "text": "2.51.002 (4102004)", "accounts": 2, "newest": True}])
        self.assertEqual(d["check"]["hidden"], 1, "the new server hides the item instead of failing the login")

    def test_fix_removes_exactly_the_reported_item(self):
        fixit = {"kind": "remove_item", "row": 478, "location": 1, "slot": 17, "item": 10109}
        ctx = Ctx({"character_id": 10, "fix": fixit})
        r = self.run_with(ctx, P.fix)
        self.assertEqual(ctx.writes, [("bag_item", 10, "remove", {"slot": 17})])
        self.assertEqual(r["check"]["light"], "green"); self.assertEqual(r["removed"], {"name": "Seal Jade(Packed)", "where": "Bag page 1, slot 18"})
        ctx = Ctx({"character_id": 10, "fix": dict(fixit, slot=3)})
        self.assertRaises(Conflict, self.run_with, ctx, P.fix); self.assertEqual(ctx.writes, [])
        ctx = Ctx({"character_id": 10, "fix": fixit}, jade=False)
        self.assertRaises(Conflict, self.run_with, ctx, P.fix)
        self.assertRaises(Bad, self.run_with, Ctx({"character_id": 10, "fix": {"kind": "delete_character"}}), P.fix)

    def test_check_route(self):
        r = self.run_with(Ctx({}), P.check)
        self.assertEqual((r["checked"], r["blocked"], r["hidden"]), (2, 0, 1))
        r = self.run_with(Ctx({"character_id": 14}), P.check)
        self.assertEqual((r["checked"], r["results"][0]["light"]), (1, "green"))


class Prevention(unittest.TestCase):
    def test_the_packed_seal_jade_cannot_be_handed_out(self):
        R = common.R
        self.assertIn("no pet inside", R.ungrantable_reason(10109)); self.assertIsNone(R.ungrantable_reason(10108)); self.assertIsNone(R.ungrantable_reason("x"))
        self.assertRaises(Bad, common.refuse_ungrantable, 10109); common.refuse_ungrantable(4100)
        rb = types.SimpleNamespace(item_meta=lambda pid: {"server": True, "overlap": 1, "name": "x", "defaults": {"bound": 0, "item_quality": 1, "item_grade": 1}},
                                   cat=lambda: {"attributes": {}})
        self.assertRaises(R.RebornError, R.item_values, rb, {"prop_id": 10109}, None, False)
        self.assertRaises(R.RebornError, R.item_values, rb, {"prop_id": 10109}, None, True)      # force does not unlock it
        self.assertRaises(R.RebornError, R.item_values, rb, {"prop_id": 10109}, {"prop_id": 4100, "bound": 0, "item_quality": 1, "item_grade": 1}, False)

    def test_every_item_adding_path_asks(self):
        need = {"modules/common.py": 2, "modules/inventory.py": 1, "modules/awards.py": 2, "modules/login_awards.py": 1,
                "modules/library.py": 1, "modules/world.py": 1, "reborn.py": 3}
        for name, least in need.items():
            text = (ROOT / name).read_text()
            self.assertGreaterEqual(text.count("ungrantable"), least, name)

    def test_the_server_has_the_same_rule(self):
        src = (ROOT.parent / "server" / "src" / "Godswar.Server" / "State" / "PetItemCatalog.cs").read_text()
        self.assertIn("CannotBeGrantedReason", src); self.assertIn("itemId == PackedSealJade", src)
        self.assertEqual(sorted(common.R.UNGRANTABLE), [10109])


if __name__ == "__main__":
    unittest.main(verbosity=1)
