"""Append-only NPC text publishing. Sealed tables stay read-only to general editors.
Every change uses rows_write for database guard/backup/transaction/audit. No row Undo:
restore by selecting an immutable historical release and previewing publication again.
"""
import json
import os
from . import common
from .common import Bad, Conflict, Refused, lit
from . import _npc_dialogue_release as R

M=common.module('npc_dialogue','NPC greetings',icon={'sprite':1063},order=56,status='ready',
 description='Draft NPC greeting text and publish it as a new version. Takes effect after the next server restart.',needs_character=False)
NO_UNDO='Dialogue releases are immutable. Use NPC greetings → History → Load into draft, then preview and publish again.'

def query(ctx,sql):return ctx.need_rb().pg.json(sql)

def bundle(ctx,revision):
 revision=R.revision(revision)
 expressions=["'header',(SELECT to_jsonb(h) FROM npc_dialogue_revisions h WHERE revision=%s)"%lit(revision)]
 for table,cols in R.TABLES.items():
  expressions.append("%s,(SELECT coalesce(json_agg(row_to_json(r)),'[]') FROM (SELECT %s FROM %s WHERE revision=%s) r)"%
   (lit(table),','.join(cols),table,lit(revision)))
 return query(ctx,'json_build_object('+','.join(expressions)+')')

def state(ctx):
 return query(ctx,"""json_build_object('publication',(SELECT to_jsonb(p) FROM npc_dialogue_publication p WHERE family='npc-dialogues'),
 'spawn',(SELECT revision FROM npc_content_publication WHERE family='npcs'),
 'history',(SELECT coalesce(json_agg(to_jsonb(r) ORDER BY created_at DESC),'[]') FROM npc_dialogue_revisions r))""")

def approved(ctx,st,value):
 if not st.get('publication'):raise Refused('No NPC dialogue release is published.')
 if st['spawn']!=R.SPAWN:raise Refused('The published NPC spawns need a newly reviewed editor contract.')
 exists=query(ctx,"to_jsonb(to_regclass('public.npc_dialogue_successor_releases') IS NOT NULL)")
 if not exists:raise Refused('Install the reviewed NPC successor server update before publishing greetings.')
 registrations=query(ctx,"(SELECT coalesce(json_agg(to_jsonb(r)),'[]') FROM npc_dialogue_successor_releases r)")
 root=bundle(ctx,R.REVIEWED);current=bundle(ctx,value)
 R.validate(current,root)
 return current,R.depth(value,registrations)

@M.get('overview')
def overview(ctx):
 st=state(ctx);p=st.get('publication');current=bundle(ctx,p['revision']) if p else None
 reason=None
 try:
  approved(ctx,st,p['revision'] if p else '')
 except (Bad,Refused) as e:reason=str(e)
 return dict(st,database=ctx.need_rb().db,texts=(current or {}).get('npc_dialogue_texts',[]),writable=bool(ctx.need_rb().writable) and reason is None,
  blocked=reason,applies='after_restart',limits={'name_chars':255,'name_bytes':1024,'description_bytes':16384})

@M.get('revision/([0-9A-F]{64})')
def history(ctx,revision):
 st=state(ctx);data,_=approved(ctx,st,revision)
 return {'revision':revision,'texts':data['npc_dialogue_texts']}

def plan(ctx):
 b=ctx.body or {};st=state(ctx);expected=b.get('expected')
 if not isinstance(expected,dict) or expected!=st['publication']:
  raise Conflict('NPC greetings were published again. Reload before previewing.')
 current,chain=approved(ctx,st,expected['revision'])
 target=R.edited(current,b.get('changes'))
 changed=[];old={r['npc_key']:r for r in current['npc_dialogue_texts']}
 for row in target['npc_dialogue_texts']:
  before=old[row['npc_key']]
  if row!=before:changed.append({'npc_key':row['npc_key'],'scene_key':row['scene_key'],'before':before,'after':row})
 if not changed:raise Bad('The draft is identical to the published greetings.')
 revision=R.digest(target);existing=query(ctx,"(SELECT to_jsonb(r) FROM npc_dialogue_revisions r WHERE revision=%s)"%lit(revision))
 if existing:approved(ctx,st,revision)
 elif chain>=64:raise Refused('This successor chain is full. Publish a reviewed historical release before creating another.')
 result={'database':ctx.need_rb().db,'expected':expected,'revision':revision,'changes':changed,'reuse':bool(existing),'spawn':st['spawn']}
 return result,target

@M.post('preview')
def preview(ctx):
 p,_=plan(ctx)
 return dict(p,token=R.token(p),applies='after_restart')

