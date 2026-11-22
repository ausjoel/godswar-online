"""Monster stat curve: every map with monsters (the admin 2026-11-19: "doesnt have medusa, atlantis, wonderland, bay under attack").

Medusa's Island HP is written to medusa_monster_rules as stock x curve; Atlantis and Wonderland are listed but view only;
Bay under Attack 1/2 get names that tell them apart."""
import copy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import curves as C
from modules.common import Bad

MAP_ROWS = [{'map_id': 2, 'scene': 'Athens_Newbie', 'published': 900, 'gm': 0, 'medusa': 0, 'templates': 28, 'levels': [10, 30, 20]},
            {'map_id': 200, 'scene': 'Medusa_Island', 'published': 0, 'gm': 0, 'medusa': 80, 'templates': 41, 'levels': [95, 200, 95]},
            {'map_id': 204, 'scene': 'Medusa_Island2', 'published': 0, 'gm': 0, 'medusa': 40, 'templates': 40, 'levels': [95, 200, 95]},
            {'map_id': 205, 'scene': 'Atlantis_Entrance', 'published': 0, 'gm': 0, 'medusa': 0, 'templates': 58, 'levels': None},
            {'map_id': 207, 'scene': 'Fane', 'published': 0, 'gm': 0, 'medusa': 0, 'templates': 38, 'levels': None},
            {'map_id': 208, 'scene': 'Salame', 'published': 0, 'gm': 56, 'medusa': 0, 'templates': 12, 'levels': [55, 60, 55]},
            {'map_id': 209, 'scene': 'Salame2', 'published': 0, 'gm': 56, 'medusa': 0, 'templates': 12, 'levels': [75, 80, 75]}]
CATALOG = {'2': {'name': "Athens' Suburb"}, '200': {'name': "Medusa's Island"}, '204': {'name': "Medusa's Island 2"},
           '205': {'name': 'Atlantis Portal'}, '207': {'name': 'Wonderland'}, '208': {'name': 'Bay under Attack'},
           '209': {'name': 'Bay under Attack'}}
LOAD = {'published': [], 'gm': [], 'edits': [], 'attributes': [], 'ranks': [],
        'medusa': [{'difficulty': 1, 'alias': 'boss-stheno', 'level': 200, 'hp': 3000000},
                   {'difficulty': 2, 'alias': 'boss-stheno', 'level': 200, 'hp': 6000000},
                   {'difficulty': 3, 'alias': 'boss-stheno', 'level': 200, 'hp': 99},          # hand-edited away from stock
                   {'difficulty': 2, 'alias': 'normal-jungle-deer', 'level': 95, 'hp': 1600000}]}


class Ctx:
    def __init__(self, reply):
        self.sql, self.reply = [], reply
        self.rb = SimpleNamespace(pg=SimpleNamespace(json=self.q))
        self.catalog = SimpleNamespace(file=lambda n: CATALOG, name=lambda kind, key: None)
        self.body = {}

    def need_rb(self):
        return self.rb

    def q(self, sql):
        self.sql.append(sql)
        return copy.deepcopy(self.reply)


class EveryMap(unittest.TestCase):
    def setUp(self):
        self._rb = C.W._rb
        C.W._rb = lambda c: c.rb

    def tearDown(self):
        C.W._rb = self._rb

    def test_picker_lists_instances_with_clear_names_and_what_can_be_edited(self):
        ctx = Ctx(MAP_ROWS)
        d = C.maps(ctx)
        sql = ctx.sql[0]
        self.assertIn('medusa_monster_rules', sql)
        self.assertIn('OR r.map_id IN (205,207)', sql)
        self.assertIn('WHEN 200 THEN ARRAY[2,3]::smallint[]', sql)
        by = {m['map_id']: m for m in d['maps']}
        self.assertEqual(by[208]['name'], 'Bay under Attack 1')
        self.assertEqual(by[209]['name'], 'Bay under Attack 2')
        self.assertEqual(by[204]['name'], "Medusa's Island (Normal)")
        self.assertEqual(by[200]['name'], "Medusa's Island (Enhanced / Mythic)")
        self.assertEqual(by[2]['name'], "Athens' Suburb")                       # regular maps keep the client name
        self.assertTrue(by[205]['view_only'] and by[207]['view_only'])
        self.assertIn('AtlantisSpawnBalancePolicy', by[205]['why'])
        self.assertIn('WonderlandMonsterPlan', by[207]['why'])
        self.assertEqual(by[200]['edits'], ['hp'])
        self.assertEqual(by[208]['edits'], C.GROUPS)
        self.assertEqual(by[200]['spawns'], 80)
        inst = next(g for g in d['sections'] if g['section'] == 'instance')
        self.assertEqual([m['map_id'] for m in inst['maps']], [208, 209, 200, 204, 205, 207])   # by level; code maps at typical level

    def test_view_only_maps_are_refused(self):
        for mid in (205, 207):
            with self.assertRaises(Bad):
                C.load(Ctx(LOAD), [mid])

    def test_medusa_hp_is_stock_times_curve_and_never_combat_rows(self):
        ctx = Ctx(LOAD)
        curves = C._curves({'hp': [[1, 2.0], [200, 2.0]], 'attack': [[1, 3.0], [200, 3.0]]})
        spawns, temps, hp_rows, attr_rows, keep = C.plan(ctx, [200, 204], curves, 'middle')
        self.assertIn('ARRAY[1,2,3]::smallint[]', ctx.sql[0])
        got = {(r['difficulty'], r['template']): (r['base_hp'], r['new_hp']) for r in hp_rows}
        self.assertEqual(got[(1, 'boss-stheno')], (3000000, 6000000))
        self.assertEqual(got[(3, 'boss-stheno')], (15000000, 30000000))           # stock x5, not the hand-edited 99
        self.assertEqual(got[(2, 'normal-jungle-deer')], (1600000, 3200000))
        self.assertEqual(attr_rows, [])                                            # attack is server code on Medusa
        self.assertEqual({s['map'] for s in spawns}, {200, 204})
        flat = C._curves({})
        _, _, hp0, _, _ = C.plan(Ctx(LOAD), [200, 204], flat, 'middle')
        self.assertEqual([(r['difficulty'], r['new_hp']) for r in hp0], [(3, 15000000)])   # "No change" restores stock

    def test_apply_writes_medusa_rules_with_their_own_scope_and_guard(self):
        ctx = Ctx(LOAD)
        ctx.body = {'maps': [204], 'curves': {'hp': [[1, 1.5], [200, 1.5]]}, 'version': 'new'}
        seen = {}
        orig = C.W._write
        C.W._write = lambda c, scopes, act, stmts, fields, **kw: seen.update(scopes=scopes, stmts=stmts, fields=fields, **kw) or {}
        try:
            C.apply(ctx)
        finally:
            C.W._write = orig
        upd = [s for s in seen['stmts'] if s.startswith('UPDATE medusa_monster_rules')]
        self.assertEqual(upd, ["UPDATE medusa_monster_rules SET maximum_health = 4500000, updated_at = now() WHERE difficulty = 1 "
                               "AND template_alias = 'boss-stheno'"])
        self.assertIn('medusa_monster_rules', [s['table'] for s in seen['scopes']])
        self.assertIn('medusa', seen['guard'])
        self.assertIn('medusa_monster_rules.maximum_health', seen['fields'])
        self.assertIn('medusa_monster_rules', seen['stmts'][0])                    # the version guard covers the rules too


if __name__ == '__main__':
    unittest.main()
