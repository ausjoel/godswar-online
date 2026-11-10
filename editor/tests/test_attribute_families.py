"""Family provenance, grade projection and exact old-item round trips on an explicit local copy."""
import os
from pathlib import Path
import re
import shlex
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import common, inventory as I, gearstats as G, _attribute_families as AF
from reborn import Reborn


class AttributeFamilies(unittest.TestCase):
    def test_all_native_stone_records_match_policy(self):
        native=(Path(__file__).resolve().parents[2]/'server/src/Godswar.Server/State/GearEnhancementMaterialCatalog.cs').read_text()
        records=[]
        for line in native.splitlines():
            m=re.search(r'Stone\((\d+), "[^"]+", "([^"]+)".*ShippedStackCap|Stone\((\d+), "[^"]+", "([^"]+)".*?, 1,',line)
            if not m:continue
            sid,name=(int(m[1]),m[2]) if m[1] else (int(m[3]),m[4])
            chain=re.search(r'Chain\((\d+), (\d+)\)',line)
            ids=list(range(int(chain[1]),int(chain[2])+1)) if chain else [int(re.search(r'\[(\d+)\]',line)[1])]
            records.append((sid,name,ids,'canEnhance: true' in line))
        self.assertEqual(len(records),45)
        self.assertEqual(sorted(records),sorted(AF.ORDINARY+AF.CLASS+AF.MOUNT))
        for sid,name,ids,enhances in records:
            for n,aid in enumerate(ids,1):
                with self.subTest(stone=sid,attribute=aid):
                    policy=AF.policy(aid)
                    self.assertEqual(policy['stone'],sid)
                    self.assertEqual(policy['rank'],n if enhances else None)
                    self.assertEqual(policy['max_rank'],len(ids) if enhances else None)

    def test_pinned_roll_anchors_use_native_rank_one(self):
        # The server infers rank relative to the first allowed family ID. The
        # shipped roll lists all anchor at index zero; do not silently assume this after a content update.
        items=common.CATALOG.file('items')
        count=0
        for item in items.values():
            allowed=set(item.get('append_attributes') or [])
            for _sid,_name,chain,quartz in AF.ORDINARY:
                matches=[aid for aid in chain if aid in allowed]
                if quartz and matches:
                    self.assertEqual(matches[0],chain[0],item.get('id'))
                    count+=1
        self.assertGreater(count,2000)

    def test_class_profession_authority_and_unknowns(self):
        native=(Path(__file__).resolve().parents[2]/'server/src/Godswar.Server/State/ClassSuitAttributePlanner.cs').read_text()
        pairs=re.findall(r'\[(995[0-7])\] = \((\d), (\d+)\)',native)
        self.assertEqual(len(pairs),8)
        for _stone,group,aid in pairs:
            for profession in range(5):
                with self.subTest(attribute=aid,profession=profession):
                    self.assertEqual(AF.class_allowed(int(aid),profession),profession in ((0,1) if group=='0' else (2,3)))
        self.assertIn('0 => profession is 0 or 1',native)
        self.assertIn('1 => profession is 2 or 3',native)
        self.assertEqual(AF.policy(900)['family'],'unknown:900')
        self.assertNotEqual(AF.policy(900)['family'],AF.policy(901)['family'])
        self.assertIsNone(AF.policy(900)['rank'])
        self.assertEqual(AF.policy(480)['group'],'elemental')

    def test_four_stat_options_use_grade_not_quartz_rank(self):
        ctx=SimpleNamespace(catalog=common.CATALOG)
        entry={'kind':'glove','append_attributes':list(range(0,5))+[40]}
        raw=common.CATALOG.file('appendattrs')
        for grade in (1,10,25):
            _kind,_why,options=G.options(ctx,entry,grade)
            for option in options:
                for level in option['levels']:
                    with self.subTest(grade=grade,attribute=level['id']):
                        self.assertEqual(level['value'],raw[str(level['id'])]['levels'][grade-1])
            patk=next(o for o in options if o['key']=='patk')
            self.assertEqual(patk['levels'][-1]['value'],raw['4']['levels'][grade-1])


COMMAND=os.environ.get('GODSWAR_ATTR_FAMILIES_PSQL')
DATABASE=os.environ.get('GODSWAR_ATTR_FAMILIES_DB')


