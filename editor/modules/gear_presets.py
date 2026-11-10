"""Offline worn-gear presets; exact class-suit mapping, native stats and socket values.

Operator edits consume no crafting materials. Preview and commit share one plan;
all twelve ordinary equipment cells and profession/revision are checked atomically.
"""
import hashlib
import json
from pathlib import Path

from . import common, gearstats as G, inventory as INV
from .common import Bad, Conflict, Refused

M = common.module('gear_presets', 'Worn gear presets', icon={'sprite':811}, order=22, status='ready',
                  needs_character=True, description='Preview four-stat/Platinum 10 or Q20/G25 five-stat presets on worn gear, with class-aware stones and one Undo.')
BRANCHES = json.loads((Path(__file__).resolve().parents[1]/'data/class-suit-branches.json').read_text())['branches']
HEATED = {0,2,8,9,10}
COOLED = {1,3,4,5,6,7,11}


def inputs(body):
    if not isinstance(body,dict):
        raise Bad('preset options must be an object')
    preset=body.get('preset','standard')
    if preset not in ('standard','boundless'):
        raise Bad('choose standard or boundless')
    tier=common.to_int(body.get('class_tier',0),0,4,'class suit tier')
    attr=body.get('class_stat',False)
    if not isinstance(attr,bool):
        raise Bad('class_stat must be true or false')
    if attr and tier in (1,2):
        raise Bad('class-specific stats require Class Suit III or IV')
    return {'preset':preset,'class_tier':tier,'class_stat':attr}


def suit_target(item_id, profession, tier):
    # Restrict conversion to the exact authored branch; never infer it from names or slot alone.
    for branch in BRANCHES:
        if branch['profession']==profession and item_id in branch['common']+branch['tiers']:
            old_tier=branch['tiers'].index(item_id)+1 if item_id in branch['tiers'] else 0
            return (branch['tiers'][max(tier,old_tier)-1] if tier else item_id), old_tier
    if tier:
        raise Bad('item #%s has no class-suit conversion branch for %s; turn off conversion or equip its authored common/class-suit piece' % (item_id,common.CLASS_NAMES.get(profession,profession)))
    return item_id,0


def stone_rows(slot, magical, count, balance):
    # Same 5% damage / 8% ignore-defense for physical and magical builds.
    if slot in HEATED:
        effects=[4 if magical else 3,2 if magical else 1,7,6 if magical else 5]
        values=[500,800,600,300 if magical else 400]
    elif slot in COOLED:
        effects=[9,10,13,19]
        values=[min(INV.value_range(e,10,balance)[1] for e in (9,10))]*2
        values += [INV.value_range(13,10,balance)[1],400]
    else:
        raise Bad('this slot has no legal heated/cooled assignment')
    result=[]
    for effect,value in zip(effects[:count],values[:count]):
        lo,hi=INV.value_range(effect,10,balance)
        if not lo<=value<=hi:
            raise Bad('current holy-spirit balance has no supported preset value for effect %s' % effect)
        result.append({'effect':effect,'level':10,'value':value,'name':common.CATALOG.file('holystones')[str(effect)]['name'],
                       'percent':INV.SPIRITS[effect][0]=='percent'})
    return result


def attributes(ctx, entry, magical, count, grade):
    typ,why,legal=G.options(ctx,entry)
    if not typ:
        raise Bad(why)
    order=(['matk','mdmg','crit','hit','maxmp','maxhp'] if magical else ['patk','pdmg','crit','hit','maxhp','maxmp']) if entry.get('slot') in HEATED else ['pdef','mdef','absorb','maxhp','maxmp','dodge','critres','hpregen','mpregen']
    rank={key:n for n,key in enumerate(order)}
    chosen=sorted(legal,key=lambda a:rank.get(a['key'],100))[:count]
    if len(chosen)!=count:
        raise Bad('not enough legal ordinary stat families on this item')
    out=[]
    for option in chosen:
        level=option['levels'][-1]
        # Quartz enhancement rank is separate from amount: character_equip
        # projects every ordinary stat value from item_grade.
        stored=level['level'] if len(option['levels'])>1 else None
        definition=ctx.rb.cat()['attributes'].get(str(level['id']))
        if not definition:
            raise Bad('missing server attribute #%s' % level['id'])
        values=definition.get('values') or []
        if not values:
            raise Bad('server attribute #%s has no value table' % level['id'])
        index=max(1,grade)-1
        value=values[min(index,len(values)-1)] if values else None
        out.append({'id':level['id'],'level':stored,'name':option['label'],'value':value,'percent':bool(definition.get('percent'))})
    return out


