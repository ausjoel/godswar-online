import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shlex
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import common,gear_presets as G,inventory as I
from reborn import Reborn


class GearPresetPolicy(unittest.TestCase):
    def test_generated_branches_match_authoritative_source_and_all_ids(self):
        path=Path(__file__).resolve().parents[1]/'build_class_suit_branches.py'
        spec=importlib.util.spec_from_file_location('branch_generator',path)
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        data=json.loads((G.Path(G.__file__).resolve().parents[1]/'data/class-suit-branches.json').read_text())
        self.assertEqual(module.generate(),data)
        self.assertEqual(len(data['branches']),62)
        self.assertEqual(len({i for b in data['branches'] for i in b['tiers']}),248)

    def test_class_conversion_is_exact_and_never_downgrades(self):
        self.assertEqual(G.suit_target(1713,2,3),(1734,0))
        self.assertEqual(G.suit_target(1735,2,1),(1735,4))
        self.assertEqual(G.suit_target(3210,3,4),(3267,0))
        with self.assertRaises(common.Bad):G.suit_target(1001,2,3)
        with self.assertRaises(common.Bad):G.inputs({'class_tier':2,'class_stat':True})

    def test_all_stones_have_correct_slot_affinity_class_values_and_no_duplicates(self):
        for profession in range(4):
            for slot in range(12):
                with self.subTest(profession=profession,slot=slot):
                    stones=G.stone_rows(slot,profession>=2,4,{})
                    self.assertEqual(len({s['effect'] for s in stones}),4)
                    self.assertTrue(all(s['level']==10 for s in stones))
                    if slot in G.HEATED:
                        self.assertEqual([s['effect'] for s in stones[:2]],[4,2] if profession>=2 else [3,1])
                        self.assertEqual([s['value'] for s in stones[:2]],[500,800])
                        self.assertTrue(all(I.SPIRITS[s['effect']][3]=='Heated' for s in stones))
                    else:
                        self.assertEqual([s['effect'] for s in stones[:2]],[9,10])
                        self.assertTrue(all(I.SPIRITS[s['effect']][3]=='Cooled' for s in stones))
        cooled=G.stone_rows(3,True,2,{'cooled_physical_reduction_grade_one_maximum':50,'cooled_magic_reduction_grade_one_maximum':40})
        self.assertEqual([s['value'] for s in cooled],[400,400])


COMMAND=os.environ.get('GODSWAR_GEAR_TEST_PSQL')
DATABASE=os.environ.get('GODSWAR_GEAR_TEST_DB')


