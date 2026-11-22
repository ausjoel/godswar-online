"""E1 tier pages: validation, lifecycle gates, scopes and honest reload reporting."""
import copy
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import _tier_overrides as T, common


class Context:
    def __init__(self, body=None, have=True, writable=True):
        self.body = body
        self.have = have
        self.rows = []
        self.calls = []
        self.version = 'a' * 32
        self.facts = {'templates': [], 'spawns': []}
        self.rb = SimpleNamespace(writable=writable, live=False, db='editor_tiers_unit', pg=SimpleNamespace(json=self.query))
        self.app = SimpleNamespace(reborn=self.rb, a=SimpleNamespace(rates_mgmt=19092))
        self.catalog = SimpleNamespace(file=lambda name: {'21': {'name': 'Elasson'}})

    def need_rb(self):
        return self.rb

    def query(self, sql):
        if "'templates'" in sql and "'spawns'" in sql:
            self.fact_sql = sql
            return copy.deepcopy(self.facts)
        if 'to_regclass' in sql:
            return self.have
        if 'SELECT EXISTS' in sql:
            return bool(self.rows)
        if 'json_agg' in sql:
            return copy.deepcopy(self.rows)
        if 'md5(' in sql:
            return self.version
        raise AssertionError(sql)

    def rows_write(self, scopes, action, build, **options):
        self.calls.append((scopes, action, build({}), options))
        return {'change': {'id': 'unit-change'}, 'applies': options['applies']}


def body(kind='drop', **changes):
    row = {'map_id': 21, 'tier': 'normal', 'note': '', 'version': 'a' * 32}
    row.update({'any_drop_basis_points': 1500} if kind == 'drop' else {'minimum_seconds': 1800, 'maximum_seconds': 3600})
    row.update(changes)
    return row


