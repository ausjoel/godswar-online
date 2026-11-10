"""Administrative title entitlements, independent of earned completion evidence.
Writes use the shared offline transaction/backup/Undo path. Candidate server v1
and additive schema must both be present; an old 64-title reader is incompatible.
"""
from .common import Bad, Conflict, Refused, to_int, lit

TABLE = 'character_title_grants'

def support(ctx):
    caps = ctx.caps
    feature = caps.get('title_grants') or {}
    if not caps.get('available') or not isinstance(feature,dict) or type(feature.get('version')) is not int or feature.get('version') != 1 or type(feature.get('ownedLimit')) is not int or feature['ownedLimit'] < 128:
        return {'available':False, 'reason':'The running game server needs the compatible title-grant update.'}
    ready = ctx.need_rb().pg.json("(SELECT to_jsonb(to_regclass('public.character_title_grants') IS NOT NULL AND to_regclass('public.editor_title_catalog') IS NOT NULL AND to_regclass('public.character_title_entitlements') IS NOT NULL))")
    if ready is not True:
        return {'available':False, 'reason':'The title-grant database update is not installed.'}
    return {'available':True, 'owned_limit':feature['ownedLimit']}

def rows(ctx, cid):
    return ctx.need_rb().pg.json("(SELECT coalesce(json_agg(g ORDER BY g.title_id), '[]') FROM character_title_grants g WHERE g.character_id = %d)" % int(cid)) or []

def catalogue(ctx):
    return ctx.need_rb().pg.json("(SELECT coalesce(json_agg(t ORDER BY t.title_id), '[]') FROM editor_title_catalog t)") or []

def ids(value, name):
    if not isinstance(value,list) or len(value)>128:raise Bad(name+' must be a list of at most 128 title IDs')
    values=[to_int(x,1,2**31-1,name) for x in value]
    if len(set(values))!=len(values):raise Bad(name+' contains duplicate title IDs')
    return sorted(values)

def write(ctx,cid):
    return write_batch(ctx,cid,ctx.body)


def write_batch(ctx,cid,body,*,guards=()):
    """Shared authoritative grant path; guards are trusted SQL from a reviewed preview planner."""
    if not isinstance(body,dict):raise Bad("title grant request must be an object")
    cid=to_int(cid,1,2**31-1,'character ID');ready=support(ctx)
    if not ready['available']:raise Refused(ready['reason'])
    add=ids(body.get('add',[]),'add');remove=ids(body.get('remove',[]),'remove')
    expected=ids(body.get('expected'),'expected grant set')
    if set(add)&set(remove):raise Bad('a title cannot be added and removed together')
    if not add and not remove:raise Bad('select at least one title to add or remove')
    known={int(t['title_id']) for t in catalogue(ctx)}
    if set(add)-known:raise Bad('unknown title IDs: '+', '.join(map(str,sorted(set(add)-known))))
    note=body.get('note','')
    if not isinstance(note,str) or len(note)>512:raise Bad('grant note must be text up to 512 characters')
    expected_sql='ARRAY['+','.join(map(str,expected))+']::integer[]'
    def build(d):
        if TABLE not in d:raise Refused('Restart the updated editor so grants are included in backups and Undo.')
        current=sorted(int(t['title_id']) for t in d[TABLE])
        if current!=expected:raise Conflict('Title grants changed since this page was loaded. Reload before editing.')
        actual_add=sorted(set(add)-set(current));actual_remove=sorted(set(remove)&set(current))
        if not actual_add and not actual_remove:raise Bad('the administrator grants already match this request')
        # Recheck under the character/account row locks, after any competing edit.
        check="DO $gwtitle$ BEGIN IF (SELECT coalesce(array_agg(title_id ORDER BY title_id), ARRAY[]::integer[]) FROM character_title_grants WHERE character_id = %d) IS DISTINCT FROM %s THEN RAISE EXCEPTION 'gwadmin-conflict: Title grants changed; reload before editing'; END IF; END $gwtitle$" % (cid,expected_sql)
        out=[*guards,check]
        if actual_remove:out.append('DELETE FROM character_title_grants WHERE character_id = %d AND title_id IN (%s)' % (cid,','.join(map(str,actual_remove))))
        if actual_add:out.append("INSERT INTO character_title_grants (character_id,title_id,granted_by,note) VALUES "+','.join("(%d,%d,'gwadmin-web',%s)" % (cid,i,lit(note)) for i in actual_add))
        out.append("DO $gwtitle$ BEGIN IF (SELECT count(*) FROM character_title_entitlements WHERE character_id = %d) > %d THEN RAISE EXCEPTION 'gwadmin: title ownership exceeds the compatible reader limit'; END IF; END $gwtitle$" % (cid,ready['owned_limit']))
        return out
    # Database statement triggers own revision bumps and clear revoked grant-only
    # selections. Never modify earned title tables or manually bump revisions.
    result=ctx.char_write(cid,'administrator title grants: add %s; remove %s' % (add,remove),build,
                          fields=[TABLE+'.*'],cross_bumps=False)
    result['mode']='database'
    return result
