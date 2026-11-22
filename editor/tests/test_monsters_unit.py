"""Wave 4 D items 1 and 4: the monster index (name, icon, rank, section, levels, loot) and the progression rows."""
import copy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import monsters as MO

CAT = {'monsters': {'A_elite_x_001': {'name': '[E]Grumpy', 'model': 'monster_Ahrimannew_001.jcs', 'names_by_map': {'5': 'Grumpy of Nemea'}},
                    'C_boss_greecewarrior_002': {'name': '[Elite]General James', 'model': 'nope.jcs'},
                    'B_normalG_fish_001': {'name': 'Fish', 'model': 'monster_fish_001.jcs'},
                    'A_ghost_only_client': {'name': 'Ghost', 'model': ''},
                    'B_bossG_octopus_001': {'name': 'Kraken', 'model': '', 'maps': [205]}},
       'maps': {'5': {'name': 'Nemea'}, '0': {'name': 'Sparta'}, '205': {'name': 'Atlantis Portal'}}}
FACTS = {'templates': [{'key': 'A_elite_x_001', 'name': 'Grumpy', 'map': 5, 'boss': False, 'elite': True, 'model': 'monster_Ahrimannew_001.jcs'},
                       {'key': 'C_boss_greecewarrior_002', 'name': 'General', 'map': 0, 'boss': False, 'elite': True, 'model': 'x.jcs'},
                       {'key': 'B_normalG_fish_001', 'name': 'Fish', 'map': 205, 'boss': False, 'elite': False, 'model': 'monster_fish_001.jcs'}],
         'spawns': [{'map': 5, 'key': 'A_elite_x_001', 'lv': 60, 'n': 3}, {'map': 5, 'key': 'A_elite_x_001', 'lv': 62, 'n': 1},
                    {'map': 0, 'key': 'C_boss_greecewarrior_002', 'lv': 30, 'n': 1}, {'map': 205, 'key': 'B_normalG_fish_001', 'lv': 95, 'n': 9}],
         'loot': ['A_elite_x_001', 'B_normalG_fish_001@205']}


class Ctx:
    def __init__(self, facts=FACTS):
        self.facts = facts
        self.sql = []
        self.rb = SimpleNamespace(writable=False, pg=SimpleNamespace(json=self.query))
        self.catalog = SimpleNamespace(file=lambda n: CAT.get(n, {}))

    def need_rb(self):
        return self.rb

    def query(self, sql):
        self.sql.append(sql)
        if 'to_regclass' in sql:
            return True
        return copy.deepcopy(self.facts)


class Monsters(unittest.TestCase):
    def setUp(self):
        self.p = patch.object(MO, 'thumbs', return_value={'monster_Ahrimannew_001', 'B_normalG_fish_001'})
        self.p.start()

    def tearDown(self):
        self.p.stop()

    def test_index_names_icons_ranks_and_loot(self):
        ix = {m['key']: m for m in MO.build_index(Ctx())['monsters']}
        e = ix['A_elite_x_001']
        self.assertEqual(e['name'], '[E]Grumpy')                               # the client (in-game) name
        self.assertEqual(e['thumb'], '/static/monster-thumbs/monster_Ahrimannew_001.png')   # by model stem
        self.assertEqual(ix['B_normalG_fish_001']['thumb'], '/static/monster-thumbs/B_normalG_fish_001.png')  # key wins
        self.assertIsNone(ix['C_boss_greecewarrior_002']['thumb'])
        self.assertEqual((e['level_min'], e['level_max'], e['level']), (60, 62, 60))
        self.assertEqual(e['section'], 'regular')
        self.assertTrue(e['loot'])
        b = ix['C_boss_greecewarrior_002']
        self.assertEqual((b['rank'], b['tier']), ('elite', 'boss'))
        self.assertIsNotNone(b['tier_note'])
        f = ix['B_normalG_fish_001']
        self.assertEqual(f['section'], 'instance')
        self.assertFalse(f['loot'])
        self.assertEqual(f['area_loot'], [205])
        self.assertIn('A_ghost_only_client', ix)                               # client-only monsters are listed too
        self.assertIn('get_byte(d.clear_bytes, 12)', ' '.join(Ctx().sql) or MO._level('d.clear_bytes'))

    def test_progression_groups_maps_by_section_in_level_order(self):
        d = MO.progression(Ctx())
        self.assertEqual([g['section'] for g in d['groups']], ['regular', 'instance'])
        reg = d['groups'][0]['maps']
        self.assertEqual([m['id'] for m in reg], [0, 5])                      # town first, then by level
        nemea = reg[1]
        self.assertEqual(nemea['monsters'][0]['name'], 'Grumpy of Nemea')     # the per-map client name
        inst = d['groups'][1]['maps'][0]
        self.assertTrue(inst['monsters'][0]['loot'])                          # template@map table counts on that map
        k = next(x for x in inst['monsters'] if x['key'] == 'B_bossG_octopus_001')   # spawned in code: typical level, guessed
        self.assertEqual((k['levels'], k['guessed']), ([100], True))
        self.assertEqual(d['unspawned'], 1)

    def test_works_without_the_database(self):
        ctx = Ctx(facts=None)
        ctx.query = lambda sql: (_ for _ in ()).throw(RuntimeError('no db'))
        ctx.rb = SimpleNamespace(writable=False, pg=SimpleNamespace(json=ctx.query))
        d = MO.build_index(ctx)
        self.assertFalse(d['db'])
        self.assertEqual(len(d['monsters']), 5)


if __name__ == '__main__':
    unittest.main()
