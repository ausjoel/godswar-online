"""Wave 4 D item 7: title giver - every title has an award source and category; view and multi-select write path."""
import json
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import title_giver as G, _title_sources as TS, title_grants as T, common

ROOT = Path(__file__).resolve().parents[1]
CLIENT = json.loads((ROOT / 'catalog-origin' / 'titles.json').read_text())


class Ctx:
    def __init__(self, body=None, writable=True):
        self.body = body
        self.rb = SimpleNamespace(writable=writable, pg=SimpleNamespace(json=self.q))
        self.catalog = SimpleNamespace(file=lambda n: CLIENT if n == 'titles' else {})
        self.sql = []

    def need_rb(self):
        return self.rb

    def q(self, sql):
        self.sql.append(sql)
        if 'character_title_entitlements' in sql:
            return [3004, 5009, 1300]
        if 'to_regclass' in sql:
            return True
        if 'medusa_completion_reward_rules' in sql:
            return [{'id': 5009, 'difficulty': 1, 'kind': 2, 'threshold': 600}, {'id': 5010, 'difficulty': 2, 'kind': 2, 'threshold': 900}]
        raise AssertionError(sql)


CATALOGUE = [{'title_id': int(k), 'display_name': v['name'], 'description': v.get('desc', '')} for k, v in CLIENT.items()]


class TitleGiver(unittest.TestCase):
    def test_every_client_title_has_a_reviewed_source_and_category(self):
        cats = {c for c, _, _ in TS.CATEGORIES}
        for k in CLIENT:
            self.assertIn(int(k), TS.S, k)
        for tid, s in TS.S.items():
            self.assertIn(s['category'], cats, tid)
            self.assertTrue(s['how'] and s['source'], tid)
        self.assertEqual((TS.S[3004]['category'], TS.S[3004]['sub']), ('stats', 'MATK'))       # the admin's example: Stats -> MATK
        self.assertIn('2,800', TS.S[3004]['how'])
        self.assertEqual(TS.S[5155]['sub'], 'Wonderland')
        self.assertEqual(TS.S[1118 and 2002]['quests'], [1118])
        self.assertIn('Title Expert', TS.S[1303]['npc'])
        self.assertTrue(TS.S[5011]['bonus'])                 # only Medusa titles carry a stat bonus
        self.assertIsNone(TS.S[3004]['bonus'])
        self.assertFalse(TS.S[5001]['live'])

    def test_view_marks_owned_earned_admin_and_grantable(self):
        ctx = Ctx()
        with patch.object(T, 'support', return_value={'available': True, 'owned_limit': 128}), \
             patch.object(T, 'catalogue', return_value=CATALOGUE), patch.object(T, 'rows', return_value=[{'title_id': 1300}]):
            v = G.view(ctx, '7')
        by = {t['id']: t for t in v['titles']}
        self.assertTrue(by[3004]['earned'] and by[3004]['owned'] and not by[3004]['admin'])
        self.assertTrue(by[1300]['admin'] and not by[1300]['earned'])
        self.assertFalse(by[5152]['grantable'])               # Medusa ids 5152-5157 are not in editor_title_catalog
        self.assertIn('Normal difficulty, cleared within 10 min', by[5009]['rules'])
        self.assertEqual(v['expected'], [1300])
        self.assertTrue(v['writable'])
        stats = next(c for c in v['categories'] if c['id'] == 'stats')
        self.assertEqual(stats['subs'], ['HP', 'ATK', 'MATK', 'DEF', 'MDEF', 'Hit', 'Dodge', 'Absorb'])

    def test_without_the_grant_update_it_is_read_only(self):
        with patch.object(T, 'support', return_value={'available': False, 'reason': 'needs the update'}):
            v = G.view(Ctx(), '7')
        self.assertFalse(v['writable'])
        self.assertTrue(all(not t['grantable'] for t in v['titles']))

    def test_apply_uses_the_shared_grant_writer(self):
        ctx = Ctx(body={'add': [3004, 3005], 'remove': [], 'expected': [1300]})
        with patch.object(T, 'write_batch', return_value={'ok': True}) as w:
            G.apply(ctx, '7')
        args = w.call_args[0]
        self.assertEqual(args[1], '7')
        self.assertEqual(args[2]['add'], [3004, 3005])
        self.assertEqual(args[2]['expected'], [1300])
        with self.assertRaises(common.Bad):
            G.apply(Ctx(body=['x']), '7')


if __name__ == '__main__':
    unittest.main()
