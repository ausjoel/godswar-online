"""B21 item 24: Accounts: search & renumber (modules/account_tools.py).
  python3 -I tests/test_account_tools.py                       # unit checks only
  GODSWAR_ACCT_TEST_PSQL="psql -h 127.0.0.1 -p <port>" GODSWAR_ACCT_TEST_DB=editor_acct_<copy> python3 -I tests/test_account_tools.py
The database part needs a COPY whose name starts editor_acct (it renumbers accounts there and puts them back)."""
import os
from pathlib import Path
import shlex
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import account_tools as A, common  # noqa: E402
from reborn import Reborn  # noqa: E402

COMMAND = os.environ.get("GODSWAR_ACCT_TEST_PSQL")
DATABASE = os.environ.get("GODSWAR_ACCT_TEST_DB")


class FakePG:
    def __init__(self, ids):
        self.ids = ids

    def json(self, q, timeout=None):
        return list(self.ids)


class Unit(unittest.TestCase):
    def rb(self, ids):
        return SimpleNamespace(pg=FakePG(ids))

    def test_compact_set_and_map(self):
        m, _ = A.plan_map(self.rb([1, 2, 3, 4, 5, 8, 9, 10, 44, 45]), {"mode": "compact"})
        self.assertEqual(m, {8: 6, 9: 7, 10: 8, 44: 9, 45: 10})
        m, _ = A.plan_map(self.rb([1, 3, 4]), {"mode": "compact"})
        self.assertEqual(m, {3: 2, 4: 3})                   # 3 is both an old and a new number
        self.assertEqual(A.plan_map(self.rb([1, 5]), {"mode": "set", "from": 5, "to": 2})[0], {5: 2})
        with self.assertRaises(common.Bad):
            A.plan_map(self.rb([1, 5]), {"mode": "set", "from": 5, "to": 1})         # taken
        with self.assertRaises(common.Bad):
            A.plan_map(self.rb([1, 5]), {"mode": "set", "from": 7, "to": 2})         # no such account
        self.assertEqual(A.plan_map(self.rb([1, 2, 3]), {"mode": "map", "map": {"2": 9, "3": 2}})[0], {2: 9, 3: 2})
        with self.assertRaises(common.Bad):
            A.plan_map(self.rb([1, 2, 3]), {"mode": "map", "map": {"2": 3}})         # two accounts would be #3

    def test_config_numbers_flagged_both_ways(self):
        m, _ = A.plan_map(self.rb([1, 2, 4, 5]), {"mode": "compact"})
        self.assertEqual(m, {4: 3, 5: 4})
        named, text = A.config_hits(m, {3})
        self.assertEqual(named, [3])
        self.assertIn("#4 will become #3", text[0])
        named, text = A.config_hits({3: 9}, {3})
        self.assertEqual(named, [3])
        self.assertIn("#3 moves to #9", text[0])
        self.assertEqual(A.config_hits({8: 6}, {3}), ([], []))

    def test_statements_use_an_offset_and_a_replica_window(self):
        tables = [{"schema": "public", "table": "accounts", "column": "id", "array": False, "where": "id IN (8)"},
                  {"schema": "public", "table": "wonderland_title_runs", "column": "admitted_account_ids", "array": True,
                   "where": "admitted_account_ids && ARRAY[8]::int[]"}]
        st = A.renumber_statements({8: 6}, tables, "public.accounts_id_seq", 10)
        self.assertEqual(st[0], "SET LOCAL session_replication_role = replica")
        self.assertIn("WHEN 8 THEN %d" % (A.OFFSET + 6), st[1])
        self.assertIn("array_replace(\"admitted_account_ids\", 8, %d)" % (A.OFFSET + 6), st[2])
        self.assertIn("- %d" % A.OFFSET, st[3])
        self.assertIn("SET LOCAL session_replication_role = origin", st)
        self.assertIn("setval('public.accounts_id_seq', GREATEST(10, (SELECT max(id) FROM public.accounts)), true)", st[-1])

    def test_item_flags(self):
        rb = SimpleNamespace(cat=lambda: {"items": {"10": {"name": "Potion", "overlap": 20}, "11": {"name": "GM Sword", "overlap": 1}}})
        cli = {"10": {"name": "Potion", "overlap": 20}}
        _, _, f = A.item_flags(rb, {"prop_id": 10, "stack": 150, "item_quality": 1, "item_grade": 1}, cli, {})
        self.assertIn("stack 150 is above 99", f)
        _, _, f = A.item_flags(rb, {"prop_id": 10, "stack": 25}, cli, {})
        self.assertIn("stack 25 is above its stack size 20", f)
        _, _, f = A.item_flags(rb, {"prop_id": 11, "stack": 1}, cli, {11: "defunct"})
        self.assertTrue({"GM item", "not in your client", "dead item (defunct)"} <= set(f), f)
        _, _, f = A.item_flags(rb, {"prop_id": 99, "stack": 1}, cli, {})
        self.assertIn("not in the server's item templates", f)
        _, _, f = A.item_flags(rb, {"prop_id": 10, "stack": 5, "item_quality": 3}, cli, {})
        self.assertEqual(f, [])