@unittest.skipUnless(COMMAND and DATABASE,'requires explicit copied editor_gear* database')
class GearPresetDatabase(unittest.TestCase):
    def setUp(self):
        tokens=shlex.split(COMMAND)
        if (Path(tokens[0]).name!='psql' or '-h' not in tokens or '-p' not in tokens
            or tokens[tokens.index('-h')+1] not in ('127.0.0.1','::1') or int(tokens[tokens.index('-p')+1])<10000
            or not DATABASE.startswith('editor_gear')):
            raise ValueError('use a direct loopback psql port >=10000 and copied editor_gear* DB')
        self.temp=tempfile.TemporaryDirectory()
        self.rb=Reborn(COMMAND,DATABASE,writable=True,live=False,backup_dir=self.temp.name)
        self.cid=4
        self.assertEqual(self.rb.dump(self.cid)['character_base'][0]['profession'],2,'fixture expects copied Priest #4')
        changes=[];self.fixtures=[]
        self.app=SimpleNamespace(reborn=self.rb,changes=changes,record_reborn_change=changes.append,a=SimpleNamespace(rates_mgmt=None),rates=None)
        common.CATALOG.rb=self.rb
        builds=[I._build_set(self.rb,self.cid,0,slot,{'prop_id':item,'stack':1,'bound':1,'item_quality':8,'item_grade':10},False)
                for slot,item in [(3,2213),(6,3613),(10,1713),(11,2013)]]
        self.rb.write(self.cid,'copied-DB gear fixture',lambda d:[s for builder in builds for s in builder(d)],self.fixtures.append)
        self.before=self.rb.dump(self.cid)

    def tearDown(self):
        self.rb.writable=True
        for c in list(self.app.changes)[::-1]:
            if not c.get('undo_of') and not c.get('undone'):
                self.rb.undo(c,False,self.app.record_reborn_change,self.app.changes)
        for c in self.fixtures[::-1]:
            self.rb.undo(c,False,lambda _:None,self.fixtures)
        self.temp.cleanup()

    def api(self,route,body):
        return common.dispatch(self.app,'POST','gear_presets/'+route+'/'+str(self.cid),{},body,{})

    def preview(self,**options):
        code,data=self.api('preview',options)
        self.assertEqual(code,200,data)
        return data

    def test_standard_batch_is_atomic_single_revision_preserves_excluded_and_undo(self):
        view=self.preview(preset='standard')
        self.assertEqual(len(view['items']),4)
        code,data=self.api('apply',dict(view['options'],token=view['token']))
        self.assertEqual(code,200,data)
        after=self.rb.dump(self.cid)
        self.assertEqual(after['character_base'][0]['inventory_revision'],self.before['character_base'][0]['inventory_revision']+1)
        for row in G.INV._items(after):
            if row['item_location']==0 and row['slot_index']<12:
                self.assertEqual(row['holy_suit_code'],410)
                self.assertEqual(row['holy_socket_count'],2)
                self.assertEqual(row['holy_socket1_level'],10)
                self.assertIsNone(row['attribute5'])
                self.assertEqual((row['item_quality'],row['item_grade']),(8,10))
        excluded=lambda d:[r for r in d['character_items'] if r['item_location']!=0 or r['slot_index']>=12]
        self.assertEqual(excluded(after),excluded(self.before))
        change=self.app.changes[-1]
        self.assertTrue(Path(change['backup']).is_file())
        undo=self.rb.undo(change,False,self.app.record_reborn_change,self.app.changes)
        self.assertTrue(undo['result']['all_identical'])

    def test_boundless_and_class_conversion_class_stat_and_grade_values(self):
        view=self.preview(preset='boundless',class_tier=3,class_stat=True)
        for row in view['items']:
            self.assertEqual((row['after']['item_quality'],row['after']['item_grade']),(20,25))
            self.assertIn(row['after']['prop_id'],I.class_suit_items())
            self.assertEqual(row['after']['class_attribute1'],220)
            self.assertEqual(len(row['stats']),5)
            self.assertEqual(len(row['stones']),4)
            for stat in row['stats']:
                if stat['level'] is None:
                    values=self.rb.cat()['attributes'][str(stat['id'])]['values']
                    self.assertEqual(stat['value'],values[24])
        code,data=self.api('apply',dict(view['options'],token=view['token']))
        self.assertEqual(code,200,data)
        # Match actual server projection, not only catalogue preview arithmetic.
        for row in view['items']:
            for stat in row['stats']:
                actual=self.rb.pg.json('(SELECT to_jsonb(attribute_value) FROM character_equipment_attributes '
                                       'WHERE user_id=%d AND prop_id=%d AND attribute_id=%d LIMIT 1)' %
                                       (self.cid,row['after']['prop_id'],stat['id']))
                self.assertAlmostEqual(float(actual),float(stat['value']),places=5)
        # Repeating identical intent produces no write / no second audit record.
        fresh=self.preview(preset='boundless',class_tier=3,class_stat=True)
        count=len(self.app.changes)
        self.assertEqual(self.api('apply',dict(fresh['options'],token=fresh['token']))[0],400)
        self.assertEqual(len(self.app.changes),count)

    def test_preview_token_and_readonly_prevent_unreviewed_changes(self):
        view=self.preview(preset='standard')
        self.assertEqual(self.api('apply',dict(view['options'],token='0'*64))[0],409)
        self.assertEqual(self.api('apply',dict(view['options'],preset='boundless',token=view['token']))[0],409)
        self.rb.writable=False
        self.assertEqual(self.api('apply',dict(view['options'],token=view['token']))[0],403)
        self.assertEqual(self.app.changes,[])
        self.assertEqual(self.rb.dump(self.cid)['character_items'],self.before['character_items'])


if __name__=='__main__':unittest.main()
