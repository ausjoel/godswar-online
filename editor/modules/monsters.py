"""modules/monsters.py - Monster progression: every monster grouped by section (regular maps, instances, events,
battlefields...) on a level chart, with its in-game name, icon, rank, maps, levels and loot-table status.
Page: static/modules/monsters.js. Also serves the shared monster index that static/shared/monster_label.js uses to show
"icon + in-game name (ID)" wherever the editor shows a monster ID (wave 4 D item 1).

Names: the client's Monster.ini Name (catalog-origin/monsters.json, per map when the map's file differs), else the
server's monster_templates.display_name. Icons: static/monster-thumbs/<template_key>.png wins, else
static/monster-thumbs/<model stem>.png (the model file without .jcs). Package E (model thumbnails) writes into that folder;
the first 116 come from Development/mob-reference/img (64 px). No icon = the label shows the name only.
Read-only: nothing here writes.
"""
import os

from . import common, world as W, _monster_sections as S

M = common.module(
    "monsters", "Monster progression", icon={"sprite": 1063}, order=64, status="ready", needs_character=False,
    description="Every monster by section on a level chart: in-game name, icon, rank, maps, levels and loot table.")

THUMBS = os.path.join(common.HERE, "static", "monster-thumbs")
THUMB_URL = "/static/monster-thumbs/"


def thumbs():
    try:
        return {f[:-4] for f in os.listdir(THUMBS) if f.endswith(".png")}
    except OSError:
        return set()


def thumb_for(key, model, have):
    if key in have:
        return THUMB_URL + key + ".png"
    stem = (model or "").rsplit(".", 1)[0]
    return THUMB_URL + stem + ".png" if stem and stem in have else None


def _level(col):
    # Level u32 at byte 12 of a spawn's appearance packet (modules/curves.py: clear_bytes level @12).
    return ("(get_byte(%(c)s, 12) + get_byte(%(c)s, 13) * 256 + get_byte(%(c)s, 14) * 65536)" % {"c": col})


def facts(ctx):
    """Templates, spawns (map, template, level, count) and loot table keys, each only when its table exists."""
    have = lambda t: bool(W._v(ctx, "(SELECT to_regclass('public.%s') IS NOT NULL)" % t))
    parts = []
    if have("monster_templates"):
        parts.append("'templates', (SELECT coalesce(json_agg(json_build_object('key', t.template_key, 'name', t.display_name, "
                     "'map', t.source_map_id, 'boss', t.is_boss, 'elite', t.is_elite, 'model', t.model_file)), '[]') FROM "
                     "(SELECT DISTINCT ON (template_key) * FROM monster_templates ORDER BY template_key, source_key) t)")
    spawn = []
    if have("monster_spawn_definitions") and have("monster_content_publication"):
        edits = have("gm_monster_spawn_edits")      # an edit's level / enabled wins over the published spawn
        spawn.append("SELECT d.map_id, d.template_key, %s AS lv FROM monster_spawn_definitions d JOIN monster_content_publication p "
                     "ON p.family = 'monsters' AND p.revision = d.revision %s WHERE length(d.clear_bytes) >= 16%s" % (
                         "coalesce(e.level, %s)" % _level("d.clear_bytes") if edits else _level("d.clear_bytes"),
                         "LEFT JOIN gm_monster_spawn_edits e ON e.map_id = d.map_id AND e.object_id = d.object_id" if edits else "",
                         " AND e.enabled IS DISTINCT FROM false" if edits else ""))
    if have("gm_monster_spawns"):
        spawn.append("SELECT g.map_id, g.template_key, %s FROM gm_monster_spawns g WHERE g.enabled AND length(g.clear_bytes) >= 16"
                     % _level("g.clear_bytes"))
    if spawn:
        parts.append("'spawns', (SELECT coalesce(json_agg(json_build_object('map', s.map_id, 'key', s.template_key, 'lv', s.lv, "
                     "'n', s.n)), '[]') FROM (SELECT map_id, template_key, lv, count(*) AS n FROM (%s) x GROUP BY 1, 2, 3) s)"
                     % " UNION ALL ".join(spawn))
    if have("monster_loot_tables"):
        parts.append("'loot', (SELECT coalesce(json_agg(template_key), '[]') FROM monster_loot_tables)")
    if not parts:
        return {}
    got = W._q(ctx, "json_build_object(%s)" % ", ".join(parts))
    return got if isinstance(got, dict) else {}


