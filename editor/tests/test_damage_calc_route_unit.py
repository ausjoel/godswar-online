"""Damage calculator route (curves/calc): characters with physical/magic absorb split, plus the Wonderland code roster."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import curves as C

ROWS = [{'user_id': 13, 'name': 'the admin', 'profession': 2, 'level': 200, 'max_hp': 150172, 'physical_defense': 5132, 'magic_defense': 7827,
         'dodge': 2660, 'critical_resistance': 400, 'damage_absorb': 35652, 'phys_only': 0, 'magic_only': 0},
        {'user_id': 7, 'name': 'Typed', 'profession': 0, 'level': 150, 'max_hp': 0, 'physical_defense': None, 'magic_defense': 10,
         'dodge': 5, 'critical_resistance': 1, 'damage_absorb': 1000, 'phys_only': 300, 'magic_only': 200}]


class Ctx:
    def __init__(self):
        self.sql = []
        self.rb = SimpleNamespace(pg=SimpleNamespace(json=self.q))

    def q(self, sql):
        self.sql.append(sql)
        return ROWS


class CalcRoute(unittest.TestCase):
    def test_split_and_roster(self):
        ctx = Ctx()
        orig = C.W._rb
        C.W._rb = lambda c: c.rb
        try:
            r = C.calc(ctx)
        finally:
            C.W._rb = orig
        admin, typed = r['characters']
        self.assertEqual((admin['pabs'], admin['mabs']), (35652, 35652))      # InjureImbibe counts on both channels
        self.assertEqual((typed['pabs'], typed['mabs']), (800, 700))         # 1000 total: magic-only 200 is not physical, and back
        self.assertEqual((typed['hp'], typed['pd']), (1, 0))                 # nulls never break the page
        self.assertIn('character_stat_summary', ctx.sql[0])
        self.assertEqual(len(r['wonderland']['roster']['athens']), 115)
        self.assertFalse(r['wonderland']['party_size_matters'])
        self.assertAlmostEqual(r['attack_interval'], 23 / 12)


if __name__ == '__main__':
    unittest.main()
