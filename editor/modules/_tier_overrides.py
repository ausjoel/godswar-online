"""Shared drop/respawn-tier editors for the server-release tables in the E1 handoff.

No schema creation: a missing table disables the page until the server release creates it.
All mutations use the normal backup/audit/Undo engine and a table-version guard.
"""
from . import common, world as W, _monster_sections as S
from .common import Bad, NotFound, lit

POLICIES = {
    'drop': {'module': 'drop_tiers', 'title': 'Drop tiers', 'table': 'drop_tier_overrides',
             'tiers': ['normal', 'elite', 'boss'], 'columns': ['any_drop_basis_points'], 'order': 62},
    'respawn': {'module': 'respawn_tiers', 'title': 'Respawn tiers', 'table': 'respawn_tier_overrides',
                'tiers': ['normal', 'elite'], 'columns': ['minimum_seconds', 'maximum_seconds'], 'order': 63},
}


def policy(kind):
    if kind not in POLICIES:
        raise Bad('Unknown tier editor.')
    return POLICIES[kind]


def parts(p):
    return [(p['table'], 'true', 'map_id, tier')]


def exists(ctx, p):
    return bool(W._v(ctx, "(SELECT to_regclass('public.%s') IS NOT NULL)" % p['table']))


def missing(p):
    return 'Table missing until the server release: %s. Nothing can be saved until that release creates it.' % p['table']


def reload_game(ctx):
    port = common.mgmt_port(ctx.app)
    if port is None:
        return {'ok': False, 'message': 'Saved data is awaiting game reload; no game management connection is configured.'}
    try:
        code, result = common._http('POST', port, '/npc-content/reload', timeout=4)
        if 200 <= code < 300 and not (isinstance(result, dict) and result.get('ok') is False):
            return {'ok': True, 'message': 'Game data reload requested. The server release must support these tier overrides.'}
    except Exception:  # The write already succeeded; report pending reload without asking users to repeat it.
        pass
    return {'ok': False, 'message': 'Saved data is awaiting game reload; the game server did not confirm the reload request.'}


# Wave 4 D item 6 ("drop tiers and respawn tiers don't show all the map data"). Cause: the page listed only the saved
# override rows plus a 0-255 drop-down (175 of its entries were "Map #N" for ids that do not exist), and no map showed
# what it actually uses. Now every real map is listed (client catalogue + map_templates + any map with spawns or a saved
# row), grouped by section, with its monster counts per tier and the effective value per tier and where it comes from.
MAP_FACTS = """json_build_object(
  'templates', CASE WHEN to_regclass('public.map_templates') IS NULL THEN '[]'::json ELSE
     (SELECT coalesce(json_agg(json_build_object('id', map_id, 'scene', scene_key) ORDER BY map_id), '[]') FROM map_templates) END,
  'spawns', (SELECT coalesce(json_agg(r ORDER BY r.map_id), '[]') FROM (
     SELECT s.map_id, count(*) FILTER (WHERE lower(s.k) LIKE '%%\\_boss%%') AS boss,
            count(*) FILTER (WHERE lower(s.k) LIKE '%%\\_elite%%' AND lower(s.k) NOT LIKE '%%\\_boss%%') AS elite,
            count(*) FILTER (WHERE lower(s.k) NOT LIKE '%%\\_elite%%' AND lower(s.k) NOT LIKE '%%\\_boss%%') AS normal
     FROM (%s) s(map_id, k) GROUP BY s.map_id) r))"""
SPAWN_SOURCES = [
    ("monster_spawn_definitions", "SELECT d.map_id, d.template_key FROM monster_spawn_definitions d JOIN monster_content_publication p "
                                  "ON p.family = 'monsters' AND p.revision = d.revision"),
    ("gm_monster_spawns", "SELECT g.map_id, g.template_key FROM gm_monster_spawns g WHERE g.enabled"),
]
DEFAULTS = {'drop': 'no gate: every kill rolls its own loot rules', 'respawn': "each spawn's own respawn interval"}


def map_facts(ctx):
    """Map templates and per-map spawn counts by server tier. Tables the database lacks are left out."""
    have = [sql for table, sql in SPAWN_SOURCES
            if W._v(ctx, "(SELECT to_regclass('public.%s') IS NOT NULL)" % table)]
    union = " UNION ALL ".join(have) if have else "SELECT NULL::smallint, NULL::text WHERE false"
    got = W._q(ctx, MAP_FACTS % union)
    return got if isinstance(got, dict) else {'templates': [], 'spawns': []}


def effective(rows, mid, tier):
    """(value-row, source) the server uses for one map/tier: the map's own row, else the all-maps row, else default."""
    own = rows.get((mid, tier))
    if own is not None:
        return own, 'map'
    allm = rows.get((-1, tier))
    if allm is not None:
        return allm, 'all_maps'
    return None, 'default'


def all_maps(ctx, rows, facts):
    cat = ctx.catalog.file('maps') or {}
    tmpl = {int(t['id']): t for t in facts.get('templates') or []}
    spawns = {int(s['map_id']): s for s in facts.get('spawns') or [] if s.get('map_id') is not None}
    ids = {int(k) for k in cat if str(k).lstrip('-').isdigit()} | set(tmpl) | set(spawns)
    ids |= {int(r['map_id']) for r in rows if int(r['map_id']) >= 0}
    out = []
    for mid in sorted(i for i in ids if 0 <= i <= 255):
        info = S.map_info(mid, (cat.get(str(mid)) or {}).get('name'))
        s = spawns.get(mid) or {}
        info['monsters'] = {t: int(s.get(t) or 0) for t in ('normal', 'elite', 'boss')}
        info['scene'] = (tmpl.get(mid) or {}).get('scene') or (cat.get(str(mid)) or {}).get('scene') or ''
        info['in_catalog'] = str(mid) in cat
        info['in_server'] = mid in tmpl if tmpl else None
        out.append(info)
    return out


