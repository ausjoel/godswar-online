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
                         'kind':meta['kind'],'row':{k:row.get(k) for k in I.CMP_FIELDS},
                         'sprite':(ctx.catalog.file('items').get(str(row['prop_id'])) or {}).get('sprite')})
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


# =========================================================================== W23: mount gear adder (the admin 24 Nov)
# "Should have an auto mount gear adder - set level of mount and mount gear. Auto best stats??? Or set the four stats
# to be copied for all 5 pieces." One preview + one write: the mount in the Mount slot at the chosen level, and all five
# mount-gear pieces (Coronet, Armor, Soul, Saddle, Tassels) at the chosen gear level, quality and grade, each with four
# stats. The W18 / B27 Mount Feeder rules (GameClientHandler.MountFeeder.cs):
#   * a mount family has one item per level 40, 50 ... 120 (catalog-origin/mount-families.json, from the server's
#     DeveloperMountCatalog.cs);
#   * a gear line is base + (level - 40) / 10: Boorish Lv40 (x00) ... Faithcreed Lv120 (x08); every step needs 10 more
#     levels, and gear from the Feeder is bound;
#   * the game's equip rule (EquipmentEligibility.cs): character level >= mount level >= gear level.
# Legal stats come from the server table item_allowed_attributes of each target item: attribute id = kind * 10 + tier,
# and the tiers a piece allows rise with its level (Lv40 tier 0, Lv80 tiers 1-3, Lv120 tiers 5-7). "Best" for a kind is
# its highest allowed tier on that piece.
GEAR_LINES = {15: 14500, 16: 14600, 17: 14700, 18: 14800, 19: 14900}   # slot -> line base (MountFamilyCatalog.IsMountGear)
GEAR_SLOT_NAMES = {15: 'Coronet', 16: 'Armor', 17: 'Soul', 18: 'Saddle', 19: 'Tassels'}
LEVELS = list(range(40, 121, 10))
KIND_OF = {key: aid // 10 for key, aid, _ in FAMILIES}                  # 'patk' -> 34
# GENERATED default (the admin's question): the four kinds "Auto best stats" picks. Physical classes take the four stats of
# the level-80 Warrior test set in server/docs/mount-quality-grade.md (attack 34x, physical damage 36x, ignore physical
# defense 40x, extra physical damage 42x); magic classes take the magic twins.
CLASS_STATS = {0: ['patk', 'pdmg', 'ignorep', 'flatp'], 1: ['patk', 'pdmg', 'ignorep', 'flatp'],
               2: ['matk', 'mdmg', 'ignorem', 'flatm'], 3: ['matk', 'mdmg', 'ignorem', 'flatm']}
ADDER_STATS = 4


def gear_id(slot, level):
    """The mount-gear item of a line at a level (40..120 in steps of 10)."""
    if slot not in GEAR_LINES:
        raise Bad('slot %s is not a mount-gear slot' % slot)
    if level not in LEVELS:
        raise Bad('mount gear levels are 40, 50 ... 120')
    return GEAR_LINES[slot] + (level - 40) // 10


def mount_families():
    """[{id, name, levels: {40: item ... 120: item}, ids: {item: level}}] from catalog-origin/mount-families.json."""
    out = []
    for f in common.CATALOG.file('mount-families').get('families') or []:
        levels, ids = {}, {}
        for v in f.get('variants') or []:
            t = v.get('tier')
            lv = 120 if t in ('max', 'special') else int(t) if str(t).isdigit() else None
            if lv is None:
                continue
            if t != 'special':
                levels[lv] = int(v['id'])
            ids[int(v['id'])] = lv
        if all(lv in levels for lv in LEVELS):
            out.append({'id': f['id'], 'name': f['name'], 'levels': levels, 'ids': ids})
    return out


def family_of(families, pid):
    return next((f for f in families if int(pid) in f['ids']), None)


def best_ids(allowed, kinds):
    """allowed: attribute ids a piece allows; kinds: [kind] -> [highest allowed id of each kind] (Bad if one is missing)."""
    out = []
    for k in kinds:
        ids = [a for a in allowed if a // 10 == k]
        if not ids:
            raise Bad('stat kind %d is not allowed on this piece' % k)
        out.append(max(ids))
    return out


def allowed_attributes(rb, ids):
    ids = sorted({int(x) for x in ids})
    got = rb.pg.json("(SELECT coalesce(jsonb_object_agg(item_id, ids), '{}') FROM (SELECT item_id, jsonb_agg(attribute_id ORDER BY "
                     "attribute_id) ids FROM item_allowed_attributes WHERE item_id IN (%s) GROUP BY item_id) x)" % ', '.join(map(str, ids)))
    out = {int(k): [int(a) for a in v] for k, v in (got or {}).items()}
    missing = [i for i in ids if not out.get(i)]
    if missing:
        raise Bad('the server has no stat list (item_allowed_attributes) for mount gear %s' % ', '.join(map(str, missing)))
    return out


def _stat_name(aid):
    return next((name for _k, epic, name in FAMILIES if epic // 10 == int(aid) // 10), 'stat %s' % aid)


@M.get(r'adder/(\d+)')
def adder_state(ctx, cid):
    """GET /api/m/mount_gear/adder/<cid> - the character's mount and mount gear, the mount families, the stat kinds and
    the class's 'best' four for the adder."""
    rb, cid, d = I._cid_check(ctx, cid)
    b = d['character_base'][0]
    fams = mount_families()
    mount = I._find(d, I.LOC_EQUIP, I.R.MOUNT_SLOT)
    fam = family_of(fams, mount['prop_id']) if mount else None
    gear = {}
    for s in GEAR_LINES:
        r = I._find(d, I.LOC_EQUIP, s)
        if r:
            gear[s] = {'prop_id': r['prop_id'], 'name': rb.item_meta(r['prop_id'])['name'], 'level': rb.item_meta(r['prop_id']).get('minlv'),
                       'item_quality': r['item_quality'], 'item_grade': r['item_grade'], 'bound': r['bound'],
                       'stats': [r.get('attribute%d' % n) for n in range(1, 6)]}
    prof = int(b.get('profession') or 0)
    return {'character': {'id': cid, 'name': b['name'], 'level': int(b['fighter_job_lv'] or 1), 'class': common.CLASS_NAMES.get(prof),
                          'profession': prof},
            'mount': {'prop_id': mount['prop_id'], 'name': rb.item_meta(mount['prop_id'])['name'], 'level': rb.item_meta(mount['prop_id']).get('minlv'),
                      'family': fam['id'] if fam else None} if mount else None,
            'families': [{'id': f['id'], 'name': f['name']} for f in fams], 'gear': gear,
            'slots': [{'slot': s, 'name': GEAR_SLOT_NAMES[s]} for s in GEAR_LINES], 'levels': LEVELS,
            'stats': [{'key': k, 'name': n, 'kind': a // 10} for k, a, n in FAMILIES], 'best': CLASS_STATS.get(prof, CLASS_STATS[0]),
            'max_stats': ADDER_STATS, 'quality': {'max': I.CLIENT_Q, 'default': I.CLIENT_Q}, 'grade': {'max': I.CLIENT_G, 'default': I.CLIENT_G},
            'writable': bool(rb.writable)}


def adder_plan(ctx, cid, body):
    if not isinstance(body, dict):
        raise Bad('request must be an object')
    rb, cid, d = I._cid_check(ctx, cid)
    b = d['character_base'][0]
    clv = int(b['fighter_job_lv'] or 1)
    prof = int(b.get('profession') or 0)
    mlv = common.to_int(body.get('mount_level'), 40, 120, 'mount level')
    glv = common.to_int(body.get('gear_level', mlv), 40, 120, 'mount gear level')
    if mlv not in LEVELS or glv not in LEVELS:
        raise Bad('levels are 40, 50 ... 120 (each grade needs 10 more levels)')
    if glv > mlv:
        raise Bad('the gear level (%d) cannot be above the mount level (%d): the game needs mount level >= gear level' % (glv, mlv))
    if mlv > clv:
        raise Bad('%s is level %d: a level-%d mount needs character level %d or more (raise the character first)' % (b['name'], clv, mlv, mlv))
    q = common.to_int(body.get('quality', I.CLIENT_Q), 1, I.CLIENT_Q, 'quality')
    g = common.to_int(body.get('grade', I.CLIENT_G), 1, I.CLIENT_G, 'grade')
    mode = body.get('mode', 'auto')
    if mode == 'auto':
        keys = CLASS_STATS.get(prof, CLASS_STATS[0])
    elif mode == 'copy':
        keys = body.get('stats')
        if not isinstance(keys, list) or not 1 <= len(keys) <= ADDER_STATS or any(k not in KIND_OF for k in keys):
            raise Bad('choose 1-%d of the listed stats' % ADDER_STATS)
        if len(set(keys)) != len(keys):
            raise Bad('the same stat cannot be chosen twice')
    else:
        raise Bad('mode must be auto (best stats) or copy (the same four stats on all five pieces)')
    kinds = [KIND_OF[k] for k in keys]
    fams = mount_families()
    mount = I._find(d, I.LOC_EQUIP, I.R.MOUNT_SLOT)
    chosen = next((f for f in fams if f['id'] == body.get('family')), None) if body.get('family') else None
    if body.get('family') and not chosen:
        raise Bad('unknown mount family %r' % body.get('family'))
    fam = chosen or (family_of(fams, mount['prop_id']) if mount else None)
    if not fam:
        raise Bad(('the equipped mount %s has no level family (a special mount): choose a mount family to replace it'
                   % rb.item_meta(mount['prop_id'])['name']) if mount else 'the Mount slot is empty: choose the mount family to put there')
    if any(I._sealed(d, I._find(d, I.LOC_EQUIP, s)) for s in [I.R.MOUNT_SLOT] + list(GEAR_LINES)):
        raise Bad('a sealed pet item sits in a mount slot; move it first')
    mount_pid = fam['levels'][mlv]
    targets = {s: gear_id(s, glv) for s in GEAR_LINES}
    allowed = allowed_attributes(rb, targets.values())
    pieces = []
    # mount first: placed and checked against the projected state (the gear checks below see the NEW mount)
    proj = {k: (list(v) if isinstance(v, list) else v) for k, v in d.items()}
    proj['character_items'] = [dict(r) for r in d['character_items']]
    m_item = {k: (mount or {}).get(k) for k in I.ITEM_FIELDS} if mount else {}
    m_item.update(prop_id=mount_pid, stack=1)
    if not mount:
        m_item.update(bound=1, item_quality=q)
    mvals, mmeta = I._values(rb, m_item, mount, False)
    for n in range(1, 6):
        mvals['attribute_level%d' % n] = (mount or {}).get('attribute_level%d' % n) if mount else None
    I.R.check_place(rb, proj, I.LOC_EQUIP, I.R.MOUNT_SLOT, mmeta, False)
    row = next((r for r in proj['character_items'] if mount and r['id'] == mount['id']), None)
    if row:
        row.update(mvals)
    else:
        proj['character_items'].append(dict(mvals, id=-1, user_id=cid, item_location=I.LOC_EQUIP, slot_index=I.R.MOUNT_SLOT))
    pieces.append({'slot': I.R.MOUNT_SLOT, 'what': 'Mount', 'before': {k: mount.get(k) for k in I.CMP_FIELDS} if mount else None,
                   'after': mvals, 'name': mmeta['name'], 'level': mlv, 'stats': []})
    warnings = []
    for s, pid in targets.items():
        old = I._find(d, I.LOC_EQUIP, s)
        item = {k: old.get(k) for k in I.ITEM_FIELDS} if old else {}
        item.update(prop_id=pid, stack=1, bound=1, item_quality=q, item_grade=g)
        ids = best_ids(allowed[pid], kinds)
        for n in range(1, 6):
            item['attribute%d' % n] = ids[n - 1] if n <= len(ids) else None
            item['attribute_level%d' % n] = None
        odd = [item.get('holy_socket%d_effect_id' % n) for n in range(1, 7) if item.get('holy_socket%d_effect_id' % n) not in (None, 0, 21, 22, 23, 24)]
        if odd:     # kept as they are (the player's stones); the in-game socketing only puts Zephyr spirits on mount gear
            warnings.append('%s keeps its holy stones (effects %s), which the game normally puts only on character gear' % (
                GEAR_SLOT_NAMES[s], ', '.join(map(str, odd))))
        _pid, meta, warn = I._check_item(rb, proj, I.LOC_EQUIP, s, item, False, False)
        warnings += warn
        vals, meta = I._values(rb, item, old, False)
        for n in range(1, 6):
            vals['attribute_level%d' % n] = None
        I.R.check_place(rb, proj, I.LOC_EQUIP, s, meta, False)
        stats = []
        for aid in ids:
            dfn = rb.cat()['attributes'].get(str(aid)) or {}
            vv = dfn.get('values') or []
            amount = vv[min(max(1, g), len(vv)) - 1] if vv else None
            stats.append({'id': aid, 'name': _stat_name(aid), 'tier': aid % 10, 'value': amount, 'percent': bool(dfn.get('percent')),
                          'display': ('%.4g%%' % (float(amount) * 100)) if dfn.get('percent') and amount is not None else str(amount)})
        pieces.append({'slot': s, 'what': GEAR_SLOT_NAMES[s], 'before': {k: old.get(k) for k in I.CMP_FIELDS} if old else None,
                       'after': vals, 'name': meta['name'], 'level': glv, 'stats': stats})
    expected = [I._expected(cid, I.LOC_EQUIP, p['slot'], p['before']) for p in pieces]
    expected.append({'table': 'character_base', 'where': {'id': cid}, 'values': {k: b[k] for k in ('inventory_revision', 'profession', 'fighter_job_lv')}})
    options = {'mount_level': mlv, 'gear_level': glv, 'quality': q, 'grade': g, 'mode': mode, 'family': fam['id']}
    if mode == 'copy':
        options['stats'] = keys
    changed = any(p['before'] is None or any(p['before'].get(k) != v for k, v in p['after'].items()) for p in pieces)
    view = {'character': b['name'], 'family': fam['name'], 'options': options, 'stat_keys': keys, 'pieces': pieces, 'warnings': warnings,
            'changed': changed, 'writable': bool(rb.writable), 'applies': 'next_login'}
    view['token'] = hashlib.sha256(json.dumps({'v': view, 'e': expected}, sort_keys=True, default=str).encode()).hexdigest()
    return view, expected


@M.post(r'adder/preview/(\d+)')
def adder_preview(ctx, cid):
    """POST {mount_level, gear_level, quality, grade, mode: auto|copy, stats?, family?} -> what the five pieces and the
    mount become (nothing is written)."""
    return adder_plan(ctx, cid, ctx.body)[0]


@M.post(r'adder/apply/(\d+)')
def adder_apply(ctx, cid):
    """POST the previewed options + {token}: one transaction (offline only) with backup, change log and Undo."""
    rb = ctx.need_rb()
    if not rb.writable:
        raise Refused('this editor is read-only')
    view, expected = adder_plan(ctx, cid, ctx.body)
    if (ctx.body or {}).get('token') != view['token']:
        raise Conflict('the character or the options changed since the preview; preview again')
    if not view['changed']:
        raise Bad('the mount and its gear already match the preview')
    cid = int(cid)

    def build(d):
        sql = []
        # the mount first, so the database never holds gear above the mount (the gear rows follow in the same transaction)
        for p in view['pieces']:
            old = I._find(d, I.LOC_EQUIP, p['slot'])
            if (old or {}).get('id') != (p['before'] or {}).get('id'):
                raise Conflict('%s changed since the preview; preview again' % p['what'])
            vals = dict(p['after'])
            if old:
                sql.append('UPDATE character_items SET %s, updated_at = now() WHERE id = %d AND user_id = %d' % (
                    ', '.join('%s = %s' % (common.ident(k), common.lit(v)) for k, v in vals.items()), old['id'], cid))
                sql.append(I.R._audit(cid, 'update', I.LOC_EQUIP, p['slot'], vals, old))
            else:
                sql.append(common.insert_sql('character_items', dict(user_id=cid, item_location=I.LOC_EQUIP, slot_index=p['slot'], **vals)))
                sql.append(I.R._audit(cid, 'create', I.LOC_EQUIP, p['slot'], vals, None))
        sql.append('UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d' % cid)
        sql.append('SELECT public.recompute_character_holy_suit_points(%d)' % cid)
        return sql
    o = view['options']
    what = 'mount gear adder: %s Lv%d, gear Lv%d Q%d G%d, %s' % (view['family'], o['mount_level'], o['gear_level'], o['quality'], o['grade'],
                                                                 'best stats' if o['mode'] == 'auto' else 'stats ' + ', '.join(o['stats']))
    r = I._write(ctx, cid, what, build, expected)
    r['warnings'] = view['warnings']
    return r
