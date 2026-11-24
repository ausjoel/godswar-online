"""W23 (the admin 2026-11-24): the character maxer's quest finisher pays every finished quest's rewards, and the maxer's result
is sanity-checked (class / level / forge caps) before it is written. Pure tests: no database, no server."""
import copy
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import reborn  # noqa: E402
from modules import character, maxer as MX  # noqa: E402

ITEMS = {
    1000: {"name": "Short Sword", "slot": 10, "classes": [0], "minlv": 1, "overlap": 1},
    1400: {"name": "Spear", "slot": 10, "classes": [1], "minlv": 1, "overlap": 1},
    1700: {"name": "Short Wand", "slot": 10, "classes": [2], "minlv": 1, "overlap": 1},
    1800: {"name": "Long Staff", "slot": 10, "classes": [3], "minlv": 1, "overlap": 1},
    2100: {"name": "Light Armor", "slot": 3, "classes": [0, 1, 2, 3], "minlv": 1, "overlap": 1},
    3876: {"name": "Newbie Gift Bag", "slot": -1, "classes": [], "minlv": None, "overlap": 1},
    4232: {"name": "Level 3 Crystal", "slot": -1, "classes": [], "minlv": None, "overlap": 99},
    8040: {"name": "Standard Robe(7 days)", "slot": 12, "classes": [0, 1, 2, 3], "minlv": 1, "overlap": 1},
    5000: {"name": "Mage Robe Lv150", "slot": 3, "classes": [2, 3], "minlv": 150, "overlap": 1},
}


def meta_of(pid):
    m = ITEMS.get(int(pid), {"name": "item %s" % pid, "slot": -1, "classes": [], "minlv": None, "overlap": 1})
    return dict(m, id=int(pid), defaults={"bound": 1, "item_quality": 1, "item_grade": 1}, server=True)


def policy(item):
    return {"overlap": meta_of(item)["overlap"], "bound": 1}


RULES = {
    "518": {"id": 518, "camp": "Sparta", "main": True, "minlv": 1, "exp": 100, "tp": 50, "silver": 1000, "gold": 0},
    "519": {"id": 519, "camp": "Sparta", "main": True, "minlv": 2, "exp": 200, "tp": 60, "silver": 2000, "gold": 1},
    "376": {"id": 376, "camp": "Sparta", "main": True, "minlv": 120, "exp": 400000, "tp": 40, "silver": 0, "gold": 0},
    "1518": {"id": 1518, "camp": "Athens", "main": True, "minlv": 1, "exp": 100, "tp": 50, "silver": 1000, "gold": 0},
    "600": {"id": 600, "camp": "Sparta", "main": False, "daily": True, "minlv": 1, "exp": 9, "tp": 9, "silver": 9, "gold": 9},
}
DATA = {"items": {"518": [{"slot": 0, "item": 3876}], "519": [{"slot": 0, "item": 1000}, {"slot": 1, "item": 1400},
                                                            {"slot": 2, "item": 1700}, {"slot": 3, "item": 1800}]},
        "titles": {"376": 2506}, "mycenae": {"quests": [], "item": 4232, "quantity": 8}}
OPTS = dict(MX.DEFAULTS, skills=False, talent_rank=0, zodiac=False, gear=False, capacity=False)


def char(prof=0, level=1, camp=0, items=(), quests=(), **kw):
    b = {"id": 7, "name": "Tester", "profession": prof, "camp": camp, "fighter_job_lv": level, "fighter_job_exp": 0,
         "fighter_level_sealed": False, "zodiac_level": 1, "zodiac_energy": 0, "pet_shed_capacity": 2, "Money": 0, "Stone": 0,
         "SkillPoint": 0, "SkillExp": 0, "GM": 0}
    b.update(kw)
    return {"character_base": [b], "character_items": [dict(r) for r in items],
            "character_quests": [{"character_id": 7, "quest_id": q, "state": 1, "progress": 0} for q in quests],
            "character_skills": [], "character_talents": []}


