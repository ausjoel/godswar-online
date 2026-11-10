import sys, unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import title_grants as T, skills
from modules.common import Bad, Conflict, Refused
import reborn

class Context:
 def __init__(self,body,current=()):
  self.body=body;self.caps={'available':True,'title_grants':{'version':1,'ownedLimit':128}};self.current=current;self.sql=[]
  self.rb=SimpleNamespace(pg=SimpleNamespace(json=self.json));self.backed=True
 def need_rb(self):return self.rb
 def json(self,query):
  return [{'title_id':1000},{'title_id':1001}] if 'json_agg(t ' in query else True
 def char_write(self,cid,action,build,**kw):
  self.kw=kw;self.sql=build({T.TABLE:[{'title_id':i} for i in self.current]} if self.backed else {});return {}

class TitleGrantTests(unittest.TestCase):
 def test_refuses_old_or_unavailable_runtime(self):
  for caps in [{},{'available':True,'title_grants':['invalid']},{'available':True,'title_grants':{'version':True,'ownedLimit':128}},{'available':False,'title_grants':{'version':1,'ownedLimit':128}},{'available':True,'title_grants':{'version':1,'ownedLimit':64}}]:
   ctx=Context({'add':[1000],'expected':[]});ctx.caps=caps
   with self.subTest(caps=caps),self.assertRaises(Refused):T.write(ctx,7)
   self.assertFalse(ctx.sql)
 def test_refuses_missing_schema(self):
  ctx=Context({'add':[1000],'expected':[]});ctx.rb.pg.json=lambda q:False
  with self.assertRaises(Refused):T.write(ctx,7)
 def test_requires_explicit_nonduplicated_expected_set(self):
  for val in [None,{},[True],[1000,1000]]:
   with self.subTest(val=val),self.assertRaises(Bad):T.write(Context({'add':[1000],'expected':val}),7)
 def test_refuses_unknown_ids_and_conflicting_batch(self):
  for body in [{'add':[1234],'expected':[]},{'add':[1000],'remove':[1000],'expected':[]},{'add':[1000],'expected':[],'note':'x'*513}]:
   with self.subTest(body=body),self.assertRaises(Bad):T.write(Context(body),7)
 def test_stale_snapshot_refused(self):
  ctx=Context({'add':[1001],'expected':[]},[1000])
  with self.assertRaises(Conflict):T.write(ctx,7)
 def test_requires_backup_registration(self):
  ctx=Context({'add':[1000],'expected':[]});ctx.backed=False
  with self.assertRaises(Refused):T.write(ctx,7)
 def test_batched_plan_has_locked_compare_and_no_earned_writes(self):
  ctx=Context({'add':[1001],'remove':[1000],'expected':[1000],'note':"owner's grant"},[1000]);T.write(ctx,7)
  self.assertEqual(ctx.kw,{'fields':['character_title_grants.*'],'cross_bumps':False})
  self.assertIn('IS DISTINCT FROM ARRAY[1000]::integer[]',ctx.sql[0]);self.assertIn("owner''s grant",'\n'.join(ctx.sql))
  self.assertTrue(any('character_title_entitlements' in sql for sql in ctx.sql))
  self.assertFalse(any('UPDATE character_base' in sql or 'DELETE FROM character_title_ownership' in sql for sql in ctx.sql))
 def test_noop_is_refused(self):
  with self.assertRaises(Bad):T.write(Context({'add':[1000],'expected':[1000]},[1000]),7)
 def test_owned_set_tracks_compatible_runtime(self):
  st={'titles_medusa':[{'title_id':1}],'titles_atlantis':[{'title_id':2}],'titles_wonderland':[{'title_id':3}],'titles_admin':[{'title_id':1},{'title_id':4}]}
  self.assertEqual(skills.owned_titles(st),[1,2]);st['title_grants_support']={'available':True};self.assertEqual(skills.owned_titles(st),[1,2,3,4])
 def test_registered_dump_and_generic_undo_sql(self):
  self.assertIn((T.TABLE,'character_id = {cid}',('character_id','title_id')),reborn.DUMP_TABLES_EXTRA)
  rb=reborn.Reborn();row={'character_id':7,'title_id':1000,'granted_by':'gwadmin-web','note':'','granted_at':'2026-10-30T00:00:00Z'}
  sql=rb.restore_statements({T.TABLE:[]},{T.TABLE:[row]},tables=[(T.TABLE,('character_id','title_id'))])
  self.assertTrue(any('DELETE FROM character_title_grants' in s for s in sql))
  sql=rb.restore_statements({T.TABLE:[row]},{T.TABLE:[]},tables=[(T.TABLE,('character_id','title_id'))])
  self.assertTrue(any('INSERT INTO character_title_grants' in s for s in sql))

if __name__=='__main__':unittest.main(verbosity=2)