def overview(ctx, kind):
    p = policy(kind)
    rb = ctx.need_rb()
    have = exists(ctx, p)
    rows = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(t) ORDER BY map_id, tier), '[]') FROM %s t)" % p['table']) if have else []
    try:
        facts = map_facts(ctx)
    except Exception:  # noqa: BLE001 - the page still lists the catalogue maps without spawn facts
        facts = {'templates': [], 'spawns': []}
    maps = all_maps(ctx, rows, facts)
    names = {m['id']: m['name'] for m in maps}
    for row in rows:
        mid = row['map_id']
        row['map_name'] = 'All maps' if mid == -1 else names.get(mid) or 'Map #%s' % mid
        row['section'] = 'all' if mid == -1 else S.section_of(mid)
    keyed = {(int(r['map_id']), r['tier']): r for r in rows}
    for m in maps:
        eff = {}
        for tier in p['tiers']:
            row, source = effective(keyed, m['id'], tier)
            eff[tier] = {'source': source, 'row': row}
        m['effective'] = eff
        m['overrides'] = sum(1 for t in p['tiers'] if (m['id'], t) in keyed)
    return {'kind': kind, 'table': have, 'table_name': p['table'], 'rows': rows,
            'maps': maps, 'groups': S.grouped(maps), 'sections': [{'id': k, 'title': t} for k, t, _ in S.SECTIONS],
            'tiers': p['tiers'], 'writable': bool(rb.writable), 'default_text': DEFAULTS[kind],
            'version': W._version(ctx, parts(p)) if have else None,
            'why_missing': None if have else missing(p), 'applies': 'game_reload'}


def key(body, p):
    mid = common.to_int(body.get('map_id'), -1, 255, 'Map ID')
    tier = body.get('tier')
    if tier not in p['tiers']:
        raise Bad('Tier must be one of: %s.' % ', '.join(p['tiers']))
    return mid, tier


def mutate(ctx, kind, delete=False):
    p = policy(kind)
    if not exists(ctx, p):
        raise Bad(missing(p))
    W._need_writable(ctx)
    body = ctx.body if ctx.body is not None else {}
    if not isinstance(body, dict):
        raise Bad('The request body must be a JSON object.')
    mid, tier = key(body, p)
    where = 'map_id = %s AND tier = %s' % (lit(mid), lit(tier))
    statements = [
        # Serialize missing-row inserts too; row locks alone cannot protect an absent key.
        'LOCK TABLE public.%s IN SHARE ROW EXCLUSIVE MODE' % p['table'],
        W._ver_guard(parts(p), body.get('version'), p['title']),
    ]
    if delete:
        if not W._v(ctx, '(SELECT EXISTS (SELECT 1 FROM %s WHERE %s))' % (p['table'], where)):
            raise NotFound('This override no longer exists; reload the page.')
        statements.append('DELETE FROM %s WHERE %s' % (p['table'], where))
        action = 'Remove %s override for map %s / %s' % (kind, mid, tier)
    else:
        if not isinstance(body.get('note', ''), str):
            raise Bad('Note must be text.')
        values = {'map_id': mid, 'tier': tier, 'note': W._text(body.get('note', ''), 'Note', 200, empty=True)}
        if kind == 'drop':
            values['any_drop_basis_points'] = common.to_int(body.get('any_drop_basis_points'), 0, 10000, 'Drop chance (basis points)')
        else:
            values['minimum_seconds'] = common.to_int(body.get('minimum_seconds'), 15, 86400, 'Minimum respawn seconds')
            values['maximum_seconds'] = common.to_int(body.get('maximum_seconds'), 15, 86400, 'Maximum respawn seconds')
            if values['minimum_seconds'] > values['maximum_seconds']:
                raise Bad('Minimum respawn seconds cannot exceed maximum respawn seconds.')
        statements.append(W._upsert(p['table'], ['map_id', 'tier'], values, touch=False))
        action = 'Save %s override for map %s / %s' % (kind, mid, tier)
    result = W._write(ctx, [common.scope(p['table'], where, pk=['map_id', 'tier'])], action,
                      statements, [p['table'] + '.*'], applies='game_reload', target='%s map %s / %s' % (kind, mid, tier))
    result['reload'] = reload_game(ctx)
    return result


def register(kind):
    p = policy(kind)
    module = common.module(p['module'], p['title'], order=p['order'], status='ready', needs_character=False,
                           description='Per-map or all-map %s overrides. Save, Undo and reload game data.' % kind)
    module.get('overview')(lambda ctx: overview(ctx, kind))
    module.post('save')(lambda ctx: mutate(ctx, kind))
    module.post('delete')(lambda ctx: mutate(ctx, kind, delete=True))

    @module.post('reload')
    def reload_route(ctx):
        if not exists(ctx, p):
            raise Bad(missing(p))
        W._need_writable(ctx)
        return reload_game(ctx)
    return module
