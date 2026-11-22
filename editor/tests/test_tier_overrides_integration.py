"""Opt-in E1 proof against a local disposable database copy; no live/remote targets.

GODSWAR_TIER_TEST_PSQL='psql -h 127.0.0.1 -p 15439 -U patchtest'
GODSWAR_TIER_TEST_DB=editor_tiers python -m pytest editor/tests/test_tier_overrides_integration.py

Only the two new fixture tables are created/cleared/dropped. Existing game data is not changed.
"""
import concurrent.futures
import json
import os
from pathlib import Path
import shlex
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import common, drop_tiers, respawn_tiers
from reborn import Reborn

COMMAND = os.environ.get('GODSWAR_TIER_TEST_PSQL')
DATABASE = os.environ.get('GODSWAR_TIER_TEST_DB')
DDL = '''
CREATE TABLE IF NOT EXISTS public.drop_tier_overrides (
 map_id smallint NOT NULL CHECK (map_id BETWEEN -1 AND 255),
 tier text NOT NULL CHECK (tier IN ('normal', 'elite', 'boss')),
 any_drop_basis_points integer NOT NULL CHECK (any_drop_basis_points BETWEEN 0 AND 10000),
 note text NOT NULL DEFAULT '', PRIMARY KEY (map_id, tier));
CREATE TABLE IF NOT EXISTS public.respawn_tier_overrides (
 map_id smallint NOT NULL CHECK (map_id BETWEEN -1 AND 255),
 tier text NOT NULL CHECK (tier IN ('normal', 'elite')),
 minimum_seconds integer NOT NULL CHECK (minimum_seconds BETWEEN 15 AND 86400),
 maximum_seconds integer NOT NULL CHECK (maximum_seconds BETWEEN 15 AND 86400),
 note text NOT NULL DEFAULT '', PRIMARY KEY (map_id, tier), CHECK (minimum_seconds <= maximum_seconds));
'''


