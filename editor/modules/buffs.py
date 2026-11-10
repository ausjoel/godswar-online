"""Live application of authored gameplay buffs; never writes offline stat rows."""
import re
from . import common
from .common import Bad, NotFound

M=common.module('buffs','Buffs',icon={'sprite':17},order=45,status='ready',
    description='Apply authored class buffs to an online character, using native effects and stacking.',
    needs_character=True)

def _library_rows(ctx):
    """The client's status list with unique labels and icons (shared by the library and the Apply list)."""
    lib=ctx.catalog.file('buff-library')
    if not lib.get('entries'):raise Bad('The buff library file (catalog-origin/buff-library.json) is missing; run build_buff_library.py.')
    rows=[dict(e) for e in lib['entries']]
    _names_and_icons(ctx,rows)
    return lib,rows

# Status.ini effects the server does NOT compute (its own source says so: StatusItemPolicy remarks). The client text still
# promises them, so the editor says what will not happen instead of letting the description oversell the buff.
UNAPPLIED_CODES={16:'maximum MP',17:'maximum HP'}

def _not_applied(lib,description):
    out=[x['label'] for x in (lib.get('effects') or []) if x.get('code') in UNAPPLIED_CODES]
    if 'nearby' in (description or '').lower():out.append('the bonus per nearby player (the server heals the flat amount only)')
    return out

def runtime_catalogue(ctx):
    caps=ctx.caps
    if 'character.buff_apply' not in (caps.get('operations') or {}):
        return {'supported':False,'buffs':[],'reason':'The game server needs the live buff tools update.'}
    code,doc=common._http('GET',caps['port'],'/admin/buffs/catalog',timeout=3)
    if code!=200 or not isinstance(doc,dict) or doc.get('version')!=1 or not isinstance(doc.get('buffs'),list):
        raise Bad('The running buff catalogue is unavailable; try again after checking the server.')
    try:by_id={r['id']:r for r in _library_rows(ctx)[1]}
    except Bad:by_id={}
    rows=[]
    for definition in doc['buffs']:
        row=dict(definition);row.setdefault('source','class-skill')    # an older server lists class buffs only, without a source
        cls=row['source']=='class-skill'
        # Only class buffs are named after their skill. An item buff's "skill" is the item's use-skill, whose client name is
        # often untranslated Chinese and shared by many rows, so those take the library's English, unique label instead.
        entry=(ctx.catalog.entry('skill',row['skillId']) if cls and row.get('skillId') else None) or {}
        status=ctx.catalog.entry('status',row['statusId']) or {};lib=by_id.get(row['statusId']) or {}
        effect_text='; '.join(x['label'] for x in lib.get('effects') or [])
        row.update(name=entry.get('name') or lib.get('label') or status.get('name') or 'Status #'+str(row['statusId']),
                   description=(status.get('note') or entry.get('desc') or effect_text) if cls else (lib.get('note') or effect_text),
                   skill_description=entry.get('desc') or '',rank=entry.get('level'),
                   status_name=status.get('name') or 'Status #'+str(row['statusId']),effect_text=effect_text)
        row['not_applied']=_not_applied(lib,row['description'])
        # FINALISE2 P5 (F-102): a newer server says itself what it applies (note) and marks hostile rows; show that
        # instead of the editor's own guess, which only knew the max HP / max MP gaps.
        if isinstance(row.get('note'),str) and row['note']:
            row['server_note']=row['note'];row['not_applied']=[]
        row['hostile']=bool(row.get('hostile',not row.get('beneficial',True)))
        # ROUND3: native riding is conditional; do not describe effect 33's riding flag as a literal +1 speed bonus.
        if row.get('activation')=='mount':
            row['description']=row.get('activationNote') or 'Requires the matching equipped mount and enough MP.'
            row['effect_text']='Mounted appearance and speed use the equipped mount.'
        elif row.get('activation')=='periodic-damage':  # ROUND3: exact HP loss, cadence and life-bound ownership.
            row['description']=row.get('activationNote') or row['description']
            row['effect_text']='Lose %s HP every %s seconds.'%(row['effects']['hpDamagePerPulse'],row['effects']['damageIntervalSeconds'])
        rows.append(row)
    return dict(doc,supported=True,buffs=rows)

