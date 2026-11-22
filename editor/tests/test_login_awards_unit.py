"""W4-B: login-window awards, Prize Chest sends, queued and server-wide buffs (modules/login_awards.py)."""
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import common, favourites, login_awards as L  # noqa: E402

ITEMS = {4102: {"id": 4102, "overlap": "99", "name": "Storage Box Key"}, 2001: {"id": 2001, "overlap": "1", "name": "Sword"}}


def fake_q(ctx, sql):
    """Answers the few read queries the module makes, from fixed data."""
    if "FROM item_templates" in sql:
        ids = [int(x) for x in sql.split("ARRAY[")[1].split("]")[0].split(",") if x]
        return [ITEMS[i] for i in ids if i in ITEMS]
    if "editor_title_catalog WHERE title_id" in sql:
        return "1000" in sql
    if "FROM character_base" in sql:
        ids = [int(x) for x in sql.split("ARRAY[")[1].split("]")[0].split(",") if x]
        return [{"id": i, "name": "Char%d" % i} for i in ids if i < 900]
    if "to_regclass" in sql:
        return list(L.TABLES)
    raise AssertionError("unexpected query: " + sql)


class Ctx:
    def __init__(self, body=None):
        self.body = body or {}
        self.query = {}
        self.app = SimpleNamespace()
        self.rb = SimpleNamespace(writable=True)
        self.catalog = SimpleNamespace(name=lambda t, i: None, entry=lambda t, i: None)

    def need_rb(self):
        return self.rb

    def q(self, key, default=""):
        return self.query.get(key, default)


def good_award(**kw):
    a = {"name": "Launch week", "starts": "2026-11-13 00:00", "ends": "2026-11-20 00:00", "per_scope": "character",
         "delivery": "bag", "items": [{"item_id": 4102, "quantity": 5}], "silver": 1000, "buffs": [{"status_id": 1000}],
         "title_id": 1000, "min_level": 10}
    a.update(kw)
    return a


