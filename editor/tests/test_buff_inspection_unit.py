import sys,unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from modules import buffs,common
import test_buffs_unit as fixtures

class BuffInspectionEditorTests(unittest.TestCase):
 def expected(self):return {'epoch':'a'*32,'kind':34,'statusId':274,'revision':str(2**63-1)}
 def test_exact_revision_and_url_character_are_preserved(self):
  ctx=fixtures.Context({'expected':self.expected(),'character':'wrong'})
  buffs.remove(ctx,7);args,kw=ctx.calls[0]
  self.assertEqual(args,('character.buff_remove','PortalAth',self.expected()));self.assertEqual(kw['cid'],7)
 def test_invalid_tokens_do_not_call_runtime(self):
  for key,value in [('revision',1),('revision',1.5),('revision','0'),('revision',str(2**63)),('epoch','0'*32),('epoch','bad'),('kind',False)]:
   token=self.expected();token[key]=value;ctx=fixtures.Context({'expected':token})
   with self.subTest(key=key,value=value),self.assertRaises(common.Bad):buffs.remove(ctx,7)
   self.assertFalse(ctx.calls)
 def test_inspection_legacy_server_is_disabled(self):
  ctx=fixtures.Context()
  with patch.object(common,'_http') as http:self.assertFalse(buffs.active(ctx,7)['supported']);http.assert_not_called()
 def test_inspection_names_come_from_native_catalogue(self):
  ctx=fixtures.Context();ctx.caps['operations']['character.buff_inspect']={}
  data={'ok':True,'epoch':'a'*32,'buffs':[{'statusId':274,'kind':34,'revision':'1','removable':True}]}
  with patch.object(common,'_http',return_value=(200,data)):result=buffs.active(ctx,7)
  self.assertEqual(result['character']['id'],7);self.assertEqual(result['buffs'][0]['name'],"Hermes' Agility Lv. 5")
 def test_offline_remove_is_conflict_without_db_fallback(self):
  ctx=fixtures.Context({'expected':self.expected()});ctx.live_call=lambda *a,**kw:{'ok':False,'offline':True}
  with self.assertRaises(common.Conflict):buffs.remove(ctx,7)
 def test_runtime_changed_conflict_never_records_a_success(self):
  records=[];app=SimpleNamespace(reborn=SimpleNamespace(db='disposable'),record_reborn_change=records.append);ctx=common.Ctx(app,'POST','buffs/remove/7',{},{});caps={'port':51090,'operations':{'character.buff_remove':{'path':'/admin/buff/remove'}}}
  with patch.object(common,'capabilities',return_value=caps),patch.object(common,'_http',return_value=(409,{'error':'buff_changed'})),self.assertRaises(common.Conflict):ctx.live_call('character.buff_remove','PortalAth',self.expected(),cid=7)
  self.assertFalse(records)
if __name__=='__main__':unittest.main(verbosity=2)