def plan(ctx,cid,body):
    options=inputs(body)
    rb,cid,d=INV._cid_check(ctx,cid)
    base=d['character_base'][0]
    profession=int(base['profession'])
    if profession not in range(4):
        raise Bad('character has an unsupported profession')
    magical=profession in (2,3)
    balance=INV._balance(rb)
    result=[]; skipped=[]; expected=[]
    for slot in range(12):
        old=INV._find(d,INV.LOC_EQUIP,slot)
        expected.append(INV._expected(cid,INV.LOC_EQUIP,slot,old))
        if not old:
            continue
        entry=G._item(ctx,old['prop_id'])
        if not entry or entry.get('kind') not in INV.SLOTS[slot][1]:
            raise Bad('worn slot %s has an unknown or mismatched item' % slot)
        item={k:old.get(k) for k in INV.ITEM_FIELDS}
        target,old_tier=suit_target(old['prop_id'],profession,options['class_tier'])
        target_tier=max(options['class_tier'],old_tier)
        item['prop_id']=target
        target_entry=G._item(ctx,target)
        if options['preset']=='boundless':
            item.update(item_quality=20,item_grade=25)
        item['holy_suit_code']=410
        count=5 if options['preset']=='boundless' else 4
        try:
            stats=attributes(ctx,target_entry,magical,count,int(item['item_grade']))
        except Bad as error:
            raise Bad('%s: %s' % (entry.get('name',old['prop_id']),error))
        for n in range(1,6):
            stat=stats[n-1] if n<=len(stats) else {}
            item['attribute%d'%n]=stat.get('id');item['attribute_level%d'%n]=stat.get('level')
        if options['class_stat']:
            if target_tier<3 or target not in INV.class_suit_items():
                raise Bad('%s needs Class Suit III/IV before adding its class stat; choose conversion tier III/IV or equip that suit' % entry.get('name',target))
            item['class_attribute1']=220 if magical else 200
        stone_count=4 if options['preset']=='boundless' else 2
        stones=stone_rows(slot,magical,stone_count,balance)
        item['holy_socket_count']=stone_count
        for n in range(1,7):
            stone=stones[n-1] if n<=len(stones) else {}
            item['holy_socket%d_effect_id'%n]=stone.get('effect')
            item['holy_socket%d_level'%n]=stone.get('level')
            if n<=4:item['holy_socket%d_value'%n]=stone.get('value')
        _pid,_meta,warnings=INV._check_item(rb,d,INV.LOC_EQUIP,slot,item,False,False)
        INV.R.check_place(rb,d,INV.LOC_EQUIP,slot,_meta,False)
        class_stat=None
        if item.get('class_attribute1') is not None:
            aid=item['class_attribute1'];definition=rb.cat()['attributes'].get(str(aid)) or {}
            vals=definition.get('values') or []
            class_stat={'id':aid,'name':(ctx.catalog.file('appendattrs').get(str(aid)) or {}).get('text') or definition.get('key'),
                        'value':vals[min(max(1,int(item['item_grade']))-1,len(vals)-1)] if vals else None,'percent':bool(definition.get('percent'))}
        result.append({'slot':slot,'slot_name':INV.SLOTS[slot][0],'before':{k:old.get(k) for k in INV.CMP_FIELDS},
                       'after':item,'name':target_entry.get('name',str(target)),'stats':stats,'stones':stones,'class_stat':class_stat,'changed':any(old.get(k)!=v for k,v in item.items()),'warnings':warnings})
    for old in INV._items(d):
        if old['item_location']==INV.LOC_EQUIP and old['slot_index'] not in range(12):
            skipped.append({'slot':old['slot_index'],'item':old['prop_id'],'reason':'Fashion, mounts and mount gear use separate systems.'})
    if not result:
        raise Bad('no worn armor or weapons in the twelve ordinary equipment slots')
    expected.append({'table':'character_base','where':{'id':cid},'values':{'profession':profession,'inventory_revision':base['inventory_revision']}})
    token=hashlib.sha256(json.dumps({'cid':cid,'options':options,'expected':expected,'rows':result,'balance':balance},sort_keys=True,default=str).encode()).hexdigest()
    return {'character':{'id':cid,'name':base['name'],'class':common.CLASS_NAMES[profession]},'options':options,'items':result,
            'skipped':skipped,'token':token,'writable':bool(rb.writable),'applies':'next_login'},expected


@M.post(r'preview/(\d+)')
def preview(ctx,cid):
    return plan(ctx,cid,ctx.body)[0]


@M.post(r'apply/(\d+)')
def apply(ctx,cid):
    if not ctx.need_rb().writable:
        raise Refused('this editor is read-only for the selected database')
    view,expected=plan(ctx,cid,ctx.body)
    if ctx.body.get('token')!=view['token']:
        raise Conflict('gear, profession, options or stone balance changed; preview again before applying')
    if all(all(row['before'].get(k)==v for k,v in row['after'].items()) for row in view['items']):
        raise Bad('all worn gear already matches this preset')
    rb=ctx.need_rb();cid=int(cid)
    def build(d):
        statements=[]
        for row in view['items']:
            old=INV._find(d,INV.LOC_EQUIP,row['slot'])
            if old is None:
                raise Conflict('a worn slot changed; preview again')
            values,meta=INV._values(rb,row['after'],old,False)
            # Inventory's generic builder defaults absent levels to 1. Here a
            # non-levelled stat keeps NULL; amount is always projected at item_grade.
            for n in range(1,6):
                if row['after'].get('attribute_level%d'%n) is None:
                    values['attribute_level%d'%n]=None
            INV.R.check_place(rb,d,INV.LOC_EQUIP,row['slot'],meta,False)
            statements.append('UPDATE character_items SET %s, updated_at=now() WHERE id=%d AND user_id=%d' %
                              (', '.join('%s=%s'%(common.ident(k),common.lit(v)) for k,v in values.items()),old['id'],cid))
            statements.append(INV.R._audit(cid,'update',INV.LOC_EQUIP,row['slot'],values,old))
        statements += ['UPDATE character_base SET inventory_revision=inventory_revision+1 WHERE id=%d'%cid,
                       'SELECT public.recompute_character_holy_suit_points(%d)'%cid]
        return statements
    result=INV._write(ctx,cid,'worn gear %s preset: %d items'%(view['options']['preset'],len(view['items'])),build,expected)
    result.update(items=len(view['items']),warnings=[w for row in view['items'] for w in row['warnings']])
    return result
