import json,os,sys,tempfile,unittest
from types import SimpleNamespace
from unittest.mock import patch
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT));sys.path.insert(0,str(ROOT.parent))
from modules import favourites as F,inventory as I,common as C
class FakeRB:
 def item_meta(self,pid):return dict(name='Test item',kind='consumable',slot=-1,server=True,client=True,overlap=99,defaults=dict(bound=0,item_quality=0,item_grade=0))
 def cat(self):return {'attributes':{}}
class FavouriteTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.store=F.Store(os.path.join(self.tmp.name,'favourites.sqlite3'))
  self.d={'character_base':[{'id':1,'name':'Fixture','inventory_revision':8,'warehouse_capacity':40,'profession':0,'fighter_job_lv':1}],'character_items':[dict(id=7,user_id=1,item_location=1,slot_index=0,prop_id=4102,stack=2,bound=0,item_quality=0,item_grade=0,item_exp=0)],'sealed_pet_items':[]}
  self.rb=FakeRB()
 def tearDown(self):self.tmp.cleanup()
 def test_store_survives_reopen(self):
  f=self.store.save('Gear set',[{'prop_id':4102,'stack':2}]);s=F.Store(self.store.path);self.assertEqual(s.get(f['id'])['items'],f['items']);self.assertEqual(os.stat(s.path).st_mode&0o777,0o600)
 def test_archive_is_soft_and_revision_checked(self):
  f=self.store.save('Set',[{'prop_id':4102}])
  with self.assertRaises(C.Conflict):self.store.archive(f['id'],2)
  self.store.archive(f['id'],1);self.assertEqual(self.store.list(),[])
  with self.store.db() as c:self.assertEqual(c.execute('SELECT count(*) FROM favourites').fetchone()[0],1)
 def test_preview_expiration_and_receipt(self):
  token=self.store.preview({'target':1})
  with patch.object(F.time,'time',return_value=F.time.time()+1000):
   with self.assertRaises(C.Conflict):self.store.plan(token)
  self.store.complete(token,'change-1');self.assertEqual(self.store.plan(token)[1],'change-1')
 def test_repeated_copy_uses_empty_cells_and_preserves_stack(self):
  with patch.object(I,'_balance',return_value={}):p,w=F.make_plan(self.rb,self.d,[dict(prop_id=4102,stack=2)],1,3)
  self.assertEqual([x['slot'] for x in p],[1,2,3]);self.assertEqual([x['item']['stack'] for x in p],[2,2,2]);self.assertEqual(len(self.d['character_items']),1)
 def test_capacity_refuses_whole_batch(self):
  with self.assertRaises(C.Bad):F.make_plan(self.rb,self.d,[dict(prop_id=4102)],3,41)
 def test_total_limit_and_invalid_location(self):
  with self.assertRaises(C.Bad):F.make_plan(self.rb,self.d,[dict(prop_id=4102)]*2,1,96)
  with self.assertRaises(C.Bad):F.make_plan(self.rb,self.d,[dict(prop_id=4102)],2,1)
 def test_stack_validated_before_apply(self):
  with patch.object(I,'_balance',return_value={}):
   with self.assertRaises(C.Bad):F.make_plan(self.rb,self.d,[dict(prop_id=4102,stack=100)],1,1)
 def test_server_only_item_refused(self):
  meta=self.rb.item_meta(4102);meta['client']=False
  with patch.object(self.rb,'item_meta',return_value=meta),patch.object(I,'_balance',return_value={}):
   with self.assertRaises(C.Bad):F.make_plan(self.rb,self.d,[dict(prop_id=4102)],1,1)
 def ctx(self):return SimpleNamespace(need_rb=lambda:self.rb,body={'source':1,'source_revision':8,'selection':[dict(id=7,location=1,slot=0)]},app=object())
 def test_snapshot_excludes_identity_and_ownership(self):
  ctx=self.ctx()
  with patch.object(self.rb,'dump',return_value=self.d,create=True),patch.object(C,'online_info',return_value={'online':False}):items=F.snapshot(ctx)
  self.assertNotIn('id',items[0]);self.assertNotIn('user_id',items[0]);self.assertNotIn('slot_index',items[0]);self.assertEqual(items[0]['stack'],2)
 def test_sealed_pet_excluded(self):
  ctx=self.ctx();self.d['sealed_pet_items']=[dict(item_instance_id=7)]
  with patch.object(self.rb,'dump',return_value=self.d,create=True),patch.object(C,'online_info',return_value={'online':False}):
   with self.assertRaises(C.Refused):F.snapshot(ctx)
 def test_source_revision_and_row_identity(self):
  ctx=self.ctx();ctx.body['source_revision']=7
  with patch.object(self.rb,'dump',return_value=self.d,create=True),patch.object(C,'online_info',return_value={'online':False}):
   with self.assertRaises(C.Conflict):F.snapshot(ctx)
   ctx.body['source_revision']=8;ctx.body['selection'][0]['id']=99
   with self.assertRaises(C.Conflict):F.snapshot(ctx)
 def test_duplicate_source_cell_rejected(self):
  ctx=self.ctx();ctx.body['selection']*=2
  with patch.object(self.rb,'dump',return_value=self.d,create=True),patch.object(C,'online_info',return_value={'online':False}):
   with self.assertRaises(C.Bad):F.snapshot(ctx)
 def test_picked_items_keep_only_plain_fields(self):
  items=F.plain_items([dict(prop_id=4102,stack=2,bound=0,item_exp=5,attribute1=3,id=99,user_id=1,slot_index=4)])
  self.assertEqual(items,[dict(prop_id=4102,stack=2,bound=0)])
 def test_picked_items_limits(self):
  for bad in ([],[dict(prop_id=1)]*97,'x',['x'],[dict(stack=1)]):
   with self.assertRaises(C.Bad):F.plain_items(bad)
 def give_ctx(self,items,bag_item):return SimpleNamespace(need_rb=lambda:self.rb,body=dict(target=1,items=items),app=object(),bag_item=bag_item)
 def run_give(self,ctx,online=True):
  with patch.object(self.rb,'dump',return_value=self.d,create=True),patch.object(C,'online_info',return_value={'online':online}),patch.object(I,'_balance',return_value={}):return F.give(ctx)
 def test_give_offline_refused(self):
  with self.assertRaises(C.Conflict):self.run_give(self.give_ctx([dict(prop_id=4102)],None),online=False)
 def test_give_validates_everything_first(self):
  calls=[];ctx=self.give_ctx([dict(prop_id=4102),dict(prop_id=4102,stack=100)],lambda *a:calls.append(a))
  with self.assertRaises(C.Bad):self.run_give(ctx)
  self.assertEqual(calls,[])
 def test_give_needs_free_cells_for_all(self):
  calls=[];self.d['character_items']=[dict(id=i,user_id=1,item_location=1,slot_index=i,prop_id=4102) for i in range(95)]
  with self.assertRaises(C.Bad):self.run_give(self.give_ctx([dict(prop_id=4102)]*2,lambda *a:calls.append(a)))
  self.assertEqual(calls,[])
 def test_give_omits_unset_fields_and_stops_at_refusal(self):
  calls=[]
  def bag(cid,op,args):
   calls.append(args)
   if len(calls)==2:raise C.Conflict('busy')
  r=self.run_give(self.give_ctx([dict(prop_id=4102,stack=2),dict(prop_id=4102),dict(prop_id=4102)],bag))
  self.assertEqual(calls[0],dict(item=4102,quantity=2));self.assertEqual(len(calls),2)
  self.assertEqual(r['given'],['Test item']);self.assertEqual(r['failed']['error'],'busy');self.assertEqual(r['not_attempted'],['Test item'])
 def test_attribute_level_above_client_needs_override(self):
  rb=self.rb;rb.cat=lambda:{'attributes':{'150':{'max':30}}}
  item=dict(prop_id=4102,attribute1=150,attribute_level1=26)
  with patch.object(I,'_balance',return_value={}):
   with self.assertRaises(C.Bad):I._check_item(rb,self.d,1,0,item,False)
   self.assertTrue(any('L25' in w for w in I._check_item(rb,self.d,1,0,item,True)[2]))
   self.assertEqual(I._check_item(rb,self.d,1,0,dict(item,attribute_level1=25),False)[2],[])
if __name__=='__main__':unittest.main(verbosity=2)