class TierOverrides(unittest.TestCase):
    def test_missing_table_is_visible_and_never_written(self):
        for kind in T.POLICIES:
            ctx = Context(body(kind), have=False)
            view = T.overview(ctx, kind)
            self.assertFalse(view['table'])
            self.assertIsNone(view['version'])
            self.assertIn('Table missing until the server release', view['why_missing'])
            with self.assertRaises(common.Bad), patch.object(common, '_http') as http:
                T.mutate(ctx, kind)
            self.assertEqual(ctx.calls, [])
            http.assert_not_called()

    def test_names_include_all_maps_and_unknown_map_fallback(self):
        ctx = Context()
        ctx.rows = [{'map_id': -1, 'tier': 'boss'}, {'map_id': 21, 'tier': 'normal'}, {'map_id': 255, 'tier': 'elite'}]
        view = T.overview(ctx, 'drop')
        self.assertEqual([r['map_name'] for r in view['rows']], ['All maps', 'Elasson', 'Map #255'])
        # Only real maps: the catalogue map plus any map with a saved row (no 0-255 filler list any more).
        self.assertEqual([m['id'] for m in view['maps']], [21, 255])
        self.assertNotIn('boss', T.overview(ctx, 'respawn')['tiers'])

    def test_every_real_map_is_listed_with_section_counts_and_effective_source(self):
        # Wave 4 D item 6: maps from the catalogue, map_templates and spawns all appear, grouped by section.
        ctx = Context()
        ctx.catalog = SimpleNamespace(file=lambda name: {'21': {'name': 'Elasson'}, '200': {'name': "Medusa's Island"}})
        ctx.facts = {'templates': [{'id': 21, 'scene': 'Elasson_All'}, {'id': 25, 'scene': 'Agate'}, {'id': 46, 'scene': 'Colosseum'}],
                     'spawns': [{'map_id': 21, 'normal': 40, 'elite': 3, 'boss': 1}, {'map_id': 29, 'normal': 150, 'elite': 0, 'boss': 0}]}
        ctx.rows = [{'map_id': -1, 'tier': 'boss', 'any_drop_basis_points': 5000},
                    {'map_id': 21, 'tier': 'normal', 'any_drop_basis_points': 1500}]
        view = T.overview(ctx, 'drop')
        self.assertEqual([m['id'] for m in view['maps']], [21, 25, 29, 46, 200])
        by = {m['id']: m for m in view['maps']}
        self.assertEqual(by[21]['monsters'], {'normal': 40, 'elite': 3, 'boss': 1})
        self.assertEqual(by[21]['effective']['normal']['source'], 'map')
        self.assertEqual(by[21]['effective']['boss']['source'], 'all_maps')
        self.assertEqual(by[21]['effective']['elite']['source'], 'default')
        self.assertEqual(by[29]['name'], 'Cursed Zone 3')          # not in the catalogue, only in spawns
        self.assertEqual([g['section'] for g in view['groups']], ['regular', 'instance', 'event', 'arena'])
        self.assertEqual(by[200]['section'], 'instance')
        self.assertEqual(by[25]['section'], 'event')
        self.assertIn("lower(s.k) LIKE '%\\_boss%'", ctx.fact_sql)
        self.assertIn('gm_monster_spawns', ctx.fact_sql)
        # respawn: bosses are never offered (the server ignores them there)
        self.assertNotIn('boss', T.overview(ctx, 'respawn')['maps'][0]['effective'])

    def test_drop_save_uses_shared_audited_write_and_exact_scope(self):
        ctx = Context(body(note="Editor's 15% chance"))
        with patch.object(common, '_http', return_value=(200, {'ok': True})) as http:
            result = T.mutate(ctx, 'drop')
        scopes, action, sql, options = ctx.calls[0]
        self.assertEqual(scopes[0]['table'], 'drop_tier_overrides')
        self.assertEqual(scopes[0]['pk'], ['map_id', 'tier'])
        self.assertIn('map_id = 21', scopes[0]['where'])
        self.assertIn("tier = 'normal'", scopes[0]['where'])
        self.assertIn('LOCK TABLE public.drop_tier_overrides', sql[0])
        self.assertIn('gwadmin-conflict', sql[1])
        self.assertIn("Editor''s 15% chance", sql[2])
        self.assertEqual(options['fields'], ['drop_tier_overrides.*'])
        self.assertEqual(options['applies'], 'game_reload')
        self.assertTrue(result['reload']['ok'])
        http.assert_called_once_with('POST', 19092, '/npc-content/reload', timeout=4)

    def test_respawn_example_and_boundary_values(self):
        for mid, lo, hi in [(-1, 15, 86400), (21, 1800, 3600), (255, 86400, 86400)]:
            ctx = Context(body('respawn', map_id=mid, minimum_seconds=lo, maximum_seconds=hi))
            with patch.object(common, '_http', return_value=(200, {})):
                T.mutate(ctx, 'respawn')
            self.assertEqual(ctx.calls[0][0][0]['table'], 'respawn_tier_overrides')

    def test_invalid_values_cannot_reach_the_write_engine(self):
        cases = [('drop', {'any_drop_basis_points': n}) for n in [-1, 10001, 1.5, True, None, float('nan'), float('inf')]]
        cases += [('respawn', {'minimum_seconds': 14}), ('respawn', {'maximum_seconds': 86401}),
                  ('respawn', {'minimum_seconds': 3601}), ('respawn', {'tier': 'boss'}),
                  ('drop', {'map_id': -2}), ('drop', {'map_id': 256}), ('drop', {'map_id': True}),
                  ('drop', {'tier': "normal'; DELETE FROM accounts; --"}),
                  ('drop', {'note': {'unexpected': 'object'}}), ('drop', {'note': 'x' * 201}),
                  ('drop', {'version': None})]
        for kind, changes in cases:
            ctx = Context(body(kind, **changes))
            with self.subTest(kind=kind, changes=changes), self.assertRaises(common.Bad):
                T.mutate(ctx, kind)
            self.assertEqual(ctx.calls, [])
        with self.assertRaises(common.Bad):
            T.mutate(Context(['not', 'an', 'object']), 'drop')

    def test_read_only_cannot_write_or_reload(self):
        ctx = Context(body(), writable=False)
        with patch.object(common, '_http') as http, self.assertRaises(common.Refused):
            T.mutate(ctx, 'drop')
        self.assertEqual(ctx.calls, [])
        http.assert_not_called()
        self.assertFalse(T.overview(ctx, 'drop')['writable'])

    def test_delete_is_scoped_and_keeps_undo(self):
        ctx = Context(body())
        ctx.rows = [{'map_id': 21, 'tier': 'normal'}]
        with patch.object(common, '_http', return_value=(404, {})):
            result = T.mutate(ctx, 'drop', delete=True)
        self.assertIn("DELETE FROM drop_tier_overrides WHERE map_id = 21 AND tier = 'normal'", ctx.calls[0][2][-1])
        self.assertIn('change', result)
        self.assertFalse(result['reload']['ok'])
        self.assertTrue(result['ok'])
        ctx = Context(body())
        with self.assertRaises(common.NotFound):
            T.mutate(ctx, 'drop', delete=True)
        self.assertEqual(ctx.calls, [])

    def test_reload_failure_reports_saved_state_without_repeating_write(self):
        for response in [(404, {}), (503, {}), (200, {'ok': False})]:
            ctx = Context(body())
            with patch.object(common, '_http', return_value=response):
                result = T.mutate(ctx, 'drop')
            self.assertEqual(len(ctx.calls), 1)
            self.assertFalse(result['reload']['ok'])
            self.assertIn('awaiting', result['reload']['message'])
        ctx = Context(body())
        ctx.app.a.rates_mgmt = None
        with patch.object(common, '_http') as http:
            self.assertFalse(T.reload_game(ctx)['ok'])
        http.assert_not_called()

    def test_write_rules_cover_both_tables(self):
        for kind, p in T.POLICIES.items():
            self.assertEqual(common.GLOSSARY.rules(p['table'])['editor'], 'edit')
            common.check_fields([p['table'] + '.*'], False)


if __name__ == '__main__':
    unittest.main()