@unittest.skipUnless(COMMAND and DATABASE,'requires explicit editor_attrfamilies* local database copy')
class AttributeDatabase(unittest.TestCase):
    def setUp(self):
        args=shlex.split(COMMAND)
        if (Path(args[0]).name!='psql' or '-h' not in args or '-p' not in args or args[args.index('-h')+1] not in ('127.0.0.1','::1')
            or int(args[args.index('-p')+1])<10000 or not DATABASE.startswith('editor_attrfamilies')):
            raise ValueError('only a direct loopback psql port >=10000 and editor_attrfamilies* test copy')
        self.temp=tempfile.TemporaryDirectory();self.changes=[];self.fixtures=[];self.cid=4
        self.rb=Reborn(COMMAND,DATABASE,writable=True,live=False,backup_dir=self.temp.name)
        self.app=SimpleNamespace(reborn=self.rb,changes=self.changes,record_reborn_change=self.changes.append,a=SimpleNamespace(rates_mgmt=None),rates=None)
        common.CATALOG.rb=self.rb
        d=self.rb.dump(self.cid);self.old=I._find(d,0,6)
        item={k:self.old.get(k) for k in I.ITEM_FIELDS}
        item.update(item_grade=10,attribute1=0,attribute_level1=None,attribute2=40,attribute_level2=2,attribute3=367,attribute_level3=None)
        self.rb.write(self.cid,'attribute family copied-DB fixture',I._build_set(self.rb,self.cid,0,6,item,False),self.fixtures.append)
        self.source=I._find(self.rb.dump(self.cid),0,6)

    def tearDown(self):
        self.rb.writable=True
        for c in list(self.changes)[::-1]:
            if not c.get('undo_of') and not c.get('undone'):
                u=self.rb.undo(c,False,self.changes.append,self.changes)
                self.assertTrue(u['result']['all_identical'])
        for c in self.fixtures[::-1]:
            u=self.rb.undo(c,False,lambda _:None,self.fixtures)
            self.assertTrue(u['result']['all_identical'])
        self.temp.cleanup()

    def post(self,body):return common.dispatch(self.app,'POST','inventory/set/4',{},body,{})

    def test_metadata_and_roundtrip_keep_existing_ids_and_null_levels(self):
        code,meta=common.dispatch(self.app,'GET','inventory/meta',{},None,{})
        self.assertEqual(code,200,meta)
        self.assertEqual(meta['attributes']['0']['family_policy']['rank'],1)
        self.assertEqual(meta['attributes']['4']['family_policy']['rank'],5)
        self.assertIsNone(meta['attributes']['40']['family_policy']['rank'])
        self.assertIsNone(meta['attributes']['367']['family_policy']['rank'])
        item={k:self.source.get(k) for k in I.ITEM_FIELDS}
        code,result=self.post(dict(location=0,slot=6,item=item,expected=self.source))
        self.assertEqual(code,200,result)
        after=I._find(self.rb.dump(4),0,6)
        for key in ['id']+I.ITEM_FIELDS:self.assertEqual(after.get(key),self.source.get(key),key)

    def test_grade_changes_and_max_replacement_match_native_equipment_projection(self):
        source=self.source
        for grade in (1,10,25):
            item={k:source.get(k) for k in I.ITEM_FIELDS}
            item.update(item_grade=grade,attribute1=4,attribute_level1=5)
            code,result=self.post(dict(location=0,slot=6,item=item,expected=source))
            self.assertEqual(code,200,result)
            source=I._find(self.rb.dump(4),0,6)
            for aid in (4,40,367):
                value=self.rb.pg.json('(SELECT to_jsonb(attribute_value) FROM character_equipment_attributes WHERE user_id=4 AND body_part_id=6 AND attribute_id=%d)'%aid)
                self.assertAlmostEqual(float(value),float(self.rb.cat()['attributes'][str(aid)]['values'][grade-1]),places=5)
            self.assertEqual(source['attribute_level1'],5)
            self.assertEqual(source['attribute_level2'],2)
            self.assertIsNone(source['attribute_level3'])
        # Ordinary maximum is not the dedicated class-suit attribute.
        self.assertEqual(source['attribute1'],4)
        self.assertIsNone(source['class_attribute1'])

    def test_four_stat_route_and_unlevelled_storage(self):
        code,options=common.dispatch(self.app,'GET','gearstats/options/2900',{'grade':'25'},None,{})
        self.assertEqual(code,200,options)
        hp=next(o for o in options['options'] if o['key']=='maxhp')['levels'][-1]
        self.assertEqual(hp['value'],float(self.rb.cat()['attributes']['134']['values'][24]))
        body=dict(location=0,slot=6,stats=[{'key':'maxhp','level':5},{'key':'dodge','level':1}],expected=self.source)
        code,result=common.dispatch(self.app,'POST','gearstats/apply/4',{},body,{})
        self.assertEqual(code,200,result)
        row=I._find(self.rb.dump(4),0,6)
        self.assertEqual((row['attribute1'],row['attribute_level1']),(134,5))
        self.assertEqual((row['attribute2'],row['attribute_level2']),(50,None))
        projected=self.rb.pg.json('(SELECT to_jsonb(attribute_value) FROM character_equipment_attributes WHERE user_id=4 AND body_part_id=6 AND attribute_id=134)')
        self.assertEqual(float(projected),float(self.rb.cat()['attributes']['134']['values'][9]))

    def test_readonly_online_and_stale_editor_state_remain_refused(self):
        body=dict(location=0,slot=6,item={k:self.source.get(k) for k in I.ITEM_FIELDS},expected=self.source)
        self.rb.writable=False
        code,data=self.post(body)
        self.assertEqual(code,400,data)
        self.assertIn('read-only',data['error'].lower())
        self.rb.writable=True
        with patch.object(common,'online_info',return_value={'online':True,'name':'the admin','phase':'world','reason':'test character online'}):
            self.assertEqual(self.post(body)[0],423)
        body['expected']=dict(self.source,item_grade=25)
        self.assertEqual(self.post(body)[0],409)
        self.assertFalse(self.changes)

    def test_class_attributes_are_separate_and_wrong_profession_is_refused(self):
        item={k:self.source.get(k) for k in I.ITEM_FIELDS}
        item.update(prop_id=next(pid for pid in I.class_suit_items() if self.rb.item_meta(pid).get('classes')==[3]),class_attribute1=200)
        code,data=self.post(dict(location=1,slot=next(s for s in range(I.BAG_SLOTS) if I._find(self.rb.dump(4),1,s) is None),item=item,expected=None))
        self.assertEqual(code,400,data);self.assertIn('profession',data['error'])
        item['attribute1']=220;item['class_attribute1']=None
        code,data=self.post(dict(location=0,slot=6,item=item,expected=self.source))
        self.assertEqual(code,400,data);self.assertIn('dedicated',data['error'])
        self.assertFalse(self.changes)
