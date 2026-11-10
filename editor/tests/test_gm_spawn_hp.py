"""Opt-in copied-DB E3 proof. Never accepts remote DBs or low ports.

GODSWAR_GM_HP_TEST_PSQL='psql -h 127.0.0.1 -p 15439 -U patchtest'
GODSWAR_GM_HP_TEST_DB=editor_gmhp python -m pytest editor/tests/test_gm_spawn_hp.py
"""
import json
import os
from pathlib import Path
import shlex
import struct
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from modules import common, world as W
from reborn import Reborn

COMMAND = os.environ.get('GODSWAR_GM_HP_TEST_PSQL')
DATABASE = os.environ.get('GODSWAR_GM_HP_TEST_DB')


@unittest.skipUnless(COMMAND and DATABASE, 'requires explicit local editor_gmhp database copy')
class GmSpawnHpTests(unittest.TestCase):
    def setUp(self):
        tokens = shlex.split(COMMAND)
        if (Path(tokens[0]).name != 'psql' or '-h' not in tokens or '-p' not in tokens
                or tokens[tokens.index('-h') + 1] not in ('127.0.0.1', '::1')
                or int(tokens[tokens.index('-p') + 1]) < 10000 or not DATABASE.startswith('editor_gmhp')):
            raise ValueError('Use a direct loopback psql on port >=10000 and an editor_gmhp* copy')
        self.temp = tempfile.TemporaryDirectory()
        rb = self.rb = Reborn(COMMAND, DATABASE, writable=True, live=False, backup_dir=self.temp.name)
        changes = []
        self.app = SimpleNamespace(reborn=rb, changes=changes, record_reborn_change=changes.append,
                                   a=SimpleNamespace(rates_mgmt=None), rates=None)
        self.key = 'A_normal_deer_001'
        self.mid = 1
        # Use real published content, with one owned edit to prove unrelated fields survive.
        self.oid = rb.pg.json("(SELECT to_jsonb(min(d.object_id)) FROM monster_spawn_definitions d JOIN monster_content_publication p "
                              "ON p.family='monsters' AND p.revision=d.revision WHERE d.map_id=1 AND d.template_key='A_normal_deer_001' "
                              "AND NOT EXISTS (SELECT 1 FROM gm_monster_spawn_edits e WHERE e.map_id=1 AND e.object_id=d.object_id))")
        assert self.oid is not None, 'fixture needs one unedited Athens deer'
        self.gid = rb.pg.json('(SELECT to_jsonb(coalesce(max(id),0)+1) FROM gm_monster_spawns)')
        self.goid = rb.pg.json("(SELECT to_jsonb(min(i)) FROM generate_series(46000,49999) i WHERE NOT EXISTS "
                               "(SELECT 1 FROM gm_monster_spawns WHERE map_id=1 AND object_id=i) AND NOT EXISTS "
                               "(SELECT 1 FROM gm_npc_spawns WHERE map_id=1 AND object_id=i))")
        assert self.goid is not None
        oid_le = struct.pack('<I', self.goid).hex()
        rb.pg.run("INSERT INTO gm_monster_spawn_edits (map_id, object_id, enabled, pos_x, pos_z, facing, physical_attack, note, updated_by) "
                  "VALUES (1,%d,false,12,13,1.25,123,'codex-hp-fixture','fixture')" % self.oid)
        rb.pg.run("INSERT INTO gm_monster_spawns (id,name,map_id,scene_key,template_key,display_name,object_id,pos_x,pos_z,clear_bytes,enabled,note,updated_by) "
                  "OVERRIDING SYSTEM VALUE SELECT %d,'codex-hp-fixture',d.map_id,m.scene_key,d.template_key,'HP fixture',%d,d.pos_x,d.pos_z," 
                  "overlay(d.clear_bytes placing decode('%s','hex') from 9 for 4),false,'keep this note','fixture' "
                  "FROM monster_spawn_definitions d JOIN monster_content_publication p ON p.family='monsters' AND p.revision=d.revision "
                  "JOIN map_templates m ON m.map_id=d.map_id WHERE d.map_id=1 AND d.object_id=%d"
                  % (self.gid, self.goid, oid_le, self.oid))
        self.before = self.snapshot()

    def tearDown(self):
        for change in list(self.app.changes)[::-1]:
            if not change.get('undo_of') and not change.get('undone'):
                self.rb.undo_rows(change, False, self.app.record_reborn_change, self.app.changes)
        self.rb.pg.run('DELETE FROM gm_monster_spawn_edits WHERE map_id=1 AND object_id=%d; '
                       'DELETE FROM gm_monster_spawns WHERE id=%d;' % (self.oid, self.gid))
        self.temp.cleanup()

    def api(self, body, route='gm/spawn-hp'):
        return common.dispatch(self.app, 'POST', 'world/' + route, {}, body, {})

    def payload(self, hp=12345):
        ctx = common.Ctx(self.app, 'GET', '', {}, None)
        return dict(map_id=self.mid, template_key=self.key, maximum_health=hp,
                    version=W._version(ctx, W._gm_monster_parts(self.mid)),
                    publication_version=W._version(ctx, W._gm_publication_parts()))

    def snapshot(self):
        return self.rb.pg.json("json_build_object('gm',(SELECT json_agg(to_jsonb(g)-'updated_at'-'updated_by' ORDER BY id) "
                               "FROM gm_monster_spawns g WHERE map_id=1 AND template_key='A_normal_deer_001'),"
                               "'edits',(SELECT json_agg(to_jsonb(e)-'updated_at'-'updated_by' ORDER BY object_id) "
                               "FROM gm_monster_spawn_edits e WHERE map_id=1),"
                               "'attrs',(SELECT json_agg(to_jsonb(a) ORDER BY template_key) FROM gm_monster_attributes a WHERE map_id=1))")

    def test_write_preserves_other_fields_patches_only_hp_and_undo_restores(self):
        code, result = self.api(self.payload())
        self.assertEqual(code, 200, result)
        self.assertEqual(result['applies'], 'after_restart')
        after = self.snapshot()
        self.assertEqual(after['attrs'], self.before['attrs'])
        before_g = next(g for g in self.before['gm'] if g['id'] == self.gid)
        after_g = next(g for g in after['gm'] if g['id'] == self.gid)
        before_packet = bytes.fromhex(before_g.pop('clear_bytes')[2:])
        after_packet = bytes.fromhex(after_g.pop('clear_bytes')[2:])
        self.assertEqual(before_g, after_g)
        self.assertEqual(before_packet[:20]+before_packet[28:], after_packet[:20]+after_packet[28:])
        self.assertEqual(struct.unpack_from('<II', after_packet, 20), (12345,12345))
        existing = next(e for e in after['edits'] if e['object_id'] == self.oid)
        self.assertEqual((existing['enabled'],existing['pos_x'],existing['pos_z'],existing['physical_attack'],existing['note']),
                         (False,12,13,123,'codex-hp-fixture'))
        self.assertEqual((existing['current_health'],existing['maximum_health']), (12345,12345))
        self.assertGreater(len(after['edits']), len(self.before['edits']))
        change = self.app.changes[-1]
        self.assertTrue(Path(change['backup']).is_file())
        undo = self.rb.undo_rows(change, False, self.app.record_reborn_change, self.app.changes)
        self.assertTrue(undo['result']['all_identical'])
        # Restore the locally popped packet fields before comparing the original snapshot.
        before_g['clear_bytes'] = '\\x'+before_packet.hex()
        self.assertEqual(self.snapshot(), self.before)

    def test_stale_versions_and_readonly_never_change_hp(self):
        body = self.payload()
        stale_pub = dict(body, publication_version='0'*32)
        self.assertEqual(self.api(stale_pub)[0], 409)
        self.assertEqual(self.snapshot(), self.before)
        self.assertEqual(self.app.changes, [])
        self.assertEqual(self.api(body)[0], 200)
        self.assertEqual(self.api(dict(body,maximum_health=23456))[0], 409)
        after = self.snapshot()
        self.rb.writable = False
        self.assertEqual(self.api(self.payload(23456))[0], 403)
        self.assertEqual(self.snapshot(), after)
        self.rb.writable = True
        self.rb.undo_rows(self.app.changes[-1], False, self.app.record_reborn_change, self.app.changes)

    def test_bad_hp_missing_target_and_ignored_template_hp_are_rejected(self):
        for hp in [None, 0, -1, 1.5, True, 2147483648]:
            with self.subTest(hp=hp):
                self.assertEqual(self.api(self.payload(hp))[0], 400)
        self.assertEqual(self.api(dict(self.payload(),template_key='CodexMissing'))[0], 400)
        body = dict(self.payload(),values={'maximum_health':12345})
        self.assertEqual(self.api(body,'gm/attributes')[0], 400)
        self.assertEqual(self.snapshot(), self.before)
        self.assertEqual(self.app.changes, [])


if __name__ == '__main__':
    unittest.main()