def library(ctx):
    """Every status effect the game data defines (build_buff_library.py), each marked with whether the RUNNING server can
    apply it to a player today: only effects in the server's own catalogue (GET /admin/buffs/catalog) can, so the others are
    listed with their labels but cannot be applied, and the page says so."""
    lib,rows=_library_rows(ctx)
    try:live=runtime_catalogue(ctx)
    except Bad:live={'supported':False,'buffs':[]}
    by_status={r['statusId']:r for r in live.get('buffs',[]) if 'statusId' in r}
    for row in rows:
        apply=by_status.get(row['id'])
        row['apply_status_id']=row['id'] if apply else None
        row['apply_source']=apply['source'] if apply else None
        # ROUND3: the library tooltip must show the same mount requirement as the Apply panel.
        if apply and apply.get('activation') in ('mount','periodic-damage'):  # ROUND3
            row['note']=apply['description']
            row['duration_s']=apply['durationSeconds']  # ROUND3: editor rides expire even though the source ride is permanent.
            code=33 if apply['activation']=='mount' else 27
            row['effects']=[dict(effect,label=apply['effect_text']) if effect.get('code')==code else dict(effect)
                            for effect in row.get('effects') or []]
    return {'version':lib.get('version'),'source':lib.get('source'),'count':len(rows),'categories':lib.get('categories',[]),
            'applicable':sum(1 for r in rows if r['apply_status_id'] is not None),'server_supported':bool(live.get('supported')),
            'entries':rows}

_CJK=re.compile('[\u3000-\u9fff\uff00-\uffef]')
EXP_ICON_STATUS=1000      # '[Guild] Double EXP': the client's EXP-boost icon, used for server-only EXP / patron effects

def _entry(ctx,kind,i):
    try:return ctx.catalog.entry(kind,i) or {}
    except Exception:return {}      # noqa: BLE001 - server-only ids are not in the client catalogue

def _names_and_icons(ctx,rows):
    """2026-11-05 (buff library as an icon grid): every entry gets
      label: a name that tells same-named effects apart - 'Bleeding' x10 becomes 'Bleeding (Blood Claw 1)', ... (the first
             English skill that casts it; untranslated Chinese skill names are never shown), else 'Bleeding (rank N)';
      icon:  its own Status.ini icon (catalog sprite), else the icon of the skill that casts it, else the EXP-boost icon for
             server-only EXP / patron effects; icon_source says which."""
    groups={}
    for r in rows:groups.setdefault(r.get('name') or '',[]).append(r)
    exp_icon=_entry(ctx,'status',EXP_ICON_STATUS).get('sprite')
    for name,g in groups.items():
        used=set()
        for i,r in enumerate(g):
            label=name or 'Status #'+str(r['id'])
            if len(g)>1:
                eng=[k['name'] for k in r.get('skills') or [] if k.get('name') and not _CJK.search(k['name'])]
                tag=next((k for k in eng if k not in used),None)
                if tag:used.add(tag);label='%s (%s)'%(label,tag)
                else:label='%s (rank %d)'%(label,i+1)
            r['label']=label
            st=_entry(ctx,'status',r['id'])
            if st.get('icon') and st.get('sprite') is not None:r['icon'],r['icon_source']=st['sprite'],'status'
            else:
                sk=next((_entry(ctx,'skill',k['id']) for k in r.get('skills') or [] if _entry(ctx,'skill',k['id']).get('sprite') is not None),None)
                if sk:r['icon'],r['icon_source']=sk['sprite'],'skill'
                elif 'EXP' in (r.get('category') or '') or r.get('server_only'):r['icon'],r['icon_source']=exp_icon,'generic'
                else:r['icon'],r['icon_source']=None,'none'
            if r.get('note') and _CJK.search(r['note']):r['note']=''

@M.get('library')
def library_view(ctx):
    return library(ctx)

def character(ctx,cid):
    cid=common.to_int(cid,1,2**31-1,'character ID')
    row=ctx.need_rb().pg.json("(SELECT json_build_object('id',id,'name',name) FROM character_base WHERE id=%d AND lifecycle_state='active')"%cid)
    if not row:raise NotFound('No active character #'+str(cid))
    return cid,row

