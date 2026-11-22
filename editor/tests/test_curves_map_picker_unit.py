"""Wave 4 D item 5: the stat-curve map picker gets sections, minimap pictures and level ranges."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import curves as C

ROWS = [{'map_id': 22, 'scene': 'Elasson_All', 'published': 300, 'gm': 2, 'levels': [140, 160, 150]},
        {'map_id': 2, 'scene': 'Athens_Newbie', 'published': 900, 'gm': 0, 'levels': [10, 30, 20]},
        {'map_id': 1, 'scene': 'Athens', 'published': 50, 'gm': 0, 'levels': [1, 5, 3]},
        {'map_id': 29, 'scene': 'Execratively3', 'published': 150, 'gm': 0, 'levels': [55, 200, 115]},
        {'map_id': 25, 'scene': 'Agate', 'published': 0, 'gm': 18, 'levels': [None, None, None]},
        {'map_id': 99, 'scene': 'Nowhere', 'published': 1, 'gm': 0, 'levels': None}]


class Ctx:
    def __init__(self):
        self.sql = []
        self.rb = SimpleNamespace(pg=SimpleNamespace(json=self.q))
        self.catalog = SimpleNamespace(file=lambda n: {'22': {'name': 'Elasson'}, '2': {'name': "Athens' Suburb"}, '1': {'name': 'Athens'},
                                                         '29': {'name': 'Cursed Zone 3'}, '25': {'name': 'Treasure Land 1'}})

    def need_rb(self):
        return self.rb

    def q(self, sql):
        self.sql.append(sql)
        import copy
        return copy.deepcopy(ROWS)


class MapPicker(unittest.TestCase):
    def test_sections_order_pictures_and_levels(self):
        ctx = Ctx()
        d = C.maps(ctx)
        self.assertIn('get_byte(d.clear_bytes, 12)', ctx.sql[0])
        self.assertEqual([g['section'] for g in d['sections']], ['regular', 'instance', 'event', 'other'])
        self.assertEqual([m['map_id'] for m in d['sections'][0]['maps']], [1, 2, 22])   # town first, then by median level
        by = {m['map_id']: m for m in d['maps']}
        self.assertEqual(by[22]['thumb'], '/static/modules/character-minimaps/elasson_all.png')
        self.assertIsNone(by[99]['thumb'])
        self.assertIsNone(by[25]['levels'])                  # no level could be read
        self.assertEqual(by[29]['section_title'], 'Instances')
        self.assertIn('presets', d)
        self.assertIn('groups', d)                           # the stat groups the page already used are unchanged


if __name__ == '__main__':
    unittest.main()
