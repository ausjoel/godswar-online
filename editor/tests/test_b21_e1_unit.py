"""B21 E1 (items 21, 22, 29, 30): pure-logic checks for guild max, buff grouping / event job selection, item legality
and the Online Award template store. stdlib only: python3 -m unittest tests/test_b21_e1_unit.py (from editor/)."""
import os
import sys
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import awards, buff_tools, guild, legality  # noqa: E402


class GuildMax(unittest.TestCase):
    G = {"level": 1, "member_limit": 100, "silver": 5, "gold": 2_000_000_000, "bijou": 0}
    TYPES = [{"type": 1, "max": 12}, {"type": 30, "max": 8}]
    MEMBERS = [{"character_id": 13}, {"character_id": 14}]

    def test_game_targets_are_the_client_caps(self):
        t = guild.max_targets({"MaxMoney": 900000000, "MaxBijou": 90000000, "MaxContribute": 900000, "MaxMember": 10000}, "game")
        self.assertEqual((t["silver"], t["bijou"], t["contribute"], t["member_limit"]), (900000000, 90000000, 900000, 10000))
        s = guild.max_targets({}, "server")
        self.assertLess(s["silver"], 2 ** 31 - 1)       # headroom: a donation adds to it

    def test_only_raises(self):
        t = guild.max_targets({}, "game")
        st = guild.max_statements(1, self.G, self.TYPES, self.MEMBERS, list(guild.MAX_PARTS), t, 12)
        upd = st[0]
        self.assertIn("level = 12", upd)
        self.assertIn("silver = 900000000", upd)
        self.assertNotIn("gold =", upd)                  # already above the cap: kept
        self.assertTrue(any("ON CONFLICT (guild_id, slot)" in s and "GREATEST" in s for s in st))
        self.assertTrue(any("consortia_contribute = GREATEST(consortia_contribute, 900000)" in s and "13,14" in s for s in st))

    def test_nothing_to_do(self):
        g = dict(self.G, level=12, member_limit=10000, silver=900000000, bijou=90000000)
        self.assertEqual(guild.max_statements(1, g, [], [], ["level", "funds", "member_limit"], guild.max_targets({}, "game"), 12), [])


class BuffTools(unittest.TestCase):
    def test_same_effect_levels_merge(self):
        e = lambda i, v, pr: {"id": i, "name": "Bleeding", "family_name": "Bleeding", "kind": 1, "priority": pr, "category": "Damage over time",
                               "effects": [{"code": 27, "value": v, "label": "Loses HP over time (%d per second)" % v}],
                               "apply_status_id": i, "label": "Bleeding %d" % pr}
        other = {"id": 99, "name": "Bleeding", "family_name": "Bleeding", "kind": 1, "priority": 1, "category": "Damage over time",
                 "effects": [{"code": 28, "value": 1, "label": "x"}], "apply_status_id": None}
        g = buff_tools.group_library([e(12, 200, 3), e(10, 50, 1), e(11, 100, 2), other])
        self.assertEqual(len(g), 2)
        merged = next(x for x in g if len(x["levels"]) == 3)
        self.assertEqual([l["status_id"] for l in merged["levels"]], [10, 11, 12])
        self.assertEqual(merged["applicable"], 3)

    def test_event_job_gives_once_per_session_in_scope(self):
        now = time.time()
        ev = {"id": 1, "ends": now + 600, "scope": {"kind": "all", "camp": 1, "min_level": 10, "max_level": 200}}
        rows = [{"id": 1, "online": True, "session": "a", "camp": 1, "level": 50},
                {"id": 2, "online": True, "session": "b", "camp": 0, "level": 50},     # wrong faction
                {"id": 3, "online": False, "session": "", "camp": 1, "level": 50},     # offline
                {"id": 4, "online": True, "session": "c", "camp": 1, "level": 5},      # too low
                {"id": 5, "online": True, "session": "d", "camp": 1, "level": 99}]     # already given this session
        out = buff_tools.due([ev], rows, lambda e: {(5, "d")}, now)
        self.assertEqual([(r["id"], left) for _, r, left in out], [(1, 600)])
        self.assertEqual(buff_tools.due([dict(ev, ends=now + 2)], rows, lambda e: set(), now), [])   # nearly over

    def test_event_store_roundtrip(self):
        with tempfile.TemporaryDirectory() as d:
            st = buff_tools.Store(os.path.join(d, "e.sqlite3"))
            i = st.add(1000, "EXP", time.time() + 60, {"kind": "ids", "ids": [6]}, "t")
            st.mark(i, 6, "s1", "applied")
            self.assertEqual(st.given(i), {(6, "s1")})
            self.assertEqual(len(st.running()), 1)
            self.assertEqual(st.stop_all("t"), 1)
            self.assertEqual(st.running(), [])


