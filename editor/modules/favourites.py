"""Reusable item snapshots and atomic offline batches. No instance/owner identifiers are cloned."""
import json,os,secrets,sqlite3,time,uuid
from contextlib import contextmanager
from . import common,inventory as inv
from .common import Bad,Conflict,NotFound,Refused,OnlineRefused
M=common.module('favourites','Favourites & copying',icon={'sprite':684},order=21,status='ready',
 description='Save selected items as reusable favourites; preview and copy batches into a player’s bags or warehouse.',needs_character=True)
MAX_ITEMS=96
PLAIN_FIELDS=('prop_id','stack','bound','item_quality','item_grade')

def plain_items(raw):
 """New items picked in the editor: only the fields a live give can also carry, never instance data."""
 if not isinstance(raw,list) or not 1<=len(raw)<=MAX_ITEMS:raise Bad('Select 1–96 items')
 out=[]
 for row in raw:
  if not isinstance(row,dict):raise Bad('Each item must be an object')
  item={k:row[k] for k in PLAIN_FIELDS if row.get(k) is not None}
  item['prop_id']=common.to_int(item.get('prop_id'),1,inv.I32,'item')
  out.append(item)
 return out

class Store:
 def __init__(self,path):
  self.path=path;os.makedirs(os.path.dirname(path),exist_ok=True)
  with self.db() as c:
   c.execute('CREATE TABLE IF NOT EXISTS favourites (id TEXT PRIMARY KEY,name TEXT NOT NULL,items TEXT NOT NULL,created REAL NOT NULL,revision INTEGER NOT NULL DEFAULT 1,archived INTEGER NOT NULL DEFAULT 0)')
   c.execute('CREATE TABLE IF NOT EXISTS previews (id TEXT PRIMARY KEY,plan TEXT NOT NULL,expires REAL NOT NULL,change_id TEXT)')
  os.chmod(path,0o600)
 @contextmanager
 def db(self):
  c=sqlite3.connect(self.path,timeout=10);c.row_factory=sqlite3.Row
  try:
   with c:yield c
  finally:c.close()
 def list(self):
  with self.db() as c:rows=c.execute('SELECT * FROM favourites WHERE archived=0 ORDER BY name,id').fetchall()
  return [dict(id=r['id'],name=r['name'],items=json.loads(r['items']),created=r['created'],revision=r['revision']) for r in rows]
 def get(self,id):
  with self.db() as c:r=c.execute('SELECT * FROM favourites WHERE id=? AND archived=0',(id,)).fetchone()
  if not r:raise NotFound('Favourite no longer exists')
  return dict(id=r['id'],name=r['name'],items=json.loads(r['items']),revision=r['revision'])
 def save(self,name,items):
  name=str(name or '').strip()
  if not name or len(name)>80:raise Bad('Favourite name must be 1–80 characters')
  if not 1<=len(items)<=MAX_ITEMS:raise Bad('Select 1–96 item rows')
  id=str(uuid.uuid4())
  with self.db() as c:c.execute('INSERT INTO favourites(id,name,items,created) VALUES(?,?,?,?)',(id,name,json.dumps(items),time.time()))
  return self.get(id)
 def archive(self,id,revision):
  with self.db() as c:
   if c.execute('UPDATE favourites SET archived=1,revision=revision+1 WHERE id=? AND revision=? AND archived=0',(id,revision)).rowcount!=1:raise Conflict('Favourite changed; refresh before removing it')
 def preview(self,plan):
  id=secrets.token_urlsafe(24)
  with self.db() as c:
   c.execute('DELETE FROM previews WHERE expires<? AND change_id IS NULL',(time.time()-86400,))
   c.execute('INSERT INTO previews(id,plan,expires) VALUES(?,?,?)',(id,json.dumps(plan),time.time()+900))
  return id
 def plan(self,id):
  with self.db() as c:r=c.execute('SELECT * FROM previews WHERE id=?',(id,)).fetchone()
  if not r:raise NotFound('Preview not found; preview the batch again')
  if not r['change_id'] and r['expires']<time.time():raise Conflict('Preview expired; preview the batch again')
  return json.loads(r['plan']),r['change_id']
 def complete(self,id,change_id):
  with self.db() as c:c.execute('UPDATE previews SET change_id=? WHERE id=?',(change_id,id))

