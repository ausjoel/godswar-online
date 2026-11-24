"""W23: unit tests for modules/_account_purge.py (delete an account completely + Restore). No database needed."""
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import _account_purge as AP  # noqa: E402
from modules.common import Bad, Refused  # noqa: E402


def col(t, c, array=False, schema="public", intcol=True):
    return {"schema": schema, "table": t, "column": c, "array": array, "int": intcol}


class Classify(unittest.TestCase):
    def test_covered_kept_unreviewed(self):
        cols = [col("character_items", "user_id"), col("accounts", "id"), col("profession_state", "character_id"),
                col("account_emails", "account_id", schema="signup"), col("character_pets", "user_id"),
                col("pet_work_state", "pet_id"), col("mentor_relations", "master_id"), col("character_marriages", "groom_id"),
                col("player_trade_log", "first_character_id"), col("operator_log", "actor_account_id"),
                col("account_username_holds", "account_id"), col("medusa_completion_rewards", "character_ids", array=True),
                col("event_drop_windows", "item_ids", array=True), col("battlefield_rounds", "winner"),
                col("new_feature", "killer_id"), col("x", "name", intcol=False)]
        covered, kept, unreviewed = AP.classify(cols)
        cov = {(c["qt"], c["column"]): c["by"] for c in covered}
        self.assertEqual(cov[("public.character_items", "user_id")], "character")
        self.assertEqual(cov[("signup.account_emails", "account_id")], "account")
        self.assertEqual(cov[("public.pet_work_state", "pet_id")], "pet")
        self.assertIn(("public.mentor_relations", "master_id"), cov)
        self.assertIn(("public.character_marriages", "groom_id"), cov)
        self.assertNotIn(("public.accounts", "id"), cov)                   # the account itself comes from the cascade
        keptk = {(k["qt"], k["column"]) for k in kept}
        for k in [("public.player_trade_log", "first_character_id"), ("public.operator_log", "actor_account_id"),
                  ("public.account_username_holds", "account_id"), ("public.medusa_completion_rewards", "character_ids")]:
            self.assertIn(k, keptk)
        self.assertNotIn(("public.event_drop_windows", "item_ids"), keptk)  # not a list of players
        self.assertEqual([(u["qt"], u["column"]) for u in unreviewed], [("public.new_feature", "killer_id")])

    def test_id_conditions_literal_and_skips_empty(self):
        covered = [{"qt": "public.a", "column": "account_id", "by": "account"}, {"qt": "public.a", "column": "character_id", "by": "character"},
                   {"qt": "public.p", "column": "pet_id", "by": "pet"}]
        w = AP.id_conditions(covered, 7, [3, 2], [])
        self.assertEqual(w["public.a"], '("account_id" IN (7)) OR ("character_id" IN (2, 3))')
        self.assertNotIn("public.p", w)                                     # no pets: no condition

    def test_merge_plan_order_parents_first(self):
        where, order = AP.merge_plan({"accounts": "id = 7", "character_base": "account_id IN (...)"}, ["accounts", "character_base"],
                                     {"monster_loot_pickup_claims": "x"}, {"public.character_base": "account_id IN (7)", "signup.account_emails": "account_id IN (7)"})
        self.assertEqual(order, ["public.accounts", "public.character_base", "public.monster_loot_pickup_claims", "signup.account_emails"])
        self.assertEqual(where["public.character_base"], "(account_id IN (...)) OR (account_id IN (7))")


