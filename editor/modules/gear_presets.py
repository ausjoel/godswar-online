"""Offline worn-gear presets; exact class-suit mapping, native stats and socket values.

Operator edits consume no crafting materials. Preview and commit share one plan;
all twelve ordinary equipment cells and profession/revision are checked atomically.
"""
import hashlib
import json
from pathlib import Path

from . import common, gearstats as G, inventory as INV, _class_stat_anywhere as CSA
from .common import Bad, Conflict, Refused

M = common.module('gear_presets', 'Worn gear presets', icon={'sprite':811}, order=22, status='ready',
                  needs_character=True, description='Preview four-stat/Platinum 10 or Q20/G25 five-stat presets on worn gear, with class-aware stones and one Undo.')
BRANCHES = json.loads((Path(__file__).resolve().parents[1]/'data/class-suit-branches.json').read_text())['branches']
HEATED = {0,2,8,9,10}
COOLED = {1,3,4,5,6,7,11}


# B21 item 25: every knob of the preset can be set (quality / grade, number of ordinary stats, stone count and level,
# holy-suit tier and level, the class-suit tier and which class stone), worn slots can be limited, and empty slots can
# be filled with the class's best item for the character's level (loadout_pick). Defaults keep the two original presets.
PRESETS = {'standard': {'quality': None, 'grade': None, 'stats': 4, 'stones': 2},
           'boundless': {'quality': 20, 'grade': 25, 'stats': 5, 'stones': 4}}
SUIT_TIERS = range(0, 8)


def _opt_int(body, key, default, lo, hi, what):
    v = body.get(key, default)
    return None if v in (None, '') else common.to_int(v, lo, hi, what)


def inputs(body):
    if not isinstance(body,dict):
        raise Bad('preset options must be an object')
    preset=body.get('preset','standard')
    if preset not in PRESETS:
        raise Bad('choose standard or boundless')
    d=PRESETS[preset]
    tier=common.to_int(body.get('class_tier',0),0,4,'class suit tier')
    attr=body.get('class_stat',False)
    if not isinstance(attr,bool):
        raise Bad('class_stat must be true or false')
    stat_id=_opt_int(body,'class_stat_id',None,200,231,'class stone stat')
    if stat_id is not None and stat_id not in INV.CLASS_ATTRS:
        raise Bad('class stone stat must be one of %s' % ', '.join(map(str,INV.CLASS_ATTRS)))
    # B22 d55: whether a piece may carry the class stone is decided per piece in plan() (the database rule); the tier
    # rule (Class Suit III / IV) still applies to class-suit pieces unless "also on other gear" (any_gear) is ticked
    any_gear=body.get('non_suit_special',False)
    if not isinstance(any_gear,bool):
        raise Bad('non_suit_special must be true or false')
    if attr and tier in (1,2) and not any_gear:
        raise Bad('class-specific stats require Class Suit III or IV (or tick "Also on other gear")')
    if any_gear and not attr:
        raise Bad('choose a class stone first: "also on other gear" puts it on every chosen piece')
    suit_tier=_opt_int(body,'suit_tier',4,0,7,'holy suit tier')
    suit_level=_opt_int(body,'suit_level',10,0,10,'holy suit level')
    code=0 if not suit_tier else suit_tier*100+max(1,suit_level or 1)
    if code and code not in INV._suit_codes():
        raise Bad('holy suit tier %s level %s is not a tier/level the client knows' % (suit_tier,suit_level))
    slots=body.get('slots')
    if slots is not None:
        if not isinstance(slots,list) or not slots:
            raise Bad('slots must be a list of worn slots 0-11')
        slots=sorted({common.to_int(x,0,11,'slot') for x in slots})
    fill=body.get('fill_empty',False)
    if not isinstance(fill,bool):
        raise Bad('fill_empty must be true or false')
    return {'preset':preset,'class_tier':tier,'class_stat':attr,'class_stat_id':stat_id,'any_gear':any_gear,
            'quality':_opt_int(body,'quality',d['quality'],1,20,'quality'),
            'grade':_opt_int(body,'grade',d['grade'],1,25,'grade'),
            'stats':_opt_int(body,'stats',d['stats'],0,5,'number of ordinary stats'),
            'stones':_opt_int(body,'stones',d['stones'],0,4,'number of holy stones'),
            'stone_level':_opt_int(body,'stone_level',10,1,10,'holy stone level'),
            'suit_tier':suit_tier,'suit_level':suit_level,'suit_code':code,'slots':slots,'fill_empty':fill}


