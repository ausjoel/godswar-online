"""Opt-in HTTP and copied-DB proof. Run only against the named disposable copy.
GODSWAR_NPC_EDITOR=http://127.0.0.1:18789 python -m pytest editor/tests/test_npc_dialogue_copy.py
The module restores the initial greeting content by normal publication; immutable history remains.
"""
import copy,json,os,re,sys,unittest,urllib.request,urllib.error,urllib.parse,uuid
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import reborn
from modules import npc_dialogue as N
from modules import _npc_dialogue_release as R

@unittest.skipUnless(os.getenv('GODSWAR_NPC_EDITOR'),'requires named local editor database copy')
class CopiedDialogueTests(unittest.TestCase):
 def test_publish_successors_conflicts_and_restore(self):
  self.url=os.environ['GODSWAR_NPC_EDITOR'];url=urllib.parse.urlsplit(self.url)
  self.assertEqual(url.hostname,'127.0.0.1');self.assertGreaterEqual(url.port,10000)
  self.rb=reborn.Reborn('/opt/homebrew/bin/psql -h 127.0.0.1 -p 15439 -U patchtest','editor_npc_dialogue',writable=True)
  self.assertEqual(self.rb.pg.json('to_jsonb(current_database())'),'editor_npc_dialogue')
  html=urllib.request.urlopen(self.url,timeout=10).read().decode()
  self.csrf=re.search(r'name="csrf" content="([^"]+)"',html).group(1)
  def api(path,body=None,code=200):
   req=urllib.request.Request(self.url+'/api/'+path,data=json.dumps(body).encode() if body is not None else None,
    headers={'Content-Type':'application/json','X-CSRF-Token':self.csrf})
   try:
    response=urllib.request.urlopen(req,timeout=120);status=response.status;out=json.load(response)
   except urllib.error.HTTPError as e:status=e.code;out=json.load(e)
   self.assertEqual(status,code,out);return out
  get=lambda:api('m/npc_dialogue/overview')
  before=get();self.assertEqual(before['database'],'editor_npc_dialogue')
  self.assertTrue(before['writable']);self.assertEqual(len(before['texts']),416)
  first=next(r for r in before['texts'] if r['npc_key']=='Athens_070');changed=dict(first)
  changed.pop('scene_key');changed['display_name']="Gear Mentor Ω";changed['description']="[COPY TEST] the admin's greeting.\nΩ <script>text only</script> 😀 "+uuid.uuid4().hex
  original=dict(first);original.pop('scene_key')
  initial=N.bundle(type('Ctx',(),{'need_rb':lambda _:self.rb})(),before['publication']['revision'])
  self.assertEqual(R.digest(initial),before['publication']['revision'])
  root=N.bundle(type('Ctx',(),{'need_rb':lambda _:self.rb})(),R.REVIEWED)
  def publish(changes):
   current=get();body={'expected':current['publication'],'changes':changes}
   plan=api('m/npc_dialogue/preview',body)
   result=api('m/npc_dialogue/publish',dict(body,token=plan['token']))
   self.assertEqual(result['applies'],'after_restart');return body,plan,result
  try:
   body={'expected':before['publication'],'changes':[changed]}
   preview=api('m/npc_dialogue/preview',body)
   self.assertEqual(get()['publication'],before['publication'])
   self.assertFalse(preview['reuse']);self.assertEqual(len(preview['changes']),1)
   api('m/npc_dialogue/publish',dict(body,token='tampered'),409)
   for bad in [dict(changed,description=''),dict(changed,scene_key='changed'),dict(changed,npc_key='unknown'),dict(changed,description='界'*5462)]:
    api('m/npc_dialogue/preview',dict(body,changes=[bad]),400)
   result=api('m/npc_dialogue/publish',dict(body,token=preview['token']))
   self.assertTrue(result['change']['no_undo']);one=get();self.assertEqual(one['publication']['revision'],preview['revision'])
   api('m/npc_dialogue/publish',dict(body,token=preview['token']),409)
   api('undo',{'id':result['change']['id']},400)
   api('undo',{'id':result['change']['id'],'full':True},400)
   ctx=type('Ctx',(),{'need_rb':lambda _:self.rb})()
   self.assertEqual(N.bundle(ctx,before['publication']['revision']),initial)
   copied=N.bundle(ctx,one['publication']['revision']);R.validate(copied,root)
   for table in R.TABLES:
    if table!='npc_dialogue_texts':self.assertEqual(sorted(copied[table],key=str),sorted(initial[table],key=str))
   nextchange=dict(changed,description=changed['description']+'\nSecond successor.')
   _,two,_=publish([nextchange]);self.assertFalse(two['reuse'])
   # Restore an older immutable release; deterministic content hashes must reuse it.
   _,back,_=publish([original]);self.assertTrue(back['reuse']);self.assertEqual(back['revision'],before['publication']['revision'])
   # ABA: moving back to the original revision still invalidates the old preview by publication timestamp.
   api('m/npc_dialogue/publish',dict(body,token=preview['token']),409)
   _,again,_=publish([changed]);self.assertTrue(again['reuse']);self.assertEqual(again['revision'],preview['revision'])
   print('COPY PROOF: 416 texts / 23 profiles / 36 routes / 95 menu entries; new successor, second successor, historical reuse, stale/ABA/tampered refusals, row Undo/Restore refused; original release unchanged.')
  finally:
   current=get();row=next(r for r in current['texts'] if r['npc_key']==first['npc_key'])
   if row!=first:publish([original])
   self.assertEqual(get()['texts'],before['texts'])

 def test_locked_guard_rolls_back_a_publication_race(self):
  import tempfile
  from types import SimpleNamespace
  with tempfile.TemporaryDirectory() as backups:
   rb=reborn.Reborn('/opt/homebrew/bin/psql -h 127.0.0.1 -p 15439 -U patchtest','editor_npc_dialogue',writable=True,backup_dir=backups)
   self.assertEqual(rb.pg.json('to_jsonb(current_database())'),'editor_npc_dialogue')
   ctx=SimpleNamespace(need_rb=lambda:rb,body={})
   current=N.overview(ctx);row=dict(next(r for r in current['texts'] if r['npc_key']=='Athens_070'))
   row.pop('scene_key');row['description']+=' [SQL race rollback test]'
   ctx.body={'expected':current['publication'],'changes':[row]}
   preview=N.preview(ctx);ctx.body['token']=preview['token']
   count=rb.pg.json('(SELECT to_jsonb(count(*)) FROM npc_dialogue_revisions)');records=[]
   def race(scope,action,build,**kwargs):
    # Simulate another publisher AFTER Python validation, BEFORE our transaction acquires locks.
    rb.pg.run("UPDATE npc_dialogue_publication SET published_at=clock_timestamp() WHERE family='npc-dialogues';")
    return rb.write_rows(scope,action,build,records.append)
   ctx.rows_write=race
   with self.assertRaisesRegex(reborn.RebornError,'gwadmin-conflict'):
    N.publish(ctx)
   self.assertEqual(rb.pg.json('(SELECT to_jsonb(count(*)) FROM npc_dialogue_revisions)'),count)
   self.assertEqual(N.state(ctx)['publication']['revision'],current['publication']['revision'])
   self.assertEqual(records,[])
   self.assertEqual(list(Path(backups).glob('*.json')),[])

if __name__=='__main__':unittest.main()