class Legality(unittest.TestCase):
    META = {"name": "Short Sword", "server": True, "overlap": 1, "slot": 10, "minlv": 1, "classes": []}

    def rows(self, **kw):
        r = {"id": 3, "prop_id": 1000, "item_location": 0, "slot_index": 10, "stack": 1, "item_quality": 1, "item_grade": 1,
             "attribute1": 104, "attribute_level1": 50, "attribute2": 9999, "attribute_level2": 1, "holy_socket_count": 0}
        r.update(kw)
        return [r]

    def findings(self, rows):
        b = {"fighter_job_lv": 10, "profession": 0}
        return legality.item_findings(b, rows, lambda pid: self.META, {"1000": {"append_attributes": [0, 1, 40]}},
                                      {"104": {"max": 25}, "0": {"max": 25}, "1": {"max": 25}, "40": {"max": 25}})

    def test_names_and_rules(self):
        f = self.findings(self.rows())
        rules = {(x["rule"], x.get("index")) for x in f}
        self.assertEqual(rules, {("attribute-level", 1), ("attribute", 1), ("attribute-unknown", 2)})
        roll = next(x for x in f if x["rule"] == "attribute")
        self.assertNotIn("attribute 104", roll["message"])        # the admin: the NAME, not the number
        self.assertTrue(roll["attribute_name"])
        self.assertEqual({c["id"] for c in roll["choices"]}, {0, 1, 40})

    def test_server_verdict(self):
        f = self.findings(self.rows())
        f, extra = legality.classify(f, {"canEnter": True, "findings": [{"severity": "hidden", "container": "equipment", "slot": 10, "message": "x"},
                                                                       {"severity": "blocks", "stage": "load", "message": "y"}]})
        self.assertTrue(all(x["cls"] == "hidden" for x in f))
        self.assertEqual(len(extra), 1)
        f2, _ = legality.classify(self.findings(self.rows()), {"canEnter": True, "findings": []})
        self.assertTrue(all(x["cls"] == "harmless" for x in f2))

    def test_fix_plan(self):
        f = self.findings(self.rows())
        d = {"character_items": self.rows()}
        plan, rows = legality._plan(d, f, [{"row": 3, "rule": "attribute-level", "index": 1},
                                           {"row": 3, "rule": "attribute", "index": 1, "kind": "replace_attribute", "choice": 40}])
        st = legality._statements(7, plan, rows)
        upd = next(s for s in st if s.startswith("UPDATE character_items"))
        self.assertIn('"attribute1" = 40', upd.replace("attribute1 = 40", '"attribute1" = 40'))
        self.assertIn("25", upd)
        self.assertTrue(any("inventory_revision" in s for s in st))
        plan, rows = legality._plan(d, f, [{"row": 3, "rule": "attribute-unknown", "index": 2, "kind": "remove"},
                                           {"row": 3, "rule": "attribute-level", "index": 1}])
        self.assertEqual([k for _, k, _ in plan], ["remove"])
        with self.assertRaises(Exception):
            legality._plan(d, f, [{"row": 3, "rule": "attribute", "index": 1, "kind": "replace_attribute", "choice": 104}])


class LegalityServerRows(unittest.TestCase):
    def test_server_finding_without_rule_gets_its_own_row(self):
        rows = [{"id": 9, "prop_id": 10109, "item_location": 1, "slot_index": 4, "stack": 1}]
        f, extra = legality.classify([], {"canEnter": True, "findings": [{"severity": "hidden", "container": "bag", "slot": 4, "message": "no pet"}]},
                                     rows, lambda pid: {"name": "Seal Jade"})
        self.assertEqual([(x["rule"], x["cls"], x["fixes"]) for x in f], [("server", "hidden", ["remove"])])
        db = [{"severity": "blocks", "title": "Empty Seal Jade", "detail": "x", "fix": {"location": 1, "slot": 4}}]
        f, _ = legality.classify([], None, rows, lambda pid: {"name": "Seal Jade"}, db)
        self.assertEqual(f[0]["cls"], "breaks_login")

    def test_fix_never_deletes(self):
        f = {"fixes": ["remove"], "item": "Cart", "message": "m", "row": 1, "rule": "equip-level", "index": None}
        self.assertIsNone(legality._default_fix(f))
        with self.assertRaises(Exception):
            legality._plan({"character_items": [{"id": 1}]}, [f], [{"row": 1, "rule": "equip-level"}])
        s = {"fixes": ["clamp_sockets", "remove"]}
        self.assertEqual(legality._default_fix(s), "clamp_sockets")
        v = legality._set_values(s, "clamp_sockets")
        self.assertEqual(v["holy_socket_count"], 4)
        self.assertIn("holy_socket6_level", v)


class AwardTemplates(unittest.TestCase):
    def test_store(self):
        with tempfile.TemporaryDirectory() as d:
            st = awards._TemplateStore(os.path.join(d, "t.sqlite3"))
            i = st.add("Weekend", [{"item_id": 1, "quantity": 2, "item_quality": 1, "bound": 0}], "", "t")
            self.assertEqual(st.list()[0]["name"], "Weekend")
            st.archive(i)
            self.assertEqual(st.list(), [])
            self.assertEqual(len(st.list(archived=True)), 1)
            st.hide(3)
            self.assertEqual(st.hidden(), {3})
            st.hide(3, False)
            self.assertEqual(st.hidden(), set())


if __name__ == "__main__":
    unittest.main()
