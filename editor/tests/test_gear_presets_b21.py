"""B21 item 25: the worn-gear preset's new knobs (quality / grade, stat count, stone count and level, holy-suit tier and
level, which class stone, only some slots, fill empty slots) and the refused non-class-suit class stat.
  python3 -I tests/test_gear_presets_b21.py                          # policy checks only
  GODSWAR_GEAR_TEST_PSQL="psql -h 127.0.0.1 -p <port>" GODSWAR_GEAR_TEST_DB=editor_gear_<copy> python3 -I tests/test_gear_presets_b21.py
The database part uses the same copied-DB fixture as test_gear_presets.py (Priest #4, four worn pieces) and undoes it."""
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from modules import common, gear_presets as G, inventory as I  # noqa: E402
import test_gear_presets as T  # noqa: E402


class Policy(unittest.TestCase):
    def test_defaults_keep_the_two_original_presets(self):
        s = G.inputs({'preset': 'standard'})
        self.assertEqual((s['quality'], s['grade'], s['stats'], s['stones'], s['stone_level'], s['suit_code']), (None, None, 4, 2, 10, 410))
        b = G.inputs({'preset': 'boundless'})
        self.assertEqual((b['quality'], b['grade'], b['stats'], b['stones']), (20, 25, 5, 4))

    def test_new_knobs_and_limits(self):
        o = G.inputs({'quality': 12, 'grade': 18, 'stats': 3, 'stones': 3, 'stone_level': 6, 'suit_tier': 2, 'suit_level': 3, 'slots': [3, 10, 3]})
        self.assertEqual((o['quality'], o['grade'], o['stats'], o['stones'], o['stone_level'], o['suit_code'], o['slots']), (12, 18, 3, 3, 6, 203, [3, 10]))
        self.assertEqual(G.inputs({'suit_tier': 0})['suit_code'], 0)
        for bad in ({'grade': 26}, {'quality': 21}, {'stats': 6}, {'stones': 5}, {'stone_level': 11}, {'suit_tier': 8}, {'slots': [12]},
                    {'class_stat_id': 205}, {'fill_empty': 'yes'}):
            with self.subTest(bad=bad), self.assertRaises(common.Bad):
                G.inputs(bad)

    def test_class_stone_on_other_gear_is_an_option(self):
        # B22 d55: "Also on other gear" is accepted with a class stone (the database decides per piece in plan())
        o = G.inputs({'class_stat': True, 'non_suit_special': True})
        self.assertTrue(o['any_gear'] and o['class_stat'])
        with self.assertRaises(common.Bad):
            G.inputs({'non_suit_special': True})              # needs a class stone
        with self.assertRaises(common.Bad):
            G.inputs({'class_stat': True, 'non_suit_special': 'yes'})

    def test_class_stone_follows_the_class(self):
        self.assertEqual(G.class_stone(0), 200)
        self.assertEqual(G.class_stone(3), 220)
        self.assertEqual(G.class_stone(2, 221), 221)
        with self.assertRaises(common.Bad):
            G.class_stone(1, 230)

    def test_stone_levels_scale_inside_the_level_range(self):
        for level in range(1, 11):
            for slot in (3, 10):
                for s in G.stone_rows(slot, True, 4, {}, level):
                    lo, hi = I.value_range(s['effect'], level, {})
                    self.assertTrue(lo <= s['value'] <= hi and s['level'] == level, (slot, level, s))
        self.assertEqual([s['value'] for s in G.stone_rows(10, True, 2, {}, 5)], [250, 400])


@unittest.skipUnless(T.COMMAND and T.DATABASE, 'requires explicit copied editor_gear* database')
class Database(T.GearPresetDatabase):
    def test_standard_batch_is_atomic_single_revision_preserves_excluded_and_undo(self):
        pass                                  # covered by test_gear_presets.py

    def test_boundless_and_class_conversion_class_stat_and_grade_values(self):
        pass

    def test_preview_token_and_readonly_prevent_unreviewed_changes(self):
        pass

    def test_custom_grade_stones_suit_and_class_stone_written(self):
        view = self.preview(preset='standard', quality=15, grade=20, stats=4, stones=3, stone_level=7, suit_tier=3, suit_level=5,
                            class_tier=4, class_stat=True, class_stat_id=221)
        self.assertEqual(len(view['items']), 4)
        code, data = self.api('apply', dict(view['options'], token=view['token']))
        self.assertEqual(code, 200, data)
        rows = [r for r in G.INV._items(self.rb.dump(self.cid)) if r['item_location'] == 0 and r['slot_index'] < 12]
        self.assertEqual(len(rows), 4)
        for r in rows:
            self.assertEqual((r['item_quality'], r['item_grade'], r['holy_suit_code'], r['holy_socket_count'], r['holy_socket1_level']),
                             (15, 20, 305, 3, 7))
            self.assertEqual(r['class_attribute1'], 221)
            self.assertIn(r['prop_id'], I.class_suit_items())
            self.assertIsNotNone(r['attribute4'])
            self.assertIsNone(r['attribute5'])

    def test_only_some_slots_and_fill_empty(self):
        view = self.preview(preset='standard', slots=[3], grade=12)
        self.assertEqual([r['slot'] for r in view['items']], [3])
        view = self.preview(preset='standard', fill_empty=True)
        new = [r for r in view['items'] if r['new']]
        self.assertTrue(new, 'a Priest with four worn pieces has empty slots to fill')
        code, data = self.api('apply', dict(view['options'], token=view['token']))
        self.assertEqual(code, 200, data)
        worn = {r['slot_index'] for r in G.INV._items(self.rb.dump(self.cid)) if r['item_location'] == 0 and r['slot_index'] < 12}
        self.assertTrue({r['slot'] for r in new} <= worn)

    def test_wrong_class_stone_refused(self):
        code, data = self.api('preview', {'preset': 'standard', 'class_tier': 3, 'class_stat': True, 'class_stat_id': 200})
        self.assertEqual(code, 400, data)


if __name__ == '__main__':
    unittest.main()