@unittest.skipUnless(COMMAND and DATABASE, "requires an explicit copied editor_acct* database")
class Database(unittest.TestCase):
    def setUp(self):
        tokens = shlex.split(COMMAND)
        if Path(tokens[0]).name != "psql" or tokens[tokens.index("-h") + 1] not in ("127.0.0.1", "::1") \
                or int(tokens[tokens.index("-p") + 1]) < 10000 or not DATABASE.startswith("editor_acct"):
            raise ValueError("use a direct loopback psql port >= 10000 and a copied editor_acct* database")
        self.temp = tempfile.TemporaryDirectory()
        self.rb = Reborn(COMMAND, DATABASE, writable=True, live=False, backup_dir=self.temp.name)
        self.changes = []
        self.app = SimpleNamespace(reborn=self.rb, changes=self.changes, record_reborn_change=self.changes.append,
                                   a=SimpleNamespace(rates_mgmt=None), rates=None)
        common.CATALOG.rb = self.rb
        self.ids0 = self.ids()
        self.links0 = self.links()

    def tearDown(self):
        self.temp.cleanup()

    def api(self, method, route, body=None, query=None):
        return common.dispatch(self.app, method, "account_tools/" + route, query or {}, body or {}, {})

    def ids(self):
        return self.rb.pg.json("(SELECT json_agg(id ORDER BY id) FROM accounts)")

    def links(self):
        """(account username, character / ledger rows) - what must follow the account through a renumber."""
        return self.rb.pg.json("""(SELECT json_agg(r ORDER BY r.u) FROM (SELECT a.username AS u,
            (SELECT json_agg(c.name ORDER BY c.id) FROM character_base c WHERE c.account_id = a.id) AS chars,
            (SELECT count(*) FROM character_currency_ledger l WHERE l.account_id = a.id) AS ledger,
            (SELECT count(*) FROM character_inventory_ledger l WHERE l.account_id = a.id) AS inv,
            (SELECT count(*) FROM account_realm l WHERE l.account_id = a.id) AS realm,
            (SELECT count(*) FROM signup.account_emails l WHERE l.account_id = a.id) AS emails FROM accounts a) r)""")

    def test_search_filters(self):
        code, r = self.api("GET", "search", query={"class": "2"})
        self.assertEqual(code, 200, r)
        self.assertTrue(r["characters"] and all(c["profession"] == 2 for c in r["characters"]))
        code, r = self.api("GET", "search", query={"faction": "1", "level_min": "150", "stat": "max_hp", "stat_min": "1"})
        self.assertEqual(code, 200, r)
        self.assertTrue(all(c["camp"] == 1 and c["level"] >= 150 and c["stats"]["max_hp"] >= 1 for c in r["characters"]))
        hp = [c["stats"]["max_hp"] for c in r["characters"]]
        self.assertEqual(hp, sorted(hp, reverse=True))
        self.assertEqual(self.api("GET", "search", query={"stat": "password"})[0], 400)

    def test_item_search(self):
        code, r = self.api("GET", "items", query={"loc": "bag,warehouse"})
        self.assertEqual(code, 200, r)
        self.assertTrue(r["items"] and all(x["item_location"] in (1, 3) for x in r["items"]))
        some = r["items"][0]
        code, r2 = self.api("GET", "items", query={"q": str(some["prop_id"])})
        self.assertTrue(all(x["prop_id"] == some["prop_id"] for x in r2["items"]))
        code, r3 = self.api("GET", "items", query={"q": some["name"][:6]})
        self.assertIn(some["prop_id"], [x["prop_id"] for x in r3["items"]])
        code, r4 = self.api("GET", "items", query={"illegal": "1", "deep": "1"})
        self.assertEqual(code, 200, r4)
        self.assertTrue(all(x["flags"] for x in r4["items"]))

    def test_compact_then_back(self):
        code, v = self.api("POST", "renumber/preview", {"mode": "compact"})
        self.assertEqual(code, 200, v)
        want = {str(o): i + 1 for i, o in enumerate(self.ids0) if o != i + 1}
        self.assertEqual(v["map"], want)
        self.assertTrue(v["tables"] and v["rows_total"] >= len(want))
        self.assertEqual(self.ids(), self.ids0, "the dry run writes nothing")
        self.assertEqual(self.api("POST", "renumber/apply", {"mode": "compact", "token": "0" * 64})[0], 409)
        code, r = self.api("POST", "renumber/apply", {"mode": "compact", "token": v["token"]})
        self.assertEqual(code, 200, r)
        self.assertEqual(self.ids(), list(range(1, len(self.ids0) + 1)))
        self.assertEqual(self.links(), self.links0, "characters, ledgers, realm and e-mail rows follow their account")
        self.assertEqual(self.rb.pg.json("(SELECT to_jsonb(last_value) FROM accounts_id_seq)"), len(self.ids0))
        self.assertTrue(Path(self.changes[-1]["backup"]).is_file())
        # Undo is refused with the reason; "renumber back" uses the inverse map
        code, back = self.api("POST", "renumber/preview", {"mode": "map", "map": r["inverse"]})
        self.assertEqual(code, 200, back)
        code, r2 = self.api("POST", "renumber/apply", {"mode": "map", "map": r["inverse"], "token": back["token"]})
        self.assertEqual(code, 200, r2)
        self.assertEqual(self.ids(), self.ids0)
        self.assertEqual(self.links(), self.links0)

    def test_set_refusals_online_and_config(self):
        top = self.ids0[-1]
        self.assertEqual(self.api("POST", "renumber/preview", {"mode": "set", "from": top, "to": self.ids0[0]})[0], 400)
        self.rb.pg.run("UPDATE accounts SET login_status = 1 WHERE id = %d;" % top)
        try:
            code, v = self.api("POST", "renumber/preview", {"mode": "set", "from": top, "to": top + 100})
            self.assertTrue(v["online"])
            self.assertEqual(self.api("POST", "renumber/apply", {"mode": "set", "from": top, "to": top + 100, "token": v["token"]})[0], 423)
        finally:
            self.rb.pg.run("UPDATE accounts SET login_status = 0 WHERE id = %d;" % top)
        if 3 in self.ids0:
            code, v = self.api("POST", "renumber/preview", {"mode": "set", "from": 3, "to": top + 100})
            self.assertEqual(v["config_named"], [3])
            self.assertEqual(self.api("POST", "renumber/apply", {"mode": "set", "from": 3, "to": top + 100, "token": v["token"]})[0], 400)
        self.assertEqual(self.ids(), self.ids0)


if __name__ == "__main__":
    unittest.main()