def store(ctx):return Store(os.path.join(ctx.app.a.backup_dir,'item-favourites.sqlite3'))
def offline(ctx,cid):
 info=common.online_info(ctx.app,cid)
 if info['online']:raise OnlineRefused('Batch snapshots and copying require the character logged out; atomic online batches are not supported yet.',info)
def snapshot(ctx):
 rb,cid,d=inv._cid_check(ctx,common.to_int(ctx.body.get('source'),1,None,'source character'))
 offline(ctx,cid)
 rev=common.to_int(ctx.body.get('source_revision'),0,None,'source inventory revision')
 if int(d['character_base'][0]['inventory_revision'])!=rev:raise Conflict('Source inventory changed; reload its items')
 cells=ctx.body.get('selection')
 if not isinstance(cells,list) or not 1<=len(cells)<=MAX_ITEMS:raise Bad('Select 1–96 item rows')
 result=[];seen=set()
 for cell in cells:
  loc,slot=inv._place(cell,'source cell');inv._place_rules(d,loc,slot)
  if (loc,slot) in seen:raise Bad('The same source cell was selected twice')
  seen.add((loc,slot));row=inv._find(d,loc,slot)
  if not row or row['id']!=common.to_int(cell.get('id'),1,None,'source item id'):raise Conflict('A selected item changed; reload its items')
  if inv._sealed(d,row):raise Refused('A sealed pet must be copied through pet operations, not as an item')
  item={k:row[k] for k in inv.ITEM_FIELDS if k in row}
  result.append(item)
 return result

def make_plan(rb,d,items,location,copies):
 if location not in (inv.LOC_BAG,inv.LOC_WH):raise Bad('Batch destinations are bags or warehouse')
 copies=common.to_int(copies,1,96,'copies')
 if not items or len(items)*copies>MAX_ITEMS:raise Bad('A batch may contain at most 96 item rows')
 cap=inv.BAG_SLOTS if location==inv.LOC_BAG else min(int(d['character_base'][0].get('warehouse_capacity') or inv.WH_BOX),inv.WH_BOX*inv.WH_BOXES)
 free=[s for s in range(cap) if inv._find(d,location,s) is None]
 if len(free)<len(items)*copies:raise Bad('Not enough empty destination cells for the complete batch')
 planned=[];warnings=[]
 for item,slot in zip(items*copies,free):
  item=dict(item);inv._place_rules(d,location,slot)
  pid,meta,w=inv._check_item(rb,d,location,slot,item,False,False)
  if not meta['server'] or not meta['client']:raise Bad('Item %d needs matching server and client data before batch copying'%pid)
  # Enforce stack limits and all existing item-column checks during preview as well as apply.
  inv._values(rb,item,None,False)
  planned.append(dict(location=location,slot=slot,item=item,name=meta['name']));warnings.extend(w)
 return planned,sorted(set(warnings))

@M.get('list')
def list_favourites(ctx):return {'favourites':store(ctx).list()}

@M.post('capture')
def capture(ctx):return {'favourite':store(ctx).save(ctx.body.get('name'),snapshot(ctx))}

@M.post('archive')
def archive(ctx):
 store(ctx).archive(str(ctx.body.get('id')),common.to_int(ctx.body.get('revision'),1,None,'revision'))
 return {'archived':True}