def rw(**kw):
    base = {"data": DATA, "values": {}, "slots": {}, "rates": {}, "appraisal_bp": 0, "policy": policy, "chest_ready": True,
            "title_expert": True, "titles": {"available": True, "owned": [], "catalog": [2506], "names": {2506: "Greek Vanguard"},
                                              "limit": 128}, "talent_ceiling": None}
    base.update(kw)
    return base


def plan(d, o=None, r=None, client=None, checker=None):
    return MX.build_plan(d, dict(OPTS, **(o or {})), {}, [], RULES, client or {}, meta_of, r if r is not None else rw(), checker)


def bag_row(i, slot, pid, stack=1, q=1, g=1, bound=1):
    return {"id": i, "user_id": 7, "item_location": 1, "slot_index": slot, "prop_id": pid, "stack": stack, "bound": bound,
            "item_quality": q, "item_grade": g, "item_exp": 0}


class GameRuleTests(unittest.TestCase):
    def test_rate_multiply_matches_server_rates(self):
        self.assertEqual(MX.rate_multiply(1000, 1, 10), 1000)          # rate 1 passes unchanged, even above the cap
        self.assertEqual(MX.rate_multiply(1001, 2.5, 10 ** 9), 2502)   # truncated
        self.assertEqual(MX.rate_multiply(10 ** 8, 10, MX.QUEST_RATE_CAP), MX.QUEST_RATE_CAP)
        self.assertEqual(MX.rate_multiply(3, 0.1, 100), 0)
        self.assertEqual(MX.rate_multiply(0, 10, 100), 0)

    def test_payout_uses_override_then_rates_and_appraisal(self):
        rates = {"exp.quest": 10, "tp.quest": 10, "money.quest_silver": 10, "money.quest_gold": 2}
        self.assertEqual(MX.quest_payout(RULES["519"], None, rates), {"exp": 2000, "tp": 600, "silver": 20000, "gold": 2})
        over = {"exp": 5, "tp": 0, "silver": 7, "gold": 0}
        self.assertEqual(MX.quest_payout(RULES["519"], over, rates), {"exp": 50, "tp": 0, "silver": 70, "gold": 0})
        self.assertEqual(MX.quest_payout(RULES["519"], None, {}, appraisal_bp=1000)["exp"], 220)   # +10%, then the rate

    def test_apply_exp_like_the_server(self):
        self.assertEqual(MX.apply_exp(1, 0, 199), (1, 199))
        self.assertEqual(MX.apply_exp(1, 0, 200 + 252), (3, 0))
        self.assertEqual(MX.apply_exp(200, 0, 10 ** 8), (200, 0))      # the top level keeps no EXP
        self.assertEqual(MX.apply_exp(1, 0, 10 ** 9, cap_level=5), (5, character.EXP_NEXT[4] - 1))
        self.assertEqual(MX.apply_exp(30, 5, 10 ** 6, sealed=True), (30, 10 ** 6 + 5))

    def test_talent_exp_makes_points(self):
        self.assertEqual(MX.apply_talent_exp(50, 3, 160), (10, 5))
        self.assertEqual(MX.apply_talent_exp(0, 0, 0), (0, 0))

    def test_override_menu_replaces_the_quests_own(self):
        own = MX.reward_menu(519, DATA["items"], {})
        self.assertEqual([m["item"] for m in own], [1000, 1400, 1700, 1800])
        over = MX.reward_menu(519, DATA["items"], {519: [{"slot_index": 0, "item_id": 2100, "item_quality": 5, "item_grade": None}]})
        self.assertEqual([(m["item"], m["quality"]) for m in over], [(2100, 5)])

    def test_one_slot_per_quest_and_the_class_weapon(self):
        menu = MX.reward_menu(519, DATA["items"], {})
        for prof, item in ((0, 1000), (1, 1400), (2, 1700), (3, 1800)):
            pick, skipped = MX.pick_slot(menu, prof, meta_of)
            self.assertEqual(pick["item"], item)
            self.assertEqual(len(skipped), 3)
        pick, skipped = MX.pick_slot(MX.reward_menu(518, DATA["items"], {}), 3, meta_of)
        self.assertEqual((pick["item"], skipped), (3876, []))

    def test_place_items_tops_up_then_free_cells_then_chest_never_above_99(self):
        grant = {"item": 4232, "qty": 150, "bound": 1, "quality": 1, "grade": 1, "attrs": [], "quest": 1, "name": "x"}
        rows = [bag_row(1, 0, 4232, stack=90)] + [bag_row(10 + s, s, 3876) for s in range(1, 95)]
        out = MX.place_items(rows, [grant], policy)
        self.assertEqual([(u[1], u[2]) for u in out["updates"]], [(99, 9)])
        self.assertEqual([(s, n) for s, _g, n in out["inserts"]], [(95, 99)])
        self.assertEqual([n for _g, n in out["chest"]], [42])
        full = [bag_row(100 + s, s, 3876) for s in range(96)]
        out = MX.place_items(full, [dict(grant, qty=250)], policy)
        self.assertEqual([n for _g, n in out["chest"]], [99, 99, 52])