@M.get(r'sheet/(\d+)')
def sheet(ctx,cid):
    cid,row=character(ctx,cid)
    return {'character':row,'online':common.online_info(ctx.app,cid),'catalogue':runtime_catalogue(ctx),'writable':bool(ctx.rb.writable)}

@M.post(r'apply/(\d+)')
def apply(ctx,cid):
    if not ctx.need_rb().writable:raise Bad('This editor connection is read-only.')
    cid,row=character(ctx,cid);catalogue=runtime_catalogue(ctx)
    if not catalogue['supported']:raise Bad(catalogue['reason'])
    by_status={r['statusId']:r for r in catalogue['buffs']}
    if ctx.body.get('status_id') is not None:
        buff=by_status.get(common.to_int(ctx.body['status_id'],1,2**31-1,'buff status'))
    elif ctx.body.get('skill_id') is not None:
        skill=common.to_int(ctx.body['skill_id'],1,2**31-1,'buff skill')
        buff=next((r for r in catalogue['buffs'] if r.get('skillId')==skill and r['source']=='class-skill'),None)
    else:raise Bad('Choose a buff.')
    if buff is None:raise Bad('Choose a buff from the running server catalogue.')
    # Class buffs are sent by skill, as before, so the editor and an older server work together; every other row has no skill
    # and is selected by its Status.ini id. (`row` stays the CHARACTER: it names the target of the live call below.)
    body={'skillId':buff['skillId']} if buff.get('skillId') and buff['source']=='class-skill' else {'statusId':buff['statusId']}
    if 'duration_seconds' in ctx.body:
        body['durationSeconds']=common.to_int(ctx.body['duration_seconds'],1,catalogue['maxDurationSeconds'],'duration in seconds')
    result=ctx.live_call('character.buff_apply',row['name'],body,cid=cid,
        no_undo='This live session effect expires naturally or when the player logs out.')
    if result.get('offline'):raise Bad(row['name']+' must be logged into the game to receive a session buff.')
    result.update(applies='live',applies_text=common.APPLIES_TEXT['live'])
    return result


@M.get(r'active/(\d+)')
def active(ctx,cid):
    cid,row=character(ctx,cid);caps=ctx.caps
    if 'character.buff_inspect' not in (caps.get('operations') or {}):
        return {'supported':False,'character':row,'reason':'Active-effect controls need the compatible server update.'}
    code,doc=common._http('POST',caps['port'],'/admin/buff/inspect',{'actor':'gwadmin-web','character':row['name']},timeout=8)
    if code==409 and isinstance(doc,dict) and doc.get('error')=='offline':
        return {'supported':True,'online':False,'character':row,'buffs':[]}
    if code!=200 or not isinstance(doc,dict) or not doc.get('ok'):
        raise common.Conflict('The live effects are unavailable or the character is changing sessions. Refresh shortly.')
    for effect in doc.get('buffs',[]):
        status=ctx.catalog.entry('status',effect['statusId']) or {}
        effect['name']=status.get('name') or 'Status #'+str(effect['statusId'])
    return dict(doc,supported=True,online=True,character=row)

@M.post(r'remove/(\d+)')
def remove(ctx,cid):
    if not ctx.need_rb().writable:raise Bad('This editor connection is read-only.')
    cid,row=character(ctx,cid);expected=ctx.body.get('expected')
    if not isinstance(expected,dict):raise Bad('The displayed effect token is required.')
    epoch=expected.get('epoch');revision=expected.get('revision')
    if not isinstance(epoch,str) or not re.fullmatch('[0-9a-fA-F]{32}',epoch) or int(epoch,16)==0:
        raise Bad('The status lifetime token is invalid.')
    if not isinstance(revision,str) or not re.fullmatch('[0-9]{1,19}',revision) or not 1<=int(revision)<=2**63-1:
        raise Bad('The exact displayed revision string is required.')
    payload={'epoch':epoch,'revision':revision,
        'kind':common.to_int(expected.get('kind'),1,2**31-1,'effect kind'),
        'statusId':common.to_int(expected.get('statusId'),1,2**31-1,'status ID')}
    result=ctx.live_call('character.buff_remove',row['name'],payload,cid=cid,
        no_undo='This removes a live session effect; reapply the effect if required.')
    if result.get('offline'):raise common.Conflict('The character left the game; refresh active effects.')
    result.update(applies='live',applies_text=common.APPLIES_TEXT['live'])
    return result