@M.post('preview')
def preview(ctx):
 rb,cid,d=inv._cid_check(ctx,common.to_int(ctx.body.get('target'),1,None,'target character'));offline(ctx,cid)
 fav=ctx.body.get('favourite')
 items=plain_items(ctx.body['items']) if 'items' in ctx.body else store(ctx).get(str(fav))['items'] if fav else snapshot(ctx)
 location=common.to_int(ctx.body.get('location',1),1,3,'destination')
 planned,warnings=make_plan(rb,d,items,location,ctx.body.get('copies',1))
 plan=dict(database=rb.db,target=cid,revision=int(d['character_base'][0]['inventory_revision']),items=planned)
 if 'items' in ctx.body:plan['label']='add %d items'%len(planned)
 token=store(ctx).preview(plan)
 return dict(preview=token,target=cid,character=d['character_base'][0]['name'],items=planned,warnings=warnings,expires_seconds=900)

@M.post('apply')
def apply(ctx):
 st=store(ctx);token=str(ctx.body.get('preview') or '');plan,receipt=st.plan(token)
 if receipt:return dict(already_applied=True,change_id=receipt,change=None)
 rb=ctx.need_rb();cid=int(plan['target'])
 if plan['database']!=rb.db:raise Conflict('Database changed since preview')
 offline(ctx,cid)
 expected=[dict(table='character_base',where={'id':cid},values={'inventory_revision':plan['revision']})]
 for p in plan['items']:expected.append(inv._expected(cid,p['location'],p['slot'],None))
 def build(d):
  if int(d['character_base'][0]['inventory_revision'])!=plan['revision']:raise Conflict('Destination inventory changed; preview again')
  stm=[]
  for p in plan['items']:
   if inv._find(d,p['location'],p['slot']) is not None:raise Conflict('A destination cell is occupied; preview again')
   inv._place_rules(d,p['location'],p['slot'])
   _,meta,_=inv._check_item(rb,d,p['location'],p['slot'],p['item'],False,False)
   if not meta['server'] or not meta['client']:raise Bad('Client/server item data changed since preview')
   stm.extend(inv._build_set(rb,cid,p['location'],p['slot'],p['item'],False)(d))
  return stm
 r=inv._write(ctx,cid,plan.get('label') or 'copy item batch (%d rows)'%len(plan['items']),build,expected)
 st.complete(token,r['change']['id']);r['copied']=len(plan['items']);return r

@M.post('give')
def give(ctx):
 """Online character: give picked items one at a time through the running server. Everything is validated first, so a
 bad row gives nothing. Not atomic after that: a refusal stops the run and the answer lists what was and was not given."""
 rb,cid,d=inv._cid_check(ctx,common.to_int(ctx.body.get('target'),1,None,'target character'))
 if not common.online_info(ctx.app,cid)['online']:raise Conflict('The character is no longer online; review the batch again')
 items=plain_items(ctx.body.get('items'))
 free=[s for s in range(inv.BAG_SLOTS) if inv._find(d,inv.LOC_BAG,s) is None]
 if len(free)<len(items):raise Bad('The bag has %d free cells; %d needed. Nothing was given.'%(len(free),len(items)))
 warnings=[]
 for item in items:
  pid,meta,w=inv._check_item(rb,d,inv.LOC_BAG,free[0],item,False,False)
  if not meta['server'] or not meta['client']:raise Bad('Item %d needs matching server and client data. Nothing was given.'%pid)
  inv._values(rb,item,None,False);warnings.extend(w)
 given=[]
 for n,item in enumerate(items):
  name=rb.item_meta(item['prop_id'])['name']
  try:
   args={'item':item['prop_id'],'quantity':item.get('stack',1)}
   for field,arg in (('item_quality','quality'),('item_grade','grade'),('bound','bound')):
    if field in item:args[arg]=item[field]
   ctx.bag_item(cid,'add',args)
  except (Bad,Conflict,Refused,OnlineRefused) as e:
   return dict(mode='live',given=given,failed=dict(name=name,error=str(e)),not_attempted=[rb.item_meta(i['prop_id'])['name'] for i in items[n+1:]],warnings=sorted(set(warnings)))
  given.append(name)
 return dict(mode='live',given=given,failed=None,not_attempted=[],warnings=sorted(set(warnings)))