class Statements(unittest.TestCase):
    def test_delete_children_first_in_replica_window_then_hold(self):
        st = AP.delete_statements({"public.accounts": "id = 7", "public.character_base": "account_id = 7"},
                                  ["public.accounts", "public.character_base"], 7, "bob", "INSERT INTO hold")
        self.assertEqual(st[0], "SET LOCAL session_replication_role = replica;")
        self.assertIn('"character_base"', st[1])
        self.assertIn('"accounts"', st[2])
        self.assertEqual(st[3], "SET LOCAL session_replication_role = origin;")
        self.assertEqual(st[4], "INSERT INTO hold;")

    def test_restore_parents_first_overriding_and_hold(self):
        doc = {"tables": [{"table": "public.accounts", "rows": 1, "columns": ["id", "username"], "json": '[{"id": 7, "username": "it\'s"}]'},
                          {"table": "public.character_base", "rows": 0, "columns": ["id"], "json": "[]"},
                          {"table": "signup.account_emails", "rows": 1, "columns": ["account_id"], "json": '[{"account_id": 7}]'}],
               "hold_key": "bob", "holds_json": "[]", "holds_columns": []}
        st = AP.restore_statements(doc)
        self.assertEqual(st[0], "SET LOCAL session_replication_role = replica;")
        self.assertTrue(st[1].startswith('INSERT INTO "public"."accounts" ("id", "username") OVERRIDING SYSTEM VALUE'))
        self.assertIn("it''s", st[1])                                       # quoted literal
        self.assertTrue(st[2].startswith('INSERT INTO "signup"."account_emails"'))
        self.assertEqual(st[3], "SET LOCAL session_replication_role = origin;")
        self.assertIn("DELETE FROM public.account_username_holds WHERE username_key = 'bob'", st[4])
        self.assertEqual(len(st), 5)                                        # no hold existed before: none re-inserted

    def test_guards(self):
        g = AP.count_guard_sql({"public.a": "x = 1"}, {"public.a": 3}, "changed")
        self.assertIn("3 AS want", g)
        self.assertIn("RAISE EXCEPTION", g)
        self.assertEqual(AP.count_guard_sql({}, {}, "x"), "")
        fk = AP.fk_guard_sql([{"child": "public.c", "parent": "public.p", "cols": ["pid"], "pcols": ["id"]}])
        self.assertIn('NOT EXISTS (SELECT 1 FROM "public"."p" p WHERE p."id" = c."pid")', fk)
        self.assertIn("RAISE EXCEPTION", AP.offline_sql(5))
        same = AP.same_rows_guard_sql({"public.a": "x = 1"}, {"public.a": (2, "abc")})
        self.assertIn("'2|abc' AS want", same)
        self.assertIn("md5(string_agg(x::text", same)

    def test_partner_queries_name_only_other_side(self):
        for q in AP.PARTNER_SQL:
            sql = q % {"ids": "9"}
            self.assertIn("IN (9)", sql)
            self.assertTrue(sql.startswith("SELECT CASE WHEN"))


class Backups(unittest.TestCase):
    def test_write_list_load_and_refusals(self):
        with tempfile.TemporaryDirectory() as d:
            doc = {"kind": AP.KIND, "version": AP.VERSION, "database": "godswar_x", "created": "2026-11-24T10:00:00.000001",
                   "account_id": 7, "username": "bob", "characters": [{"id": 1, "name": "B", "state": "active"}],
                   "tables": [{"table": "public.accounts", "rows": 1, "columns": [], "json": "[]"}], "total_rows": 1}
            p = AP.write_backup(d, doc)
            name = os.path.basename(p)
            self.assertTrue(name.startswith("account-full-delete-godswar_x-7-bob-"))
            ls = AP.list_backups(d, "godswar_x")
            self.assertEqual([(x["file"], x["rows"]) for x in ls], [(name, 1)])
            self.assertEqual(AP.list_backups(d, "other"), [])
            got, _ = AP.load_backup(d, name, "godswar_x")
            self.assertEqual(got["username"], "bob")
            for bad in ["../" + name, "/etc/passwd", "x.json", ""]:
                with self.assertRaises(Bad):
                    AP.load_backup(d, bad, "godswar_x")
            with self.assertRaises(Refused):
                AP.load_backup(d, name, "godswar")
            Path(d, name).write_text(json.dumps(dict(doc, version=99)))
            with self.assertRaises(Bad):
                AP.load_backup(d, name, "godswar_x")


if __name__ == "__main__":
    unittest.main()