@unittest.skipUnless(COMMAND and DATABASE, 'requires explicit local copied-DB tier-test environment')
class TierDatabaseIntegration(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        tokens = shlex.split(COMMAND)
        if Path(tokens[0]).name != 'psql' or '-h' not in tokens or '-p' not in tokens:
            raise ValueError('Use a direct local psql command with explicit -h and -p.')
        if tokens[tokens.index('-h') + 1] not in ('127.0.0.1', '::1') or int(tokens[tokens.index('-p') + 1]) < 10000:
            raise ValueError('Only loopback PostgreSQL ports >=10000 are allowed.')
        if not DATABASE.startswith('editor_tiers'):
            raise ValueError('Use a disposable editor_tiers* database copy.')
        cls.reloads = []
        class ReloadHandler(BaseHTTPRequestHandler):
            def do_POST(self):
                cls.reloads.append(self.path)
                self.send_response(200 if self.path == '/npc-content/reload' else 404)
                self.send_header('Content-Type', 'application/json'); self.end_headers()
                self.wfile.write(b'{"ok":true}')
            def log_message(self, *args):
                pass
        cls.http = ThreadingHTTPServer(('127.0.0.1', 0), ReloadHandler)
        assert cls.http.server_port >= 10000
        cls.thread = threading.Thread(target=cls.http.serve_forever, daemon=True); cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.http.shutdown(); cls.http.server_close(); cls.thread.join()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.app = self.application('main')
        self.rb = self.app.reborn
        self.rb.pg.run(DDL + 'TRUNCATE public.drop_tier_overrides, public.respawn_tier_overrides;')
        self.reloads.clear()

    def tearDown(self):
        self.rb.pg.run('TRUNCATE public.drop_tier_overrides, public.respawn_tier_overrides;')
        self.temp.cleanup()

    def application(self, name):
        rb = Reborn(COMMAND, DATABASE, writable=True, live=False, backup_dir=str(Path(self.temp.name) / name))
        changes = []
        return SimpleNamespace(reborn=rb, changes=changes, record_reborn_change=changes.append,
                               a=SimpleNamespace(rates_mgmt=self.http.server_port), rates=None)

    def api(self, module='drop_tiers', route='overview', body=None, app=None):
        return common.dispatch(app or self.app, 'GET' if body is None else 'POST', module + '/' + route, {}, body, {})

    def save(self, module, fields, version=None, app=None):
        target = app or self.app
        if version is None:
            code, data = self.api(module, app=target); self.assertEqual(code, 200)
            version = data['version']
        return self.api(module, 'save', dict(fields, version=version), app=target)

    def test_missing_tables_disable_both_routes(self):
        self.rb.pg.run('DROP TABLE public.drop_tier_overrides, public.respawn_tier_overrides;')
        try:
            for module in ['drop_tiers', 'respawn_tiers']:
                code, view = self.api(module); self.assertEqual(code, 200)
                self.assertFalse(view['table']); self.assertIsNone(view['version'])
                code, _ = self.api(module, 'save', {'map_id': 21, 'tier': 'normal', 'version': 'new'})
                self.assertEqual(code, 400)
            self.assertEqual(self.app.changes, [])
        finally:
            self.rb.pg.run(DDL)

    def test_examples_update_delete_undo_and_reload(self):
        code, result = self.save('drop_tiers', {'map_id': 21, 'tier': 'normal', 'any_drop_basis_points': 1500, 'note': "the admin's example"})
        self.assertEqual(code, 200, result); self.assertTrue(result['reload']['ok'])
        self.assertIn('/npc-content/reload', self.reloads)
        first = self.app.changes[-1]; self.assertTrue(Path(first['backup']).is_file())
        code, data = self.api(); self.assertEqual(data['rows'][0]['map_name'], common.CATALOG.file('maps')['21']['name'])
        self.assertEqual(data['rows'][0]['any_drop_basis_points'], 1500)
        code, result = self.save('drop_tiers', {'map_id': 21, 'tier': 'normal', 'any_drop_basis_points': 2000, 'note': 'edit'})
        self.assertEqual(code, 200, result); second = self.app.changes[-1]
        _, view = self.api()
        code, result = self.api('drop_tiers', 'delete', {'map_id': 21, 'tier': 'normal', 'version': view['version']})
        self.assertEqual(code, 200, result); third = self.app.changes[-1]
        self.assertEqual(self.api()[1]['rows'], [])
        for change, expected in [(third, 2000), (second, 1500), (first, None)]:
            undo = self.rb.undo_rows(change, False, self.app.record_reborn_change, self.app.changes)
            self.assertTrue(undo['result']['all_identical'])
            rows = self.api()[1]['rows']
            self.assertEqual(rows[0]['any_drop_basis_points'] if rows else None, expected)
        code, result = self.save('respawn_tiers', {'map_id': 21, 'tier': 'elite', 'minimum_seconds': 1800, 'maximum_seconds': 3600, 'note': 'E1'})
        self.assertEqual(code, 200, result)
        self.assertEqual(self.api('respawn_tiers')[1]['rows'][0]['maximum_seconds'], 3600)

    def test_stale_version_and_read_only_never_overwrite(self):
        _, view = self.api(); stale = view['version']
        fields = {'map_id': -1, 'tier': 'boss', 'any_drop_basis_points': 10000, 'note': ''}
        self.assertEqual(self.save('drop_tiers', fields, stale)[0], 200)
        count = len(list(Path(self.temp.name).rglob('*.json')))
        code, _ = self.save('drop_tiers', dict(fields, any_drop_basis_points=0), stale)
        self.assertEqual(code, 409)
        self.assertEqual(len(list(Path(self.temp.name).rglob('*.json'))), count)
        self.assertEqual(self.api()[1]['rows'][0]['any_drop_basis_points'], 10000)
        self.rb.writable = False
        self.assertEqual(self.save('drop_tiers', fields)[0], 403)
        self.assertEqual(self.api('drop_tiers', 'reload', {})[0], 403)

    def test_concurrent_missing_key_inserts_have_one_version_winner(self):
        _, view = self.api(); version = view['version']
        other = self.application('other')
        fields = [{'map_id': 21, 'tier': 'normal', 'any_drop_basis_points': 1500},
                  {'map_id': 22, 'tier': 'elite', 'any_drop_basis_points': 2000}]
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(self.save, 'drop_tiers', row, version, app) for row, app in zip(fields, [self.app, other])]
            results = [future.result() for future in futures]
        self.assertEqual(sorted(code for code, _ in results), [200, 409], results)
        self.assertEqual(len(self.api()[1]['rows']), 1)
        self.assertEqual(len(self.app.changes) + len(other.changes), 1)
        self.assertEqual(len(list(Path(self.temp.name).rglob('*.json'))), 1)
