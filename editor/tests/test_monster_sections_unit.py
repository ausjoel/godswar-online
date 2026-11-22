"""Wave 4 D: map sections and monster tiers shared by the tier, progression and curve pages."""
import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import _monster_sections as S


class Sections(unittest.TestCase):
    def test_every_catalogue_map_has_a_section_and_known_kinds(self):
        import json
        maps = json.loads((Path(__file__).resolve().parents[1] / 'catalog-origin' / 'maps.json').read_text())
        for mid in maps:
            self.assertIn(S.section_of(mid), S.SECTION_TITLE)
        self.assertEqual(S.section_of(22), 'regular')
        self.assertEqual(S.section_of(200), 'instance')
        self.assertEqual(S.section_of(205), 'instance')
        self.assertEqual(S.section_of(25), 'event')
        self.assertEqual(S.section_of(38), 'battlefield')
        self.assertEqual(S.section_of(50), 'arena')
        self.assertEqual(S.section_of(999), 'other')
        self.assertEqual(S.section_of('x'), 'other')
        # no map in two sets
        sets = [set(S.REGULAR), set(S.INSTANCE), set(S.EVENT), set(S.BATTLEFIELD), S.ARENA, set(S.OTHER)]
        for i, a in enumerate(sets):
            for b in sets[i + 1:]:
                self.assertFalse(a & b)

    def test_server_tier_matches_the_key_rule(self):
        self.assertEqual(S.server_tier('C_boss_greecewarrior_002'), 'boss')
        self.assertEqual(S.server_tier('A_Boss_gensbd_002'), 'boss')
        self.assertEqual(S.server_tier('A_elite_stub_001'), 'elite')
        self.assertEqual(S.server_tier('A_normal_stub_001'), 'normal')
        self.assertEqual(S.server_tier('A_bannerfqs_001_all'), 'normal')

    def test_rank_and_disagreement_note(self):
        self.assertEqual(S.rank_of('C_boss_x', is_boss=False, is_elite=True), 'elite')
        self.assertEqual(S.rank_of('A_normal_x', name='[Elite]Someone'), 'elite')
        self.assertIsNotNone(S.tier_note('C_boss_greecewarrior_002', 'elite'))
        self.assertIsNone(S.tier_note('A_elite_x', 'elite'))

    def test_grouped_order(self):
        g = S.grouped([{'id': 200}, {'id': 22}, {'id': 0}, {'id': 25}])
        self.assertEqual([x['section'] for x in g], ['regular', 'instance', 'event'])
        self.assertEqual([m['id'] for m in g[0]['maps']], [0, 22])


if __name__ == '__main__':
    unittest.main()
