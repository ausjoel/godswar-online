import json
from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
import shlex
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import common, inventory as I, mount_gear as G
from reborn import Reborn

EXPECTED=['99','79','1.8%','2.2%','1104','184','9','8','184','55','1.8%','1.8%','221','166','1.5%','230']


class MountGearOptions(unittest.TestCase):
    def test_pinned_epic_g10_values_match_all_sixteen_requested_options(self):
        source=common.CATALOG.file('appendattrs')
        rb=SimpleNamespace(cat=lambda:{'attributes':{k:{'values':a.get('levels'),'percent':bool(a.get('flag'))} for k,a in source.items()}})
        options=G.stat_options(rb)
        self.assertEqual([o['display'] for o in options],EXPECTED)
        self.assertEqual(len({o['key'] for o in options}),16)
        self.assertEqual(len({o['id'] for o in options}),16)


COMMAND=os.environ.get('GODSWAR_MOUNT_GEAR_PSQL')
DATABASE=os.environ.get('GODSWAR_MOUNT_GEAR_DB')


@unittest.skipUnless(COMMAND and DATABASE,'requires explicit editor_mountgear* copied database')
class MountGearDatabase(unittest.TestCase):
    def setUp(self):
        tokens=shlex.split(COMMAND)
        if (Path(tokens[0]).name!='psql' or '-h' not in tokens or '-p' not in tokens or tokens[tokens.index('-h')+1] not in ('127.0.0.1','::1')
            or int(tokens[tokens.index('-p')+1])<10000 or not DATABASE.startswith('editor_mountgear')):
            raise ValueError('use a direct loopback psql port >=10000 and an editor_mountgear* copy')
        self.temp=tempfile.TemporaryDirectory();self.cid=4;self.fixtures=[]
        self.rb=Reborn(COMMAND,DATABASE,writable=True,live=False,backup_dir=self.temp.name)
        changes=[];self.app=SimpleNamespace(reborn=self.rb,changes=changes,record_reborn_change=changes.append,a=SimpleNamespace(rates_mgmt=None),rates=None)
        common.CATALOG.rb=self.rb
        d=self.rb.dump(self.cid)
        self.slot=next(s for s in range(I.BAG_SLOTS) if I._find(d,1,s) is None)
        item={'prop_id':14508,'stack':1,'bound':1,'item_quality':8,'item_grade':10,'item_exp':123,
              'attribute1':347,'attribute2':377,'attribute3':307,'attribute_level1':None,'attribute_level2':2,'attribute_level3':None,
              'holy_socket_count':1,'holy_socket1_effect_id':21,'holy_socket1_level':1,'holy_socket1_value':20}
        values,_=I._values(self.rb,item,None,False)
        values['attribute_level1']=None;values['attribute_level3']=None
        self.rb.write(self.cid,'mount gear copied-DB fixture',lambda _:[common.insert_sql('character_items',dict(user_id=self.cid,item_location=1,slot_index=self.slot,**values)),
                      'UPDATE character_base SET inventory_revision=inventory_revision+1 WHERE id=%d'%self.cid],self.fixtures.append)
        self.before=self.rb.dump(self.cid);self.source=I._find(self.before,1,self.slot)

    def tearDown(self):
        self.rb.writable=True
        for c in list(self.app.changes)[::-1]:
            if not c.get('undo_of') and not c.get('undone'):self.rb.undo(c,False,self.app.record_reborn_change,self.app.changes)
        for c in self.fixtures[::-1]:self.rb.undo(c,False,lambda _:None,self.fixtures)
        self.temp.cleanup()

    def api(self,route,body):return common.dispatch(self.app,'POST','mount_gear/'+route+'/'+str(self.cid),{},body,{})
    def preview(self,**options):
        body=dict(location=1,slot=self.slot,target=14608,mode='edit',epic_g10=False);body.update(options)
        code,data=self.api('preview',body);self.assertEqual(code,200,data);return data

    def test_morph_keeps_identity_and_every_selected_stat_field(self):
        view=self.preview();code,data=self.api('apply',dict(view['options'],token=view['token']))
        self.assertEqual(code,200,data)
        after=self.rb.dump(self.cid);item=I._find(after,1,self.slot)
        self.assertEqual(item['id'],self.source['id']);self.assertEqual(item['prop_id'],14608)
        for key in I.ITEM_FIELDS:
            if key!='prop_id':self.assertEqual(item.get(key),self.source.get(key),key)
        self.assertEqual(after['character_base'][0]['inventory_revision'],self.before['character_base'][0]['inventory_revision']+1)
        self.assertTrue(Path(self.app.changes[-1]['backup']).is_file())
        fresh=self.preview();self.assertFalse(fresh['changed'])
        self.assertEqual(self.api('apply',dict(fresh['options'],token=fresh['token']))[0],400)

    def test_copy_preserves_source_and_stats_but_creates_new_identity_and_blocks_replay(self):
        view=self.preview(mode='copy',target=14708)
        code,data=self.api('apply',dict(view['options'],token=view['token']));self.assertEqual(code,200,data)
        after=self.rb.dump(self.cid);copy=I._find(after,1,view['destination']['slot'])
        self.assertNotEqual(copy['id'],self.source['id']);self.assertEqual(copy['prop_id'],14708)
        self.assertEqual(I._find(after,1,self.slot),self.source)
        for key in I.ITEM_FIELDS:
            if key!='prop_id':self.assertEqual(copy.get(key),self.source.get(key),key)
        self.assertEqual(self.api('apply',dict(view['options'],token=view['token']))[0],409)
        self.assertEqual(len(self.app.changes),1)
        undo=self.rb.undo(self.app.changes[-1],False,self.app.record_reborn_change,self.app.changes)
        self.assertTrue(undo['result']['all_identical'])

    def test_epic_selection_and_actual_equipment_projection(self):
        # The copy has a level-120 mount; equip the level-120 fixture in its legal head slot.
        self.rb.write(self.cid,'equip mount fixture',lambda d:[
            'UPDATE character_items SET item_location=0,slot_index=15 WHERE id=%d'%self.source['id'],
            'UPDATE character_base SET inventory_revision=inventory_revision+1 WHERE id=%d'%self.cid],self.fixtures.append)
        view=self.preview(location=0,slot=15,target=14508,epic_g10=True,stats=['patk','matk','pdmg','mdmg','hp'])
        code,data=self.api('apply',dict(view['options'],token=view['token']));self.assertEqual(code,200,data)
        row=I._find(self.rb.dump(self.cid),0,15)
        self.assertEqual((row['item_quality'],row['item_grade']),(8,10))
        self.assertEqual([row['attribute%d'%n] for n in range(1,6)],[347,357,367,377,307])
        self.assertTrue(all(row['attribute_level%d'%n] is None for n in range(1,6)))
        for stat in view['attributes']:
            value=self.rb.pg.json('(SELECT to_jsonb(attribute_value) FROM character_equipment_attributes WHERE user_id=4 AND body_part_id=15 AND attribute_id=%d)'%stat['id'])
            self.assertAlmostEqual(float(value),float(stat['value']),places=5)
        # An equipped coronet cannot morph into armor without moving to its correct slot.
        code,_=self.api('preview',dict(view['options'],target=14608));self.assertEqual(code,400)

    def test_concurrent_copy_has_one_commit_and_one_conflict_without_orphan_backup(self):
        view=self.preview(mode='copy',target=14608)
        other_rb=Reborn(COMMAND,DATABASE,writable=True,live=False,backup_dir=str(Path(self.temp.name)/'other'))
        other_changes=[]
        other=SimpleNamespace(reborn=other_rb,changes=other_changes,record_reborn_change=other_changes.append,a=SimpleNamespace(rates_mgmt=None),rates=None)
        body=dict(view['options'],token=view['token'])
        with ThreadPoolExecutor(max_workers=2) as pool:
            a=pool.submit(self.api,'apply',body)
            b=pool.submit(common.dispatch,other,'POST','mount_gear/apply/4',{},body,{})
            results=[a.result(),b.result()]
        self.app.changes.extend(other_changes)
        self.assertEqual(sorted(code for code,_ in results),[200,409],results)
        self.assertEqual(len(self.app.changes),1)
        self.assertEqual(len(list(Path(self.temp.name).rglob('*.json'))),2)  # fixture + one committed copy

    def test_bad_stats_invalid_target_readonly_online_and_token_reject_without_writes(self):
        for options in [{'epic_g10':True,'stats':['patk']*2}, {'epic_g10':True,'stats':[k for k,_,_ in G.FAMILIES[:6]]}, {'target':1001}]:
            code,_=self.api('preview',dict(location=1,slot=self.slot,**options));self.assertEqual(code,400)
        view=self.preview()
        self.assertEqual(self.api('apply',dict(view['options'],token='bad'))[0],409)
        self.rb.writable=False
        self.assertEqual(self.api('apply',dict(view['options'],token=view['token']))[0],403)
        self.rb.writable=True
        original=self.rb.online_state
        try:
            self.rb.online_state=lambda _:dict(online=True,name='fixture',reasons=['test session'])
            self.assertEqual(self.api('apply',dict(view['options'],token=view['token']))[0],423)
        finally:self.rb.online_state=original
        self.assertEqual(self.app.changes,[])
        self.assertEqual(self.rb.dump(self.cid)['character_items'],self.before['character_items'])


if __name__=='__main__':unittest.main()
