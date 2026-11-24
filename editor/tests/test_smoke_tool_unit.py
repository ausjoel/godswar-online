"""Unit checks for editor/tools/smoke.py (POLISH-REVIEW #16) that need no database and no editor: the test admin's
password record is one the editor accepts, the safety lists hold, and the JSON helpers find route parameters.
Run: python3 -I editor/tests/test_smoke_tool_unit.py"""
import os, re, sys, unittest
from pathlib import Path
EDITOR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(EDITOR)); sys.path.insert(0, str(EDITOR / "tools"))
import smoke  # noqa: E402
from editor_auth import verify_password  # noqa: E402


class Smoke(unittest.TestCase):
    def test_test_admin_record_is_accepted_by_the_editor(self):
        rec = smoke.password_record("Smoke-pass-123")
        self.assertTrue(verify_password("Smoke-pass-123", rec))
        self.assertFalse(verify_password("Smoke-pass-124", rec))

    def test_never_a_live_or_template_database(self):
        for name in ("godswar", "godswar_live_copy", "godswar_release", "postgres"):
            self.assertIn(name, smoke.PROTECTED)
        self.assertTrue(smoke.DB_PREFIX.startswith("gw_edsmoke_"))

    def test_only_the_sign_in_is_posted(self):
        src = (EDITOR / "tools" / "smoke.py").read_text()
        posts = re.findall(r'request\("(POST|PUT|PATCH|DELETE)",\s*"([^"]+)"', src)
        self.assertEqual(posts, [("POST", "/auth/login")])

    def test_the_cache_writing_get_is_skipped(self):
        self.assertIn(("graphics", "convert"), smoke.SKIP)

    def test_rig_only_answers_are_narrow(self):
        self.assertTrue(smoke.RIG_ONLY.search("no game server management port is configured for this database"))
        self.assertFalse(smoke.RIG_ONLY.search("ERROR:  permission denied for table character_fortune_teller_log"))
        self.assertFalse(smoke.RIG_ONLY.search("TypeError: format argument 1"))

    def test_with_a_game_server_rig_answers_are_failures(self):
        import tempfile, types
        with tempfile.TemporaryDirectory() as d:
            mk = lambda mgmt: smoke.Smoke(types.SimpleNamespace(workdir=d, pg_host="127.0.0.1", pg_port=1, pg_user="x", mgmt=mgmt))
            note = "The running game server needs the compatible title-grant update."
            self.assertTrue(mk(None).rig_only(note))
            self.assertFalse(mk(17002).rig_only(note))     # --mgmt given: an old game build is a real failure

    def test_first_finds_nested_values(self):
        doc = {"guilds": [{"name": "x", "guild_id": "", "members": []}, {"guild_id": 7}]}
        self.assertEqual(smoke.first(doc, ("guild_id",), smoke.isint), 7)
        self.assertIsNone(smoke.first({"a": [{"b": 1}]}, ("c",)))

    def test_gwadmin_dir_has_gwadmin(self):
        d = smoke.find_gwadmin_dir()
        if d is None:
            self.skipTest("gwadmin.py not on this machine (set GWADMIN_TOOLS)")
        self.assertTrue(os.path.isfile(os.path.join(d, "gwadmin.py")))


if __name__ == "__main__":
    unittest.main()
