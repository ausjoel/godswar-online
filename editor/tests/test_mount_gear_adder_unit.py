"""W23: unit tests for the mount gear adder (modules/mount_gear.py): gear ids per level, best legal tier per stat kind,
class defaults, the mount families. No database needed."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import mount_gear as G  # noqa: E402
from modules.common import Bad  # noqa: E402

# item_allowed_attributes on the W22 rig copy (2026-11-24): Lv40, Lv80 and Lv120 Coronet
LV40 = [300, 310, 320, 330, 340, 350, 360, 370, 380, 390, 400, 410, 420, 430, 440, 450]
LV80 = [k * 10 + t for k in range(30, 46) for t in (1, 2, 3)]
LV120 = [k * 10 + t for k in range(30, 46) for t in (5, 6, 7)]


class Adder(unittest.TestCase):
    def test_gear_ids_follow_the_feeder_lines(self):
        self.assertEqual(G.gear_id(15, 40), 14500)
        self.assertEqual(G.gear_id(15, 120), 14508)
        self.assertEqual(G.gear_id(16, 80), 14604)
        self.assertEqual(G.gear_id(19, 70), 14903)
        self.assertEqual({s: G.gear_id(s, 120) for s in G.GEAR_LINES}, {15: 14508, 16: 14608, 17: 14708, 18: 14808, 19: 14908})
        for bad in [(15, 45), (15, 130), (15, 30), (20, 40)]:
            with self.assertRaises(Bad):
                G.gear_id(*bad)

    def test_best_tier_per_kind_rises_with_level(self):
        phys = [G.KIND_OF[k] for k in G.CLASS_STATS[0]]
        self.assertEqual(phys, [34, 36, 40, 42])                              # the Warrior test set of mount-quality-grade.md
        self.assertEqual(G.best_ids(LV40, phys), [340, 360, 400, 420])
        self.assertEqual(G.best_ids(LV80, phys), [343, 363, 403, 423])
        self.assertEqual(G.best_ids(LV120, phys), [347, 367, 407, 427])
        magic = [G.KIND_OF[k] for k in G.CLASS_STATS[3]]
        self.assertEqual(G.best_ids(LV120, magic), [357, 377, 417, 437])
        with self.assertRaises(Bad):
            G.best_ids(LV120, [99])

    def test_class_defaults_are_four_distinct_known_stats(self):
        for prof, keys in G.CLASS_STATS.items():
            self.assertEqual(len(keys), G.ADDER_STATS)
            self.assertEqual(len(set(keys)), 4)
            self.assertTrue(all(k in G.KIND_OF for k in keys))

    def test_mount_families_have_every_level(self):
        fams = G.mount_families()
        self.assertGreater(len(fams), 20)
        for f in fams:
            self.assertEqual(sorted(f["levels"]), G.LEVELS)
        steed = next(f for f in fams if f["name"] == "Greek Steed")
        self.assertEqual(steed["levels"][40], 14220)
        self.assertEqual(steed["levels"][120], 14228)
        self.assertEqual(G.family_of(fams, 14229)["id"], steed["id"])     # the special Lv120 variant belongs to its family
        self.assertIsNone(G.family_of(fams, 6025))                         # a special mount without a level family


if __name__ == "__main__":
    unittest.main()
