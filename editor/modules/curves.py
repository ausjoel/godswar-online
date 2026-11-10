"""modules/curves.py - Monster stat curve: plot level against HP / attack / defence / ratings for the monsters of one or more
maps, shape a multiplier curve (drag its points), preview, then write. Page: static/modules/curves.js.
the admin 2026-11-05 (triage B9): "north/new-map mobs 3x stronger at Lv200, scaled down for 150/160"; start from
Development/north-quests/SCALE-PROPOSAL-V2.md.

What the server does with each value (stage2-on-r3 f541eed, read for this module):
  * Level and HP live in each spawn's appearance packet (clear_bytes: level u32 @12, current HP @20, max HP @24).
  * Attack, defence, hit, dodge, critical, critical resistance are NOT stored: MonsterCombatProfileCatalog.Resolve computes
    them from the packet level (formula below, x1.15 elite, x1.3 boss, integer maths), then gm_monster_attributes
    (map, template) and gm_monster_spawn_edits (map, object) override them ("the operator's GM overrides are the last word").
  * HP: gm_monster_attributes.maximum_health / current_health are IGNORED by the server (MonsterOverridePolicy.ApplyEdit
    only reads a per-spawn edit; template attributes reach the combat profile only). So HP is written per spawn:
      - published spawns (monster_spawn_definitions): gm_monster_spawn_edits.maximum_health + current_health
        (other columns of an existing edit are kept);
      - GM spawns (gm_monster_spawns, where the north maps live): the packet's HP bytes. The HP before the first curve
        write is kept in the row's note as "[curve-base-hp N]", so applying a curve again never compounds.
  * Level is never written (SCALE-PROPOSAL-V2: gm_monster_attributes.level changes the combat profile only, while the shown
    level and kill EXP keep reading the packet, so they would drift apart).
  * Combat stats are per (map, template). When one template spawns at several levels on a map, the page shows the spread
    and you choose which level the row is computed at (lowest / middle / highest); nothing is picked silently.
All of it is start-up content: it applies after a gw251 restart. Writes go through world._write (backup, version check,
guard, change log, Undo).
"""
import re
import struct

from . import common, world as W
from .common import Bad, lit

M = common.module(
    "curves", "Monster stat curve",
    icon={"sprite": 696},
    order=61,
    status="ready",
    description="Plot monster level against HP, attack and defence for one or more maps, drag a multiplier curve, preview, "
                "and write it (per-spawn HP, per-template combat stats). After a gw251 restart.",
    needs_character=False,
)

STATS = ["physical_attack", "magic_attack", "physical_defense", "magic_defense", "hit", "dodge", "critical", "critical_resistance"]
GROUP_OF = {"physical_attack": "attack", "magic_attack": "attack", "physical_defense": "defence", "magic_defense": "defence",
            "hit": "rating", "dodge": "rating", "critical": "rating", "critical_resistance": "rating"}
GROUPS = ["hp", "attack", "defence", "rating"]
TAG_RX = re.compile(r"\[curve-base-hp (\d+)\]")
INT_MAX = 2_147_483_647
ACTOR = "gwadmin-curve"

PRESETS = [
    {"key": "north3x", "generated": True, "title": "North maps 3x at Lv200 (GENERATED)",
     "why": "GENERATED for the admin's ask (B9), not from IGG or the wiki: x1 at level 140 rising in a straight line to x3 at 200 "
            "(x1.33 at 150, x1.67 at 160, x2.33 at 180) for HP, attack and defence; hit / dodge / crit unchanged. "
            "Maps: Mycenae, Olympia, Larissa, Delphi Forest, Olympus, Elasson (the SCALE-PROPOSAL-V2 north set).",
     "maps": [6, 7, 10, 20, 21, 22],
     "curves": {"hp": [[140, 1.0], [200, 3.0]], "attack": [[140, 1.0], [200, 3.0]], "defence": [[140, 1.0], [200, 3.0]], "rating": [[1, 1.0], [200, 1.0]]}},
    {"key": "flat", "generated": False, "title": "No change (x1 everywhere)",
     "why": "Writing this removes the curve's own rows and puts every HP back to the value before the first curve write.",
     "maps": [], "curves": {g: [[1, 1.0], [200, 1.0]] for g in GROUPS}},
]