class PlanTests(unittest.TestCase):
    def test_finisher_pays_rewards_once(self):
        d = char(prof=3, level=1)
        p = plan(d, {"level": 10})
        r = p["rewards"]
        self.assertEqual(r["quests"], 2)                                # 518, 519 (the daily 600 never counts)
        self.assertEqual((r["silver"], r["gold"], r["tp"]), (3000, 1, 110))
        self.assertEqual([(x["item"], x["qty"]) for x in r["bag"]], [(3876, 1), (1800, 1)])   # a Mage gets the staff
        self.assertEqual(r["chest"], [])
        sql = "\n".join(p["sql"])
        self.assertIn('"Money" = 3000', sql)
        self.assertIn('"Stone" = 1', sql)
        self.assertIn('"SkillPoint" = 1', sql)
        self.assertIn('"SkillExp" = 10', sql)
        self.assertIn("\"wallet_revision\" = \"wallet_revision\" + 1", sql)
        self.assertEqual(sql.count("INSERT INTO character_items"), 2)
        self.assertEqual(sql.count("character_item_audit"), 2)
        again = plan(p["after"], {"level": 10})                       # quests now done: nothing more is paid
        self.assertIsNone(again["rewards"])
        self.assertNotIn("character_items", "\n".join(again["sql"]))

    def test_full_bag_goes_to_the_prize_chest(self):
        d = char(prof=0, level=1, items=[bag_row(100 + s, s, 3876) for s in range(96)])
        p = plan(d, {"level": 5})
        self.assertEqual([(x["item"], x["qty"]) for x in p["rewards"]["chest"]], [(3876, 1), (1000, 1)])
        sql = "\n".join(p["sql"])
        self.assertEqual(sql.count("INSERT INTO prize_chest_entries"), 2)
        self.assertNotIn("INSERT INTO character_items", sql)
        self.assertIn(MX.CHEST_FIELD, MX._fields(p))
        with self.assertRaises(MX.Refused):
            plan(d, {"level": 5}, rw(chest_ready=False))

    def test_quest_title_granted_or_explained(self):
        d = char(prof=0, level=119)
        p = plan(d, {"level": 120})
        self.assertEqual([t["id"] for t in p["rewards"]["titles"]], [2506])
        self.assertIn("INSERT INTO character_title_grants", "\n".join(p["sql"]))
        self.assertIn(MX.TITLE_FIELD, MX._fields(p))
        owned = plan(d, {"level": 120}, rw(titles={"available": True, "owned": [2506], "catalog": [2506], "limit": 128}))
        self.assertEqual(owned["rewards"]["titles"], [])
        off = plan(d, {"level": 120}, rw(title_expert=False))
        self.assertEqual(off["rewards"]["titles"], [])
        self.assertTrue(any("npc.title_expert" in n for n in off["rewards"]["notes"]))
        none = plan(d, {"level": 120}, rw(titles={"available": False, "reason": "no server"}))
        self.assertTrue(any("Title Expert grants it" in n for n in none["rewards"]["notes"]))

    def test_rewards_option_off_marks_quests_only(self):
        p = plan(char(level=1), {"level": 10, "rewards": False})
        self.assertIsNone(p["rewards"])
        self.assertIn("2 main quests marked done", p["changes"])

    def test_quest_exp_is_capped_at_the_chosen_level(self):
        p = plan(char(level=1), {"level": 2})
        b = p["after"]["character_base"][0]
        self.assertEqual(b["fighter_job_lv"], 2)
        self.assertLess(b["fighter_job_exp"], character.EXP_NEXT[1])

    def test_never_lowers_a_level(self):
        with self.assertRaises(MX.Bad):
            plan(char(level=150), {"level": 100})


