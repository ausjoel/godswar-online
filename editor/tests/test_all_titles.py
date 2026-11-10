import json
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
from modules import all_titles as A, common
from reborn import Reborn


class AllTitlePolicy(unittest.TestCase):
    def test_wonderland_ids_and_names_match_native_policy(self):
        source=(Path(__file__).resolve().parents[2]/'server/src/Godswar.Server/Application/WorldInstances/WonderlandTitleContracts.cs').read_text()
        actual={int(aid):name for aid,name in re.findall(r'\d => new\(\d, (\d+), "([^"]+)"\)',source)}
        self.assertEqual(len(actual),8)
        self.assertEqual(A.WONDERLAND,actual)

    def test_ownership_limit_is_whole_batch_and_earned_titles_are_not_regranted(self):
        class Pg:
            def json(self,query):return []
        rb=SimpleNamespace(writable=True,pg=Pg(),dump=lambda cid:{'character_base':[{'id':cid,'name':'copy','selected_title_id':1000,'medusa_reward_revision':3}]})
        ctx=SimpleNamespace(need_rb=lambda:rb,catalog=SimpleNamespace(file=lambda name:{'1000':{'name':'One'},'1001':{'name':'Two'}}))
        with patch.object(A.T,'support',return_value={'available':True,'owned_limit':128}),patch.object(A.T,'catalogue',return_value=[{'title_id':1000,'display_name':'One'},{'title_id':1001,'display_name':'Two'}]),patch.object(A.T,'rows',return_value=[]),patch.object(A,'owned',return_value=list(range(1000,1128))):
            p=A.plan(ctx,4)
            self.assertFalse(p['changed'])
            self.assertEqual(len(p['owned']),128)
            self.assertEqual(p['selected'],1000)
            ctx.catalog.file=lambda name:{'1000':{},'1001':{},'2000':{}}
            with patch.object(A.T,'catalogue',return_value=[{'title_id':1000,'display_name':'One'},{'title_id':1001,'display_name':'Two'},{'title_id':2000,'display_name':'Three'}]):
                p=A.plan(ctx,4)
                self.assertEqual(p['add'],[2000]);self.assertFalse(p['capacity_ok'])
                self.assertEqual(p['final_count'],129)
                self.assertTrue(all(row['reason'] for row in p['blocked']))


COMMAND=os.environ.get('GODSWAR_ALL_TITLES_PSQL')
DATABASE=os.environ.get('GODSWAR_ALL_TITLES_DB')
MGMT=os.environ.get('GODSWAR_ALL_TITLES_MGMT')