class LoginAwardTests(unittest.TestCase):
    def setUp(self):
        self.q = patch.object(L.W, "_q", side_effect=fake_q)
        self.q.start()
        self.writes = []
        self.w = patch.object(L.W, "_write", side_effect=lambda ctx, scopes, what, stmts, fields, **kw:
                              self.writes.append((scopes, what, stmts, fields, kw)) or {"ok": True, "change": {"id": "x"}})
        self.w.start()
        self.v = patch.object(L.W, "_ver_guard", return_value="-- version guard")
        self.v.start()
        from modules import buffs as B
        self.c = patch.object(B, "runtime_catalogue", return_value={"supported": False})
        self.c.start()

    def tearDown(self):
        patch.stopall()

    def test_brisbane_times_are_stored_in_utc(self):
        self.assertEqual(L.brisbane_to_utc("2026-11-13 00:00", "Start").isoformat(), "2026-11-12T14:00:00+00:00")
        self.assertEqual(L.brisbane_to_utc("2026-11-13T09:30", "Start").isoformat(), "2026-11-12T23:30:00+00:00")
        self.assertEqual(L.utc_to_brisbane("2026-11-12T14:00:00+00:00"), "2026-11-13 00:00")
        for bad in ("2026-13-01 00:00", "tomorrow", "", None, "2026-02-30 10:00"):
            with self.subTest(bad=bad), self.assertRaises(common.Bad):
                L.brisbane_to_utc(bad, "Start")

    def test_award_row_validates_and_defaults(self):
        row = L.award_row(Ctx(), good_award())
        self.assertEqual(row["starts_at"], "2026-11-12T14:00:00+00:00")
        self.assertEqual(json.loads(row["items"]), [{"item_id": 4102, "quantity": 5, "quality": 1, "grade": 1, "bound": 1}])
        self.assertEqual(json.loads(row["buffs"]), [{"status_id": 1000}])
        self.assertEqual((row["silver"], row["gold"], row["binding_gold"], row["title_id"], row["min_level"]), (1000, 0, 0, 1000, 10))
        refusals = [good_award(ends="2026-11-12 00:00"), good_award(name=""), good_award(per_scope="guild"),
                    good_award(delivery="warehouse"), good_award(items=[{"item_id": 4102, "quantity": 100}]),
                    good_award(items=[{"item_id": 7777, "quantity": 1}]), good_award(title_id=5),
                    good_award(items=[], silver=0, buffs=[], title_id=None), good_award(gold=10 ** 7),
                    good_award(items=[{"item_id": 4102}] * 17), good_award(buffs=[{"status_id": 1, "duration_seconds": 0}])]
        for bad in refusals:
            with self.subTest(bad=bad), self.assertRaises((common.Bad,)):
                L.award_row(Ctx(), bad)

    def test_save_inserts_new_and_updates_existing(self):
        L.award_save(Ctx({"award": good_award(), "version": "new"}))
        scopes, what, stmts, fields, _ = self.writes[-1]
        self.assertEqual(fields, ["login_window_awards.*"])
        self.assertTrue(stmts[1].startswith("INSERT INTO login_window_awards"))
        self.assertIn("created_by", stmts[1])
        L.award_save(Ctx({"id": 4, "award": good_award(delivery="prize_chest"), "version": "0" * 32}))
        stmts = self.writes[-1][2]
        self.assertTrue(stmts[1].startswith("UPDATE login_window_awards SET") and stmts[1].endswith("WHERE id = 4"))
        self.assertIn("'prize_chest'", stmts[1])

    def test_disable_is_a_versioned_update(self):
        L.award_enabled(Ctx({"enabled": False, "version": "0" * 32}), "4")
        stmts = self.writes[-1][2]
        self.assertIn("SET enabled = false", stmts[1])

    def test_chest_send_writes_one_row_per_item_and_currency(self):
        L.chest_send(Ctx({"targets": [3, 5], "items": [{"item_id": 2001, "quantity": 2, "quality": 8, "grade": 3, "bound": 0}],
                          "binding_gold": 500}))
        scopes, what, stmts, fields, _ = self.writes[-1]
        self.assertEqual(len(stmts), 4)  # (item + B-Gold) x 2 characters
        self.assertTrue(all(s.startswith("INSERT INTO prize_chest_entries") for s in stmts))
        self.assertIn("'binding_gold'", stmts[1])
        self.assertIn("character_id IN (3,5)", scopes[0]["where"])
        with self.assertRaises(common.NotFound):
            L.chest_send(Ctx({"targets": [3, 999], "items": [{"item_id": 2001}]}))
        with self.assertRaises(common.Bad):
            L.chest_send(Ctx({"targets": [3], "items": []}))

    def test_chest_send_tells_online_players_and_survives_a_silent_server(self):
        body = {"targets": [3, 5], "items": [{"item_id": 2001}]}
        # No management port (offline database copy): the send still succeeds and says why nobody was told.
        out = L.chest_send(Ctx(dict(body)))
        self.assertEqual((out["sent_to"], out["online"]["notified"]), (2, 0))
        self.assertIn("at login", out["online"]["note"])
        calls = []

        def http(method, port, path, payload=None, timeout=2.0):
            calls.append((method, port, path, payload))
            return 200, {"ok": True, "requested": 2, "online": 1, "notified": 1,
                         "characters": [{"id": 3, "name": "Char3", "result": "notified; waiting=1"}]}
        with patch.object(common, "mgmt_port", return_value=9090), patch.object(common, "_http", side_effect=http):
            out = L.chest_send(Ctx(dict(body)))
        self.assertEqual(calls[0][:3], ("POST", 9090, "/admin/prize-chest/notify"))
        self.assertEqual(calls[0][3]["characterIds"], [3, 5])
        self.assertEqual((out["online"]["notified"], out["online"]["characters"][0]["name"]), (1, "Char3"))
        # The notify call comes after the write, and an older or failing server never fails the send.
        self.assertTrue(self.writes and calls)
        with patch.object(common, "mgmt_port", return_value=9090), patch.object(common, "_http", return_value=(404, {"error": "unknown admin route"})):
            out = L.chest_send(Ctx(dict(body)))
        self.assertIn("W5-F", out["online"]["note"])
        with patch.object(common, "mgmt_port", return_value=9090), patch.object(common, "_http", side_effect=OSError("refused")):
            out = L.chest_send(Ctx(dict(body)))
        self.assertEqual(out["online"]["notified"], 0)

    def test_queue_buff_and_cancel(self):
        out = L.queue_buff(Ctx(), [3], 1000, 600, "2026-12-01 18:00")
        self.assertEqual(out["queued"], 1)
        stmt = self.writes[-1][2][0]
        self.assertIn("pending_character_buffs", stmt)
        self.assertIn("'2026-12-01T08:00:00+00:00'", stmt)
        L.buffs_cancel(Ctx(), "9")
        self.assertIn("result = 'cancelled in the editor'", self.writes[-1][2][1])

    def test_broadcast_body_and_support(self):
        body = L.broadcast_body({"status_id": "1000", "duration_seconds": "600", "camp": "1", "min_level": "50", "max_level": "",
                                 "queue_offline": True})
        self.assertEqual(body, {"statusId": 1000, "durationSeconds": 600, "camp": 1, "minLevel": 50, "maxLevel": 200,
                                "queueOffline": True, "allowHostile": False})
        self.assertNotIn("camp", L.broadcast_body({"status_id": 5, "camp": "all"}))
        for bad in ({}, {"status_id": 5, "camp": 2}, {"status_id": 5, "min_level": 90, "max_level": 10}):
            with self.subTest(bad=bad), self.assertRaises(common.Bad):
                L.broadcast_body(bad)
        caps = {"endpoints": [{"path": "/admin/buff/apply"}, {"path": "/admin/buff/broadcast"}]}
        with patch.object(common, "mgmt_port", return_value=9090), patch.object(common, "_http", return_value=(200, caps)):
            self.assertTrue(L.broadcast_support(Ctx())["available"])
        with patch.object(common, "mgmt_port", return_value=9090), patch.object(common, "_http", return_value=(200, {"endpoints": []})):
            self.assertFalse(L.broadcast_support(Ctx())["available"])

    def test_broadcast_posts_to_the_management_port(self):
        caps = {"endpoints": [{"path": "/admin/buff/broadcast"}]}
        calls = []

        def http(method, port, path, body=None, timeout=2.0):
            calls.append((method, path, body))
            return (200, caps) if method == "GET" else (200, {"ok": True, "matched": 3, "applied": 3})
        with patch.object(common, "mgmt_port", return_value=9090), patch.object(common, "_http", side_effect=http):
            out = L.buffs_broadcast(Ctx({"status_id": 1000}))
        self.assertEqual(out["applied"], 3)
        self.assertEqual(calls[-1][1], "/admin/buff/broadcast")
        self.assertEqual(calls[-1][2]["statusId"], 1000)

    def test_mount_and_damage_over_time_cannot_be_queued(self):
        catalogue = {"supported": True, "buffs": [{"statusId": 1000, "name": "Double EXP"},
                                                   {"statusId": 1100, "name": "Greek Horse", "activation": "mount"},
                                                   {"statusId": 10, "name": "Bleeding", "activation": "periodic-damage"}]}
        from modules import buffs as B
        with patch.object(B, "runtime_catalogue", return_value=catalogue):
            L.queue_buff(Ctx(), [3], 1000)
            for status in (1100, 10, 4242):
                with self.subTest(status=status), self.assertRaises(common.Bad):
                    L.queue_buff(Ctx(), [3], status)
            with self.assertRaises(common.Bad):
                L.award_row(Ctx(), good_award(buffs=[{"status_id": 1100}]))
        with patch.object(B, "runtime_catalogue", side_effect=common.Bad("server down")):
            L.queue_buff(Ctx(), [3], 4242)  # the server checks again at the login

    def test_favourite_to_prize_chest_splits_stacks_at_99(self):
        sent = []
        with patch.object(L, "chest_send", side_effect=lambda ctx: sent.append(list(ctx.body["items"])) or {"ok": True}):
            out = favourites.give_chest(Ctx({"target": 3, "items": [{"prop_id": 4102, "stack": 150}]}))
        self.assertEqual(out["sent"], 2)
        self.assertEqual([r["quantity"] for r in sent[0]], [99, 51])


if __name__ == "__main__":
    unittest.main()
