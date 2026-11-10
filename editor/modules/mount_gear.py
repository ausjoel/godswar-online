"""Epic G10 mount-gear selection and offline stat-preserving morph/copy."""
import hashlib
import json

from . import common, inventory as I
from .common import Bad, Conflict, NotFound, Refused

M=common.module('mount_gear','Mount gear: stats & morph',icon={'sprite':811},order=23,status='ready',needs_character=True,
                description='Choose up to five Epic G10 mount-gear stats, or morph/copy a mount piece while keeping its stats. Offline, previewed, with Undo.')
KINDS={'mounthead','mountarmor','mountsoul','mountornament','mountamulet'}
# Epic quality is the eighth native strength family (base + 7); amounts use grade 10.
FAMILIES=[('patk',347,'Physical attack'),('matk',357,'Magic attack'),('pdmg',367,'Physical damage'),
          ('mdmg',377,'Magic damage'),('hp',307,'Max HP'),('mp',317,'Max MP'),('hit',327,'Hit rating'),
          ('dodge',337,'Dodge rating'),('hpregen',387,'HP recovery'),('mpregen',397,'MP recovery'),
          ('ignorep',407,'Ignore physical defense'),('ignorem',417,'Ignore magic defense'),
          ('flatp',427,'Extra physical damage'),('flatm',437,'Extra magic damage'),
          ('critpct',447,'Critical damage'),('critflat',457,'Extra critical damage')]


def stat_options(rb):
    out=[]
    for key,aid,name in FAMILIES:
        definition=rb.cat()['attributes'].get(str(aid)) or {}
        values=definition.get('values') or []
        if len(values)<10:
            raise Bad('missing Epic G10 attribute data for #%d'%aid)
        amount=values[9];percent=bool(definition.get('percent'))
        out.append({'key':key,'id':aid,'name':name,'value':amount,'percent':percent,
                    'display':('%.1f%%'%(float(amount)*100)) if percent else str(amount)})
    return out


def item_meta(rb,pid):
    meta=rb.item_meta(pid)
    if not meta.get('server') or not meta.get('client') or meta.get('kind') not in KINDS:
        raise Bad('choose a mount-gear template present in both server and client')
    return meta


@M.get(r'gear/(\d+)')
def gear(ctx,cid):
    rb,cid,d=I._cid_check(ctx,cid)
    rows=[]
    for row in I._items(d):
        meta=rb.item_meta(row['prop_id'])
        if meta.get('kind') in KINDS and row['item_location'] in (0,1,3):
            rows.append({'location':row['item_location'],'slot':row['slot_index'],'name':meta['name'],
                         'kind':meta['kind'],'row':{k:row.get(k) for k in I.CMP_FIELDS}})
    targets=[{'id':int(k),'name':v.get('name'),'kind':v.get('kind'),'min_level':v.get('minlv')}
             for k,v in rb.cat()['items'].items() if v.get('kind') in KINDS and str(k) in ctx.catalog.file('items')]
    return {'items':rows,'targets':sorted(targets,key=lambda t:(t['kind'],t['min_level'] or 0,t['id'])),
            'stats':stat_options(rb),'writable':bool(rb.writable),'max_stats':5}