class MaxerAuditTests(unittest.TestCase):
    def worn(self, pid, q, g, slot=10):
        return {"id": 50, "user_id": 7, "item_location": 0, "slot_index": slot, "prop_id": pid, "stack": 1, "bound": 1,
                "item_quality": q, "item_grade": g, "item_exp": 0}

    def test_gear_goes_exactly_to_the_forge_cap(self):
        client = {"1000": {"max_quality": 10, "max_grade": 12}}
        for q, g in ((1, 1), (20, 25)):                                 # raised, and an over-cap item brought down
            p = plan(char(prof=0, level=1, items=[self.worn(1000, q, g)]), {"gear": True, "quests": False, "level": 1}, client=client)
            r = p["after"]["character_items"][0]
            self.assertEqual((r["item_quality"], r["item_grade"]), (10, 12))
            self.assertIn("recompute_character_holy_suit_points", "\n".join(p["sql"]))
            self.assertIn("character_item_audit", "\n".join(p["sql"]))

    def test_gear_without_forge_data_is_left_alone(self):
        p = plan(char(prof=0, level=1, items=[self.worn(8040, 1, 1, slot=12)]), {"gear": True, "quests": False, "level": 1}, client={})
        self.assertNotIn("UPDATE character_items", "\n".join(p["sql"]))
        self.assertTrue(any("no forge limits" in w for w in p["warnings"]))
        self.assertEqual(MX.gear_target((1, 3), 20, None, {"quality": 20, "grade": 25}), (20, 3))   # mount: quality only

    def test_gear_respects_the_servers_end_game_limits(self):
        original = MX.gear_caps({"endgame.enabled": 2, "limits.original_gw": 1})
        self.assertEqual((original["quality"], original["grade"]), (10, 12))
        self.assertEqual(MX.gear_target((1, 1), 20, 25, original), (10, 12))
        self.assertEqual(MX.gear_target((15, 20), 20, 25, original), (15, 20))   # past the limit already: kept
        self.assertEqual(MX.gear_target((15, 20), 10, 12, original), (10, 12))   # past the item's own limit: brought down
        self.assertEqual(MX.gear_caps({"endgame.enabled": 0, "limits.original_gw": 0})["quality"], 10)
        self.assertEqual(MX.gear_caps({"endgame.enabled": 1})["grade"], 25)
        self.assertEqual(MX.gear_caps({"limits.original_gw": 0, "gem.god_gem_drops": 0})["grade"], 24)
        self.assertEqual(MX.gear_caps({"limits.original_gw": 0, "gem.god_gem_drops": 1})["grade"], 25)
        p = plan(char(prof=0, level=1, items=[self.worn(1000, 1, 1)]), {"gear": True, "quests": False, "level": 1},
                 rw(gear_caps=original), client={"1000": {"max_quality": 20, "max_grade": 25}})
        r = p["after"]["character_items"][0]
        self.assertEqual((r["item_quality"], r["item_grade"]), (10, 12))

    def test_gear_the_class_or_level_cannot_wear_is_left_alone(self):
        client = {"1700": {"max_quality": 10, "max_grade": 12}, "5000": {"max_quality": 10, "max_grade": 12}}
        p = plan(char(prof=0, level=100, items=[self.worn(1700, 1, 1), dict(self.worn(5000, 1, 1, slot=3), id=51)]),
                 {"gear": True, "quests": False, "level": 100}, client=client)
        self.assertNotIn("UPDATE character_items", "\n".join(p["sql"]))
        self.assertEqual(len(p["warnings"]), 2)

    def test_validator_reports_only_new_findings(self):
        def checker(d):
            b = d["character_base"][0]
            out = [{"rule": "old", "where": "x", "message": "was already there"}]
            if b["Money"] > 2000:
                out.append({"rule": "money", "where": "character", "message": "too rich"})
            return out
        p = plan(char(prof=3, level=1), {"level": 10}, checker=checker)
        self.assertEqual([f["rule"] for f in p["findings"]], ["money"])

    def test_talents_only_of_the_class(self):
        talents = [{"id": 1, "class": 0, "prefix": 0, "prefix_rank": 0}, {"id": 2, "class": 1, "prefix": 0, "prefix_rank": 0},
                   {"id": 3, "class": 0, "prefix": 1, "prefix_rank": 80, "name": "Deep"}]
        p = MX.build_plan(char(prof=0, level=1), dict(OPTS, quests=False, level=1, talent_rank=60), {}, talents, RULES, {}, meta_of, rw())
        ids = sorted(int(r["talent_id"]) for r in p["after"]["character_talents"])
        self.assertEqual(ids, [1])
        self.assertTrue(any("Deep" in w for w in p["warnings"]))


