"""Preview and grant all supported authored titles through the existing v1 grant writer."""
import hashlib
import json

from . import common, inventory as I, title_grants as T
from .common import Bad, Conflict, Refused

M=common.module('all_titles','Titles: Give all',icon={'sprite':811},order=41,status='ready',needs_character=True,
                description='Preview all authored titles, grant missing supported titles offline, and keep the shown title. Normal backup and Undo.')
# Native WonderlandTitlePolicy.Resolve. Runtime-only identities are listed even
# when the reviewed client/admin catalog cannot grant them; no earned evidence is forged.
WONDERLAND={5155:'Gatebreaker',5114:'Demonbreaker',5156:'Flamebreaker',5115:'Stonebreaker',
            5157:"Marshal's Bane",5116:'Dragonbane',5117:"Hydra's Bane",5118:'Wonderland Sovereign'}


def owned(ctx,cid):
    return ctx.need_rb().pg.json('(SELECT coalesce(json_agg(title_id ORDER BY title_id),\'[]\') FROM character_title_entitlements WHERE character_id=%d)'%int(cid)) or []


def plan(ctx,cid):
    ready=T.support(ctx)
    if not ready['available']:raise Refused(ready['reason'])
    rb,cid,d=I._cid_check(ctx,cid)
    base=d['character_base'][0]
    catalog={int(row['title_id']):row for row in T.catalogue(ctx)}
    client={int(k):row for k,row in ctx.catalog.file('titles').items()}
    medusa=rb.pg.json('(SELECT coalesce(json_agg(json_build_object(\'id\',client_title_id,\'name\',display_name)),\'[]\') FROM medusa_reward_title_definitions)') or []
    authored={aid:dict(name=row.get('name') or row.get('label') or 'Title #%d'%aid) for aid,row in client.items()}
    for aid,name in WONDERLAND.items():authored.setdefault(aid,dict(name=name))
    for row in medusa:authored.setdefault(int(row['id']),dict(name=row['name']))
    for aid,row in catalog.items():authored.setdefault(aid,dict(name=row['display_name']))
    current=sorted(set(int(aid) for aid in owned(ctx,cid)))
    grants=sorted(int(row['title_id']) for row in T.rows(ctx,cid))
    for aid in current:authored.setdefault(aid,dict(name='Title #%d'%aid))
    rows=[];missing=[]
    for aid,row in sorted(authored.items()):
        reason=None
        if aid not in catalog:reason='Not in the reviewed administrator-grant catalog; earned run evidence cannot be fabricated.'
        elif aid not in client:reason='No pinned client title entry; this client cannot display a reviewed title identity.'
        status='owned' if aid in current else 'blocked' if reason else 'grant'
        if status=='grant':missing.append(aid)
        rows.append(dict(id=aid,name=(catalog.get(aid) or {}).get('display_name') or row['name'],status=status,reason=reason,
                         description=(catalog.get(aid) or {}).get('description') or '',administrator=aid in grants))
    final_count=len(set(current)|set(missing))
    limit=ready['owned_limit']
    view=dict(character=dict(id=cid,name=base['name']),titles=rows,add=missing,owned=current,expected=grants,
              selected=int(base['selected_title_id']),revision=int(base['medusa_reward_revision']),limit=limit,final_count=final_count,
              writable=bool(rb.writable),changed=bool(missing),blocked=[r for r in rows if r['status']=='blocked'],
              capacity_ok=final_count<=limit,applies='next_login')
    view['token']=hashlib.sha256(json.dumps(view,sort_keys=True,default=str).encode()).hexdigest()
    return view


@M.get(r'preview/(\d+)')
def preview(ctx,cid):return plan(ctx,cid)


def _array(values):return 'ARRAY['+','.join(str(int(v)) for v in values)+']::integer[]'


@M.post(r'apply/(\d+)')
def apply(ctx,cid):
    rb=ctx.need_rb()
    if not rb.writable:raise Refused('this editor is read-only')
    view=plan(ctx,cid);cid=int(cid)
    info=common.online_info(ctx.app,cid)
    if info.get('online'):I._online_refuse(ctx,info,'granting titles (ownership refreshes at next login)')
    if not isinstance(ctx.body,dict) or ctx.body.get('token')!=view['token']:
        raise Conflict('Title ownership, catalog or shown title changed; preview again')
    if not view['capacity_ok']:raise Bad('all supported titles would exceed the compatible ownership limit; nothing was granted')
    if not view['changed']:return dict(unchanged=True,added=0,selected=view['selected'],applies='next_login')
    # Native grant writer locks the character/account, refuses online writes,
    # backs up grants, compares their IDs and lets statement triggers own counters.
    # This extra guard compares earned+admin ownership and title selection too.
    guard="""DO $gwalltitles$ BEGIN
      IF (SELECT coalesce(array_agg(title_id ORDER BY title_id),ARRAY[]::integer[]) FROM character_title_entitlements WHERE character_id=%d) IS DISTINCT FROM %s
        OR NOT EXISTS(SELECT 1 FROM character_base WHERE id=%d AND selected_title_id=%d AND medusa_reward_revision=%d)
      THEN RAISE EXCEPTION 'gwadmin-conflict: Title ownership or selection changed; preview again'; END IF;
      END $gwalltitles$"""%(cid,_array(view['owned']),cid,view['selected'],view['revision'])
    result=T.write_batch(ctx,cid,dict(add=view['add'],remove=[],expected=view['expected'],note='Give all supported authored titles (previewed)'),guards=[guard])
    result.update(added=len(view['add']),selected=view['selected'],blocked=view['blocked'],applies='next_login')
    return result