def build_index(ctx):
    cat = ctx.catalog.file("monsters") or {}
    cmaps = ctx.catalog.file("maps") or {}
    try:
        f = facts(ctx)
    except Exception:  # noqa: BLE001 - without the database the index still lists the client catalogue
        f = {}
    have = thumbs()
    loot = set(f.get("loot") or [])
    area = {}
    for k in loot:
        if "@" in k:
            area.setdefault(k.split("@", 1)[0], []).append(int(k.split("@", 1)[1]))
    db = {t["key"]: t for t in f.get("templates") or []}
    sp = {}
    for s in f.get("spawns") or []:
        x = sp.setdefault(s["key"], {})
        x.setdefault(int(s["map"]), {})[int(s["lv"])] = x.get(int(s["map"]), {}).get(int(s["lv"]), 0) + int(s["n"])
    out = []
    for key in sorted(set(cat) | set(db)):
        c, d = cat.get(key) or {}, db.get(key) or {}
        per = sp.get(key) or {}
        levels = sorted({lv for m in per.values() for lv in m})
        counts = {}
        for m in per.values():
            for lv, n in m.items():
                counts[lv] = counts.get(lv, 0) + n
        maps = sorted(per) or sorted({int(m) for m in c.get("maps") or []} | ({int(d["map"])} if d.get("map") is not None else set()))
        main = max(per, key=lambda m: (sum(per[m].values()), -m)) if per else (maps[0] if maps else None)
        rank = S.rank_of(key, d.get("boss") if d else None, d.get("elite") if d else None, c.get("name") or d.get("name"))
        if not d and c.get("rank") in ("boss", "elite", "normal"):
            rank = c["rank"]
        out.append({
            "key": key, "name": c.get("name") or d.get("name") or key, "names_by_map": c.get("names_by_map") or {},
            "rank": rank, "tier": S.server_tier(key), "tier_note": S.tier_note(key, rank),
            "maps": maps, "map": main, "section": S.section_of(main) if main is not None else "other",
            "levels": levels, "level": (max(counts, key=lambda lv: (counts[lv], lv)) if counts else None),
            "level_min": levels[0] if levels else None, "level_max": levels[-1] if levels else None,
            "spawns": sum(counts.values()), "per_map": {str(m): sorted(v) for m, v in per.items()},
            "loot": key in loot, "area_loot": sorted(area.get(key, [])),
            "thumb": thumb_for(key, c.get("model") or d.get("model"), have),
            "model": c.get("model") or d.get("model") or "",
        })
    used = {m for x in out for m in x["maps"]}
    maps = {str(m): S.map_info(m, (cmaps.get(str(m)) or {}).get("name")) for m in sorted(used)}
    return {"monsters": out, "maps": maps, "sections": [{"id": k, "title": t, "why": w} for k, t, w in S.SECTIONS],
            "thumbs": len(have), "db": bool(f), "thumb_contract": "static/monster-thumbs/<template_key>.png or <model stem>.png"}


@M.get("index")
def index(ctx):
    return build_index(ctx)


@M.get("progression")
def progression(ctx):
    """The chart rows: per section, its maps in level order, each with the monsters that spawn there."""
    d = build_index(ctx)
    rows = {}
    for m in d["monsters"]:
        for mid, lvs in m["per_map"].items():
            r = rows.setdefault(int(mid), [])
            r.append({"key": m["key"], "name": (m["names_by_map"] or {}).get(mid) or m["name"], "rank": m["rank"], "tier": m["tier"],
                      "levels": lvs, "thumb": m["thumb"], "loot": m["loot"] or int(mid) in m["area_loot"]})
    # Monsters the server spawns in code (instances, events) have no spawn rows: show them on their client map at the map's
    # typical level, marked as guessed.
    for m in d["monsters"]:
        if m["per_map"]:
            continue
        for mid in m["maps"]:
            if S.section_of(mid) in ("instance", "event") and mid in S.TYPICAL_LEVEL:
                rows.setdefault(int(mid), []).append({"key": m["key"], "name": (m["names_by_map"] or {}).get(str(mid)) or m["name"],
                                                      "rank": m["rank"], "tier": m["tier"], "levels": [S.TYPICAL_LEVEL[mid]],
                                                      "thumb": m["thumb"], "loot": m["loot"] or int(mid) in m["area_loot"], "guessed": True})
    maps = []
    for mid, mons in rows.items():
        info = dict(d["maps"].get(str(mid)) or S.map_info(mid))
        lv = sorted(x for mon in mons for x in mon["levels"])
        info.update(monsters=sorted(mons, key=lambda x: (x["levels"][0] if x["levels"] else 0, x["name"])),
                    level_min=lv[0] if lv else None, level_max=lv[-1] if lv else None,
                    level_median=lv[len(lv) // 2] if lv else None, no_loot=sum(1 for x in mons if not x["loot"]))
        maps.append(info)
    groups = S.grouped(maps)
    for g in groups:                       # inside a section: by median level (game progression), towns first
        g["maps"].sort(key=lambda m: (not m.get("town"), m["level_median"] or 0, m["id"]))
    drawn = {x["key"] for r in rows.values() for x in r}
    unspawned = [m for m in d["monsters"] if m["key"] not in drawn]
    return {"groups": groups, "unspawned": len(unspawned), "db": d["db"], "thumbs": d["thumbs"],
            "unspawned_by_section": {s: sum(1 for m in unspawned if m["section"] == s) for s, _, _ in S.SECTIONS}}
