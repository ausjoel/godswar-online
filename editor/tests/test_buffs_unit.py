import sys,unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import buffs,common

class Context:
 def __init__(self,body=None):
  self.body=body or {};self.rb=SimpleNamespace(writable=True,pg=SimpleNamespace(json=lambda q:{'id':7,'name':'PortalAth'}));self.catalog=common.CatalogService();self.calls=[]
  self.caps={'port':51090,'operations':{'character.buff_apply':{'path':'/admin/buff/apply'}}}
 def need_rb(self):return self.rb
 def live_call(self,*args,**kw):self.calls.append((args,kw));return {'ok':True,'result':{'applied':True}}

def catalogue():return {'version':1,'maxDurationSeconds':2**32-1,'buffs':[{'skillId':774,'statusId':274,'kind':34,'priority':5,'beneficial':True,'durationSeconds':10,'effects':{'dodge':3000,'criticalResistance':1000}}]}

class BuffEditorTests(unittest.TestCase):
 def test_periodic_damage_explains_exact_pulse_and_life_without_changing_request(self):
  doc=catalogue();doc['buffs']=[{'source':'status','activation':'periodic-damage','activationNote':'Loses 50 HP every 2 seconds. Ends on death.', 'statusId':10,'kind':1,'durationSeconds':6,'effects':{'hpDamagePerPulse':50,'damageIntervalSeconds':2}}]
  raw={'id':10,'name':'Bleeding','note':'old text','duration_s':6,'effects':[{'code':27,'label':'old HP label'}]}
  ctx=Context({'status_id':10,'duration_seconds':8})
  with patch.object(common,'_http',return_value=(200,doc)),patch.object(buffs,'_library_rows',return_value=({'version':1},[dict(raw)])):
   row=buffs.runtime_catalogue(ctx)['buffs'][0];self.assertIn('Ends on death',row['description']);self.assertIn('50 HP every 2 seconds',row['effect_text'])
   library=buffs.library(ctx)['entries'][0];self.assertIn('Ends on death',library['note']);self.assertIn('50 HP every 2 seconds',library['effects'][0]['label'])
   buffs.apply(ctx,7)
  self.assertEqual(ctx.calls[0][0],('character.buff_apply','PortalAth',{'statusId':10,'durationSeconds':8}))
  self.assertEqual(raw['effects'][0]['label'],'old HP label')
 def test_mount_requirements_replace_flag_text_and_keep_native_request(self):
  doc=catalogue();doc['buffs']=[{'source':'mount','activation':'mount','activationNote':'Requires matching equipped mount; costs 50 MP.', 'statusId':1100,'kind':110,'durationSeconds':600,'effects':{}}]
  raw={'id':1100,'name':'Greek Horse','note':'speed +1','duration_s':-1,'effects':[{'code':33,'label':'speed +1'}]}
  ctx=Context({'status_id':1100,'duration_seconds':45})
  with patch.object(common,'_http',return_value=(200,doc)),patch.object(buffs,'_library_rows',return_value=({'version':1},[dict(raw)])):
   row=buffs.runtime_catalogue(ctx)['buffs'][0]
   self.assertIn('50 MP',row['description']);self.assertNotIn('+1',row['effect_text'])
   library=buffs.library(ctx)['entries'][0]
   self.assertIn('equipped mount',library['note']);self.assertNotIn('+1',library['effects'][0]['label'])
   self.assertEqual(library['duration_s'],600)
   buffs.apply(ctx,7)
  self.assertEqual(raw['effects'][0]['label'],'speed +1')
  self.assertEqual(raw['duration_s'],-1)
  self.assertEqual(ctx.calls[0][0],('character.buff_apply','PortalAth',{'statusId':1100,'durationSeconds':45}))
  ctx.rb.writable=False
  with self.assertRaises(common.Bad):buffs.apply(ctx,7)
 def test_legacy_server_disables_tools_without_probe(self):
  ctx=Context();ctx.caps['operations']={}
  with patch.object(common,'_http') as http:self.assertFalse(buffs.runtime_catalogue(ctx)['supported']);http.assert_not_called()
 def test_catalogue_uses_actual_native_names_and_descriptions(self):
  with patch.object(common,'_http',return_value=(200,catalogue())):
   row=buffs.runtime_catalogue(Context())['buffs'][0]
  self.assertEqual(row['name'],'Gaia Care 5');self.assertIn('Dodge',row['description']);self.assertEqual(row['effects']['dodge'],3000)
 def test_finalise2_server_note_and_hostile_rows(self):
  doc={'version':1,'maxDurationSeconds':2**32-1,'buffs':[{'source':'hostile','name':'Caged','hostile':True,'note':'Applies: Physical damage taken -75%. Server enforces: no skill casts (stunned).','skillId':0,'statusId':400,'kind':13,'priority':1,'beneficial':False,'durationSeconds':10,'effects':{'physicalDamageReduction':0.75}}]}
  with patch.object(common,'_http',return_value=(200,doc)):
   row=buffs.runtime_catalogue(Context())['buffs'][0]
  self.assertTrue(row['hostile']);self.assertIn('stunned',row['server_note']);self.assertEqual(row['not_applied'],[])
  ctx=Context({'status_id':400})
  with patch.object(common,'_http',return_value=(200,doc)):self.assertEqual(buffs.apply(ctx,'7')['applies'],'live')
  self.assertEqual(ctx.calls[0][0][2],{'statusId':400})
 def test_invalid_runtime_catalogue_refused(self):
  for result in [(404,{}),(200,{'version':2,'buffs':[]}),(200,{'version':1,'buffs':'bad'})]:
   with self.subTest(result=result),patch.object(common,'_http',return_value=result),self.assertRaises(common.Bad):buffs.runtime_catalogue(Context())
 def test_unknown_and_invalid_durations_never_apply(self):
  for body in [{'skill_id':999},{'skill_id':774,'duration_seconds':0},{'skill_id':774,'duration_seconds':True},{'skill_id':774,'duration_seconds':2**32},{'skill_id':774,'duration_seconds':1.5}]:
   ctx=Context(body)
   with self.subTest(body=body),patch.object(common,'_http',return_value=(200,catalogue())),self.assertRaises(common.Bad):buffs.apply(ctx,'7')
   self.assertFalse(ctx.calls)
 def test_exact_url_target_and_native_duration_reach_runtime(self):
  ctx=Context({'skill_id':774,'duration_seconds':2**32-1,'character':'wrong target'})
  with patch.object(common,'_http',return_value=(200,catalogue())):self.assertEqual(buffs.apply(ctx,'7')['applies'],'live')
  args,kw=ctx.calls[0];self.assertEqual(args,('character.buff_apply','PortalAth',{'skillId':774,'durationSeconds':2**32-1}));self.assertEqual(kw['cid'],7)
 def test_readonly_and_offline_have_no_database_fallback(self):
  ctx=Context({'skill_id':774});ctx.rb.writable=False
  with self.assertRaises(common.Bad):buffs.apply(ctx,7)
  ctx.rb.writable=True;ctx.live_call=lambda *a,**k:{'ok':False,'offline':True}
  with patch.object(common,'_http',return_value=(200,catalogue())),self.assertRaisesRegex(common.Bad,'logged into'):buffs.apply(ctx,7)
 def test_live_audit_is_bound_to_character_and_honest_about_undo(self):
  records=[];app=SimpleNamespace(reborn=SimpleNamespace(db='disposable'),record_reborn_change=records.append)
  ctx=common.Ctx(app,'POST','buffs/apply/7',{},{});caps={'port':51090,'operations':{'character.buff_apply':{'path':'/admin/buff/apply'}}}
  with patch.object(common,'capabilities',return_value=caps),patch.object(common,'_http',return_value=(200,{'applied':True})) as http:
   ctx.live_call('character.buff_apply','PortalAth',{'skillId':774},cid=7,no_undo='Session effect')
  self.assertEqual(records[0]['cid'],7);self.assertEqual(records[0]['no_undo'],'Session effect');self.assertEqual(http.call_args.args[3]['character'],'PortalAth')
if __name__=='__main__':unittest.main(verbosity=2)
