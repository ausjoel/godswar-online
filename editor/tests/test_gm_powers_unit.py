"""Unit checks for modules/gm_powers.py: the GM power catalogue (the editor's copy equals the server source), the
switch save (only known keys, version guard, no row = allowed), and grant / revoke of the GM flag (one column, checked
against what the page showed). No database, no game server: SQL and the capabilities answer are stubs.
Run: python3 -I tests/test_gm_powers_unit.py"""
import json, os, re, subprocess, sys, types, unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from modules import gm_powers as G, world as W, common  # noqa: E402
from modules.common import Bad, Conflict, NotFound, Refused  # noqa: E402

SOURCE = ROOT.parent / "server" / "src" / "Godswar.Server" / "Application" / "Operators" / "GmPowerPolicy.cs"
INV = G.load_inventory()


def caps(denied=()):
    """What the W5B game build answers on /admin/capabilities."""
    return {"version": 1, "gmAuthority": "character_flag", "gmCommandsEnabled": True,
            "gmPowers": {"version": 1, "table": "gm_power_policy", "source": "database", "master": "master" not in denied,
                         "powers": [dict(p, allowed=p["key"] not in denied, effective=True) for p in INV]}}


class Inventory(unittest.TestCase):
    def test_editor_copy_equals_the_server_source(self):
        p = subprocess.run([sys.executable, "-I", str(ROOT / "build_gm_powers_inventory.py"), "--check"], capture_output=True, text=True)
        self.assertEqual(p.returncode, 0, p.stdout + p.stderr)

    def test_every_chat_command_of_the_server_is_listed(self):
        keys = {p["key"] for p in INV}
        self.assertEqual(len(keys), len(INV)); self.assertIn("master", keys); self.assertIn("power.gate_bypass", keys)
        ops = (SOURCE.parent.parent.parent / "Game" / "GameClientHandler.OperatorCommands.cs").read_text()
        block = ops[ops.index("OperatorCommandWords ="):ops.index("];", ops.index("OperatorCommandWords ="))]
        alias = {"**": "gm", "panel": "gm"}
        for word in re.findall(r'"/([a-z*]+)"', block):
            self.assertIn("cmd." + alias.get(word, word), keys, word)
        for word in ("level", "talentpoints", "zodiacenergy", "item", "ruby", "medusatitle"):
            self.assertIn("cmd." + word, keys)
        for p in INV:
            self.assertRegex(p["key"], G.KEY); self.assertIn(p["group"], dict(G.GROUPS)); self.assertTrue(p["label"] and p["description"])

    def test_every_gm_check_in_the_server_goes_through_the_policy(self):
        """No bare HasOperatorAuthorityAsync call is left outside the three command dispatchers (which check the
        command's own switch right after it) and the GM window's 'unknown command' reply."""
        game = SOURCE.parent.parent.parent / "Game"
        allowed = {"GameClientHandler.OperatorAuthority.cs", "GameClientHandler.OperatorCommands.cs", "GameClientHandler.DeveloperCommands.cs",
                   "GameClientHandler.DeveloperProgression.cs", "GameClientHandler.DeveloperMedusaTitle.cs", "GameClientHandler.GmPanel.cs"}
        bare = sorted(f.name for f in game.glob("*.cs") if "HasOperatorAuthorityAsync(cancellationToken)" in f.read_text() and f.name not in allowed)
        self.assertEqual(bare, [])
        for name in allowed - {"GameClientHandler.OperatorAuthority.cs", "GameClientHandler.GmPanel.cs"}:
            self.assertIn("RefuseSwitchedOffCommandAsync", (game / name).read_text(), name)


class Describe(unittest.TestCase):
    def test_defaults_are_everything_on(self):
        d = G.describe(caps(), {}, {}, True)
        self.assertTrue(d["ready"]); self.assertTrue(d["master"]); self.assertTrue(all(p["allowed"] for p in d["powers"]))
        self.assertEqual(d["authority"], "character_flag"); self.assertEqual(d["pending"], []); self.assertEqual(len(d["powers"]), len(INV))

    def test_rows_switch_off_and_pending_shows_what_the_game_has_not_read_yet(self):
        rows = {"cmd.give": {"allowed": False, "note": "", "updated_at": "2026-11-12T10:00:00Z", "updated_by": "gweditor"}}
        d = G.describe(caps(), {}, rows, True)
        give = next(p for p in d["powers"] if p["key"] == "cmd.give")
        self.assertFalse(give["allowed"]); self.assertTrue(give["server_allowed"]); self.assertEqual(d["pending"], ["cmd.give"])
        self.assertEqual(G.describe(caps(["cmd.give"]), {}, rows, True)["pending"], [])
        self.assertFalse(G.describe(caps(), {}, {"master": {"allowed": False}}, True)["master"])

    def test_old_game_build_shows_the_inventory_read_only(self):
        d = G.describe({"version": 1, "gmAuthority": "account_allowlist"}, {"GODSWAR_DEVELOPER_COMMANDS_ENABLED": "true"}, {}, False)
        self.assertFalse(d["ready"]); self.assertFalse(d["from_server"]); self.assertEqual(len(d["powers"]), len(INV))
        self.assertIn("no GM power switches yet", d["why_not_ready"]); self.assertEqual(d["authority"], "account_allowlist"); self.assertTrue(d["commands_enabled"])
        d = G.describe(None, {"GODSWAR_CHARACTER_GM_FLAGS": "true"}, {}, False)
        self.assertEqual(d["authority"], "character_flag"); self.assertFalse(d["commands_enabled"])
        self.assertIn("has not created the table", G.describe(caps(), {}, {}, False)["why_not_ready"])