def plan(ctx,cid,body):
    if not isinstance(body,dict):raise Bad('request must be an object')
    rb,cid,d=I._cid_check(ctx,cid)
    loc,slot=I._place(body,'source cell');I._place_rules(d,loc,slot)
    source=I._find(d,loc,slot)
    if not source:raise NotFound('the source cell is empty; reload its items')
    item_meta(rb,source['prop_id'])
    mode=body.get('mode','edit')
    if mode not in ('edit','copy'):raise Bad('choose edit or copy')
    pid=common.to_int(body.get('target',source['prop_id']),1,I.I32,'target item')
    target_meta=item_meta(rb,pid)
    epic=body.get('epic_g10',False)
    if not isinstance(epic,bool):raise Bad('epic_g10 must be true or false')
    item={k:source.get(k) for k in I.ITEM_FIELDS};item['prop_id']=pid
    selected=[]
    if epic:
        keys=body.get('stats')
        if not isinstance(keys,list) or not 1<=len(keys)<=5 or any(not isinstance(k,str) for k in keys):
            raise Bad('choose 1–5 distinct Epic G10 stats')
        if len(set(keys))!=len(keys):raise Bad('the same stat family cannot be selected twice')
        options={s['key']:s for s in stat_options(rb)}
        if any(k not in options for k in keys):raise Bad('unknown Epic G10 stat family')
        selected=[options[k] for k in keys]
        item.update(item_quality=8,item_grade=10)
        for n in range(1,6):
            item['attribute%d'%n]=selected[n-1]['id'] if n<=len(selected) else None
            item['attribute_level%d'%n]=None
    target_loc,target_slot=loc,slot
    if mode=='copy':
        target_loc=I.LOC_BAG
        free=[s for s in range(I.BAG_SLOTS) if I._find(d,I.LOC_BAG,s) is None]
        if not free:raise Bad('the bag has no empty cell for the copy')
        target_slot=free[0]
    I._place_rules(d,target_loc,target_slot)
    for n in range(1,7):
        effect=item.get('holy_socket%d_effect_id'%n)
        if effect not in (None,0,21,22,23,24):
            raise Bad('mount gear accepts Zephyr spirit effects only; correct the source sockets first')
    _pid,meta,warnings=I._check_item(rb,d,target_loc,target_slot,item,False,False)
    I._values(rb,item,source if mode=='edit' else None,False)
    I.R.check_place(rb,d,target_loc,target_slot,meta,False)
    expected=[I._expected(cid,loc,slot,source)]
    if mode=='copy':expected.append(I._expected(cid,target_loc,target_slot,None))
    base=d['character_base'][0]
    expected.append({'table':'character_base','where':{'id':cid},'values':{k:base[k] for k in ('inventory_revision','profession','fighter_job_lv')}})
    options={'location':loc,'slot':slot,'mode':mode,'target':pid,'epic_g10':epic}
    if epic:options['stats']=body['stats']
    out={'source_name':rb.item_meta(source['prop_id'])['name'],'name':target_meta['name'],'before':{k:source.get(k) for k in I.CMP_FIELDS},
         'after':item,'destination':{'location':target_loc,'slot':target_slot},'options':options,'selected':selected,'warnings':warnings,
         'changed':mode=='copy' or any(source.get(k)!=v for k,v in item.items()),'writable':bool(rb.writable),'applies':'next_login'}
    projected=[]
    for n in range(1,6):
        aid=item.get('attribute%d'%n)
        if aid is None:continue
        definition=rb.cat()['attributes'].get(str(aid)) or {}
        values=definition.get('values') or []
        if not values:raise Bad('missing projected values for attribute #%s'%aid)
        amount=values[min(max(1,int(item['item_grade']))-1,len(values)-1)]
        percent=bool(definition.get('percent'))
        name=next((name for _key,epic_id,name in FAMILIES if epic_id//10==int(aid)//10),definition.get('key') or str(aid))
        projected.append({'id':aid,'level':item.get('attribute_level%d'%n),'name':name,'value':amount,'percent':percent,
                          'display':('%.1f%%'%(float(amount)*100)) if percent else str(amount)})
    out['attributes']=projected
    out['token']=hashlib.sha256(json.dumps({'plan':out,'expected':expected},sort_keys=True,default=str).encode()).hexdigest()
    return out,expected


@M.post(r'preview/(\d+)')
def preview(ctx,cid):return plan(ctx,cid,ctx.body)[0]


@M.post(r'apply/(\d+)')
def apply(ctx,cid):
    rb=ctx.need_rb()
    if not rb.writable:raise Refused('this editor is read-only')
    view,expected=plan(ctx,cid,ctx.body)
    if ctx.body.get('token')!=view['token']:raise Conflict('source, destination or options changed; preview again')
    if not view['changed']:raise Bad('this mount piece already matches the preview')
    cid=int(cid);dest=view['destination'];item=view['after'];mode=view['options']['mode']
    def build(d):
        old=I._find(d,dest['location'],dest['slot'])
        values,meta=I._values(rb,item,old,False)
        # Preserve NULL versus explicit enhancement levels exactly on morph/copy.
        for n in range(1,6):values['attribute_level%d'%n]=item.get('attribute_level%d'%n)
        I.R.check_place(rb,d,dest['location'],dest['slot'],meta,False)
        if mode=='edit':
            if not old:raise Conflict('source cell changed; preview again')
            sql=['UPDATE character_items SET %s,updated_at=now() WHERE id=%d AND user_id=%d'%
                 (', '.join('%s=%s'%(common.ident(k),common.lit(v)) for k,v in values.items()),old['id'],cid)]
        else:
            if old:raise Conflict('copy destination is occupied; preview again')
            sql=[common.insert_sql('character_items',dict(user_id=cid,item_location=dest['location'],slot_index=dest['slot'],**values))]
        sql += [I.R._audit(cid,'update' if mode=='edit' else 'create',dest['location'],dest['slot'],values,old),
                'UPDATE character_base SET inventory_revision=inventory_revision+1 WHERE id=%d'%cid]
        return sql
    result=I._write(ctx,cid,'mount gear %s: %s → %s'%(mode,view['source_name'],view['name']),build,expected)
    result.update(destination=dest,warnings=view['warnings'])
    return result
