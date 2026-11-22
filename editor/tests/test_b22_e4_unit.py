"""B22 E4 (d50, d51, d55, d56): pure-logic checks. stdlib only: python3 -I tests/test_b22_e4_unit.py (from editor/).
  d50 Max guild also fills altar offerings; d51 GM commands window (parsing, the log); d55 class stone on any gear
  (database detection, legality findings, preset option); d56 renumber: server-wide online gate, setval at commit."""
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import common, guild, legality, gear_presets as G, account_tools as A  # noqa: E402
from modules import _class_stat_anywhere as CSA, gm_console as GC  # noqa: E402


class AltarMax(unittest.TestCase):
    G = {"level": 12, "member_limit": 10000, "silver": 0, "gold": 0, "bijou": 0}
    TYPES = [{"type": 10, "max": 10}, {"type": 19, "max": 10}]
    MEMBERS = [{"character_id": 13}, {"character_id": 14}]

    def test_altars_fill_to_the_bonus_cap_after_the_buildings(self):
        t = guild.max_targets({}, "server")
        st = guild.max_statements(7, self.G, self.TYPES, self.MEMBERS, ["buildings", "altars"], t, 12)
        i_build = next(i for i, x in enumerate(st) if x.startswith("INSERT INTO guild_buildings"))
        i_altar = next(i for i, x in enumerate(st) if x.startswith("INSERT INTO guild_altar_worship"))
        self.assertLess(i_build, i_altar, "altars built by this max must exist before the offerings are filled")
        sql = st[i_altar]
        self.assertEqual(guild.ALTAR_MAX_POINTS, 1_000_000)          # GuildAltarWorshipBonusPolicy.MaxPoints, both modes
        self.assertIn(", 1000000, now(), now()", sql)
        self.assertIn("BETWEEN 10 AND 19 AND b.level > 0", sql)
        self.assertIn("WHERE guild_altar_worship.points < EXCLUDED.points", sql)   # only raises; drain clock kept otherwise

    def test_altars_are_optional_and_need_members(self):
        t = guild.max_targets({}, "game")
        self.assertFalse(any("guild_altar_worship" in x for x in guild.max_statements(7, self.G, self.TYPES, self.MEMBERS, ["buildings"], t, 12)))
        self.assertFalse(any("guild_altar_worship" in x for x in guild.max_statements(7, self.G, self.TYPES, [], ["altars"], t, 12)))
        self.assertIn("altars", guild.MAX_PARTS)


class FakePG:
    def __init__(self, n):
        self.n, self.calls = n, 0

    def json(self, q, timeout=None):
        self.calls += 1
        return self.n


class ClassStoneAnywhere(unittest.TestCase):
    def setUp(self):
        CSA.forget()

    def rb(self, checks, db):
        return SimpleNamespace(db=db, pg=FakePG(checks))

    def test_detects_the_database_rule(self):
        self.assertFalse(CSA.allowed(self.rb(1, "with_check")))
        self.assertTrue(CSA.allowed(self.rb(0, "without_check")))
        self.assertFalse(CSA.allowed(None))

    def test_eligible(self):
        suits = [3232]
        strict, free = self.rb(1, "s"), self.rb(0, "f")
        self.assertEqual(CSA.eligible(strict, 3232, {"name": "Suit", "slot": 3}, suits), (True, None))
        ok, why = CSA.eligible(strict, 2100, {"name": "Light Armor", "slot": 3}, suits)
        self.assertFalse(ok)
        self.assertIn("one-off database step", why)
        self.assertEqual(CSA.eligible(free, 2100, {"name": "Light Armor", "slot": 3}, suits), (True, None))
        ok, why = CSA.eligible(free, 10, {"name": "Potion", "slot": -1}, suits)
        self.assertFalse(ok)
        self.assertIn("not gear", why)

    def test_preset_option(self):
        o = G.inputs({"class_stat": True, "class_stat_id": 221, "non_suit_special": True, "class_tier": 1})
        self.assertTrue(o["any_gear"])
        with self.assertRaises(common.Bad):
            G.inputs({"non_suit_special": True})

    def test_legality_agrees(self):
        meta_gear, meta_potion = {"name": "Light Armor", "slot": 3}, {"name": "Potion", "slot": -1}
        row = {"id": 1, "item_location": 0, "slot_index": 3, "prop_id": 2100}

        def find(r, meta, prof):
            out = []
            legality.class_stone_findings(out, r, meta, prof)
            return out
        common_row = dict(row, class_attribute1=221)
        self.assertEqual(find(common_row, meta_gear, 2), [], "a class stone on any gear is legal (d55)")
        f = find(common_row, meta_gear, 0)
        self.assertEqual([x["rule"] for x in f], ["class-stone"])       # Priest stone on a Warrior
        self.assertIn("clear_class_stone", f[0]["fixes"])
        self.assertEqual(legality._set_values(f[0], "clear_class_stone"), {"class_attribute1": None})
        self.assertEqual(len(find(dict(row, class_attribute1=205), meta_gear, 2)), 1)   # not a class stone
        self.assertEqual(len(find(dict(row, prop_id=10, class_attribute1=221), meta_potion, 2)), 1)
        self.assertEqual(find(dict(row, class_attribute1=None), meta_gear, 2), [])


