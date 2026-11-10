import copy
import sys
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import _npc_dialogue_release as R
from modules.common import Bad,Refused

class DialogueTests(unittest.TestCase):
 def fixture(self):
  return {'header':{'spawn_revision':R.SPAWN},'npc_dialogue_texts':[{'npc_key':'x','scene_key':'s','display_name':'Name','description':'Hello'}],
   'npc_dialogue_profiles':[{'profile_key':'p','dialog_index':4,'behavior':1,'initial_request_sub_id':-1}],
   'npc_dialogue_bindings':[{'npc_key':'x','client_script_key':'x','profile_key':'p','route_order':0}],
   'npc_dialogue_profile_entries':[{'profile_key':'p','menu_order':0,'sub_id':1}]}
 def test_draft_is_text_only_and_does_not_mutate_source(self):
  source=self.fixture();before=copy.deepcopy(source)
  got=R.edited(source,[{'npc_key':'x','display_name':"the admin's Ω",'description':'line 1\nline 2 😀'}])
  self.assertEqual(source,before);self.assertNotEqual(R.digest(source),R.digest(got))
  for t in R.TABLES:
   if t!='npc_dialogue_texts':self.assertEqual(got[t],source[t])
 def test_invalid_text_and_structural_edits(self):
  base={'npc_key':'x','display_name':'Name','description':'Hello'}
  cases=[[],[dict(base,scene_key='bad')],[dict(base,npc_key='missing')],[base,base],
   [dict(base,description=' ')],[dict(base,display_name='a'*256)],
   [dict(base,description='界'*5462)],[dict(base,description='\0')],[dict(base,description='\ud800')]]
  for changes in cases:
   with self.subTest(changes=str(changes)[:60]),self.assertRaises(Bad):R.edited(self.fixture(),changes)
 def test_unicode_hash_and_multi_route_order(self):
  a=self.fixture();b=copy.deepcopy(a);b['npc_dialogue_bindings'][0]['route_order']=1
  self.assertNotEqual(R.digest(a),R.digest(b))
  b=copy.deepcopy(a);b['header']['spawn_revision']='A'*64
  self.assertNotEqual(R.digest(a),R.digest(b))
  self.assertEqual(R.ordinal('😀')<R.ordinal('\ue000'),True) # .NET ordinal UTF-16, unlike Python scalar order
 def test_chain_bounds_publisher_and_cycles(self):
  rows=[];previous=R.REVIEWED
  for i in range(65):
   current=('%064X'%(i+1));rows.append(dict(revision=current,predecessor_revision=previous,publisher=R.PUBLISHER));previous=current
  self.assertEqual(R.depth(rows[63]['revision'],rows),64)
  with self.assertRaises(Refused):R.depth(rows[64]['revision'],rows)
  for records in [[],[dict(rows[0],publisher='other')],[dict(rows[0],predecessor_revision=rows[0]['revision'])]]:
   with self.assertRaises(Refused):R.depth(rows[0]['revision'],records)
 def test_structural_drift_and_forged_hash_refuse(self):
  from unittest.mock import patch
  root=self.fixture();root['header'].update(text_count=1,profile_count=1,route_count=1,menu_entry_count=1)
  root['header']['revision']=R.digest(root)
  with patch.object(R,'REVIEWED',root['header']['revision']):
   R.validate(root,root)
   changes=[('npc_dialogue_texts','scene_key','other'),('npc_dialogue_bindings','client_script_key','other'),
    ('npc_dialogue_profiles','initial_request_sub_id',7),('npc_dialogue_profile_entries','sub_id',9)]
   for table,field,value in changes:
    with self.subTest(table=table):
     bad=copy.deepcopy(root);bad[table][0][field]=value
     with self.assertRaises(Refused):R.validate(bad,root)
   for field,value in [('revision','A'*64),('text_count',2),('spawn_revision','A'*64)]:
    with self.subTest(header=field):
     bad=copy.deepcopy(root);bad['header'][field]=value
     with self.assertRaises(Refused):R.validate(bad,root)
 def test_missing_registry_and_new_spawn_refuse(self):
  from unittest.mock import patch
  from modules import npc_dialogue as N
  state={'publication':{'revision':R.REVIEWED},'spawn':R.SPAWN}
  with patch.object(N,'query',return_value=False),self.assertRaises(Refused):N.approved(None,state,R.REVIEWED)
  state['spawn']='A'*64
  with self.assertRaises(Refused):N.approved(None,state,R.REVIEWED)
 def test_missing_reviewed_release_refuses_cleanly(self):
  with self.assertRaises(Refused):R.validate({"header":None},{"header":None})
 def test_revision_requires_exact_hash(self):
  for v in [None,'',R.REVIEWED.lower(),"';select 1",42]:
   with self.assertRaises(Bad):R.revision(v)
  self.assertEqual(R.revision(R.REVIEWED),R.REVIEWED)

if __name__=='__main__':unittest.main()