@M.post('publish')
def publish(ctx):
 p,target=plan(ctx)
 if (ctx.body or {}).get('token')!=R.token(p):raise Conflict('The draft changed. Preview it again before publishing.')
 new=p['revision'];old=p['expected']['revision']
 scope=[common.scope('npc_dialogue_publication',"family='npc-dialogues'",pk=['family']),
        common.scope('npc_content_publication',"family='npcs'",pk=['family'])]
 scope += [common.scope(t,{'revision':new},pk=['revision']+({'npc_dialogue_texts':['npc_key'],
  'npc_dialogue_profiles':['profile_key'],'npc_dialogue_bindings':['npc_key','route_order'],
  'npc_dialogue_profile_entries':['profile_key','menu_order']}.get(t,[])))
  for t in ['npc_dialogue_revisions',*R.TABLES,'npc_dialogue_successor_releases']]
 def build(before):
  # Scope locks both publication rows. Recheck full pointer including its timestamp (ABA-safe).
  expected=json.dumps(p['expected'],sort_keys=True)
  sql=["DO $npc$ BEGIN IF (SELECT to_jsonb(p) FROM npc_dialogue_publication p WHERE family='npc-dialogues') IS DISTINCT FROM %s::jsonb OR "
       "(SELECT revision FROM npc_content_publication WHERE family='npcs') IS DISTINCT FROM %s THEN RAISE EXCEPTION 'gwadmin-conflict: NPC publication changed; preview again'; END IF; END $npc$"%(lit(expected),lit(p['spawn']))]
  if not p['reuse']:
   sql.append("INSERT INTO npc_dialogue_revisions(revision,spawn_revision,text_count,profile_count,route_count,menu_entry_count,source) "
    "SELECT %s,spawn_revision,text_count,profile_count,route_count,menu_entry_count,%s FROM npc_dialogue_revisions WHERE revision=%s"%(lit(new),lit(R.PUBLISHER),lit(old)))
   # Text values came from the validated server snapshot; all structure is copied untouched.
   values=[ '(%s,%s,%s,%s,%s)'%(lit(new),*(lit(row[k]) for k in R.TABLES['npc_dialogue_texts'])) for row in target['npc_dialogue_texts']]
   sql.append('INSERT INTO npc_dialogue_texts(revision,npc_key,scene_key,display_name,description) VALUES '+','.join(values))
   for table in ['npc_dialogue_profiles','npc_dialogue_profile_entries','npc_dialogue_bindings']:
    cols=','.join(R.TABLES[table]);sql.append('INSERT INTO %s(revision,%s) SELECT %s,%s FROM %s WHERE revision=%s'%(table,cols,lit(new),cols,table,lit(old)))
   sql.append('INSERT INTO npc_dialogue_successor_releases(revision,predecessor_revision,publisher) VALUES (%s,%s,%s)'%(lit(new),lit(old),lit(R.PUBLISHER)))
  sql.append("UPDATE npc_dialogue_publication SET revision=%s,publisher=%s,published_at=clock_timestamp() WHERE family='npc-dialogues'"%(lit(new),lit(R.PUBLISHER)))
  return sql
 result=ctx.rows_write(scope,'publish NPC greetings %s (%d changed)'%(new[:12],len(p['changes'])),build,fields=[],
  applies='after_restart',target='NPC greetings',no_undo=NO_UNDO)
 result.update(revision=new,reused=p['reuse'],changed=len(p['changes']))
 return result

COLOURS=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),'data','colour_codes.json')

@M.get('colours')
def colours(ctx):
 """The |cAARRGGBB colours the client's English text uses, most used first (built by build_colour_codes.py)."""
 try:
  with open(COLOURS,encoding='utf-8') as f:return json.load(f)
 except (OSError,ValueError):
  return {'colours':[],'total_uses':0,'error':'data/colour_codes.json is missing; run build_colour_codes.py on the Mac'}

# ---- B21 item 20: the whole dialogue (greeting + function page text + yellow option lines). Helpers: _npc_menu.py
from . import _npc_menu as MENU


@M.get('menus')
def menus(ctx):
 """Every published route resolved to the words the client shows, plus the option-text draft (client-side words)."""
 doc=MENU.menu_data()
 if not doc:return {'available':False,'error':'data/npc_menu_text.json is missing; run build_npc_menu_text.py on the Mac'}
 out={}
 for key,rs in MENU.routes(ctx).items():
  out[key]=[dict(r,page=MENU.page(doc,r['dialog_index'],r['subs'],extra=MENU.extensions(key,r['behavior']))) for r in rs]
 draft,version=MENU.read_draft(ctx)
 used={}
 for rs in out.values():
  for r in rs:
   for el in r['page']['texts']+[o['label'] for o in r['page']['options'] if o['label']]+[t for o in r['page']['options'] for t in o['texts']]:
    for k in el.get('keys') or []:used.setdefault(k,set()).add(r['npc_key'])
 strings=doc.get('strings') or {}
 return {'available':True,'client':doc.get('client'),'built':doc.get('generated'),'routes':out,
  'strings':{k:dict(strings[k],npcs=sorted(v)) for k,v in used.items() if k in strings},
  'draft':draft,'draft_version':version,'applies':'next_client_update','quests':MENU.quests_by_npc(ctx.catalog)}


@M.post('menus/draft')
def menus_draft(ctx):
 b=ctx.body or {}
 draft,version=MENU.save_draft(ctx,b.get('labels'),b.get('version'))
 return {'draft':draft,'draft_version':version,'applies':'next_client_update'}


@M.get('menus/export')
def menus_export(ctx):
 doc=MENU.menu_data() or {};draft,version=MENU.read_draft(ctx)
 return {'lua':MENU.export_lua(draft,doc.get('strings') or {}),'draft':draft,'draft_version':version}