class Writes(unittest.TestCase):
    def setUp(self):
        self.saved = G._capabilities, G._table_ready, G._rows, W._need_writable, W._q, W._ver_guard, W._write
        self.caps, self.table, self.rows, self.q, self.writes = caps(), True, {}, None, []
        G._capabilities = lambda ctx: (self.caps, None if self.caps else "the game server did not answer")
        G._table_ready = lambda ctx: self.table
        G._rows = lambda ctx: self.rows
        W._need_writable = lambda ctx: ctx.rb
        W._q = lambda ctx, sql: self.q
        W._ver_guard = lambda parts, version, label: "GUARD %s" % version
        W._write = lambda ctx, scope, action, stmts, fields, **k: self.writes.append({"scope": scope, "action": action, "stmts": stmts, "fields": fields, "k": k}) or {"change": {"action": action}}

    def tearDown(self):
        G._capabilities, G._table_ready, G._rows, W._need_writable, W._q, W._ver_guard, W._write = self.saved

    def ctx(self, body):
        c = types.SimpleNamespace(app=None, body=body, query={}, rb=types.SimpleNamespace(db="godswar_test", writable=True))

        def rows_write(scope, action, build, **k):
            self.writes.append({"scope": scope, "action": action, "stmts": build(None), "k": k}); return {"change": {"action": action}}
        c.rows_write = rows_write
        return c

    def test_switch_off_writes_a_row_and_on_removes_it(self):
        self.rows = {"cmd.kick": {"allowed": False}}
        r = G.save(self.ctx({"changes": {"cmd.give": False, "cmd.dupe": False, "cmd.kick": True, "cmd.ban": True}, "version": "a" * 32}))
        w = self.writes[0]
        self.assertEqual(w["stmts"][0], "GUARD " + "a" * 32)
        self.assertEqual(len(w["stmts"]), 4)                                           # guard + 2 upserts + 1 delete; cmd.ban was on already
        self.assertTrue(all("ON CONFLICT (\"power_key\")" in s and "false" in s.lower() for s in w["stmts"][1:3]))
        self.assertEqual(w["stmts"][3], "DELETE FROM gm_power_policy WHERE power_key = 'cmd.kick'")
        self.assertEqual(w["action"], "GM powers: OFF cmd.dupe, cmd.give; ON cmd.kick"); self.assertEqual(w["k"]["applies"], "hot_reload")
        self.assertEqual(w["fields"], ["gm_power_policy.*"]); self.assertEqual((r["off"], r["on"]), (["cmd.dupe", "cmd.give"], ["cmd.kick"]))
        self.assertEqual(w["scope"][0]["ignore"], ["updated_at", "updated_by"])

    def test_master_switch(self):
        G.save(self.ctx({"changes": {"master": False}, "version": "new"}))
        self.assertIn("'master'", self.writes[0]["stmts"][1]); self.assertEqual(self.writes[0]["action"], "GM powers: OFF master")

    def test_save_refusals_write_nothing(self):
        for body, err in (({"changes": {"cmd.nosuch": False}, "version": "new"}, Bad), ({"changes": {"cmd.give": "off"}, "version": "new"}, Bad),
                          ({"changes": {}, "version": "new"}, Bad), ({"changes": {"cmd.give": True}, "version": "new"}, Bad),      # already on
                          ({"changes": {"cmd.give'; DROP TABLE x;--": False}, "version": "new"}, Bad), ({"version": "new"}, Bad)):
            with self.subTest(body=body), self.assertRaises(err):
                G.save(self.ctx(body))
        self.caps = {"version": 1}                                                     # a game build without the switches
        with self.assertRaisesRegex(Refused, r"would do\s+nothing"):
            G.save(self.ctx({"changes": {"cmd.give": False}, "version": "new"}))
        self.caps = None
        with self.assertRaises(Refused):
            G.save(self.ctx({"changes": {"cmd.give": False}, "version": "new"}))
        self.caps, self.table = caps(), False
        with self.assertRaises(Refused):
            G.save(self.ctx({"changes": {"cmd.give": False}, "version": "new"}))
        self.assertEqual(self.writes, [])

    def test_real_version_guard_rejects_a_missing_version(self):
        W._ver_guard = self.saved[5]
        with self.assertRaises(Bad):
            G.save(self.ctx({"changes": {"cmd.give": False}}))

    ROW = {"name": "Odin3", "gm": 0, "deleted": False, "account_id": 3, "username": "gmhost", "account_status": 0}

    def test_grant_writes_only_the_gm_column_and_undo_restores_only_it(self):
        self.q = dict(self.ROW)
        r = G.grant(self.ctx({"character_id": 7, "gm": True, "expected": 0}))
        w = self.writes[0]
        self.assertEqual(w["stmts"], ['UPDATE character_base SET "GM" = 1 WHERE "id" = 7'])
        self.assertEqual(w["k"]["fields"], ["character_base.GM"]); self.assertEqual(w["k"]["applies"], "now")
        self.assertEqual(w["k"]["expected"], [{"table": "character_base", "where": {"id": 7}, "values": {"GM": 0}}])
        sc = w["scope"][0]
        self.assertEqual(sc["pk"], ["id"]); self.assertNotIn("GM", sc["ignore"]); self.assertNotIn("id", sc["ignore"])
        for col in ("fighter_job_lv", "name", "account_id", "checkpoint_owner_id"):        # nothing else is ever restored by Undo
            self.assertIn(col, sc["ignore"])
        self.assertNotIn("lock_cids", w["k"])                                          # the player may stay online
        self.assertEqual(r["character"], {"id": 7, "name": "Odin3", "gm": 1}); self.assertIn("grant GM rights: Odin3", w["action"])

    def test_revoke(self):
        self.q = dict(self.ROW, gm=1)
        G.grant(self.ctx({"character_id": 7, "gm": False, "expected": 1}))
        self.assertEqual(self.writes[0]["stmts"], ['UPDATE character_base SET "GM" = 0 WHERE "id" = 7'])
        self.q = dict(self.ROW, gm=1, account_status=1, deleted=True); self.writes.clear()   # a revoke is always allowed
        G.grant(self.ctx({"character_id": 7, "gm": False, "expected": 1})); self.assertEqual(len(self.writes), 1)

    def test_grant_refusals(self):
        cases = [(dict(self.ROW), {"character_id": 7, "gm": True, "expected": 1}, Conflict),       # changed since shown
                 (dict(self.ROW, gm=1), {"character_id": 7, "gm": True, "expected": 1}, Bad),      # already a GM
                 (dict(self.ROW, deleted=True), {"character_id": 7, "gm": True, "expected": 0}, Refused),
                 (dict(self.ROW, account_status=1), {"character_id": 7, "gm": True, "expected": 0}, Refused),
                 (dict(self.ROW), {"character_id": 7, "gm": "yes", "expected": 0}, Bad),
                 (dict(self.ROW), {"character_id": "7; DROP", "gm": True, "expected": 0}, Bad),
                 (None, {"character_id": 7, "gm": True, "expected": 0}, NotFound)]
        for row, body, err in cases:
            self.q = row
            with self.subTest(body=body, row=row), self.assertRaises(err):
                G.grant(self.ctx(body))
        self.assertEqual(self.writes, [])

    def test_find_escapes_like_and_limits_length(self):
        seen = []
        W._q = lambda ctx, sql: seen.append(sql) or []
        c = self.ctx(None); c.query = {"q": "Jo%_'x"}
        G.find(c)
        self.assertIn("'Jo\\%\\_''x%'", seen[0]); self.assertIn("LIMIT 20", seen[0])
        for q in ("", "x" * 33):
            c.query = {"q": q}
            with self.assertRaises(Bad):
                G.find(c)


class WriteRules(unittest.TestCase):
    def test_rules_allow_exactly_these_writes(self):
        common.check_fields(["gm_power_policy.*"])
        common.check_fields(["character_base.GM"], advanced=True)
        self.assertEqual(common.GLOSSARY.rules("gm_power_policy")["applies"], "hot_reload")
        self.assertEqual(common.GLOSSARY.field("character_base", "GM")["applies_rule"], "now")

    def test_module_is_registered_with_its_page_files(self):
        self.assertIn("gm_powers", common.REGISTRY)
        for f in ("static/modules/gm_powers.js", "static/modules/gm_powers.css", "data/gm_powers_inventory.json"):
            self.assertTrue((ROOT / f).is_file(), f)


if __name__ == "__main__":
    unittest.main(verbosity=2)
