"""EDITOR-SMOKE: sql/editor-smoke-delete-plan-grants.sql gives the editor role SELECT on every table the account delete
plan (modules/character.py _delete_plan, GET character/account/<id>/delete-plan) reads, and nothing more than SELECT.

The SQL part needs a scratch copy of the game database (never live): set EDITOR_TEST_PG="host port user dbname" and
optionally EDITOR_TEST_ROLE (default gweditor). It runs inside BEGIN ... ROLLBACK, so the database is not changed.
    EDITOR_TEST_PG="127.0.0.1 55496 $USER <scratch-copy>" python3 -m pytest editor/tests/test_delete_plan_grants.py
"""
import os, re, shutil, subprocess, unittest
from pathlib import Path

SQL = Path(__file__).resolve().parents[1] / "sql" / "editor-smoke-delete-plan-grants.sql"
CHECK = r"""
WITH RECURSIVE fk AS (SELECT conrelid AS child, confrelid AS parent, confdeltype AS del FROM pg_constraint WHERE contype = 'f'),
cl(t) AS (SELECT 'public.accounts'::regclass::oid UNION SELECT fk.child FROM fk JOIN cl ON fk.parent = cl.t WHERE fk.del = 'c'),
rd(t) AS (SELECT t FROM cl UNION SELECT fk.child FROM fk JOIN cl ON fk.parent = cl.t WHERE fk.del IN ('r','a','n'))
SELECT 'UNREADABLE=' || count(*) FROM rd JOIN pg_class k ON k.oid = rd.t AND k.relkind IN ('r','p')
WHERE NOT has_table_privilege(:'editor_role', rd.t, 'SELECT');
"""


class Static(unittest.TestCase):
    def test_grants_select_only_and_is_rerunnable(self):
        sql = re.sub(r"--[^\n]*", "", SQL.read_text())
        self.assertIn("GRANT SELECT ON", sql)
        self.assertNotRegex(sql.upper(), r"\b(INSERT|UPDATE|DELETE|TRUNCATE|ALL PRIVILEGES)\b")
        self.assertIn("NOT has_table_privilege", sql)          # only what is missing: a second run grants nothing
        self.assertIn("'public.accounts'::regclass", sql)


@unittest.skipUnless(os.environ.get("EDITOR_TEST_PG") and shutil.which("psql"), "needs EDITOR_TEST_PG (a scratch database copy)")
class OnACopy(unittest.TestCase):
    def test_every_table_the_plan_reads_is_readable_after_the_grants(self):
        host, port, user, db = os.environ["EDITOR_TEST_PG"].split()
        if db in ("godswar", "postgres"):
            self.skipTest("refusing a non-scratch database")
        role = os.environ.get("EDITOR_TEST_ROLE", "gweditor")
        script = "BEGIN;\n\\i %s\n%s\nROLLBACK;\n" % (SQL, CHECK)
        p = subprocess.run(["psql", "-X", "-At", "-h", host, "-p", port, "-U", user, "-d", db, "-v", "editor_role=" + role,
                            "-v", "ON_ERROR_STOP=1"], input=script, capture_output=True, text=True, timeout=120)
        self.assertEqual(p.returncode, 0, p.stderr)
        self.assertIn("UNREADABLE=0", p.stdout)


if __name__ == "__main__":
    unittest.main()