def formula(level, elite=False, boss=False):
    """MonsterCombatProfileCatalog.Resolve (integer maths, rank scale in basis points)."""
    L = max(1, min(10000, int(level)))
    bp = 13000 if boss else 11500 if elite else 10000
    s = lambda v: max(0, min(INT_MAX, (v * bp) // 10000))
    return {"physical_attack": s(21 + 3 * L + L // 3), "magic_attack": s(23 + 3 * L + L // 2),
            "physical_defense": s(10 + 6 * L + (L * L) // 10), "magic_defense": s(10 + 5 * L + (L * L) // 12),
            "hit": s(100 + 20 * L), "dodge": s(50 + 12 * L), "critical": s(25 + 8 * L), "critical_resistance": s(25 + 10 * L)}


def curve_value(points, level):
    """Piecewise-linear multiplier through the control points, flat outside them."""
    pts = sorted((float(x), float(y)) for x, y in points)
    if not pts:
        return 1.0
    if level <= pts[0][0]:
        return pts[0][1]
    for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
        if level <= x1:
            return y0 + (y1 - y0) * (level - x0) / (x1 - x0) if x1 != x0 else y1
    return pts[-1][1]


def _curves(raw):
    if not isinstance(raw, dict):
        raise Bad("curves must be {hp, attack, defence, rating: [[level, multiplier], ...]}")
    out = {}
    for g in GROUPS:
        pts = raw.get(g) or [[1, 1.0], [200, 1.0]]
        if not isinstance(pts, list) or not 1 <= len(pts) <= 12:
            raise Bad("%s: 1-12 control points" % g)
        clean = []
        for p in pts:
            try:
                x, y = float(p[0]), float(p[1])
            except (TypeError, ValueError, IndexError):
                raise Bad("%s: every point is [level, multiplier]" % g)
            if not (1 <= x <= 255 and 0.1 <= y <= 20):
                raise Bad("%s: level 1-255 and multiplier 0.1-20 (got %g, %g)" % (g, x, y))
            clean.append([round(x, 2), round(y, 4)])
        out[g] = clean
    return out


def _name(ctx, key):
    try:
        return ctx.catalog.name("monster", key)
    except Exception:          # noqa: BLE001 - a template the client catalogue lacks
        return None


def _u32(b, off):
    return struct.unpack_from("<I", b, off)[0] if len(b) >= off + 4 else None


def _maps_sql(maps):
    return "ARRAY[%s]::smallint[]" % ",".join(str(int(m)) for m in maps)


def _parts(maps):
    w = "map_id = ANY(%s)" % _maps_sql(maps)
    return [("gm_monster_attributes", w, "map_id, template_key"), ("gm_monster_spawn_edits", w, "map_id, object_id"),
            ("gm_monster_spawns", w, "id")]


def load(ctx, maps):
    """Every spawn of the maps with what the server will use: level, HP now, base HP, rank, formula stats."""
    if not maps:
        raise Bad("choose at least one map")
    arr = _maps_sql(maps)
    d = W._q(ctx, """json_build_object(
      'published', (SELECT coalesce(json_agg(json_build_object('map', d.map_id, 'object', d.object_id, 'template', d.template_key, 'name', d.display_name,
           'head', encode(substring(d.clear_bytes from 1 for 32), 'hex'))), '[]') FROM monster_spawn_definitions d
           JOIN monster_content_publication p ON p.family = 'monsters' AND p.revision = d.revision WHERE d.map_id = ANY(%(a)s)),
      'gm', (SELECT coalesce(json_agg(json_build_object('id', g.id, 'map', g.map_id, 'object', g.object_id, 'template', g.template_key, 'name', g.display_name,
           'enabled', g.enabled, 'note', g.note, 'head', encode(substring(g.clear_bytes from 1 for 32), 'hex'))), '[]') FROM gm_monster_spawns g WHERE g.map_id = ANY(%(a)s)),
      'edits', (SELECT coalesce(json_agg(to_jsonb(e)), '[]') FROM gm_monster_spawn_edits e WHERE e.map_id = ANY(%(a)s)),
      'attributes', (SELECT coalesce(json_agg(to_jsonb(t)), '[]') FROM gm_monster_attributes t WHERE t.map_id = ANY(%(a)s)),
      'ranks', (SELECT coalesce(json_agg(json_build_object('template', g.template_key, 'map', g.source_map_id, 'elite', g.is_elite, 'boss', g.is_boss)), '[]')
           FROM gameplay_monster_templates g JOIN gameplay_content_publication p ON p.revision = g.revision))""" % {"a": arr})
    exact, fallback = {}, {}
    for r in d["ranks"]:
        if r["map"] is not None:
            exact[(int(r["map"]), r["template"])] = r
        fallback.setdefault(r["template"], r)
    edits = {(int(e["map_id"]), int(e["object_id"])): e for e in d["edits"]}
    spawns = []
    for src in ("published", "gm"):
        for s in d[src]:
            b = bytes.fromhex(s["head"])
            lv, hp = _u32(b, 12), _u32(b, 24)
            key = (int(s["map"]), int(s["object"]))
            e = edits.get(key) if src == "published" else None
            if src == "gm" and not s.get("enabled", True):
                continue
            if e and e.get("enabled") is False:
                continue
            level = (e or {}).get("level") or lv
            base, tagged = hp, False
            if src == "gm":
                m = TAG_RX.search(s.get("note") or "")
                if m:
                    base, tagged = int(m.group(1)), True
            hp_now = (e or {}).get("maximum_health") or hp
            rk = exact.get((key[0], s["template"])) or fallback.get(s["template"]) or {}
            spawns.append({"source": src, "id": s.get("id"), "map": key[0], "object": key[1], "template": s["template"],
                           "name": _name(ctx, s["template"]) or s.get("name") or s["template"],
                           "level": level, "hp": hp_now, "base_hp": base, "tagged": tagged, "elite": bool(rk.get("elite")), "boss": bool(rk.get("boss")),
                           "edit": bool(e)})
    attrs = {(int(a["map_id"]), a["template_key"]): a for a in d["attributes"]}
    return spawns, attrs, edits


def _templates(spawns, attrs):
    t = {}
    for s in spawns:
        k = (s["map"], s["template"])
        x = t.setdefault(k, {"map": s["map"], "template": s["template"], "name": s["name"], "elite": s["elite"], "boss": s["boss"],
                              "levels": {}, "count": 0})
        x["levels"][s["level"]] = x["levels"].get(s["level"], 0) + 1
        x["count"] += 1
    for k, x in t.items():
        lv = sorted(x["levels"])
        x["level_list"] = lv
        a = attrs.get(k)
        x["override"] = {c: a.get(c) for c in STATS} if a else None
        x["override_by"] = a.get("updated_by") if a else None
    return t


def anchor_level(levels, anchor):
    lv = sorted(levels)
    return lv[0] if anchor == "lowest" else lv[-1] if anchor == "highest" else lv[(len(lv) - 1) // 2]


def plan(ctx, maps, curves, anchor):
    """The rows the write would produce: per-spawn HP and per-template combat stats."""
    spawns, attrs, edits = load(ctx, maps)
    temps = _templates(spawns, attrs)
    hp_rows, attr_rows, keep_rows = [], [], []
    for s in spawns:
        m = curve_value(curves["hp"], s["level"])
        new = max(1, min(INT_MAX, int(round(s["base_hp"] * m))))
        if new != s["hp"] or (s["source"] == "gm" and not s["tagged"] and abs(m - 1.0) > 1e-9):
            hp_rows.append(dict(s, new_hp=new, mult=round(m, 4)))
    for k, t in temps.items():
        L = anchor_level(t["level_list"], anchor)
        base = formula(L, t["elite"], t["boss"])
        vals, changed = {}, False
        for c in STATS:
            m = curve_value(curves[GROUP_OF[c]], L)
            v = max(0, min(INT_MAX, int(round(base[c] * m))))
            vals[c] = v if abs(m - 1.0) > 1e-9 else None
            changed = changed or vals[c] is not None
        foreign = t["override"] is not None and t["override_by"] != ACTOR
        if foreign:
            keep_rows.append({"map": t["map"], "template": t["template"], "name": t["name"], "by": t["override_by"]})
            continue
        if changed and t["override"] is not None and all(t["override"].get(c) == vals[c] for c in STATS):
            continue                       # already exactly this curve's row
        if changed or t["override"] is not None:
            attr_rows.append({"map": t["map"], "template": t["template"], "name": t["name"], "level": L, "levels": t["level_list"],
                              "elite": t["elite"], "boss": t["boss"], "base": base, "values": vals if changed else None})
    return spawns, temps, hp_rows, attr_rows, keep_rows


@M.get("maps")
def maps(ctx):
    rows = W._q(ctx, """(SELECT coalesce(json_agg(r ORDER BY r.map_id), '[]') FROM (SELECT m.map_id,
        (SELECT count(*) FROM monster_spawn_definitions d JOIN monster_content_publication p ON p.family = 'monsters' AND p.revision = d.revision
          WHERE d.map_id = m.map_id) AS published,
        (SELECT count(*) FROM gm_monster_spawns g WHERE g.map_id = m.map_id AND g.enabled) AS gm FROM map_templates m) r
        WHERE r.published + r.gm > 0)""")
    for r in rows:
        r["name"] = W._map_name(ctx, r["map_id"])
    return {"maps": rows, "presets": PRESETS, "groups": GROUPS}


@M.post("load")
def load_route(ctx):
    mp = [W._int(m, 0, 255, "map") for m in (ctx.body or {}).get("maps") or []]
    spawns, attrs, edits = load(ctx, mp)
    temps = _templates(spawns, attrs)
    return {"spawns": spawns, "templates": [dict(v, levels=None) for v in temps.values()], "version": W._version(ctx, _parts(mp)),
            "maps": mp, "formula": "MonsterCombatProfileCatalog.Resolve"}


@M.post("preview")
def preview(ctx):
    b = ctx.body or {}
    mp = [W._int(m, 0, 255, "map") for m in b.get("maps") or []]
    cv = _curves(b.get("curves"))
    anchor = b.get("anchor") if b.get("anchor") in ("lowest", "middle", "highest") else "middle"
    spawns, temps, hp_rows, attr_rows, keep = plan(ctx, mp, cv, anchor)
    spread = [{"map": t["map"], "template": t["template"], "name": t["name"], "levels": t["level_list"]} for t in temps.values() if len(t["level_list"]) > 1]
    return {"hp_rows": len(hp_rows), "attr_rows": len(attr_rows), "kept_foreign": keep, "spread": spread,
            "sample": attr_rows[:40], "anchor": anchor}


@M.post("apply")
def apply(ctx):
    b = ctx.body or {}
    mp = [W._int(m, 0, 255, "map") for m in b.get("maps") or []]
    cv = _curves(b.get("curves"))
    anchor = b.get("anchor") if b.get("anchor") in ("lowest", "middle", "highest") else "middle"
    spawns, temps, hp_rows, attr_rows, keep = plan(ctx, mp, cv, anchor)
    note = str(b.get("note") or "")[:200]
    stmts = [W._ver_guard(_parts(mp), b.get("version"), "the monsters of these maps")]
    for r in hp_rows:
        if r["source"] == "published":
            m = r["mult"]
            hp = None if abs(m - 1.0) < 1e-9 else r["new_hp"]
            stmts.append("INSERT INTO gm_monster_spawn_edits (map_id, object_id, maximum_health, current_health, note, updated_by) VALUES "
                         "(%d, %d, %s, %s, %s, %s) ON CONFLICT (map_id, object_id) DO UPDATE SET maximum_health = EXCLUDED.maximum_health, "
                         "current_health = EXCLUDED.current_health, updated_at = now(), updated_by = EXCLUDED.updated_by" % (
                             r["map"], r["object"], "NULL" if hp is None else hp, "NULL" if hp is None else hp,
                             lit("monster stat curve" + (": " + note if note else "")), lit(ACTOR)))
        else:
            le = struct.pack("<I", r["new_hp"]).hex()
            tag = "[curve-base-hp %d]" % r["base_hp"]
            stmts.append("UPDATE gm_monster_spawns SET clear_bytes = overlay(overlay(clear_bytes placing decode(%s, 'hex') from 21 for 4) "
                         "placing decode(%s, 'hex') from 25 for 4), note = CASE WHEN note ~ '\\[curve-base-hp [0-9]+\\]' THEN note ELSE "
                         "btrim(note || ' ' || %s) END, updated_at = now(), updated_by = %s WHERE id = %d" % (lit(le), lit(le), lit(tag), lit(ACTOR), r["id"]))
    # empty curve edits (every override column NULL, enabled, not moved) are removed so they leave nothing behind
    stmts.append("DELETE FROM gm_monster_spawn_edits WHERE map_id = ANY(%s) AND updated_by = %s AND enabled AND pos_x IS NULL AND pos_z IS NULL "
                 "AND facing IS NULL AND level IS NULL AND maximum_health IS NULL AND current_health IS NULL AND physical_attack IS NULL AND "
                 "magic_attack IS NULL AND physical_defense IS NULL AND magic_defense IS NULL AND hit IS NULL AND dodge IS NULL AND critical IS NULL "
                 "AND critical_resistance IS NULL" % (_maps_sql(mp), lit(ACTOR)))
    for r in attr_rows:
        if r["values"] is None:
            stmts.append("DELETE FROM gm_monster_attributes WHERE map_id = %d AND template_key = %s AND updated_by = %s" % (r["map"], lit(r["template"]), lit(ACTOR)))
            continue
        row = {"map_id": r["map"], "template_key": r["template"], "display_name": "", "level": None, "current_health": None, "maximum_health": None,
               "note": "monster stat curve at level %d%s" % (r["level"], (": " + note) if note else ""), "updated_by": ACTOR}
        row.update(r["values"])
        stmts.append(W._upsert("gm_monster_attributes", ["map_id", "template_key"], row))
    if len(stmts) <= 2 and not hp_rows and not attr_rows:
        raise Bad("nothing to change: every value already follows this curve")
    names = ", ".join(W._map_name(ctx, m) for m in mp)
    act = "monster stat curve on %s: %d HP values, %d combat-stat rows%s" % (names, len(hp_rows), len(attr_rows),
                                                                           " (kept %d rows written by others)" % len(keep) if keep else "")
    w = "map_id = ANY(%s)" % _maps_sql(mp)
    r = W._write(ctx, [common.scope("gm_monster_attributes", w), common.scope("gm_monster_spawn_edits", w), common.scope("gm_monster_spawns", w)],
                 act, stmts, ["gm_monster_attributes.*", "gm_monster_spawn_edits.*", "gm_monster_spawns.*"], guard=["gm_monsters"],
                 applies="after_restart", target="monster curve")
    r.update(hp_rows=len(hp_rows), attr_rows=len(attr_rows), kept_foreign=keep)
    return r