class LegalityFamilies(unittest.TestCase):
    def test_enhanced_level_of_a_rollable_family_is_legal(self):
        b = {"fighter_job_lv": 100, "profession": 0}
        row = {"id": 1, "item_location": 1, "slot_index": 0, "prop_id": 2100, "stack": 1, "item_quality": 1, "item_grade": 1,
               "attribute1": 14, "attribute_level1": None}
        meta = lambda pid: {"name": "Light Armor", "server": True, "overlap": 1, "slot": 3}
        attrs = {"14": {"max": 1}, "10": {"max": 1}, "104": {"max": 1}}
        cli = {"2100": {"append_attributes": [10]}}
        self.assertEqual([f["rule"] for f in legality.item_findings(b, [row], meta, cli, attrs)], [])
        bad = dict(row, attribute1=104)                     # Damage Absorbed family: not on this item's list
        self.assertEqual([f["rule"] for f in legality.item_findings(b, [bad], meta, cli, attrs)], ["attribute"])


class GmConsole(unittest.TestCase):
    def test_parse(self):
        self.assertEqual(GC.parse("/kick Bob afk now"), ("/kick", ["Bob", "afk", "now"], "Bob afk now"))
        self.assertEqual(GC.parse("online")[0], "/online")
        self.assertEqual(GC.parse("/WHO")[0], "/online")
        with self.assertRaises(common.Bad) as e:
            GC.parse("/god on")
        self.assertIn("only in game", str(e.exception))
        with self.assertRaises(common.Bad):
            GC.parse("/nosuch")
        with self.assertRaises(common.Bad):
            GC.parse("   ")

    def test_confirm_and_read_only(self):
        rb = SimpleNamespace(writable=False, db="x")
        ctx = SimpleNamespace(need_rb=lambda: rb)
        with self.assertRaises(common.Refused):
            GC.run(ctx, "/announce hello")
        rb.writable = True
        with self.assertRaises(common.Bad) as e:
            GC.run(ctx, "/kick Bob")
        self.assertIn("confirming", str(e.exception))

    def test_give_is_capped_at_99(self):
        self.assertEqual(GC.MAX_GIVE, 99)
        with self.assertRaises(common.Bad):
            GC._num("100", 1, GC.MAX_GIVE, "count")

    def test_log_is_append_only_and_names_the_user(self):
        with tempfile.TemporaryDirectory() as d:
            app = SimpleNamespace(a=SimpleNamespace(backup_dir=d), reborn=SimpleNamespace(db="copy"))
            common.ACTOR.name = "admin"
            try:
                GC.log_entry(app, "/online", True, "Nobody is online.")
                GC.log_entry(app, "/kick Bob", False, "needs confirming")
            finally:
                common.ACTOR.name = None
            rows = GC.read_log(app)
            self.assertEqual([r["command"] for r in rows], ["/kick Bob", "/online"])      # newest first
            self.assertEqual({r["user"] for r in rows}, {"admin"})
            with open(Path(d) / GC.LOG_NAME) as f:
                self.assertEqual(len([json.loads(x) for x in f]), 2)


class RenumberGate(unittest.TestCase):
    def test_setval_uses_the_highest_number_at_commit(self):
        st = A.renumber_statements({8: 6}, [], "public.accounts_id_seq", 10)
        self.assertIn("GREATEST(10, (SELECT max(id) FROM public.accounts))", st[-1])

    def test_new_account_tables_are_reviewed(self):
        for k in ("public.account_deletions.account_id", "public.account_username_holds.account_id",
                  "public.login_window_award_claims.account_id", "public.session_health.account_id"):
            self.assertIn(k, A.REVIEWED)

    def test_online_sql_covers_the_three_signals(self):
        for s in ("login_status <> 0", "login_presence_token IS NOT NULL", "checkpoint_owner_id IS NOT NULL"):
            self.assertIn(s, A.ONLINE_SQL)
        self.assertEqual(A.online_text([{"id": 5, "username": "bob", "in_world": ["Bobby"]}]), "#5 bob (Bobby)")


if __name__ == "__main__":
    unittest.main()