class DataTests(unittest.TestCase):
    def test_prize_chest_is_in_the_undo_dump(self):
        self.assertIn(("prize_chest_entries", "character_id = {cid}", ("id",)), reborn.DUMP_TABLES_EXTRA)

    def test_reward_json_matches_the_server_source(self):
        here = os.path.dirname(os.path.abspath(__file__))
        src = os.path.normpath(os.path.join(here, "..", "..", "server"))
        if not os.path.isdir(os.path.join(src, "src", "Godswar.Server")):
            self.skipTest("no server source beside the editor")
        sys.path.insert(0, os.path.join(here, "..", "modules", "skills_data"))
        import build_quest_rewards as B
        built = B.build(src)
        data = copy.deepcopy(MX.quest_reward_data())
        self.assertEqual(built["items"], data["items"])
        self.assertEqual(built["titles"], data["titles"])
        self.assertEqual(built["mycenae"], data["mycenae"])
        self.assertEqual([x["item"] for x in data["items"]["519"]], [1000, 1400, 1700, 1800])


class FinisherW24Tests(unittest.TestCase):
    """W24 (the admin d154 / d155): the quest finisher pays every item of a choice menu, at the quests' base values."""
    def test_finisher_pays_every_choice_item_at_base_values(self):
        d = char(prof=3, level=1)
        r, sql, _v = MX.finisher_rewards({"rw": rw(rates={"quest.silver": 3}, all_choices=True) | {"rates": {}},
                                          "meta_of": meta_of}, d, [518, 519], RULES)
        self.assertEqual(sorted(g["item"] for g in r["grants"]), [1000, 1400, 1700, 1800, 3876])
        self.assertEqual(r["skipped"], [])
        self.assertEqual((r["silver"], r["gold"], r["tp"]), (3000, 1, 110))     # base values, no rate applied

    def test_maxer_still_pays_one_pick(self):
        d = char(prof=3, level=1)
        r, _sql, _v = MX.finisher_rewards({"rw": rw(), "meta_of": meta_of}, d, [518, 519], RULES)
        self.assertEqual(sorted(g["item"] for g in r["grants"]), [1800, 3876])


if __name__ == "__main__":
    unittest.main(verbosity=2)