@unittest.skipUnless(COMMAND and DATABASE and MGMT,'requires explicit editor_titles* local copy and compatible management endpoint')
class AllTitlesDatabase(unittest.TestCase):
    def setUp(self):
        args=shlex.split(COMMAND)
        if (Path(args[0]).name!='psql' or '-h' not in args or '-p' not in args or args[args.index('-h')+1] not in ('127.0.0.1','::1')
            or int(args[args.index('-p')+1])<10000 or not DATABASE.startswith('editor_titles') or not 10000<=int(MGMT)<=65535):
            raise ValueError('use only an explicit editor_titles* loopback copy on ports >=10000')
        self.temp=tempfile.TemporaryDirectory();self.fixtures=[];self.changes=[]
        self.rb=Reborn(COMMAND,DATABASE,writable=True,live=False,backup_dir=self.temp.name)
        self.rb.enable_extra_dump_tables()
        self.app=SimpleNamespace(reborn=self.rb,changes=self.fixtures,record_reborn_change=self.fixtures.append,a=SimpleNamespace(rates_mgmt=int(MGMT)),rates=None)
        common.CATALOG.rb=self.rb
        existing=self.rb.pg.json("(SELECT coalesce(json_agg(title_id ORDER BY title_id),'[]') FROM character_title_grants WHERE character_id=4)")
        code,reply=common.dispatch(self.app,'POST','skills/title-grants/4',{},dict(add=[] if 1000 in existing else [1000],remove=[aid for aid in existing if aid!=1000],expected=existing,note='copied-DB fixture; prior grant survives'),{})
        self.assertEqual(code,200,reply)
        self.rb.write(4,'shown-title copied-DB fixture',lambda d:['UPDATE character_base SET selected_title_id=1000, medusa_reward_revision=medusa_reward_revision+1 WHERE id=4'],self.fixtures.append)
        self.app.changes=self.changes;self.app.record_reborn_change=self.changes.append
        self.prior=self.rb.pg.json('(SELECT to_jsonb(g) FROM character_title_grants g WHERE character_id=4 AND title_id=1000)')
        self.before=self.rb.dump(4)

    def tearDown(self):
        self.rb.writable=True
        for c in list(self.changes)[::-1]:
            if not c.get('undo_of') and not c.get('undone'):
                result=self.rb.undo(c,False,self.changes.append,self.changes)
                self.assertTrue(result['result']['all_identical'])
        for c in self.fixtures[::-1]:
            result=self.rb.undo(c,False,lambda _:None,self.fixtures)
            self.assertTrue(result['result']['all_identical'])
        self.temp.cleanup()

    def api(self,method,route,body=None):return common.dispatch(self.app,method,'all_titles/'+route+'/4',{},body,{})
    def preview(self):
        code,p=self.api('GET','preview');self.assertEqual(code,200,p);return p

    def test_preview_accounts_for_every_authored_title_and_blocks_unreviewed_ids(self):
        p=self.preview()
        self.assertEqual(len(p['titles']),96)
        self.assertEqual(len(p['add']),89)
        self.assertEqual(p['owned'],[1000]);self.assertEqual(p['selected'],1000)
        self.assertEqual({row['id'] for row in p['blocked']},set(range(5152,5158)))
        self.assertTrue(all('reviewed' in row['reason'] for row in p['blocked']))
        self.assertEqual(p['final_count'],90)
        self.assertTrue(p['capacity_ok'])
        self.assertFalse(self.changes)

    def test_give_all_preserves_selection_and_existing_grants_and_repeat_is_noop(self):
        p=self.preview();code,r=self.api('POST','apply',{'token':p['token']})
        self.assertEqual(code,200,r);self.assertEqual(r['added'],89)
        self.assertTrue(Path(self.changes[-1]['backup']).is_file())
        after=self.rb.dump(4)
        self.assertEqual(after['character_base'][0]['selected_title_id'],1000)
        self.assertEqual(after['character_base'][0]['medusa_reward_revision'],self.before['character_base'][0]['medusa_reward_revision']+1)
        self.assertEqual(len(after['character_title_grants']),90)
        prior=self.rb.pg.json('(SELECT to_jsonb(g) FROM character_title_grants g WHERE character_id=4 AND title_id=1000)')
        self.assertEqual(prior,self.prior)
        self.assertEqual(self.api('POST','apply',{'token':p['token']})[0],409)
        fresh=self.preview();self.assertFalse(fresh['changed'])
        code,r=self.api('POST','apply',{'token':fresh['token']});self.assertEqual(code,200,r);self.assertTrue(r['unchanged'])
        self.assertEqual(len(self.changes),1)

    def test_stale_selection_and_changed_ownership_refuse_before_write(self):
        p=self.preview()
        self.rb.write(4,'selection changed after preview',lambda d:['UPDATE character_base SET selected_title_id=0, medusa_reward_revision=medusa_reward_revision+1 WHERE id=4'],self.fixtures.append)
        self.assertEqual(self.api('POST','apply',{'token':p['token']})[0],409)
        self.assertFalse(self.changes)

    def test_locked_ownership_guard_catches_race_after_preview_recheck(self):
        p=self.preview()
        # Simulate another edit committed just after request planning, before its write lock.
        self.rb.write(4,'concurrent title change',lambda d:["INSERT INTO character_title_grants(character_id,title_id,granted_by,note) VALUES (4,1001,'test-copy','race')"],self.fixtures.append)
        with patch.object(A,'plan',return_value=p):
            code,r=self.api('POST','apply',{'token':p['token']})
        self.assertEqual(code,409,r);self.assertFalse(self.changes)
        self.assertEqual(len(self.rb.dump(4)['character_title_grants']),2)

    def test_locked_selection_guard_catches_race_after_preview_recheck(self):
        p=self.preview()
        self.rb.write(4,'concurrent title selection',lambda d:['UPDATE character_base SET selected_title_id=0, medusa_reward_revision=medusa_reward_revision+1 WHERE id=4'],self.fixtures.append)
        with patch.object(A,'plan',return_value=p):
            code,r=self.api('POST','apply',{'token':p['token']})
        self.assertEqual(code,409,r)
        self.assertIn('ownership or selection',r['error'])
        self.assertFalse(self.changes)
        self.assertEqual(len(self.rb.dump(4)['character_title_grants']),1)

    def test_readonly_online_and_unavailable_capability_refuse(self):
        p=self.preview();self.rb.writable=False
        self.assertEqual(self.api('POST','apply',{'token':p['token']})[0],403)
        self.rb.writable=True
        with patch.object(common,'online_info',return_value={'online':True,'name':'the admin','reason':'test online'}):
            self.assertEqual(self.api('POST','apply',{'token':p['token']})[0],423)
        with patch.object(A.T,'support',return_value={'available':False,'reason':'compatible title-grant update required'}):
            self.assertEqual(self.api('GET','preview')[0],403)
        self.assertFalse(self.changes)
