"""Issue #18: the editor's copy of the server's QuestChainPlanner (modules/skills.py quest_chain_plan)."""
import os, sys, unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from modules import skills as S

RULES = S.quest_rules()["quests"]


def rows(completed=(), carried=()):
    return [{"quest_id": q, "state": 1} for q in completed] + [{"quest_id": q, "state": 0} for q in carried]


class QuestChainPlanTests(unittest.TestCase):
    def test_chain_is_camp_main_line_without_daily_or_repeat(self):
        ids = S.main_line_through(RULES, "Sparta", 38)
        self.assertIn(518, ids)
        for q in ids:
            r = RULES[str(q)]
            self.assertTrue(r["main"] and not r["daily"] and not r["repeat"] and r["camp"] == "Sparta", q)
        self.assertFalse(any(581 <= q <= 626 for q in S.main_line_through(RULES, "Sparta", 200)))
        self.assertFalse(set(ids) & set(S.main_line_through(RULES, "Athens", 38)))

    def test_prerequisites_are_closed(self):
        ids = set(S.main_line_through(RULES, "Sparta", 38))
        for q in ids:
            pre = int(RULES[str(q)].get("prereq") or 0)
            self.assertTrue(pre == 0 or pre in ids, (q, pre))

    def test_complete_skips_done_and_reports_carried(self):
        p = S.quest_chain_plan(RULES, "Sparta", "complete_main", 38, "main", rows(completed=[518], carried=[520]))
        self.assertNotIn(518, p["changed"]); self.assertNotIn(520, p["changed"]); self.assertIn(519, p["changed"])
        self.assertEqual(p["conflicts"], [520])
        done = S.main_line_through(RULES, "Sparta", 38)
        self.assertEqual(S.quest_chain_plan(RULES, "Sparta", "complete_main", 38, "main", rows(completed=done))["changed"], [])

    def test_reset_scopes(self):
        r = rows(completed=[518, 519, 100, 1518], carried=[520])
        p = S.quest_chain_plan(RULES, "Sparta", "reset", 0, "main", r)
        self.assertEqual(p["changed"], [518, 519]); self.assertEqual(p["conflicts"], [520])
        self.assertEqual(S.quest_chain_plan(RULES, "Sparta", "reset", 0, "all", rows(completed=[100, 518]))["changed"], [100, 518])


if __name__ == "__main__":
    unittest.main()
