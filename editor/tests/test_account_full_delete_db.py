"""W23: Delete account completely + Restore, against an explicit COPY of the database (never live).

  GODSWAR_FULL_DELETE_PSQL="psql -h 127.0.0.1 -p 55496" GODSWAR_FULL_DELETE_DB=godswar_w23d \
  GODSWAR_FULL_DELETE_ACCOUNT=10 PGGSSENCMODE=disable python3 -m unittest tests.test_account_full_delete_db

Checks: a throwaway account with sign-up rows is deleted (every row, also the sign-up site's), Restore brings every table
of public + signup back byte for byte (md5 of every table), a second delete leaves no row in any covered column. With
GODSWAR_FULL_DELETE_ACCOUNT the same round trip runs on that (rich) account, which is restored at the end. Refusals: wrong
confirmation, a backup name outside the folder, restoring an account that exists."""
import os
import secrets
import shlex
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import common, character, _account_purge as AP  # noqa: E402,F401
from reborn import Reborn  # noqa: E402

PSQL = os.environ.get("GODSWAR_FULL_DELETE_PSQL")
DB = os.environ.get("GODSWAR_FULL_DELETE_DB")
RICH = os.environ.get("GODSWAR_FULL_DELETE_ACCOUNT")


@unittest.skipUnless(PSQL and DB, "needs GODSWAR_FULL_DELETE_PSQL / _DB (a private copy of the database)")
class FullDelete(unittest.TestCase):
    def setUp(self):
        t = shlex.split(PSQL)
        if DB in ("godswar", "godswar_release") or "-p" not in t or int(t[t.index("-p") + 1]) < 10000:
            raise ValueError("use a private copy on a loopback port >= 10000, never live")
        self.tmp = tempfile.TemporaryDirectory()
        self.rb = Reborn(PSQL, DB, writable=True, live=False, backup_dir=self.tmp.name)
        self.changes = []
        self.app = SimpleNamespace(reborn=self.rb, changes=self.changes, record_reborn_change=self.changes.append,
                                   a=SimpleNamespace(rates_mgmt=None), rates=None)
        common.CATALOG.rb = self.rb

    def tearDown(self):
        self.tmp.cleanup()

    def call(self, method, route, body=None):
        return common.dispatch(self.app, method, "character/" + route, {}, body, {})

    def fingerprint(self):
        tables = self.rb.pg.json("(SELECT jsonb_agg(n.nspname || '.' || c.relname ORDER BY 1) FROM pg_class c JOIN pg_namespace n "
                                 "ON n.oid = c.relnamespace WHERE n.nspname IN ('public', 'signup') AND c.relkind IN ('r', 'p'))")
        out = {}
        for i in range(0, len(tables), 40):
            part = tables[i:i + 40]
            out.update(self.rb.pg.json("jsonb_build_object(%s)" % ", ".join(
                "%s, (SELECT count(*) || '|' || coalesce(md5(string_agg(x::text, '~' ORDER BY x::text)), '-') FROM %s x)" % (
                    common.lit(t), AP.qsql(t)) for t in part)))
        return out

    def leftovers(self, aid, cids, pids):
        covered, _k, _u = AP.classify(self.rb.pg.json(AP.columns_sql()))
        hits = {}
        for t, cond in AP.id_conditions(covered, aid, cids, pids).items():
            n = self.rb.pg.json("(SELECT to_jsonb(count(*)) FROM %s WHERE %s)" % (AP.qsql(t), cond))
            if n:
                hits[t] = n
        if self.rb.pg.json("(SELECT to_jsonb(count(*)) FROM accounts WHERE id = %d)" % aid):
            hits["public.accounts"] = 1
        return hits

    def round_trip(self, aid):
        st, a = self.call("GET", "account/%d" % aid)
        self.assertEqual(st, 200, a)
        cids = [c["id"] for c in a["characters"]]
        pids = self.rb.pg.json("(SELECT coalesce(jsonb_agg(id), '[]') FROM character_pets WHERE user_id IN (%s))" % (",".join(map(str, cids)) or "NULL"))
        before = self.fingerprint()
        st, r = self.call("POST", "account/%d/full-delete" % aid, {"confirm": a["username"] + "x"})
        self.assertEqual(st, 400, r)
        st, r = self.call("POST", "account/%d/full-delete" % aid, {"confirm": a["username"]})
        self.assertEqual(st, 200, r)
        self.assertEqual(self.leftovers(aid, cids, pids), {})
        st, ls = self.call("GET", "full-deletes")
        self.assertEqual(ls["backups"][0]["file"], r["backup"])
        st, x = self.call("POST", "full-deletes/restore", {"file": "../" + r["backup"]})
        self.assertEqual(st, 400, x)
        st, x = self.call("POST", "full-deletes/restore", {"file": r["backup"]})
        self.assertEqual(st, 200, x)
        after = self.fingerprint()
        self.assertEqual({t: (before[t], after.get(t)) for t in before if before[t] != after.get(t)}, {})
        st, x = self.call("POST", "full-deletes/restore", {"file": r["backup"]})
        self.assertEqual(st, 403, x)                                         # the account exists again
        return a, cids, pids

    def test_throwaway_account(self):
        user = "w23t" + secrets.token_hex(3)
        st, r = self.call("POST", "account/create", {"username": user, "password": secrets.token_hex(6)})
        self.assertEqual(st, 200, r)
        aid = r["account"]["id"]
        # the sign-up site's own rows (written here directly: the site, not the editor, makes them)
        self.rb.pg.run("INSERT INTO signup.account_emails (account_id, email, email_lower) VALUES (%d, %s, %s);" % (
            aid, common.lit(user + "@example.invalid"), common.lit(user + "@example.invalid")))
        self.rb.pg.run("INSERT INTO signup.account_settings (account_id, name, content) VALUES (%d, 'test', '\\x00');"
                       "INSERT INTO signup.email_tokens (token_sha256, account_id, purpose, email, expires_at) VALUES "
                       "(sha256(%s::bytea), %d, 'verify_email', %s, now() + interval '1 day');" % (
                           aid, common.lit(user), aid, common.lit(user + "@example.invalid")))
        a, cids, pids = self.round_trip(aid)
        st, r = self.call("POST", "account/%d/full-delete" % aid, {"confirm": a["username"]})
        self.assertEqual(st, 200, r)
        self.assertGreaterEqual({t["table"] for t in r["tables"]}, {"public.accounts", "signup.account_emails",
                                                                    "signup.account_settings", "signup.email_tokens"})
        self.assertEqual(self.leftovers(aid, cids, pids), {})

    @unittest.skipUnless(RICH, "GODSWAR_FULL_DELETE_ACCOUNT not given")
    def test_rich_account_round_trip(self):
        self.round_trip(int(RICH))


if __name__ == "__main__":
    unittest.main()