def suit_target(item_id, profession, tier):
    # Restrict conversion to the exact authored branch; never infer it from names or slot alone.
    for branch in BRANCHES:
        if branch['profession']==profession and item_id in branch['common']+branch['tiers']:
            old_tier=branch['tiers'].index(item_id)+1 if item_id in branch['tiers'] else 0
            return (branch['tiers'][max(tier,old_tier)-1] if tier else item_id), old_tier
    if tier:
        raise Bad('item #%s has no class-suit conversion branch for %s; turn off conversion or equip its authored common/class-suit piece' % (item_id,common.CLASS_NAMES.get(profession,profession)))
    return item_id,0


def stone_rows(slot, magical, count, balance, level=10):
    # Same 5% damage / 8% ignore-defense for physical and magical builds at level 10; lower levels scale linearly
    # (a stone's range is level x its level-1 range) and stay inside that level's range.
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
        lo,hi=INV.value_range(effect,level,balance)
        if level!=10:
            value=max(lo,min(hi,(value*level+5)//10))
        if not lo<=value<=hi:
            raise Bad('current holy-spirit balance has no supported preset value for effect %s' % effect)
        result.append({'effect':effect,'level':level,'value':value,'name':common.CATALOG.file('holystones')[str(effect)]['name'],
                       'percent':INV.SPIRITS[effect][0]=='percent'})
    return result


def class_stone(profession, wanted=None):
    """The class stone stat (points-exchanger stones 9950-9957): the one asked for when the class may use it, else the
    class's attack stone (Blood: Physical Attack for Warrior / Champion, Holy: Spell Power for Priest / Mage)."""
    from . import _attribute_families as AF
    if wanted is not None:
        if not AF.class_allowed(wanted,profession):
            raise Bad('that class stone is not for a %s' % common.CLASS_NAMES.get(profession,profession))
        return wanted
    return 220 if profession in (2,3) else 200


def attributes(ctx, entry, magical, count, grade):
    if count==0:
        return []
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
    result=[]; skipped=[]; expected=[]; notes=[]
    picks=loadout_pick(common.CATALOG.file('items'),profession,int(base['fighter_job_lv'])) if options['fill_empty'] else {}
    for slot in range(12):
        if options['slots'] is not None and slot not in options['slots']:
            continue
        old=INV._find(d,INV.LOC_EQUIP,slot)
        expected.append(INV._expected(cid,INV.LOC_EQUIP,slot,old))
        if not old:
            pe=picks.get(slot)
            if not pe:
                continue
            item={'prop_id':int(pe['id']),'stack':1,'bound':0,'item_quality':DEFAULT_QUALITY,'item_grade':DEFAULT_GRADE,'item_exp':0,
                  'holy_suit_code':0,'holy_socket_count':0}
            entry=pe
        else:
            entry=G._item(ctx,old['prop_id'])
            if not entry or entry.get('kind') not in INV.SLOTS[slot][1]:
                raise Bad('worn slot %s has an unknown or mismatched item' % slot)
            item={k:old.get(k) for k in INV.ITEM_FIELDS}
        skip_note=None
        try:
            target,old_tier=suit_target(item['prop_id'],profession,options['class_tier'])
        except Bad:
            # no class-suit version of this piece (GM / event gear): keep it as it is, the other pieces still convert
            target,old_tier=item['prop_id'],0
            skip_note='no class-suit version for this class: item kept, no class stone'
        target_tier=max(options['class_tier'],old_tier)
        item['prop_id']=target
        target_entry=G._item(ctx,target)
        if options['quality'] is not None:
            item['item_quality']=options['quality']
        if options['grade'] is not None:
            item['item_grade']=options['grade']
        item['holy_suit_code']=options['suit_code']
        try:
            stats=attributes(ctx,target_entry,magical,options['stats'],int(item['item_grade'] or 1))
        except Bad as error:
            raise Bad('%s: %s' % (entry.get('name',item['prop_id']),error))
        for n in range(1,6):
            stat=stats[n-1] if n<=len(stats) else {}
            item['attribute%d'%n]=stat.get('id');item['attribute_level%d'%n]=stat.get('level')
        suit_piece=target in INV.class_suit_items() and target_tier>=3
        if options['class_stat'] and options['any_gear'] and not suit_piece:
            # B22 d55: the class stone on gear that is not Class Suit III / IV (the database must allow it)
            ok,why=CSA.eligible(rb,target,{'name':target_entry.get('name',str(target)),'slot':slot},INV.class_suit_items(),gear=True)
            if not ok:
                raise Bad(why)
            item['class_attribute1']=class_stone(profession,options['class_stat_id'])
            skip_note=None if not skip_note else 'no class-suit version for this class: item kept, class stone added'
        elif options['class_stat'] and skip_note:
            pass
        elif options['class_stat']:
            if not suit_piece:
                raise Bad('%s needs Class Suit III/IV before adding its class stat; choose conversion tier III/IV, equip that suit, '
                          'or tick "Also on other gear"' % entry.get('name',target))
            # the class stone is a 5th stat in its own row: one per item, next to up to five ordinary stats
            item['class_attribute1']=class_stone(profession,options['class_stat_id'])
        elif old is not None and item.get('class_attribute1') is not None and target not in INV.class_suit_items() \
                and not CSA.allowed(rb):
            # a database that still has the suit-only rule would refuse the row: drop the stone (it is shown in the diff)
            item['class_attribute1']=None
        stones=stone_rows(slot,magical,options['stones'],balance,options['stone_level'])
        item['holy_socket_count']=options['stones']
        for n in range(1,7):
            stone=stones[n-1] if n<=len(stones) else {}
            item['holy_socket%d_effect_id'%n]=stone.get('effect')
            item['holy_socket%d_level'%n]=stone.get('level')
            if n<=4:item['holy_socket%d_value'%n]=stone.get('value')
        _pid,_meta,warnings=INV._check_item(rb,d,INV.LOC_EQUIP,slot,item,False,False)
        if skip_note:
            warnings=[skip_note]+list(warnings)
        INV.R.check_place(rb,d,INV.LOC_EQUIP,slot,_meta,False)
        class_stat=None
        if item.get('class_attribute1') is not None:
            aid=item['class_attribute1'];definition=rb.cat()['attributes'].get(str(aid)) or {}
            vals=definition.get('values') or []
            class_stat={'id':aid,'name':(ctx.catalog.file('appendattrs').get(str(aid)) or {}).get('text') or definition.get('key'),
                        'value':vals[min(max(1,int(item['item_grade']))-1,len(vals)-1)] if vals else None,'percent':bool(definition.get('percent'))}
        before={k:old.get(k) for k in INV.CMP_FIELDS} if old else None
        result.append({'slot':slot,'slot_name':INV.SLOTS[slot][0],'before':before,'new':old is None,
                       'after':item,'name':target_entry.get('name',str(target)),'sprite':target_entry.get('sprite'),'stats':stats,'stones':stones,
                       'class_stat':class_stat,'changed':old is None or any(old.get(k)!=v for k,v in item.items()),'warnings':warnings})
    for old in INV._items(d):
        if old['item_location']==INV.LOC_EQUIP and old['slot_index'] not in range(12):
            skipped.append({'slot':old['slot_index'],'item':old['prop_id'],'reason':'Fashion, mounts and mount gear use separate systems.'})
    if not result:
        raise Bad('no worn armor or weapons in the chosen equipment slots' + ('' if options['fill_empty'] else ' (tick "fill empty slots" to add gear)'))
    if options['quality'] and options['quality']>INV.CLIENT_Q or options['grade'] and options['grade']>INV.CLIENT_G:
        notes.append('above what the client shows')
    expected.append({'table':'character_base','where':{'id':cid},'values':{'profession':profession,'inventory_revision':base['inventory_revision']}})
    token=hashlib.sha256(json.dumps({'cid':cid,'options':options,'expected':expected,'rows':result,'balance':balance},sort_keys=True,default=str).encode()).hexdigest()
    return {'character':{'id':cid,'name':base['name'],'class':common.CLASS_NAMES[profession],'profession':profession,'level':base['fighter_job_lv']},
            'options':options,'items':result,'skipped':skipped,'notes':notes,'token':token,'writable':bool(rb.writable),'applies':'next_login'},expected


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
    if not any(row['changed'] for row in view['items']):
        raise Bad('all worn gear already matches this preset')
    rb=ctx.need_rb();cid=int(cid)
    def build(d):
        statements=[]
        for row in view['items']:
            old=INV._find(d,INV.LOC_EQUIP,row['slot'])
            if row.get('new'):
                if old is not None:
                    raise Conflict('a worn slot changed; preview again')
                vals,meta=INV._values(rb,row['after'],None,False)
                for n in range(1,6):
                    if row['after'].get('attribute_level%d'%n) is None:
                        vals['attribute_level%d'%n]=None
                INV.R.check_place(rb,d,INV.LOC_EQUIP,row['slot'],meta,False)
                cols=['user_id','item_location','slot_index']+list(vals)
                statements.append('INSERT INTO character_items (%s) VALUES (%s)' % (', '.join(common.ident(c) for c in cols),
                                  ', '.join(common.lit(x) for x in [cid,INV.LOC_EQUIP,row['slot']]+list(vals.values()))))
                statements.append(INV.R._audit(cid,'create',INV.LOC_EQUIP,row['slot'],vals,None))
                continue
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


# =========================================================================== W4A: quick-add loadout
# the admin 2026-11-12: "Quick add entire loadout with stats for class". One button equips a full class-appropriate set
# for a chosen level and class: the best item the class can wear at that level in each of the twelve ordinary
# equipment slots, each with four legal stat rolls (the same families and amounts the 4-stat preset uses), at the
# chosen quality and grade. Worn items in those slots move to the kit bag first (refused when the bag has no room).
CLASS_WEAPON = {0: "One-Hand Sword", 1: "Spear", 2: "One-Hand Wand", 3: "Two-Hand Wand"}   # the client's class weapons
CLASS_SHIELD = {0, 2}                                   # only Warrior and Priest carry a shield (one-hand weapons)
MAGICAL = {2, 3}
BODY_KIND = {0: "armor", 1: "armor", 2: "cloth", 3: "cloth"}   # physical classes armour, magical classes cloth
LOADOUT_SLOTS = list(range(12))
DEFAULT_QUALITY, DEFAULT_GRADE = 10, 10


def loadout_pick(items, profession, level):
    """{slot: item entry} - for each slot the highest-level item the class can wear at `level` (minlv <= level <=
    maxlv), class-specific gear before all-class gear, GM items and the legacy two-hand placeholder weapons left out.
    Pure: `items` is the catalogue's items.json dict."""
    profession, level = int(profession), int(level)
    magical = profession in MAGICAL
    pool = []
    for e in items.values():
        slot = e.get("slot")
        if slot not in LOADOUT_SLOTS or not e.get("kind"):
            continue
        name = str(e.get("name") or "")
        # GM items, the legacy two-hand placeholder weapons, event / novelty heads (Pumpkin 3700, Deer Horn 9000) and
        # server-only ids (>= 10000) are never part of a normal set
        if name.startswith("GM ") or (e.get("group") or "") == "Weapons/TwoHandsSwords" or int(e.get("id") or 0) >= 3500:
            continue
        classes = e.get("classes") or []
        if classes and profession not in classes:
            continue
        lo, hi = int(e.get("minlv") or 1), int(e.get("maxlv") or 200)
        if not lo <= level <= hi:
            continue
        if slot == 10 and e.get("category") != CLASS_WEAPON[profession]:
            continue
        if slot == 11 and profession not in CLASS_SHIELD:
            continue
        if slot == 3 and e.get("kind") != BODY_KIND[profession]:
            continue
        stats = e.get("stats") or {}
        role = 1 if (("MagicAk" in stats) if magical else ("Attack" in stats)) else 0
        pool.append((slot, lo, 1 if classes == [profession] else 0, role, -int(e["id"]), e))
    best = {}
    for slot, lo, own, role, nid, e in sorted(pool, key=lambda x: x[:5]):
        best[slot] = e                                   # sorted ascending: the last one wins
    if 8 in best:                                        # both ring slots take rings (template slot 8)
        best[9] = best[8]
    return best


def loadout_inputs(body, profession):
    if not isinstance(body, dict):
        raise Bad("loadout options must be an object")
    cls = common.to_int(body.get("profession", profession), 0, 3, "class")
    level = common.to_int(body.get("level"), 1, 200, "level")
    q = common.to_int(body.get("quality", DEFAULT_QUALITY), 1, 20, "quality")
    g = common.to_int(body.get("grade", DEFAULT_GRADE), 1, 25, "grade")
    return {"profession": cls, "level": level, "quality": q, "grade": g}


def free_bag_slots(d, n):
    used = {r["slot_index"] for r in INV._items(d) if r["item_location"] == INV.LOC_BAG}
    return [s for s in range(INV.BAG_SLOTS) if s not in used][:n]


def loadout_plan(ctx, cid, body):
    rb, cid, d = INV._cid_check(ctx, cid)
    base = d["character_base"][0]
    opt = loadout_inputs(body, base["profession"])
    magical = opt["profession"] in MAGICAL
    picks = loadout_pick(common.CATALOG.file("items"), opt["profession"], opt["level"])
    rows, warnings, missing, expected = [], [], [], []
    for slot in LOADOUT_SLOTS:
        old = INV._find(d, INV.LOC_EQUIP, slot)
        expected.append(INV._expected(cid, INV.LOC_EQUIP, slot, old))
        e = picks.get(slot)
        if not e:
            if not (slot == 11 and opt["profession"] not in CLASS_SHIELD):
                missing.append(INV.SLOTS[slot][0])
            continue
        try:
            stats = attributes(ctx, e, magical, 4, opt["grade"])
        except Bad as error:
            raise Bad("%s: %s" % (e.get("name", e["id"]), error))
        item = {"prop_id": int(e["id"]), "stack": 1, "item_quality": opt["quality"], "item_grade": opt["grade"], "holy_suit_code": 0,
                "holy_socket_count": 0}
        for n in range(1, 6):
            st = stats[n - 1] if n <= len(stats) else {}
            item["attribute%d" % n] = st.get("id")
            item["attribute_level%d" % n] = st.get("level")
        _pid, _meta, warn = INV._check_item(rb, d, INV.LOC_EQUIP, slot, dict(item), False, False)
        rows.append({"slot": slot, "slot_name": INV.SLOTS[slot][0], "item": int(e["id"]), "name": e.get("name"), "level": int(e.get("minlv") or 1),
                     "sprite": e.get("sprite"), "stats": stats, "after": item, "replaces": old["prop_id"] if old else None,
                     "warnings": warn})
    if opt["level"] > int(base["fighter_job_lv"]):
        warnings.append("level %d gear on a level %s character: the game will not let the player use it until level %d" % (
            opt["level"], base["fighter_job_lv"], opt["level"]))
    if opt["profession"] != int(base["profession"]):
        warnings.append("%s gear on a %s: the game refuses to equip another class's gear; it stays worn but may not count" % (
            common.CLASS_NAMES.get(opt["profession"]), common.CLASS_NAMES.get(int(base["profession"]))))
    displaced = [r for r in rows if r["replaces"] is not None]
    free = free_bag_slots(d, len(displaced))
    if len(free) < len(displaced):
        raise Bad("the kit bag has %d free cells; %d worn items must move there first. Make room (Inventory) and try again." % (
            len(free), len(displaced)))
    for r, s in zip(displaced, free):
        r["to_bag_slot"] = s
    expected.append({"table": "character_base", "where": {"id": cid}, "values": {"inventory_revision": base["inventory_revision"]}})
    token = hashlib.sha256(json.dumps({"cid": cid, "opt": opt, "rows": rows, "expected": expected}, sort_keys=True, default=str).encode()).hexdigest()
    return {"character": {"id": cid, "name": base["name"], "class": common.CLASS_NAMES.get(int(base["profession"]))}, "options": opt,
            "items": rows, "missing": missing, "warnings": warnings, "token": token, "applies": "next_login",
            "writable": bool(rb.writable)}, expected


@M.post(r'loadout/preview/(\d+)')
def loadout_preview(ctx, cid):
    """POST /api/m/gear_presets/loadout/preview/<cid> {profession?, level, quality?, grade?} - the set it would equip."""
    return loadout_plan(ctx, cid, ctx.body)[0]


@M.post(r'loadout/apply/(\d+)')
def loadout_apply(ctx, cid):
    """POST /api/m/gear_presets/loadout/apply/<cid> {..., token} - equip it (worn items move to the bag), one Undo."""
    if not ctx.need_rb().writable:
        raise Refused('this editor is read-only for the selected database')
    view, expected = loadout_plan(ctx, cid, ctx.body)
    if ctx.body.get('token') != view['token']:
        raise Conflict('gear or bag changed; preview the loadout again before applying')
    if not view["items"]:
        raise Bad("no gear found for that class and level")
    rb = ctx.need_rb()
    cid = int(cid)

    def build(d):
        st = []
        for row in view["items"]:
            old = INV._find(d, INV.LOC_EQUIP, row["slot"])
            if old is not None:
                if row.get("to_bag_slot") is None or INV._find(d, INV.LOC_BAG, row["to_bag_slot"]):
                    raise Conflict("the bag changed; preview again")
                st.append("UPDATE character_items SET item_location = %d, slot_index = %d, updated_at = now() WHERE id = %d" % (
                    INV.LOC_BAG, row["to_bag_slot"], old["id"]))
                st.append(INV.R._audit(cid, "move", INV.LOC_BAG, row["to_bag_slot"], old, old))
            vals, meta = INV._values(rb, row["after"], None, False)
            for n in range(1, 6):
                if row["after"].get("attribute_level%d" % n) is None:
                    vals["attribute_level%d" % n] = None
            cols = ["user_id", "item_location", "slot_index"] + list(vals)
            st.append("INSERT INTO character_items (%s) VALUES (%s)" % (
                ", ".join(common.ident(c) for c in cols), ", ".join(common.lit(x) for x in [cid, INV.LOC_EQUIP, row["slot"]] + list(vals.values()))))
            st.append(INV.R._audit(cid, "create", INV.LOC_EQUIP, row["slot"], vals, None))
        st += ["UPDATE character_base SET inventory_revision = inventory_revision + 1 WHERE id = %d" % cid,
               "SELECT public.recompute_character_holy_suit_points(%d)" % cid]
        return st
    o = view["options"]
    r = INV._write(ctx, cid, "quick loadout: %s level %d, Q%d G%d, %d items (4 stats each)" % (
        common.CLASS_NAMES.get(o["profession"]), o["level"], o["quality"], o["grade"], len(view["items"])), build, expected)
    r.update(items=len(view["items"]), warnings=view["warnings"] + [w for x in view["items"] for w in x["warnings"]])
    return r
