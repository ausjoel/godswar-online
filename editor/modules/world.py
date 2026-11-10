"""modules/world.py - the "World & content" editor. Contract: ../MODULES.md. Owned by the world module task: only this
file, static/modules/world.js and static/modules/world.css change for it.

What it edits, and when the game sees it (schema/WRITE-RULES.md s.3 and s.5):

  * Monster loot (monster_loot_tables / monster_loot_rules), Medusa Island rules (medusa_monster_rules,
    medusa_monster_loot_rules, medusa_completion_reward_rules, medusa_reward_title_definitions), GM monster
    placements and stat overrides (gm_monster_spawns / _spawn_edits / _attributes), GM NPCs and their dialogue
    (gm_npc_spawns / gm_npc_dialogues / _pages / _buttons), quest reward overrides (quest_reward_values /
    quest_reward_slots): START-UP content. The running server keeps the snapshot it read at start-up, so a change
    applies after "gw251 restart". A malformed row makes the NEXT start-up throw (the snapshot constructors in
    src/Godswar.Server validate every row), so every write here re-runs the same validation as SQL before COMMIT
    (CHECKS below; also run again on Undo) and GET /api/m/world/preflight runs all of them read-only.
  * Dungeon entry limits (legacy_instance_settings: Atlantis, Wonderland, Harbor Attack; medusa_instance_settings):
    read at each use -> applies at the next entry ("now").
  * EXP events: character_experience_modifiers rows (one per character, kind EVENT_KIND, source "gm-event:<name>").
    Offline characters only (write rule: the client keeps the old buff icon until relog); online characters are
    listed as skipped and get the event when it is applied again after they log out.
  * Event gifts: an item, Silver, Gold or B-Gold for every character of a group, through the framework's shared save
    path (ctx.bag_item / ctx.set_character_fields): an ONLINE character gets it at once through the round-2 server
    (the player sees it), an offline one through the guarded database write (next login).
  * Read-only views with the reason: world bosses (published gameplay content), the realm calendar / time zone
    (the realm row is guarded by triggers; change it with gw251 tools), Faction Crier and Online Award balances
    (sealed balance publications; the Online Award is edited by modules/awards.py since 2026-11-05), NPC shops and the Mall
    (the compiled lists; edited through npc_shop_overrides by modules/npcs.py), the global kill-EXP settings
    (edited on the Server rates page).

Every write goes through ctx.rows_write (backup, guarded transaction, change log, Undo) with a version check: each
GET returns a version (md5 of the rows as stored) and each save sends it back; a different version answers 409.
"""
import datetime
import hashlib
import json
import re
import math
import struct
import os
import socket
import threading
import uuid
from pathlib import Path
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError
from decimal import Decimal

from . import common
from .common import Bad, Refused, NotFound, lit, ident

M = common.module(
    "world", "World & content",
    icon={"sprite": 696},          # catalog-icons.png tile
    order=60,
    status="ready",
    description="Monster loot, Medusa Island, GM monsters and NPCs, quest rewards, dungeon entries, EXP events and "
                "gifts, world bosses, calendar, shops. Shows when each change reaches the game.",
    needs_character=False,
)

LOOT_CLIENT_MAX = 8            # POINTS-AUDIT: the Origin client's loot window shows at most 8 drops
LOOT_RULES_MAX = 32            # MonsterLootContentSnapshot: loot_index 0..31
QUEST_SLOTS = 8                # QuestRewardContentSnapshot.MaximumRewardSlots
GM_OBJECT_FIRST, GM_OBJECT_LAST = 46_000, 49_999      # tools/Godswar.LootTool MonsterStore.FirstGmObjectId..Last
GM_MONSTER_MAX, GM_NPC_MAX = 20_000, 2_000            # MonsterOverrideSnapshot / GmNpcOverrideSnapshot.MaximumSpawns
EVENT_KIND = 200               # character_experience_modifiers.kind for editor EXP events: not used by the server
                               # (ExperienceBoostKinds 14,20,22-25,100,101,1008-1010), so no potion or weekend claim
                               # overwrites it; AffectsFighter(200) = true (fighter EXP only)
EVENT_PREFIX = "gm-event:"
EVENT_STATUSES = [511, 1007, 508, 586, 512, 585]      # EXP buffs the client knows (icon + its own tooltip text)
INT_MAX = 2_147_483_647
# appearance packet offsets (tools/Godswar.LootTool MonsterStore.cs BuildPacket)
P_OBJECT, P_LEVEL, P_CUR_HP, P_MAX_HP, P_X, P_Z, P_FACING = 8, 12, 20, 24, 28, 36, 40
DEFAULT_FUNCTION_FLAG = 85     # NpcDialogueStore.DefaultFunctionFlag: the client's unused Set_NpcFun_Text slot
INSTANCE_NAMES = {1: "Atlantis", 2: "Wonderland", 3: "Harbor Attack"}
DIFFICULTY = {1: "Normal", 2: "Hard", 3: "Hell"}
REWARD_KIND = {1: "Not finished: score at least", 2: "Finished within (seconds)"}
STAT_COLS = ["level", "current_health", "maximum_health", "physical_attack", "magic_attack", "physical_defense",
             "magic_defense", "hit", "dodge", "critical", "critical_resistance"]
ATTR_COLS = [c for i in range(1, 6) for c in ("attribute%d" % i, "attribute_level%d" % i)]


# =========================================================================== small helpers
def _rb(ctx):
    return ctx.need_rb()


def _plain(o):
    """Decimal (psql numerics) -> int / float, so the page gets numbers, not strings."""
    if isinstance(o, Decimal):
        return int(o) if o == o.to_integral_value() and abs(o) < 2 ** 53 and "." not in str(o) else float(o)
    if isinstance(o, list):
        return [_plain(x) for x in o]
    if isinstance(o, dict):
        return {k: _plain(v) for k, v in o.items()}
    return o


def _q(ctx, sql):
    """one read-only SQL expression that yields json (pg.json)."""
    return _plain(_rb(ctx).pg.json(sql))


def _v(ctx, expr):
    """one scalar SQL expression (boolean, number, text) -> Python value."""
    return _plain(_rb(ctx).pg.json("to_jsonb(%s)" % expr))


def _need_writable(ctx):
    rb = _rb(ctx)
    if not rb.writable:
        raise Refused("this editor is read-only for database %s (start it on a copy, or with --live for the live "
                      "database)" % rb.db)
    return rb


def _int(v, lo, hi, what, null=False):
    if v is None or v == "":
        if null:
            return None
        raise Bad("%s is required" % what)
    if isinstance(v, bool):
        raise Bad("%s must be a whole number" % what)
    try:
        f = float(v)
    except (TypeError, ValueError):
        raise Bad("%s must be a whole number (got %r)" % (what, v))
    if f != int(f):
        raise Bad("%s must be a whole number (got %r)" % (what, v))
    n = int(f)
    if (lo is not None and n < lo) or (hi is not None and n > hi):
        raise Bad("%s must be %s..%s (got %s)" % (what, "{:,}".format(lo) if lo is not None else "-",
                                                    "{:,}".format(hi) if hi is not None else "-", "{:,}".format(n)))
    return n


def _float(v, what, lo=-100000.0, hi=100000.0):
    try:
        f = float(v)
    except (TypeError, ValueError):
        raise Bad("%s must be a number" % what)
    if f != f or f in (float("inf"), float("-inf")) or f < lo or f > hi:
        raise Bad("%s must be a finite number %s..%s" % (what, lo, hi))
    return f


def _bool(v, null=False):
    if v is None and null:
        return None
    if isinstance(v, bool):
        return v
    if v in (0, 1, "0", "1", "true", "false"):
        return v in (1, "1", "true")
    if null and v in ("", "null"):
        return None
    raise Bad("expected true / false, got %r" % (v,))


def _text(v, what, maxlen, empty=False, rx=None):
    s = "" if v is None else str(v)
    if s != s.strip():
        s = s.strip()
    if not s and not empty:
        raise Bad("%s must not be empty" % what)
    if len(s) > maxlen:
        raise Bad("%s is at most %d characters" % (what, maxlen))
    if rx and s and not re.match(rx, s):
        raise Bad("%s has characters the game cannot use" % what)
    if "\x00" in s:
        raise Bad("%s contains a NUL byte" % what)
    return s


def _sqltext(s):
    """text for a RAISE message / string literal inside a DO block: no quotes, no % (format placeholders)."""
    return str(s).replace("'", "").replace("%", " pct").replace("\\", "/")[:200]


def _ver_expr(parts):
    """md5 over the rows of every (table, where, order) part, as stored (to_jsonb). The same expression at load
    and at save time, so the page's version proves nothing changed in between."""
    return "md5(%s)" % " || '#' || ".join(
        "coalesce((SELECT string_agg(to_jsonb(t)::text, '|' ORDER BY %s) FROM %s t WHERE %s), '')" % (o, tb, w)
        for tb, w, o in parts)


def _empty_ver(n):
    return hashlib.md5("#".join([""] * n).encode()).hexdigest()


def _ver_guard(parts, version, label):
    """DO-block: 409 (gwadmin-conflict) unless the rows still have the version the page loaded."""
    if not version or not isinstance(version, str) or not re.match(r"^(new|[0-9a-f]{32})$", version):
        raise Bad("version missing: reload the page and try again")
    want = _empty_ver(len(parts)) if version == "new" else version
    return ("DO $gwv$ BEGIN IF %s IS DISTINCT FROM %s THEN RAISE EXCEPTION 'gwadmin-conflict: %s changed since you "
            "opened it (the game server or another edit); reload to see the current values'; END IF; END $gwv$"
            % (_ver_expr(parts), lit(want), _sqltext(label)))


def _version(ctx, parts):
    return _v(ctx, _ver_expr(parts))


# =========================================================================== start-up validation (SQL)
# Each check is a SELECT of problem texts; the same SQL runs as a guard before COMMIT (and on Undo) and in /preflight.
def _attr_check(table, keyexpr):
    return ("SELECT '%s ' || %s || ': append attribute ' || v.a || ' does not exist (item_attribute_templates)' "
            "FROM %s r CROSS JOIN LATERAL (VALUES (r.attribute1), (r.attribute2), (r.attribute3), (r.attribute4), "
            "(r.attribute5)) v(a) WHERE v.a IS NOT NULL AND NOT EXISTS (SELECT 1 FROM item_attribute_templates x "
            "WHERE x.id = v.a)" % (table.replace("_", " "), keyexpr, table))


CHECKS = {
    "loot": [
        "SELECT 'loot table ' || t.template_key || ' drops up to ' || t.maximum_drops || ' items but has only ' || "
        "coalesce(n.c, 0) || ' enabled drop rules (the server refuses to start)' FROM monster_loot_tables t LEFT JOIN "
        "(SELECT template_key, count(*) AS c FROM monster_loot_rules WHERE enabled GROUP BY template_key) n "
        "ON n.template_key = t.template_key WHERE t.enabled AND coalesce(n.c, 0) < t.maximum_drops",
        "SELECT 'loot rule ' || template_key || '/' || loot_index || ': item id must be above 0' FROM monster_loot_rules "
        "WHERE item_id <= 0",
        "SELECT 'loot rule ' || template_key || '/' || loot_index || ': quality ' || item_quality || ' is outside 0-20' "
        "FROM monster_loot_rules WHERE item_quality NOT BETWEEN 0 AND 20",
        "SELECT 'loot rule ' || template_key || '/' || loot_index || ': grade ' || item_grade || ' is outside 0-25' "
        "FROM monster_loot_rules WHERE item_grade NOT BETWEEN 0 AND 25",
        _attr_check("monster_loot_rules", "r.template_key || '/' || r.loot_index"),
    ],
    "medusa": [
        "SELECT 'Medusa monster ' || difficulty || '/' || template_alias || ' is disabled: the server needs every "
        "difficulty and monster exactly once' FROM medusa_monster_rules WHERE NOT enabled",
        "SELECT 'Medusa monster ' || a.template_alias || ' is missing for difficulty ' || d.d FROM (SELECT DISTINCT "
        "template_alias FROM medusa_monster_rules) a CROSS JOIN (VALUES (1), (2), (3)) d(d) WHERE NOT EXISTS (SELECT 1 "
        "FROM medusa_monster_rules m WHERE m.difficulty = d.d AND m.template_alias = a.template_alias)",
        "SELECT 'Medusa rewards difficulty ' || d.d || ': ' || x.msg FROM (VALUES (1), (2), (3)) d(d) CROSS JOIN "
        "LATERAL (SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM medusa_completion_reward_rules r WHERE r.difficulty = d.d "
        "AND r.reward_kind = 1 AND r.threshold = 0) THEN 'the not-finished list must start at score 0' END UNION ALL "
        "SELECT CASE WHEN (SELECT max(threshold) FROM medusa_completion_reward_rules r WHERE r.difficulty = d.d AND "
        "r.reward_kind = 2) IS DISTINCT FROM 2400 THEN 'the finished list must end at 2400 seconds' END) x(msg) "
        "WHERE x.msg IS NOT NULL",
        "SELECT 'Medusa rewards difficulty ' || difficulty || ': not-finished honour must not go down as the score "
        "rises (score ' || threshold || ')' FROM (SELECT difficulty, threshold, honor_points, lag(honor_points) OVER "
        "(PARTITION BY difficulty ORDER BY threshold) AS prev FROM medusa_completion_reward_rules WHERE reward_kind = 1) "
        "s WHERE prev IS NOT NULL AND honor_points < prev",
        "SELECT 'Medusa rewards difficulty ' || difficulty || ': finished honour must not go up as the time rises ('"
        " || threshold || ' s)' FROM (SELECT difficulty, threshold, honor_points, lag(honor_points) OVER (PARTITION BY "
        "difficulty ORDER BY threshold) AS prev FROM medusa_completion_reward_rules WHERE reward_kind = 2) s "
        "WHERE prev IS NOT NULL AND honor_points > prev",
        "SELECT 'Medusa loot ' || difficulty || '/' || template_alias || '/' || loot_index || ': item id must be above "
        "0' FROM medusa_monster_loot_rules WHERE item_id <= 0",
    ],
    "gm_monsters": [
        "SELECT 'GM monster spawns: ' || count(*) || ' rows, the server takes at most 20000' FROM gm_monster_spawns "
        "HAVING count(*) > 20000",
    ],
    "gm_npcs": [
        "SELECT 'GM NPC placements: ' || count(*) || ' rows, the server takes at most 2000' FROM gm_npc_spawns "
        "HAVING count(*) > 2000",
        "SELECT 'GM NPC key ' || npc_key || ' is used by ' || count(*) || ' placements (must be unique)' FROM "
        "gm_npc_spawns GROUP BY npc_key HAVING count(*) > 1",
        "SELECT 'GM dialogue ' || d.dialogue_key || ' has no pages' FROM gm_npc_dialogues d WHERE NOT EXISTS (SELECT "
        "1 FROM gm_npc_dialogue_pages p WHERE p.dialogue_key = d.dialogue_key)",
        "SELECT 'GM dialogue ' || d.dialogue_key || ': the first page ' || d.entry_page || ' does not exist' FROM "
        "gm_npc_dialogues d WHERE EXISTS (SELECT 1 FROM gm_npc_dialogue_pages p WHERE p.dialogue_key = d.dialogue_key) "
        "AND NOT EXISTS (SELECT 1 FROM gm_npc_dialogue_pages p WHERE p.dialogue_key = d.dialogue_key AND p.page_index "
        "= d.entry_page)",
        "SELECT 'GM dialogue ' || b.dialogue_key || ' page ' || b.page_index || ' button ' || b.slot || ' leads to "
        "page ' || b.next_page_index || ', which does not exist' FROM gm_npc_dialogue_buttons b WHERE "
        "b.next_page_index IS NOT NULL AND NOT EXISTS (SELECT 1 FROM gm_npc_dialogue_pages p WHERE p.dialogue_key = "
        "b.dialogue_key AND p.page_index = b.next_page_index)",
    ],
    "quests": [
        "SELECT 'quest ' || quest_id || ' reward slot ' || slot_index || ': item id must be above 0' FROM "
        "quest_reward_slots WHERE item_id <= 0",
        "SELECT 'quest ' || quest_id || ' reward slot ' || slot_index || ': quality ' || item_quality || ' is outside "
        "0-20' FROM quest_reward_slots WHERE item_quality NOT BETWEEN 0 AND 20",
        "SELECT 'quest ' || quest_id || ' reward slot ' || slot_index || ': grade ' || item_grade || ' is outside "
        "0-25' FROM quest_reward_slots WHERE item_grade NOT BETWEEN 0 AND 25",
        _attr_check("quest_reward_slots", "r.quest_id || '/' || r.slot_index"),
    ],
    "events": [
        "SELECT 'EXP boost of character ' || character_id || ' kind ' || kind || ' is invalid (status, kind, bonus, "
        "priority or source)' FROM character_experience_modifiers WHERE status_id <= 0 OR kind <= 0 OR "
        "bonus_basis_points < 0 OR priority < 0 OR length(source) > 160",
    ],
}
FAMILY_TEXT = {"loot": "Monster loot", "medusa": "Medusa Island", "gm_monsters": "GM monsters", "gm_npcs": "GM NPCs",
               "quests": "Quest rewards", "events": "EXP boosts"}


def _guard(family):
    """DO-block that RAISEs the first problems of a family (runs before COMMIT and again on Undo)."""
    sel = " UNION ALL ".join("(%s)" % s for s in CHECKS[family])
    return ("DO $gwc$ DECLARE bad text; BEGIN SELECT string_agg(x, '; ') INTO bad FROM (SELECT x FROM (%s) q(x) "
            "LIMIT 5) z; IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %s would fail at the next server start: "
            "%%. Nothing written.', bad; END IF; END $gwc$;" % (sel, FAMILY_TEXT[family]))


def _problems(ctx, family):
    sel = " UNION ALL ".join("(%s)" % s for s in CHECKS[family])
    return _q(ctx, "(SELECT coalesce(json_agg(x), '[]') FROM (SELECT x FROM (%s) q(x) LIMIT 50) z)" % sel)


# =========================================================================== catalogue / item helpers
def _item(ctx, pid, what="item"):
    pid = _int(pid, 1, INT_MAX, what)
    meta = _rb(ctx).item_meta(pid)
    if not meta["server"]:
        raise Bad("%s %d is not an item template of this server" % (what, pid))
    if not meta["client"]:
        raise Bad("%s %s (#%d) is not in the Origin client: players would see an empty or broken item" % (
            what, meta["name"], pid))
    return pid, meta


def _attrs(ctx, rows):
    """[{id, level}] x up to 5 -> {attribute1: id, attribute_level1: level, ...} (empty slot = NULL, never 0)."""
    cat = _rb(ctx).cat()["attributes"]
    out = {c: None for c in ATTR_COLS}
    rows = list(rows or [])
    if len(rows) > 5:
        raise Bad("at most 5 append attributes")
    n = 0
    for r in rows:
        if not r or r.get("id") in (None, ""):
            continue
        n += 1
        aid = _int(r.get("id"), 0, 32767, "append attribute")
        a = cat.get(str(aid))
        if not a:
            raise Bad("append attribute %d does not exist on this server" % aid)
        mx = a.get("max") or 32767
        lvl = _int(r.get("level"), 1, mx, "level of append attribute %s" % (a.get("key") or aid), null=True)
        out["attribute%d" % n], out["attribute_level%d" % n] = aid, lvl
    return out


def _grant(ctx, r, what):
    """quality / grade / attributes of a granted item (loot rule, quest slot)."""
    q = _int(r.get("item_quality"), 0, 20, "%s quality" % what, null=True)
    g = _int(r.get("item_grade"), 0, 25, "%s grade" % what, null=True)
    d = {"item_quality": q, "item_grade": g}
    d.update(_attrs(ctx, r.get("attributes")))
    return d


def _monster_known(ctx, key):
    return bool(_v(ctx, "(SELECT EXISTS (SELECT 1 FROM monster_templates WHERE template_key = %s))" % lit(key)))


def _upsert(table, pk, values, touch=True):
    cols = list(values)
    sets = ["%s = EXCLUDED.%s" % (ident(c), ident(c)) for c in cols if c not in pk]
    if touch:
        sets.append("updated_at = now()")
    return "%s ON CONFLICT (%s) DO %s" % (common.insert_sql(table, values), ", ".join(ident(c) for c in pk),
                                          ("UPDATE SET " + ", ".join(sets)) if sets else "NOTHING")


def _update(table, where, vals, touch="now()"):
    """UPDATE <table> SET col = lit, ... [, updated_at = now()] WHERE <where dict> (no revision counters: content tables)."""
    sets = ["%s = %s" % (ident(c), lit(v)) for c, v in vals.items()]
    if touch:
        sets.append("updated_at = %s" % touch)
    return "UPDATE %s SET %s WHERE %s" % (table, ", ".join(sets), common.where_sql(where))


def _write(ctx, scope_list, action, stmts, fields, guard=(), applies=None, target="", advanced=False, lock_cids=()):
    _need_writable(ctx)
    r = ctx.rows_write(scope_list, action, lambda before: stmts, fields=fields, guard_sql=[_guard(g) for g in guard],
                       applies=applies, target=target, advanced=advanced, lock_cids=lock_cids)
    r["ok"] = True
    return r


# =========================================================================== overview + restart helper + preflight
@M.get("overview")
def overview(ctx):
    rb = _rb(ctx)
    counts = _q(ctx, """json_build_object(
        'loot_tables', (SELECT count(*) FROM monster_loot_tables), 'loot_rules', (SELECT count(*) FROM monster_loot_rules),
        'medusa_monsters', (SELECT count(*) FROM medusa_monster_rules), 'medusa_loot', (SELECT count(*) FROM medusa_monster_loot_rules),
        'gm_monster_spawns', (SELECT count(*) FROM gm_monster_spawns), 'gm_monster_edits', (SELECT count(*) FROM gm_monster_spawn_edits),
        'gm_monster_attributes', (SELECT count(*) FROM gm_monster_attributes), 'gm_npc_spawns', (SELECT count(*) FROM gm_npc_spawns),
        'gm_dialogues', (SELECT count(*) FROM gm_npc_dialogues), 'quest_values', (SELECT count(*) FROM quest_reward_values),
        'quest_slots', (SELECT count(DISTINCT quest_id) FROM quest_reward_slots),
        'exp_events', (SELECT count(DISTINCT source) FROM character_experience_modifiers WHERE kind = %d),
        'world_bosses', (SELECT count(*) FROM gameplay_world_boss_definitions WHERE revision = (SELECT revision FROM
             gameplay_content_publication WHERE family = 'gameplay')),
        'characters', (SELECT count(*) FROM character_base))""" % EVENT_KIND)
    pending = [common.public_change(c) for c in ctx.app.changes if c.get("applies") in ("after_restart", "startup")
               and not c.get("undone") and not c.get("undo_of")]
    return {"counts": counts, "database": rb.db, "live": rb.live, "writable": rb.writable, "pending": pending,
            "restart": _restart_help(ctx), "server": common.server_status(ctx.app),
            "capabilities": {k: ctx.caps.get(k) for k in ("available", "port", "error")}}


def _restart_help(ctx):
    rb = _rb(ctx)
    port = common.mgmt_port(ctx.app)
    if rb.live:
        return {"where": "the live server (godswar251.service, database godswar)",
                "commands": ["ssh vps", "sudo gw251 backup", "sudo gw251 restart", "gw251 status        # wait for 'ready'",
                             "gw251 logs | grep -iE 'monster-overrides|gm-npc|loot|error' | tail -20"],
                "note": "Everyone online is disconnected for about a minute. Run the start-up check first: a row the "
                        "server rejects keeps it down until the row is fixed or the change is undone."}
    login = (port - 2) if port else "<login-port>"
    return {"where": "the test instance that uses database %s (management port %s)" % (rb.db, port),
            "commands": ["cd ~/gwed/tools-<you>/gwadmin-web/tests",
                         "./make-test-env.sh <n> %s %s stop" % (rb.db, login),
                         "./make-test-env.sh <n> %s %s start     # waits for /readyz" % (rb.db, login)],
            "note": "Shown only: the editor never restarts a game server itself."}


@M.get("preflight")
def preflight(ctx):
    """Every start-up validation of this module, read-only: what would stop the next server start."""
    out = []
    for fam in ("loot", "medusa", "gm_monsters", "gm_npcs", "quests", "events"):
        p = _problems(ctx, fam)
        out.append({"family": fam, "label": FAMILY_TEXT[fam], "ok": not p, "problems": p})
    # GM NPC placements whose model is not on the map are skipped (not fatal), like GmNpcDialoguePolicy
    skipped = _q(ctx, """(SELECT coalesce(json_agg(s.name || ' (map ' || s.map_id || ', ' || s.template_key || ')'), '[]')
        FROM gm_npc_spawns s WHERE s.enabled AND NOT EXISTS (SELECT 1 FROM npc_spawn_definitions d JOIN npc_content_publication p
        ON p.family = 'npcs' AND p.revision = d.revision WHERE d.map_id = s.map_id AND d.template_key = s.template_key))""")
    bad = _q(ctx, """(SELECT coalesce(json_agg(name || ' (object ' || object_id || ')'), '[]') FROM gm_monster_spawns g WHERE
        get_byte(clear_bytes, 0) + 256 * get_byte(clear_bytes, 1) <> octet_length(clear_bytes)
        OR substring(clear_bytes from 9 for 4) <> decode(lpad(to_hex((object_id >> 0) & 255), 2, '0') || lpad(to_hex((object_id >> 8) & 255), 2, '0')
           || lpad(to_hex((object_id >> 16) & 255), 2, '0') || lpad(to_hex((object_id >> 24) & 255), 2, '0'), 'hex')
        OR position(convert_to(template_key, 'UTF8') || decode('00', 'hex') in substring(clear_bytes from 45)) <> 1)""")
    for b_ in bad:
        out[2]["problems"].append("GM spawn %s: its appearance packet does not match the row (the server skips it)" % b_)
        out[2]["ok"] = False
    return {"families": out, "ok": all(f["ok"] for f in out), "warnings": [
        "GM NPC %s: its model is not used on that map, so the server skips it at start-up" % s for s in skipped]}


# =========================================================================== monster loot
def _loot_parts_sql(k):
    return [("monster_loot_tables", "template_key = %s" % k, "template_key"),
            ("monster_loot_rules", "template_key = %s" % k, "loot_index")]


def _loot_parts(key):
    return _loot_parts_sql(lit(key))


@M.get("loot")
def loot(ctx):
    d = _q(ctx, """json_build_object(
        'tables', (SELECT coalesce(json_agg(to_jsonb(lt) || jsonb_build_object('version', %s) ORDER BY lt.template_key), '[]')
             FROM monster_loot_tables lt),
        'rules', (SELECT coalesce(json_agg(to_jsonb(r) ORDER BY r.template_key, r.loot_index), '[]') FROM monster_loot_rules r),
        'names', (SELECT coalesce(json_object_agg(template_key, display_name), '{}') FROM (SELECT DISTINCT ON (template_key)
             template_key, display_name FROM monster_templates WHERE template_key IN (SELECT template_key FROM monster_loot_tables)
             ORDER BY template_key, source_key) x))""" % _ver_expr(_loot_parts_sql("lt.template_key")))
    d["monsters"] = _q(ctx, """(WITH locations AS (
        SELECT template_key,json_agg(map_id ORDER BY map_id) AS map_ids FROM (
            SELECT d.template_key,d.map_id FROM monster_spawn_definitions d JOIN monster_content_publication p
                ON p.family='monsters' AND p.revision=d.revision
            UNION SELECT template_key,source_map_id FROM monster_templates WHERE source_map_id IS NOT NULL
        ) src GROUP BY template_key), names AS (
            SELECT DISTINCT ON (template_key) template_key,display_name FROM monster_templates ORDER BY template_key,source_key)
        SELECT json_agg(json_build_object('template_key',m.template_key,'name',m.display_name,'map_ids',coalesce(l.map_ids,'[]'))
            ORDER BY m.display_name,m.template_key)
        FROM names m LEFT JOIN locations l USING(template_key))""") or []
    # DynamicDungeonContentMapPolicy.cs supplies these authoritative content maps.
    instance_maps={200,204,205,207,208,209}
    map_ids=sorted({mid for m in d["monsters"] for mid in m["map_ids"]})
    d["maps"]=[{"id":mid,"name":_map_name(ctx,mid),"kind":"instance" if mid in instance_maps else "world"} for mid in map_ids]
    d["limits"] = {"client_max_drops": LOOT_CLIENT_MAX, "max_rules": LOOT_RULES_MAX}
    d["problems"] = _problems(ctx, "loot")
    return d


@M.get(r"loot/monster/([A-Za-z0-9_\-]{1,64})")
def loot_monster(ctx, key):
    """one monster: is it a server template, where it spawns, its loot table (or version 'new')."""
    info = _q(ctx, """json_build_object('known', EXISTS (SELECT 1 FROM monster_templates WHERE template_key = %(k)s),
        'name', (SELECT display_name FROM monster_templates WHERE template_key = %(k)s ORDER BY source_key LIMIT 1),
        'rank', (SELECT rank FROM monster_templates WHERE template_key = %(k)s ORDER BY source_key LIMIT 1),
        'spawns', (SELECT coalesce(json_agg(json_build_object('map', map_id, 'count', n) ORDER BY map_id), '[]') FROM (SELECT
             d.map_id, count(*) n FROM monster_spawn_definitions d JOIN monster_content_publication p ON p.family = 'monsters'
             AND p.revision = d.revision WHERE d.template_key = %(k)s GROUP BY d.map_id) s),
        'exists', EXISTS (SELECT 1 FROM monster_loot_tables WHERE template_key = %(k)s))""" % {"k": lit(key)})
    info["version"] = _version(ctx, _loot_parts(key)) if info["exists"] else "new"
    return info


@M.post("loot/save")
def loot_save(ctx):
    """POST {template_key, version, table: {maximum_drops, enabled}, rules: [{loot_index?, item_id, chance_basis_points,
    minimum_quantity, maximum_quantity, enabled, bound_on_pickup, item_quality, item_grade, attributes: [{id, level}]}]}
    - the whole drop list of one monster in one transaction (rules not sent are deleted)."""
    b = ctx.body
    key = _text(b.get("template_key"), "monster template key", 64, rx=r"^[A-Za-z0-9_\-]+$")
    if not _monster_known(ctx, key):
        raise Bad("%s is not a monster template of this server (monster_templates)" % key)
    t = b.get("table") or {}
    md = _int(t.get("maximum_drops"), 1, LOOT_CLIENT_MAX, "maximum drops per kill (the client's loot window shows at most %d)"
              % LOOT_CLIENT_MAX)
    en = _bool(t.get("enabled", True))
    rules = list(b.get("rules") or [])
    if len(rules) > LOOT_RULES_MAX:
        raise Bad("a monster has at most %d drop rules" % LOOT_RULES_MAX)
    used = {int(r["loot_index"]) for r in rules if r.get("loot_index") not in (None, "")}
    free = [i for i in range(LOOT_RULES_MAX) if i not in used]
    rows, seen = [], set()
    for i, r in enumerate(rules):
        what = "drop %d" % (i + 1)
        li = _int(r.get("loot_index"), 0, LOOT_RULES_MAX - 1, what + " index", null=True)
        if li is None:
            li = free.pop(0)
        if li in seen:
            raise Bad("two drops use index %d" % li)
        seen.add(li)
        pid, meta = _item(ctx, r.get("item_id"), what + " item")
        mn = _int(r.get("minimum_quantity", 1), 1, 255, what + " minimum quantity")
        mx = _int(r.get("maximum_quantity", mn), mn, 255, what + " maximum quantity")
        if mx > max(1, int(meta.get("overlap") or 1)):
            raise Bad("%s: %s stacks to %s; the maximum quantity %d would not fit one bag cell" % (
                what, meta["name"], meta.get("overlap") or 1, mx))
        row = {"template_key": key, "loot_index": li, "item_id": pid,
               "chance_basis_points": _int(r.get("chance_basis_points"), 1, 10000, what + " chance (0.01 % - 100 %)"),
               "minimum_quantity": mn, "maximum_quantity": mx, "enabled": _bool(r.get("enabled", True)),
               "bound_on_pickup": _bool(r.get("bound_on_pickup"), null=True)}
        row.update(_grant(ctx, r, what))
        rows.append(row)
    n_on = sum(1 for r in rows if r["enabled"])
    if en and n_on < md:
        raise Bad("a kill may drop up to %d items, but only %d drop rules are on. The server refuses to start when a "
                  "monster can drop more items than it has rules: lower 'maximum drops' or add drops." % (md, n_on))
    parts = _loot_parts(key)
    stmts = [_ver_guard(parts, b.get("version"), "the loot table of " + key),
             _upsert("monster_loot_tables", ["template_key"], {"template_key": key, "maximum_drops": md, "enabled": en})]
    keep = sorted(r["loot_index"] for r in rows)
    stmts.append("DELETE FROM monster_loot_rules WHERE template_key = %s%s" % (
        lit(key), (" AND loot_index NOT IN (%s)" % ", ".join(str(x) for x in keep)) if keep else ""))
    stmts += [_upsert("monster_loot_rules", ["template_key", "loot_index"], r) for r in rows]
    name = ctx.catalog.name("monster", key) or key
    return _write(ctx, [common.scope(t_, w_) for t_, w_, _ in parts],
                  "loot of %s (%s): up to %d per kill, %d drops%s" % (name, key, md, len(rows), "" if en else ", table off"),
                  stmts, ["monster_loot_tables.*", "monster_loot_rules.*"], guard=["loot"], target="loot " + key)


@M.post("loot/delete")
def loot_delete(ctx):
    key = _text(ctx.body.get("template_key"), "monster template key", 64, rx=r"^[A-Za-z0-9_\-]+$")
    parts = _loot_parts(key)
    stmts = [_ver_guard(parts, ctx.body.get("version"), "the loot table of " + key),
             "DELETE FROM monster_loot_tables WHERE template_key = %s" % lit(key)]
    return _write(ctx, [common.scope(t_, w_) for t_, w_, _ in parts], "delete the loot table of %s (%s)" % (
        ctx.catalog.name("monster", key) or key, key), stmts, ["monster_loot_tables.*", "monster_loot_rules.*"],
        guard=["loot"], target="loot " + key)


# =========================================================================== Medusa Island
MEDUSA_MONSTER_COLS = {"monster_level": (1, 200), "maximum_health": (1, INT_MAX), "score": (0, 10000),
                       "movement_speed_basis_points": (1, 10000), "corpse_without_loot_ms": (1000, 300000),
                       "corpse_with_loot_ms": (1000, 300000), "pet_experience": (0, 100_000_000)}


@M.get("medusa")
def medusa(ctx):
    d = _q(ctx, """json_build_object(
        'monsters', (SELECT coalesce(json_agg(to_jsonb(m) || jsonb_build_object('version', %s, 'loot_version', %s)
             ORDER BY m.template_alias, m.difficulty), '[]') FROM medusa_monster_rules m),
        'loot', (SELECT coalesce(json_agg(to_jsonb(l) ORDER BY l.difficulty, l.template_alias, l.loot_index), '[]') FROM medusa_monster_loot_rules l),
        'rewards', (SELECT coalesce(json_agg(to_jsonb(r) ORDER BY r.difficulty, r.reward_kind, r.threshold), '[]') FROM medusa_completion_reward_rules r),
        'reward_versions', json_build_object('1', %s, '2', %s, '3', %s),
        'titles', (SELECT coalesce(json_agg(to_jsonb(tt) || jsonb_build_object('version', %s) ORDER BY tt.title), '[]')
             FROM medusa_reward_title_definitions tt),
        'settings', (SELECT to_jsonb(s) FROM medusa_instance_settings s WHERE instance_key = 'medusa'))""" % (
        _ver_expr(_medusa_monster_parts("m.difficulty", "m.template_alias", raw=True)),
        _ver_expr(_medusa_loot_parts("m.difficulty", "m.template_alias", raw=True)),
        _ver_expr(_medusa_reward_parts(1)), _ver_expr(_medusa_reward_parts(2)), _ver_expr(_medusa_reward_parts(3)),
        _ver_expr(_title_parts("tt.title"))))
    d["difficulty"] = DIFFICULTY
    d["problems"] = _problems(ctx, "medusa")
    return d


def _medusa_monster_parts(dif, alias, raw=False):
    return [("medusa_monster_rules", "difficulty = %s AND template_alias = %s" % ((dif, alias) if raw else (int(dif), lit(alias))),
             "template_alias")]


def _medusa_loot_parts(dif, alias, raw=False):
    return [("medusa_monster_loot_rules", "difficulty = %s AND template_alias = %s" % ((dif, alias) if raw else (int(dif), lit(alias))),
             "loot_index")]


def _medusa_reward_parts(dif):
    return [("medusa_completion_reward_rules", "difficulty = %s" % dif, "reward_kind, threshold")]


def _title_parts(t):
    return [("medusa_reward_title_definitions", "title = %s" % t, "title")]


def _alias(ctx, b):
    dif = _int(b.get("difficulty"), 1, 3, "difficulty")
    alias = _text(b.get("template_alias"), "Medusa monster", 64, rx=r"^[A-Za-z0-9_\-]+$")
    if not _v(ctx, "(SELECT EXISTS (SELECT 1 FROM medusa_monster_rules WHERE difficulty = %d AND template_alias = %s))"
              % (dif, lit(alias))):
        raise Bad("no Medusa monster %s on difficulty %d (the roster is fixed in the server)" % (alias, dif))
    return dif, alias


@M.post("medusa/monster")
def medusa_monster(ctx):
    """POST {difficulty, template_alias, version, values: {monster_level, maximum_health, score, movement_speed_basis_points,
    corpse_without_loot_ms, corpse_with_loot_ms, pet_experience}} - the roster itself (which monsters) is fixed."""
    b = ctx.body
    dif, alias = _alias(ctx, b)
    vals = {}
    for c, v in (b.get("values") or {}).items():
        if c not in MEDUSA_MONSTER_COLS:
            raise Bad("%s is not a Medusa monster value this page edits" % c)
        lo, hi = MEDUSA_MONSTER_COLS[c]
        vals[c] = _int(v, lo, hi, c.replace("_", " "), null=c.startswith("corpse"))
    if not vals:
        raise Bad("nothing to change")
    wo, wl = vals.get("corpse_without_loot_ms"), vals.get("corpse_with_loot_ms")
    if wo is not None and wl is not None and wl < wo:
        raise Bad("the corpse with loot must stay at least as long as the corpse without loot")
    parts = _medusa_monster_parts(dif, alias)
    stmts = [_ver_guard(parts, b.get("version"), "Medusa monster " + alias),
             _update("medusa_monster_rules", {"difficulty": dif, "template_alias": alias}, vals)]
    return _write(ctx, [common.scope(parts[0][0], parts[0][1])], "Medusa %s %s: %s" % (
        DIFFICULTY[dif], alias, ", ".join("%s %s" % (k, v) for k, v in vals.items())), stmts,
        ["medusa_monster_rules.%s" % c for c in vals], guard=["medusa"], target="medusa " + alias)


@M.post("medusa/loot")
def medusa_loot(ctx):
    """POST {difficulty, template_alias, version, rules: [{loot_index?, item_id, chance_basis_points, minimum_quantity,
    maximum_quantity, enabled}]} - the whole drop list of one Medusa monster."""
    b = ctx.body
    dif, alias = _alias(ctx, b)
    rules = list(b.get("rules") or [])
    if len(rules) > LOOT_RULES_MAX:
        raise Bad("at most %d drops" % LOOT_RULES_MAX)
    used = {int(r["loot_index"]) for r in rules if r.get("loot_index") not in (None, "")}
    free = [i for i in range(LOOT_RULES_MAX) if i not in used]
    rows, seen = [], set()
    for i, r in enumerate(rules):
        what = "drop %d" % (i + 1)
        li = _int(r.get("loot_index"), 0, LOOT_RULES_MAX - 1, what + " index", null=True)
        if li is None:
            li = free.pop(0)
        if li in seen:
            raise Bad("two drops use index %d" % li)
        seen.add(li)
        pid, meta = _item(ctx, r.get("item_id"), what + " item")
        mn = _int(r.get("minimum_quantity", 1), 1, 255, what + " minimum quantity")
        rows.append({"difficulty": dif, "template_alias": alias, "loot_index": li, "item_id": pid,
                     "chance_basis_points": _int(r.get("chance_basis_points"), 1, 10000, what + " chance"),
                     "minimum_quantity": mn, "maximum_quantity": _int(r.get("maximum_quantity", mn), mn, 255, what + " maximum quantity"),
                     "enabled": _bool(r.get("enabled", True))})
    parts = _medusa_loot_parts(dif, alias)
    keep = sorted(r["loot_index"] for r in rows)
    stmts = [_ver_guard(parts, b.get("version"), "Medusa loot of " + alias),
             "DELETE FROM medusa_monster_loot_rules WHERE difficulty = %d AND template_alias = %s%s" % (
                 dif, lit(alias), (" AND loot_index NOT IN (%s)" % ", ".join(str(x) for x in keep)) if keep else "")]
    stmts += [_upsert("medusa_monster_loot_rules", ["difficulty", "template_alias", "loot_index"], r) for r in rows]
    return _write(ctx, [common.scope(parts[0][0], parts[0][1])], "Medusa %s %s loot: %d drops" % (DIFFICULTY[dif], alias, len(rows)),
                  stmts, ["medusa_monster_loot_rules.*"], guard=["medusa"], target="medusa loot " + alias)


@M.post("medusa/rewards")
def medusa_rewards(ctx):
    """POST {difficulty, version, rules: [{reward_kind 1|2, threshold, honor_points, title|null}]} - the honour table of one
    difficulty (kind 1: not finished, by score 0..2999; kind 2: finished, by seconds 1..2400, the last one 2400)."""
    b = ctx.body
    dif = _int(b.get("difficulty"), 1, 3, "difficulty")
    titles = {int(t) for t in _q(ctx, "(SELECT coalesce(json_agg(title), '[]') FROM medusa_reward_title_definitions)")}
    rows, seen = [], set()
    for i, r in enumerate(b.get("rules") or []):
        k = _int(r.get("reward_kind"), 1, 2, "row %d kind" % (i + 1))
        th = _int(r.get("threshold"), 0 if k == 1 else 1, 2999 if k == 1 else 2400,
                  "row %d %s" % (i + 1, "score" if k == 1 else "seconds"))
        if (k, th) in seen:
            raise Bad("two rows use the same %s %d" % ("score" if k == 1 else "time", th))
        seen.add((k, th))
        title = _int(r.get("title"), 1, 6, "row %d title" % (i + 1), null=True)
        if title is not None and (k != 2 or title not in titles):
            raise Bad("row %d: a title can only be given for a finished run, and must be one of the 6 Medusa titles" % (i + 1))
        rows.append({"difficulty": dif, "reward_kind": k, "threshold": th,
                     "honor_points": _int(r.get("honor_points"), 1, INT_MAX, "row %d honour" % (i + 1)), "title": title})
    if len(rows) > 64:
        raise Bad("at most 64 rows")
    parts = _medusa_reward_parts(int(dif))
    stmts = [_ver_guard(parts, b.get("version"), "the Medusa honour table"),
             "DELETE FROM medusa_completion_reward_rules WHERE difficulty = %d" % dif]
    stmts += [common.insert_sql("medusa_completion_reward_rules", r) for r in rows]
    return _write(ctx, [common.scope(parts[0][0], parts[0][1])], "Medusa %s honour table: %d rows" % (DIFFICULTY[dif], len(rows)),
                  stmts, ["medusa_completion_reward_rules.*"], guard=["medusa"], target="medusa rewards")


@M.post("medusa/title")
def medusa_title(ctx):
    """POST {title, version, display_name, physical_attack_basis_points, magic_attack_basis_points, physical_defense_basis_points,
    magic_defense_basis_points} - name and stat bonus of one Medusa title (the client title id stays: players own it)."""
    b = ctx.body
    t = _int(b.get("title"), 1, 6, "title")
    vals = {"display_name": _text(b.get("display_name"), "title name", 80)}
    for c in ("physical_attack_basis_points", "magic_attack_basis_points", "physical_defense_basis_points",
              "magic_defense_basis_points"):
        vals[c] = _int(b.get(c), 1, 10000, c.replace("_basis_points", "").replace("_", " ") + " bonus (0.01 % - 100 %)")
    parts = _title_parts(int(t))
    stmts = [_ver_guard(parts, b.get("version"), "the Medusa title"),
             _update("medusa_reward_title_definitions", {"title": t}, vals)]
    return _write(ctx, [common.scope(parts[0][0], parts[0][1])], "Medusa title %d: %s" % (t, vals["display_name"]), stmts,
                  ["medusa_reward_title_definitions.%s" % c for c in vals], guard=["medusa"], target="medusa title")


# =========================================================================== instances (applies at the next entry)
@M.get("instances")
def instances(ctx):
    d = _q(ctx, """json_build_object(
        'legacy', (SELECT coalesce(json_agg(to_jsonb(s) ORDER BY instance_kind), '[]') FROM legacy_instance_settings s),
        'medusa', (SELECT to_jsonb(s) FROM medusa_instance_settings s WHERE instance_key = 'medusa'),
        'rates', (SELECT coalesce(json_agg(json_build_object('key', rate_key, 'value', value) ORDER BY rate_key), '[]') FROM
                  server_rates WHERE rate_key LIKE 'event.%' OR rate_key LIKE '%entr%' OR rate_key LIKE '%wonderland%'
                  OR rate_key LIKE '%medusa%' OR rate_key LIKE '%atlantis%' OR rate_key LIKE '%harbor%'))"""
           if _v(ctx, "(SELECT to_regclass('public.server_rates') IS NOT NULL)") else """json_build_object(
        'legacy', (SELECT coalesce(json_agg(to_jsonb(s) ORDER BY instance_kind), '[]') FROM legacy_instance_settings s),
        'medusa', (SELECT to_jsonb(s) FROM medusa_instance_settings s WHERE instance_key = 'medusa'), 'rates', '[]'::json)""")
    vers = _q(ctx, "json_build_object('1', %s, '2', %s, '3', %s, 'medusa', %s)" % tuple(
        [_ver_expr([("legacy_instance_settings", "instance_kind = %d" % k, "instance_kind")]) for k in (1, 2, 3)] +
        [_ver_expr([("medusa_instance_settings", "instance_key = 'medusa'", "instance_key")])]))
    for r in d["legacy"]:
        r["name"] = INSTANCE_NAMES.get(r["instance_kind"], str(r["instance_kind"]))
        r["version"] = vers.get(str(r["instance_kind"]))
    if d.get("medusa"):
        d["medusa"]["version"] = vers["medusa"]
    return d


@M.post("instances/save")
def instances_save(ctx):
    """POST {which: 'legacy', instance_kind 1-3, free_entry_limit, paid_retry_limit|null, version, advanced: true}
    or {which: 'medusa', daily_entry_limit, version, advanced: true}. Read at the next entry (no restart)."""
    b = ctx.body
    adv = bool(b.get("advanced"))
    if b.get("which") == "medusa":
        n = _int(b.get("daily_entry_limit"), 1, 99, "Medusa entries per day")
        parts = [("medusa_instance_settings", "instance_key = 'medusa'", "instance_key")]
        stmts = [_ver_guard(parts, b.get("version"), "the Medusa entry limit"),
                 "UPDATE medusa_instance_settings SET daily_entry_limit = %d, updated_at = clock_timestamp() WHERE instance_key "
                 "= 'medusa'" % n]
        return _write(ctx, [common.scope(parts[0][0], parts[0][1])], "Medusa Island: %d entries per day" % n, stmts,
                      ["medusa_instance_settings.daily_entry_limit"], applies="now", advanced=adv, target="instances")
    k = _int(b.get("instance_kind"), 1, 3, "instance")
    free = _int(b.get("free_entry_limit"), 0, 99, "free entries per day")
    paid = _int(b.get("paid_retry_limit"), 0, 99, "paid retries", null=True)
    parts = [("legacy_instance_settings", "instance_kind = %d" % k, "instance_kind")]
    stmts = [_ver_guard(parts, b.get("version"), INSTANCE_NAMES[k] + " entries"),
             "UPDATE legacy_instance_settings SET free_entry_limit = %d, paid_retry_limit = %s, updated_at = clock_timestamp() "
             "WHERE instance_kind = %d" % (free, "NULL" if paid is None else paid, k)]
    return _write(ctx, [common.scope(parts[0][0], parts[0][1])], "%s: %d free entries, %s paid retries" % (
        INSTANCE_NAMES[k], free, "unlimited" if paid is None else paid), stmts,
        ["legacy_instance_settings.free_entry_limit", "legacy_instance_settings.paid_retry_limit"], applies="now",
        advanced=adv, target="instances")


# =========================================================================== quest reward overrides
def _quest_parts(qid):
    return [("quest_reward_values", "quest_id = %d" % qid, "quest_id"),
            ("quest_reward_slots", "quest_id = %d" % qid, "slot_index")]


@M.get("quests")
def quests(ctx):
    return _q(ctx, """json_build_object(
        'values', (SELECT coalesce(json_agg(to_jsonb(v) ORDER BY quest_id), '[]') FROM quest_reward_values v),
        'slots', (SELECT coalesce(json_agg(to_jsonb(s) ORDER BY quest_id, slot_index), '[]') FROM quest_reward_slots s),
        'problems', '[]'::json)""")


@M.get(r"quest/(\d+)")
def quest_one(ctx, qid):
    qid = _int(qid, 0, INT_MAX, "quest")
    d = _q(ctx, """json_build_object(
        'values', (SELECT to_jsonb(v) FROM quest_reward_values v WHERE quest_id = %d),
        'slots', (SELECT coalesce(json_agg(to_jsonb(s) ORDER BY slot_index), '[]') FROM quest_reward_slots s WHERE quest_id = %d))"""
           % (qid, qid))
    d["version"] = _version(ctx, _quest_parts(qid))
    d["editable"] = qid > 0
    if qid == 0:
        d["why"] = "quest 0 cannot be overridden: the reward tables need a quest id above 0 (QuestRewardContentSnapshot)"
    try:
        e = ctx.catalog.entry("quest", qid)
        d["quest"] = {k: e.get(k) for k in ("id", "title", "rewards", "min_level", "max_level", "giver_name", "giver_map_name", "camp")}
    except NotFound:
        d["quest"] = None
    return d


@M.post("quest/save")
def quest_save(ctx):
    """POST {quest_id, version, values: {experience, talent_points, silver, gold} | null, slots: [{item_id, item_quality,
    item_grade, attributes}]} - null values / an empty slot list = the quest pays what it shipped with."""
    b = ctx.body
    qid = _int(b.get("quest_id"), 1, INT_MAX, "quest")
    try:
        title = ctx.catalog.entry("quest", qid).get("title") or "quest %d" % qid
    except NotFound:
        raise Bad("quest %d is not in the Origin client's quest list" % qid)
    v = b.get("values")
    vals = None
    if v is not None:
        vals = {"quest_id": qid}
        for c, lab in (("experience", "EXP"), ("talent_points", "talent points"), ("silver", "Silver"), ("gold", "Gold")):
            vals[c] = _int(v.get(c, 0), 0, INT_MAX, lab)
    slots = []
    for i, s in enumerate(b.get("slots") or []):
        if i >= QUEST_SLOTS:
            raise Bad("a quest reward menu has at most %d slots" % QUEST_SLOTS)
        pid, _ = _item(ctx, s.get("item_id"), "reward %d item" % (i + 1))
        row = {"quest_id": qid, "slot_index": i, "item_id": pid}
        row.update(_grant(ctx, s, "reward %d" % (i + 1)))
        slots.append(row)
    parts = _quest_parts(qid)
    stmts = [_ver_guard(parts, b.get("version"), "the rewards of quest %d" % qid),
             "DELETE FROM quest_reward_values WHERE quest_id = %d" % qid, "DELETE FROM quest_reward_slots WHERE quest_id = %d" % qid]
    if vals:
        stmts.append(common.insert_sql("quest_reward_values", vals))
    stmts += [common.insert_sql("quest_reward_slots", s) for s in slots]
    what = []
    what.append(("EXP %(experience)s, TP %(talent_points)s, Silver %(silver)s, Gold %(gold)s" % vals) if vals else "default payout")
    what.append(("%d reward items" % len(slots)) if slots else "default reward items")
    return _write(ctx, [common.scope(t_, w_) for t_, w_, _ in parts], "quest %d %s: %s" % (qid, _sqltext(title), "; ".join(what)),
                  stmts, ["quest_reward_values.*", "quest_reward_slots.*"], guard=["quests"], target="quest %d" % qid)


# =========================================================================== GM monsters
def _maps(ctx):
    return ctx.catalog.file("maps")


def _map_name(ctx, mid):
    return (_maps(ctx).get(str(mid)) or {}).get("name") or "map %s" % mid


@M.get("gm/maps")
def gm_maps(ctx):
    rows = _q(ctx, """(SELECT coalesce(json_agg(r ORDER BY r.map_id), '[]') FROM (
        SELECT m.map_id, m.scene_key,
          (SELECT count(*) FROM monster_spawn_definitions d JOIN monster_content_publication p ON p.family = 'monsters' AND
              p.revision = d.revision WHERE d.map_id = m.map_id) AS monsters,
          (SELECT count(*) FROM npc_spawn_definitions d JOIN npc_content_publication p ON p.family = 'npcs' AND
              p.revision = d.revision WHERE d.map_id = m.map_id) AS npcs,
          (SELECT count(*) FROM gm_monster_spawns g WHERE g.map_id = m.map_id) AS gm_monsters,
          (SELECT count(*) FROM gm_monster_spawn_edits g WHERE g.map_id = m.map_id) AS gm_edits,
          (SELECT count(*) FROM gm_monster_attributes g WHERE g.map_id = m.map_id) AS gm_attributes,
          (SELECT count(*) FROM gm_npc_spawns g WHERE g.map_id = m.map_id) AS gm_npcs
        FROM map_templates m) r)""")
    for r in rows:
        r["name"] = _map_name(ctx, r["map_id"])
    return rows


def _unpack(hexs):
    b = bytes.fromhex(hexs)
    if len(b) < 44:
        return {}
    return {"level": struct.unpack_from("<I", b, P_LEVEL)[0], "current_health": struct.unpack_from("<I", b, P_CUR_HP)[0],
            "maximum_health": struct.unpack_from("<I", b, P_MAX_HP)[0], "facing": round(struct.unpack_from("<f", b, P_FACING)[0], 3)}


def _gm_monster_parts(mid):
    return [("gm_monster_spawns", "map_id = %d" % mid, "id"), ("gm_monster_spawn_edits", "map_id = %d" % mid, "object_id"),
            ("gm_monster_attributes", "map_id = %d" % mid, "template_key")]


def _gm_publication_parts():
    return [("monster_content_publication", "family = 'monsters'", "revision")]


@M.get(r"gm/map/(\d+)")
def gm_map(ctx, mid):
    """one map: every published monster spawn (position, level, HP from its appearance packet), the published NPCs, and
    the GM rows of that map (placements, per-spawn edits, per-template attribute overrides)."""
    mid = _int(mid, 0, 255, "map")
    d = _q(ctx, """json_build_object(
        'published', (SELECT coalesce(json_agg(json_build_object('object_id', d.object_id, 'template_key', d.template_key,
             'name', d.display_name, 'x', round(d.pos_x::numeric, 1), 'z', round(d.pos_z::numeric, 1),
             'head', encode(substring(d.clear_bytes from 1 for 44), 'hex')) ORDER BY d.object_id), '[]')
             FROM monster_spawn_definitions d JOIN monster_content_publication p ON p.family = 'monsters' AND p.revision = d.revision
             WHERE d.map_id = %(m)d),
        'npcs', (SELECT coalesce(json_agg(json_build_object('object_id', d.object_id, 'npc_key', d.npc_key, 'template_key',
             d.template_key, 'x', round(d.pos_x::numeric, 1), 'z', round(d.pos_z::numeric, 1), 'appearance_type', d.appearance_type)
             ORDER BY d.object_id), '[]') FROM npc_spawn_definitions d JOIN npc_content_publication p ON p.family = 'npcs' AND
             p.revision = d.revision WHERE d.map_id = %(m)d),
        'gm_spawns', (SELECT coalesce(json_agg((to_jsonb(g) - 'clear_bytes') || jsonb_build_object('head',
             encode(substring(g.clear_bytes from 1 for 44), 'hex')) ORDER BY g.id), '[]') FROM gm_monster_spawns g WHERE g.map_id = %(m)d),
        'edits', (SELECT coalesce(json_agg(to_jsonb(e) ORDER BY e.object_id), '[]') FROM gm_monster_spawn_edits e WHERE e.map_id = %(m)d),
        'attributes', (SELECT coalesce(json_agg(to_jsonb(a) ORDER BY a.template_key), '[]') FROM gm_monster_attributes a WHERE a.map_id = %(m)d),
        'gm_npcs', (SELECT coalesce(json_agg(to_jsonb(n) ORDER BY n.id), '[]') FROM gm_npc_spawns n WHERE n.map_id = %(m)d))"""
           % {"m": mid})
    for r in d["published"] + d["gm_spawns"]:
        r.update(_unpack(r.pop("head", "") or ""))
    d["map"] = {"id": mid, "name": _map_name(ctx, mid), "regions": (_maps(ctx).get(str(mid)) or {}).get("regions") or []}
    d["version"] = _version(ctx, _gm_monster_parts(mid))
    d["publication_version"] = _version(ctx, _gm_publication_parts())
    d["object_range"] = [GM_OBJECT_FIRST, GM_OBJECT_LAST]
    return d


def _stats(v, allow_level=True):
    out = {}
    for c in STAT_COLS:
        if c == "level" and not allow_level:
            continue
        x = (v or {}).get(c)
        lo = 1 if c in ("level", "maximum_health") else 0
        out[c] = _int(x, lo, 10000 if c == "level" else INT_MAX, c.replace("_", " "), null=True)
    if out.get("current_health") is not None and out.get("maximum_health") is not None and out["current_health"] > out["maximum_health"]:
        raise Bad("HP at spawn cannot be above max HP")
    return out


@M.post("gm/attributes")
def gm_attributes(ctx):
    """POST {map_id, template_key, version, values: {level, ..., critical_resistance} (null = keep the formula), display_name,
    note, delete: bool} - one monster type's combat stats on one map (every spawn of it)."""
    b = ctx.body
    mid = _int(b.get("map_id"), 0, 255, "map")
    key = _text(b.get("template_key"), "monster template", 128, rx=r"^[A-Za-z0-9_\-]+$")
    if not _v(ctx, "(SELECT EXISTS (SELECT 1 FROM monster_spawn_definitions d JOIN monster_content_publication p ON p.family = "
                   "'monsters' AND p.revision = d.revision WHERE d.map_id = %d AND d.template_key = %s) OR EXISTS (SELECT 1 FROM "
                   "gm_monster_spawns WHERE map_id = %d AND template_key = %s))" % (mid, lit(key), mid, lit(key))):
        raise Bad("%s does not spawn on %s: an override there would change nothing" % (key, _map_name(ctx, mid)))
    parts = _gm_monster_parts(mid)
    where = {"map_id": mid, "template_key": key}
    name = ctx.catalog.name("monster", key) or key
    if b.get("delete"):
        stmts = [_ver_guard(parts, b.get("version"), "the GM monsters of this map"), common.delete_sql("gm_monster_attributes", where)]
        act = "remove the stat override of %s on %s" % (name, _map_name(ctx, mid))
    else:
        values = b.get("values") or {}
        if any(values.get(k) not in (None, "") for k in ("current_health", "maximum_health")):
            raise Bad("template HP is ignored by the server; use Set spawn HP to change actual spawn health")
        st = _stats(values)
        if all(v is None for v in st.values()):
            raise Bad("set at least one value (empty = keep the game's own value), or remove the override")
        row = dict(where, display_name=_text(b.get("display_name", ""), "display name", 255, empty=True),
                   note=_text(b.get("note", ""), "note", 2000, empty=True), updated_by="gwadmin-web", **st)
        stmts = [_ver_guard(parts, b.get("version"), "the GM monsters of this map"),
                 _upsert("gm_monster_attributes", ["map_id", "template_key"], row)]
        act = "stats of %s on %s: %s" % (name, _map_name(ctx, mid), ", ".join("%s %s" % (k, v) for k, v in st.items() if v is not None))
    return _write(ctx, [common.scope("gm_monster_attributes", "map_id = %d AND template_key = %s" % (mid, lit(key)))], act, stmts,
                  ["gm_monster_attributes.*"], guard=["gm_monsters"], target="gm monsters map %d" % mid)


@M.post("gm/spawn-hp")
def gm_spawn_hp(ctx):
    """Set initial/max HP on all existing published and GM spawns of one type/map.

    Like the curve editor: per-object edits for published spawns, packet offsets
    20/24 for GM spawns. Keep placement, enabled state, notes and combat overrides.
    """
    b = ctx.body
    mid = _int(b.get("map_id"), 0, 255, "map")
    key = _text(b.get("template_key"), "monster template", 128, rx=r"^[A-Za-z0-9_\-]+$")
    hp = _int(b.get("maximum_health"), 1, INT_MAX, "spawn max HP")
    published = ("SELECT d.map_id, d.object_id FROM monster_spawn_definitions d JOIN monster_content_publication p "
                 "ON p.family = 'monsters' AND p.revision = d.revision WHERE d.map_id = %d AND d.template_key = %s"
                 % (mid, lit(key)))
    gm_where = "map_id = %d AND template_key = %s" % (mid, lit(key))
    count = _v(ctx, "(SELECT count(*) FROM (%s) s) + (SELECT count(*) FROM gm_monster_spawns WHERE %s)" % (published, gm_where))
    if not count:
        raise Bad("this monster has no existing spawns on this map")
    le = lit(struct.pack("<I", hp).hex())
    stmts = [
        "LOCK TABLE gm_monster_spawns, gm_monster_spawn_edits IN SHARE ROW EXCLUSIVE MODE",
        "LOCK TABLE monster_content_publication, monster_spawn_definitions IN SHARE MODE",
        _ver_guard(_gm_monster_parts(mid), b.get("version"), "the GM monsters of this map"),
        _ver_guard(_gm_publication_parts(), b.get("publication_version"), "the published monsters"),
        "INSERT INTO gm_monster_spawn_edits (map_id, object_id, current_health, maximum_health, updated_by) "
        "SELECT s.map_id, s.object_id, %(hp)d, %(hp)d, 'gwadmin-web' FROM (%(pub)s) s "
        "ON CONFLICT (map_id, object_id) DO UPDATE SET current_health = EXCLUDED.current_health, "
        "maximum_health = EXCLUDED.maximum_health, updated_at = now(), updated_by = EXCLUDED.updated_by"
        % {"hp": hp, "pub": published},
        "UPDATE gm_monster_spawns SET clear_bytes = overlay(overlay(clear_bytes placing decode(%s, 'hex') from 21 for 4) "
        "placing decode(%s, 'hex') from 25 for 4), updated_at = now(), updated_by = 'gwadmin-web' WHERE %s"
        % (le, le, gm_where),
    ]
    edit_where = "map_id = %d AND (map_id, object_id) IN (%s)" % (mid, published)
    name = ctx.catalog.name("monster", key) or key
    result = _write(ctx, [common.scope("gm_monster_spawns", gm_where), common.scope("gm_monster_spawn_edits", edit_where)],
                    "spawn initial/max HP of %s on %s: %s (%s existing spawns)" % (name, _map_name(ctx, mid), hp, count),
                    stmts, ["gm_monster_spawns.*", "gm_monster_spawn_edits.*"], guard=["gm_monsters"],
                    applies="after_restart", target="gm monsters map %d" % mid)
    result.update(spawns=count, maximum_health=hp)
    return result


@M.post("gm/edit")
def gm_edit(ctx):
    """POST {map_id, object_id, version, enabled, pos_x|null, pos_z|null, facing|null, values: {...}, note, delete} - one
    published spawn point: switch it off, move it, or change its stats."""
    b = ctx.body
    mid = _int(b.get("map_id"), 0, 255, "map")
    oid = _int(b.get("object_id"), 1, 4294967295, "object id")
    pub = _q(ctx, "(SELECT to_jsonb(x) FROM (SELECT d.template_key, d.display_name FROM monster_spawn_definitions d JOIN "
                  "monster_content_publication p ON p.family = 'monsters' AND p.revision = d.revision WHERE d.map_id = %d AND "
                  "d.object_id = %d) x)" % (mid, oid))
    if not pub:
        raise Bad("object %d is not a published monster spawn on %s" % (oid, _map_name(ctx, mid)))
    parts = _gm_monster_parts(mid)
    where = {"map_id": mid, "object_id": oid}
    if b.get("delete"):
        stmts = [_ver_guard(parts, b.get("version"), "the GM monsters of this map"), common.delete_sql("gm_monster_spawn_edits", where)]
        act = "remove the edit of spawn %d (%s) on %s" % (oid, pub["display_name"], _map_name(ctx, mid))
    else:
        x = None if b.get("pos_x") in (None, "") else _float(b.get("pos_x"), "X")
        z = None if b.get("pos_z") in (None, "") else _float(b.get("pos_z"), "Z")
        if (x is None) != (z is None):
            raise Bad("give both X and Z to move a spawn, or neither")
        fc = None if b.get("facing") in (None, "") else _float(b.get("facing"), "facing", -1000, 1000)
        st = _stats(b.get("values"))
        en = _bool(b.get("enabled", True))
        row = dict(where, enabled=en, pos_x=x, pos_z=z, facing=fc, note=_text(b.get("note", ""), "note", 2000, empty=True),
                   updated_by="gwadmin-web", **st)
        stmts = [_ver_guard(parts, b.get("version"), "the GM monsters of this map"),
                 _upsert("gm_monster_spawn_edits", ["map_id", "object_id"], row)]
        act = "spawn %d (%s) on %s: %s" % (oid, pub["display_name"], _map_name(ctx, mid), "; ".join(
            filter(None, ["off" if not en else "", ("moved to %.1f, %.1f" % (x, z)) if x is not None else "",
                          ", ".join("%s %s" % (k, v) for k, v in st.items() if v is not None)])) or "no change")
    return _write(ctx, [common.scope("gm_monster_spawn_edits", "map_id = %d AND object_id = %d" % (mid, oid))], act, stmts,
                  ["gm_monster_spawn_edits.*"], guard=["gm_monsters"], target="gm monsters map %d" % mid)


def _free_objects(ctx, mid, n):
    ids = _q(ctx, """(SELECT coalesce(json_agg(c ORDER BY c), '[]') FROM (SELECT c FROM generate_series(%d, %d) c WHERE c NOT IN (
        SELECT d.object_id FROM monster_spawn_definitions d WHERE d.map_id = %d UNION SELECT d.object_id FROM npc_spawn_definitions d
        WHERE d.map_id = %d UNION SELECT g.object_id FROM gm_monster_spawns g WHERE g.map_id = %d UNION SELECT g.object_id FROM
        gm_npc_spawns g WHERE g.map_id = %d) ORDER BY c LIMIT %d) x)""" % (GM_OBJECT_FIRST, GM_OBJECT_LAST, mid, mid, mid, mid, n))
    if len(ids) < n:
        raise Bad("only %d free GM object ids (%d-%d) left on this map" % (len(ids), GM_OBJECT_FIRST, GM_OBJECT_LAST))
    return ids


def _packet(base_hex, oid, x, z, facing, level=None, max_hp=None):
    p = bytearray(bytes.fromhex(base_hex))
    if not 108 <= len(p) <= 1200:
        raise Bad("the template's appearance packet has %d bytes (108-1200 expected)" % len(p))
    mh = max_hp if max_hp is not None else struct.unpack_from("<I", p, P_MAX_HP)[0]
    lv = level if level is not None else struct.unpack_from("<I", p, P_LEVEL)[0]
    if not mh or not lv:
        raise Bad("level and max HP must be above 0")
    struct.pack_into("<I", p, P_OBJECT, oid)
    struct.pack_into("<I", p, P_LEVEL, lv)
    struct.pack_into("<I", p, P_MAX_HP, mh)
    struct.pack_into("<I", p, P_CUR_HP, mh)
    struct.pack_into("<f", p, P_X, x)
    struct.pack_into("<f", p, P_Z, z)
    struct.pack_into("<f", p, P_FACING, facing or 0.0)
    return "\\x" + bytes(p).hex()


@M.post("gm/spawn/create")
def gm_spawn_create(ctx):
    """POST {map_id, template_key, version, x, z, facing, count 1-50, spread, name, display_name, level, maximum_health,
    values: {physical_attack ...}} - new spawn points: the appearance packet is copied from a published spawn of that monster
    on that map (as upstream's GM tool does) with the new object id, position, level and HP."""
    b = ctx.body
    mid = _int(b.get("map_id"), 0, 255, "map")
    key = _text(b.get("template_key"), "monster template", 128, rx=r"^[A-Za-z0-9_\-]+$")
    base = _q(ctx, "(SELECT to_jsonb(x) FROM (SELECT d.scene_key, d.display_name, encode(d.clear_bytes, 'hex') AS hex FROM "
                   "monster_spawn_definitions d JOIN monster_content_publication p ON p.family = 'monsters' AND p.revision = "
                   "d.revision WHERE d.map_id = %d AND d.template_key = %s ORDER BY d.object_id LIMIT 1) x)" % (mid, lit(key)))
    if not base:
        raise Bad("%s has no published spawn on %s, so there is no appearance to copy. Pick a monster that already lives on "
                  "this map (the grid lists them)." % (key, _map_name(ctx, mid)))
    n = _int(b.get("count", 1), 1, 50, "how many")
    x0, z0 = _float(b.get("x"), "X"), _float(b.get("z"), "Z")
    spread = _float(b.get("spread", 0), "spread", 0, 200)
    fc = _float(b.get("facing", 0) or 0, "facing", -1000, 1000)
    lvl = _int(b.get("level"), 1, 10000, "level", null=True)
    mhp = _int(b.get("maximum_health"), 1, INT_MAX, "max HP", null=True)
    st = _stats(b.get("values"), allow_level=False)
    for c in ("current_health", "maximum_health"):
        st.pop(c, None)
    prefix = _text(b.get("name") or ("GM-" + key)[:100], "name", 100, rx=r"^[\x20-\x7e]+$")
    disp = _text(b.get("display_name", ""), "display name", 255, empty=True)
    if _v(ctx, "(SELECT count(*) FROM gm_monster_spawns)") + n > GM_MONSTER_MAX:
        raise Bad("the server takes at most %d GM monster spawns" % GM_MONSTER_MAX)
    names = [prefix if n == 1 else "%s-%02d" % (prefix, i + 1) for i in range(n)]
    taken = _q(ctx, "(SELECT coalesce(json_agg(name), '[]') FROM gm_monster_spawns WHERE name IN (%s))" % ", ".join(lit(x) for x in names))
    if taken:
        raise Bad("the name %s is taken (GM spawn names are unique)" % ", ".join(taken))
    ids = _free_objects(ctx, mid, n)
    stmts = [_ver_guard(_gm_monster_parts(mid), b.get("version"), "the GM monsters of this map")]
    for i, oid in enumerate(ids):
        a = 2 * math.pi * i / n
        x, z = (x0 + math.cos(a) * spread, z0 + math.sin(a) * spread) if n > 1 and spread > 0 else (x0, z0)
        # one rounded value for the row AND the packet: the server refuses a spawn whose pos_x / pos_z differ from the
        # packet's coordinates by more than a few float steps (CapturedMonsterSpawn.Validate)
        x, z = round(x, 2), round(z, 2)
        row = {"name": names[i], "map_id": mid, "scene_key": base["scene_key"], "template_key": key, "display_name": disp,
               "object_id": oid, "pos_x": round(x, 2), "pos_z": round(z, 2), "enabled": True,
               "note": "gwadmin-web (appearance copied from %s)" % key, "updated_by": "gwadmin-web"}
        stmts.append("INSERT INTO gm_monster_spawns (%s, clear_bytes) VALUES (%s, %s::bytea)" % (
            ", ".join(ident(c) for c in row), ", ".join(lit(v) for v in row.values()), lit(_packet(base["hex"], oid, x, z, fc, lvl, mhp))))
        if any(v is not None for v in st.values()):
            stmts.append(_upsert("gm_monster_spawn_edits", ["map_id", "object_id"], dict(
                {"map_id": mid, "object_id": oid, "enabled": True, "note": "stats of GM spawn " + names[i],
                 "updated_by": "gwadmin-web"}, **{k: v for k, v in st.items() if v is not None})))
    sc = [common.scope("gm_monster_spawns", "map_id = %d AND object_id IN (%s)" % (mid, ", ".join(str(i) for i in ids))),
          common.scope("gm_monster_spawn_edits", "map_id = %d AND object_id IN (%s)" % (mid, ", ".join(str(i) for i in ids)))]
    r = _write(ctx, sc, "add %d x %s (%s) on %s at %.0f, %.0f" % (n, base["display_name"], key, _map_name(ctx, mid), x0, z0),
               stmts, ["gm_monster_spawns.*", "gm_monster_spawn_edits.*"], guard=["gm_monsters"], target="gm monsters map %d" % mid)
    r["object_ids"] = ids
    return r


@M.post("gm/spawn/update")
def gm_spawn_update(ctx):
    """POST {id, version, name, display_name, enabled, x, z, facing, level, maximum_health, note}: the packet is patched too."""
    b = ctx.body
    sid = _int(b.get("id"), 1, 2 ** 62, "GM spawn")
    row = _q(ctx, "(SELECT to_jsonb(x) FROM (SELECT id, map_id, object_id, name, template_key, encode(clear_bytes, 'hex') AS hex "
                  "FROM gm_monster_spawns WHERE id = %d) x)" % sid)
    if not row:
        raise NotFound("no GM monster spawn #%d" % sid)
    mid = int(row["map_id"])
    x, z = round(_float(b.get("x"), "X"), 2), round(_float(b.get("z"), "Z"), 2)
    fc = _float(b.get("facing", 0) or 0, "facing", -1000, 1000)
    lvl = _int(b.get("level"), 1, 10000, "level", null=True)
    mhp = _int(b.get("maximum_health"), 1, INT_MAX, "max HP", null=True)
    vals = {"name": _text(b.get("name"), "name", 120, rx=r"^[\x20-\x7e]+$"),
            "display_name": _text(b.get("display_name", ""), "display name", 255, empty=True),
            "enabled": _bool(b.get("enabled", True)), "pos_x": round(x, 2), "pos_z": round(z, 2),
            "note": _text(b.get("note", ""), "note", 2000, empty=True)}
    sets = ", ".join("%s = %s" % (ident(c), lit(v)) for c, v in vals.items())
    pkt = _packet(row["hex"], int(row["object_id"]), x, z, fc, lvl, mhp)
    stmts = [_ver_guard(_gm_monster_parts(mid), b.get("version"), "the GM monsters of this map"),
             "UPDATE gm_monster_spawns SET %s, clear_bytes = %s::bytea, updated_at = now(), updated_by = 'gwadmin-web' WHERE id = %d"
             % (sets, lit(pkt), sid)]
    return _write(ctx, [common.scope("gm_monster_spawns", "id = %d" % sid)], "GM spawn %s: %s at %.1f, %.1f%s" % (
        vals["name"], "on" if vals["enabled"] else "off", x, z, (", level %s" % lvl) if lvl else ""), stmts,
        ["gm_monster_spawns.*"], guard=["gm_monsters"], target="gm monsters map %d" % mid)


@M.post("gm/spawn/delete")
def gm_spawn_delete(ctx):
    """POST {id, version}: the spawn and its own stat edit (keyed by map + object id), in one transaction."""
    b = ctx.body
    sid = _int(b.get("id"), 1, 2 ** 62, "GM spawn")
    row = _q(ctx, "(SELECT to_jsonb(x) FROM (SELECT id, map_id, object_id, name FROM gm_monster_spawns WHERE id = %d) x)" % sid)
    if not row:
        raise NotFound("no GM monster spawn #%d" % sid)
    mid, oid = int(row["map_id"]), int(row["object_id"])
    stmts = [_ver_guard(_gm_monster_parts(mid), b.get("version"), "the GM monsters of this map"),
             "DELETE FROM gm_monster_spawn_edits WHERE map_id = %d AND object_id = %d" % (mid, oid),
             "DELETE FROM gm_monster_spawns WHERE id = %d" % sid]
    return _write(ctx, [common.scope("gm_monster_spawns", "id = %d" % sid),
                        common.scope("gm_monster_spawn_edits", "map_id = %d AND object_id = %d" % (mid, oid))],
                  "delete GM spawn %s (object %d on %s)" % (row["name"], oid, _map_name(ctx, mid)), stmts,
                  ["gm_monster_spawns.*", "gm_monster_spawn_edits.*"], guard=["gm_monsters"], target="gm monsters map %d" % mid)


# =========================================================================== GM NPCs + dialogue
def _npc_parts():
    return [("gm_npc_spawns", "true", "id")]


def _dlg_parts(key):
    return _dlg_parts_sql(lit(key))


def _dlg_parts_sql(k):
    return [("gm_npc_dialogues", "dialogue_key = %s" % k, "dialogue_key"),
            ("gm_npc_dialogue_pages", "dialogue_key = %s" % k, "page_index"),
            ("gm_npc_dialogue_buttons", "dialogue_key = %s" % k, "page_index, slot"),
            ("gm_npc_spawns", "dialogue_key = %s" % k, "id")]


@M.get("npc")
def npc(ctx):
    d = _q(ctx, """json_build_object(
        'spawns', (SELECT coalesce(json_agg(to_jsonb(s) ORDER BY s.map_id, s.id), '[]') FROM gm_npc_spawns s),
        'dialogues', (SELECT coalesce(json_agg(to_jsonb(d) || jsonb_build_object('pages', (SELECT coalesce(jsonb_agg(to_jsonb(p) ||
             jsonb_build_object('buttons', (SELECT coalesce(jsonb_agg(to_jsonb(b) ORDER BY b.slot), '[]') FROM gm_npc_dialogue_buttons b
             WHERE b.dialogue_key = p.dialogue_key AND b.page_index = p.page_index)) ORDER BY p.page_index), '[]') FROM
             gm_npc_dialogue_pages p WHERE p.dialogue_key = d.dialogue_key)) ORDER BY d.dialogue_key), '[]') FROM gm_npc_dialogues d),
        'skipped', (SELECT coalesce(json_agg(s.id), '[]') FROM gm_npc_spawns s WHERE NOT EXISTS (SELECT 1 FROM npc_spawn_definitions d
             JOIN npc_content_publication p ON p.family = 'npcs' AND p.revision = d.revision WHERE d.map_id = s.map_id AND
             d.template_key = s.template_key)))""")
    d["spawns_version"] = _version(ctx, _npc_parts())
    vers = _q(ctx, "(SELECT coalesce(json_object_agg(dd.dialogue_key, %s), '{}') FROM gm_npc_dialogues dd)" % _ver_expr(
        _dlg_parts_sql("dd.dialogue_key")[:3]))
    for x in d["dialogues"]:
        x["version"] = vers.get(x["dialogue_key"])
    for s in d["spawns"]:
        s["map_name"] = _map_name(ctx, s["map_id"])
    d["problems"] = _problems(ctx, "gm_npcs")
    d["default_function_flag"] = DEFAULT_FUNCTION_FLAG
    return d


@M.get(r"npc/templates/(\d+)")
def npc_templates(ctx, mid):
    """the NPC models published on a map: a GM NPC can only use one of these (GmNpcDialoguePolicy.BuildPlacements)."""
    mid = _int(mid, 0, 255, "map")
    rows = _q(ctx, """(SELECT coalesce(json_agg(r ORDER BY r.template_key), '[]') FROM (SELECT d.template_key, min(d.npc_key) AS npc_key,
        min(d.appearance_type) AS appearance_type, count(*) AS used, min(d.pos_x) AS x, min(d.pos_z) AS z FROM npc_spawn_definitions d
        JOIN npc_content_publication p ON p.family = 'npcs' AND p.revision = d.revision WHERE d.map_id = %d GROUP BY d.template_key) r)""" % mid)
    npcs = ctx.catalog.file("npcs")
    for r in rows:
        n = npcs.get(r["npc_key"]) or {}
        t = (n.get("templates") or {}).get(r["template_key"]) or {}
        r["name"] = t.get("name") or n.get("name") or r["template_key"]
        r["desc"] = n.get("desc") or ""
    return rows


@M.post("npc/spawn/save")
def npc_spawn_save(ctx):
    """POST {id|null, version (of all GM NPC placements), name, map_id, template_key, npc_key, dialogue_key|null, x, z, facing,
    appearance_type|null (copy the model's), enabled, note}."""
    b = ctx.body
    sid = _int(b.get("id"), 1, 2 ** 62, "GM NPC", null=True)
    mid = _int(b.get("map_id"), 0, 255, "map")
    tk = _text(b.get("template_key"), "NPC model", 255, rx=r"^[A-Za-z0-9_\-]+$")
    pub = _q(ctx, "(SELECT to_jsonb(x) FROM (SELECT min(d.appearance_type) AS appearance_type FROM npc_spawn_definitions d JOIN "
                  "npc_content_publication p ON p.family = 'npcs' AND p.revision = d.revision WHERE d.map_id = %d AND d.template_key = %s "
                  "HAVING count(*) > 0) x)" % (mid, lit(tk)))
    if not pub:
        raise Bad("the model %s is not used by any NPC on %s. The server only places a GM NPC with a model already on that map "
                  "(the client would load nothing); pick one from the list." % (tk, _map_name(ctx, mid)))
    name = _text(b.get("name"), "name", 120, rx=r"^[\x20-\x7e]+$")
    key = _text(b.get("npc_key") or "GM_" + re.sub(r"[^A-Za-z0-9_]", "_", name)[:60], "NPC key", 96, rx=r"^[A-Za-z0-9_\-]+$")
    dk = b.get("dialogue_key") or None
    if dk is not None:
        dk = _text(dk, "dialogue", 120)
        if not _v(ctx, "(SELECT EXISTS (SELECT 1 FROM gm_npc_dialogues WHERE dialogue_key = %s))" % lit(dk)):
            raise Bad("no GM dialogue %s (make it on the Dialogues tab first)" % dk)
    other = "id <> %d" % sid if sid else "true"
    if _v(ctx, "(SELECT EXISTS (SELECT 1 FROM gm_npc_spawns WHERE npc_key = %s AND %s))" % (lit(key), other)):
        raise Bad("the NPC key %s is used by another GM NPC (keys must be unique)" % key)
    if _v(ctx, "(SELECT EXISTS (SELECT 1 FROM gm_npc_spawns WHERE name = %s AND %s))" % (lit(name), other)):
        raise Bad("the name %s is used by another GM NPC" % name)
    vals = {"name": name, "map_id": mid, "npc_key": key, "template_key": tk, "dialogue_key": dk,
            "appearance_type": _int(b.get("appearance_type") if b.get("appearance_type") not in (None, "") else pub["appearance_type"],
                                    0, 4294967295, "appearance type"),
            "pos_x": round(_float(b.get("x"), "X"), 2), "pos_z": round(_float(b.get("z"), "Z"), 2),
            "facing": _float(b.get("facing", 0) or 0, "facing", -1000, 1000), "enabled": _bool(b.get("enabled", True)),
            "note": _text(b.get("note", ""), "note", 2000, empty=True)}
    stmts = [_ver_guard(_npc_parts(), b.get("version"), "the GM NPC list")]
    if sid:
        cur = _q(ctx, "(SELECT to_jsonb(x) FROM (SELECT map_id, object_id FROM gm_npc_spawns WHERE id = %d) x)" % sid)
        if not cur:
            raise NotFound("no GM NPC #%d" % sid)
        if int(cur["map_id"]) != mid:
            vals["object_id"] = _free_objects(ctx, mid, 1)[0]
        stmts.append("UPDATE gm_npc_spawns SET %s, updated_at = now(), updated_by = 'gwadmin-web' WHERE id = %d" % (
            ", ".join("%s = %s" % (ident(c), lit(v)) for c, v in vals.items()), sid))
        sc = [common.scope("gm_npc_spawns", "id = %d" % sid)]
        act = "GM NPC %s on %s at %.0f, %.0f" % (name, _map_name(ctx, mid), vals["pos_x"], vals["pos_z"])
    else:
        if _v(ctx, "(SELECT count(*) FROM gm_npc_spawns)") >= GM_NPC_MAX:
            raise Bad("the server takes at most %d GM NPCs" % GM_NPC_MAX)
        vals["object_id"] = _free_objects(ctx, mid, 1)[0]
        vals["updated_by"] = "gwadmin-web"
        stmts.append(common.insert_sql("gm_npc_spawns", vals))
        sc = [common.scope("gm_npc_spawns", "npc_key = %s" % lit(key))]
        act = "add GM NPC %s (%s) on %s at %.0f, %.0f" % (name, tk, _map_name(ctx, mid), vals["pos_x"], vals["pos_z"])
    return _write(ctx, sc, act, stmts, ["gm_npc_spawns.*"], guard=["gm_npcs"], target="gm npcs")


@M.post("npc/spawn/delete")
def npc_spawn_delete(ctx):
    b = ctx.body
    sid = _int(b.get("id"), 1, 2 ** 62, "GM NPC")
    nm = _v(ctx, "(SELECT name FROM gm_npc_spawns WHERE id = %d)" % sid)
    if not nm:
        raise NotFound("no GM NPC #%d" % sid)
    stmts = [_ver_guard(_npc_parts(), b.get("version"), "the GM NPC list"), "DELETE FROM gm_npc_spawns WHERE id = %d" % sid]
    return _write(ctx, [common.scope("gm_npc_spawns", "id = %d" % sid)], "delete GM NPC %s" % nm, stmts, ["gm_npc_spawns.*"],
                  guard=["gm_npcs"], target="gm npcs")


@M.post("npc/dialogue/save")
def dialogue_save(ctx):
    """POST {dialogue_key, version, display_name, function_flag, entry_page, enabled, note, pages: [{page_index, body_text, note,
    buttons: [{slot 1-12, label, result_number, next_page_index|null}]}]} - the whole tree in one transaction."""
    b = ctx.body
    key = _text(b.get("dialogue_key"), "dialogue key", 120, rx=r"^[A-Za-z0-9_\-.]+$")
    pages = list(b.get("pages") or [])
    if not pages:
        raise Bad("a dialogue needs at least one page")
    if len(pages) > 200:
        raise Bad("at most 200 pages")
    pidx, prow, brow, results = set(), [], [], set()
    for p in pages:
        pi = _int(p.get("page_index"), 1, 100000, "page number")
        if pi in pidx:
            raise Bad("two pages have number %d" % pi)
        pidx.add(pi)
        prow.append({"dialogue_key": key, "page_index": pi, "body_text": _text(p.get("body_text", ""), "page %d text" % pi, 4000, empty=True),
                     "note": _text(p.get("note", ""), "page note", 2000, empty=True)})
        slots = set()
        for x in p.get("buttons") or []:
            sl = _int(x.get("slot"), 1, 12, "page %d button slot" % pi)
            if sl in slots:
                raise Bad("page %d has two buttons in slot %d" % (pi, sl))
            slots.add(sl)
            # the number the client sends back is the wire id page * 1000 + slot (GmNpcDialoguePolicy.ButtonSubId), and
            # the server looks the click up by result_number: so result_number IS that wire id (upstream's GM tool
            # writes the same), never a free number - a different one makes the button do nothing in game
            rn = pi * 1000 + sl
            if rn in results:
                raise Bad("page %d button %d is twice in this dialogue" % (pi, sl))
            results.add(rn)
            brow.append({"dialogue_key": key, "page_index": pi, "slot": sl, "label": _text(x.get("label", ""), "button label", 120, empty=True),
                         "result_number": rn, "next_page_index": _int(x.get("next_page_index"), 1, 100000, "next page", null=True)})
    for x in brow:
        if x["next_page_index"] is not None and x["next_page_index"] not in pidx:
            raise Bad("page %d button %d leads to page %d, which does not exist" % (x["page_index"], x["slot"], x["next_page_index"]))
    ep = _int(b.get("entry_page", min(pidx)), 1, 100000, "first page")
    if ep not in pidx:
        raise Bad("the first page %d does not exist" % ep)
    d = {"dialogue_key": key, "display_name": _text(b.get("display_name", ""), "title", 255, empty=True),
         "function_flag": _int(b.get("function_flag", DEFAULT_FUNCTION_FLAG), 0, 67108864, "client function slot"),
         "entry_page": ep, "enabled": _bool(b.get("enabled", True)), "note": _text(b.get("note", ""), "note", 2000, empty=True),
         "updated_by": "gwadmin-web"}
    parts = _dlg_parts(key)
    stmts = [_ver_guard(parts[:3], b.get("version"), "the dialogue " + key),
             _upsert("gm_npc_dialogues", ["dialogue_key"], d),
             "DELETE FROM gm_npc_dialogue_buttons WHERE dialogue_key = %s" % lit(key),
             "DELETE FROM gm_npc_dialogue_pages WHERE dialogue_key = %s AND page_index NOT IN (%s)" % (lit(key), ", ".join(str(x) for x in sorted(pidx)))]
    stmts += ["%s ON CONFLICT (dialogue_key, page_index) DO UPDATE SET body_text = EXCLUDED.body_text, note = EXCLUDED.note" %
              common.insert_sql("gm_npc_dialogue_pages", p) for p in prow]
    stmts += [common.insert_sql("gm_npc_dialogue_buttons", x) for x in brow]
    return _write(ctx, [common.scope(t_, w_) for t_, w_, _ in parts[:3]], "GM dialogue %s: %d pages, %d buttons" % (key, len(prow), len(brow)),
                  stmts, ["gm_npc_dialogues.*", "gm_npc_dialogue_pages.*", "gm_npc_dialogue_buttons.*"], guard=["gm_npcs"],
                  target="gm dialogue " + key)


@M.post("npc/dialogue/delete")
def dialogue_delete(ctx):
    """the tree, its pages and buttons; GM NPCs that used it keep standing but cannot be clicked (dialogue_key -> NULL)."""
    b = ctx.body
    key = _text(b.get("dialogue_key"), "dialogue key", 120)
    parts = _dlg_parts(key)
    stmts = [_ver_guard(parts[:3], b.get("version"), "the dialogue " + key),
             "DELETE FROM gm_npc_dialogues WHERE dialogue_key = %s" % lit(key)]
    users = _q(ctx, "(SELECT coalesce(json_agg(id), '[]') FROM gm_npc_spawns WHERE dialogue_key = %s)" % lit(key))
    sc = [common.scope(t_, w_) for t_, w_, _ in parts[:3]]
    if users:      # keyed by id: after the delete these rows no longer match dialogue_key = K
        sc.append(common.scope("gm_npc_spawns", "id IN (%s)" % ", ".join(str(int(i)) for i in users)))
    return _write(ctx, sc, "delete GM dialogue %s" % key, stmts,
                  ["gm_npc_dialogues.*", "gm_npc_dialogue_pages.*", "gm_npc_dialogue_buttons.*", "gm_npc_spawns.dialogue_key"],
                  guard=["gm_npcs"], target="gm dialogue " + key)


# =========================================================================== targets (events and gifts)
def _targets(ctx, t):
    """characters of a group: {all | camp 0/1 | class 0-3 | minlv..maxlv | ids: [..]} -> rows with online state."""
    t = t or {}
    where = ["c.lifecycle_state = 'active'"]
    if t.get("camp") not in (None, "", "all"):
        where.append("c.camp = %d" % _int(t.get("camp"), 0, 1, "camp"))
    if t.get("class") not in (None, "", "all"):
        where.append("c.profession = %d" % _int(t.get("class"), 0, 3, "class"))
    if t.get("minlv") not in (None, ""):
        where.append("c.fighter_job_lv >= %d" % _int(t.get("minlv"), 1, 200, "lowest level"))
    if t.get("maxlv") not in (None, ""):
        where.append("c.fighter_job_lv <= %d" % _int(t.get("maxlv"), 1, 200, "highest level"))
    if t.get("ids"):
        ids = [_int(x, 1, INT_MAX, "character") for x in t["ids"]][:5000]
        where.append("c.id IN (%s)" % ", ".join(str(x) for x in ids))
    rows = _q(ctx, """(SELECT coalesce(json_agg(r ORDER BY r.id), '[]') FROM (SELECT c.id, c.name, c.fighter_job_lv AS level, c.camp,
        c.profession, c."Money" AS silver, c."Stone" AS gold, c."BindingGold" AS bgold,
        (c.checkpoint_owner_id IS NOT NULL OR a.login_presence_token IS NOT NULL OR a.login_status <> 0) AS online
        FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE %s) r)""" % " AND ".join(where))
    return rows


@M.post("targets")
def targets(ctx):
    rows = _targets(ctx, ctx.body)
    return {"characters": rows, "online": sum(1 for r in rows if r["online"]), "offline": sum(1 for r in rows if not r["online"])}


# =========================================================================== EXP events
@M.get("events")
def events(ctx):
    d = _q(ctx, """json_build_object(
        'events', (SELECT coalesce(json_agg(e ORDER BY e.activated_at DESC), '[]') FROM (SELECT source, min(status_id) AS status_id,
             min(bonus_basis_points) AS bonus_basis_points, min(activated_at) AS activated_at, max(expires_at) AS expires_at,
             count(*) AS characters, sum(CASE WHEN remaining_online_ticks > 0 OR remaining_online_ticks IS NULL THEN 1 ELSE 0 END)
             AS active FROM character_experience_modifiers WHERE kind = %(k)d GROUP BY source) e),
        'boosts', (SELECT coalesce(json_agg(b ORDER BY b.kind), '[]') FROM (SELECT kind, status_id, count(*) AS characters FROM
             character_experience_modifiers GROUP BY kind, status_id) b),
        'reward_settings', (SELECT to_jsonb(s) FROM monster_reward_settings s WHERE setting_id = 1),
        'now', now())""" % {"k": EVENT_KIND})
    if _v(ctx, "(SELECT to_regclass('public.server_rates') IS NOT NULL)"):
        d["rates"] = _q(ctx, "(SELECT coalesce(json_agg(json_build_object('key', rate_key, 'value', value) ORDER BY rate_key), '[]') "
                             "FROM server_rates WHERE rate_key LIKE 'exp.%')")
    else:
        d["rates"] = []
    d["statuses"] = EVENT_STATUSES
    d["kind"] = EVENT_KIND
    d["templates"] = [
        {"name": "Double EXP weekend", "status_id": 511, "bonus_pct": 100, "hours": 48, "start": "next_saturday"},
        {"name": "Triple EXP evening", "status_id": 586, "bonus_pct": 200, "hours": 4, "start": "today_18"},
        {"name": "Holiday EXP week", "status_id": 1007, "bonus_pct": 50, "hours": 168, "start": "now"},
        {"name": "Newcomer boost (level 1-40)", "status_id": 508, "bonus_pct": 100, "hours": 72, "start": "now", "maxlv": 40},
    ]
    return d


def _ts(v, what):
    try:
        t = datetime.datetime.fromisoformat(str(v).replace("Z", "+00:00"))
    except ValueError:
        raise Bad("%s must be a date and time (got %r)" % (what, v))
    if t.tzinfo is None:
        raise Bad("%s needs a time zone" % what)
    return t.astimezone(datetime.timezone.utc)


@M.post("events/exp/start")
def exp_start(ctx):
    """POST {name, status_id, bonus_pct (1-100000 %), start, end (ISO with zone), target {...}, advanced: true} - one EXP boost
    row per OFFLINE character of the group (kind 200, source 'gm-event:<name>'); online characters are skipped (listed)."""
    b = ctx.body
    name = _text(b.get("name"), "event name", 64 - len(EVENT_PREFIX), rx=r"^[A-Za-z0-9 _\-.!']+$")
    src = EVENT_PREFIX + name
    status = _int(b.get("status_id"), 1, 65535, "buff icon")
    if status not in EVENT_STATUSES:
        raise Bad("pick one of the EXP buff icons the client knows: %s" % ", ".join(str(x) for x in EVENT_STATUSES))
    bonus = _int(round(float(b.get("bonus_pct") or 0) * 100), 1, 10_000_000, "bonus (0.01 % - 100,000 %)")
    st, en = _ts(b.get("start"), "start"), _ts(b.get("end"), "end")
    if en <= st:
        raise Bad("the end must be after the start")
    if (en - st).days > 366:
        raise Bad("an event lasts at most a year")
    rows = _targets(ctx, b.get("target"))
    off = [r for r in rows if not r["online"]]
    on = [r for r in rows if r["online"]]
    if not off:
        raise Bad("no offline character in this group (%d online: they get it when you apply the event again after they "
                  "log out)" % len(on) if on else "no character matches this group")
    ids = ", ".join(str(r["id"]) for r in off)
    stmts = ["""INSERT INTO character_experience_modifiers (character_id, status_id, kind, bonus_basis_points, priority, source,
        activated_at, expires_at, remaining_online_ticks) SELECT c.id, %(s)d, %(k)d, %(b)d, 1, %(src)s, %(st)s::timestamptz,
        %(en)s::timestamptz, NULL FROM character_base c WHERE c.id IN (%(ids)s)
        ON CONFLICT (character_id, kind) DO UPDATE SET status_id = EXCLUDED.status_id, bonus_basis_points = EXCLUDED.bonus_basis_points,
        priority = EXCLUDED.priority, source = EXCLUDED.source, activated_at = EXCLUDED.activated_at, expires_at = EXCLUDED.expires_at,
        remaining_online_ticks = NULL""" % {"s": status, "k": EVENT_KIND, "b": bonus, "src": lit(src), "st": lit(st.isoformat()),
                                            "en": lit(en.isoformat()), "ids": ids}]
    r = _write(ctx, [common.scope("character_experience_modifiers", "kind = %d AND character_id IN (%s)" % (EVENT_KIND, ids))],
               "EXP event %s: +%s %% for %d characters (%s - %s UTC)" % (name, bonus / 100, len(off), st.strftime("%Y-%m-%d %H:%M"),
                                                                          en.strftime("%Y-%m-%d %H:%M")),
               stmts, ["character_experience_modifiers.*"], guard=["events"], applies="next_login", target="exp event " + name,
               advanced=bool(b.get("advanced")), lock_cids=[r_["id"] for r_ in off])
    r["applied"] = [{"id": x["id"], "name": x["name"]} for x in off]
    r["skipped"] = [{"id": x["id"], "name": x["name"], "why": "online"} for x in on]
    return r


@M.post("events/exp/stop")
def exp_stop(ctx):
    """POST {name, advanced: true} - remove the event's rows of every OFFLINE character (online ones are listed)."""
    b = ctx.body
    name = _text(b.get("name"), "event name", 64)
    src = name if name.startswith(EVENT_PREFIX) else EVENT_PREFIX + name
    who = _q(ctx, """(SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name, 'online', c.checkpoint_owner_id IS NOT NULL
        OR a.login_presence_token IS NOT NULL OR a.login_status <> 0) ORDER BY c.id), '[]') FROM character_experience_modifiers m
        JOIN character_base c ON c.id = m.character_id JOIN accounts a ON a.id = c.account_id WHERE m.kind = %d AND m.source = %s)"""
             % (EVENT_KIND, lit(src)))
    off = [x for x in who if not x["online"]]
    if not off:
        raise Bad("no offline character has the event %s%s" % (src[len(EVENT_PREFIX):], (" (%d online)" % len(who)) if who else ""))
    ids = ", ".join(str(x["id"]) for x in off)
    r = _write(ctx, [common.scope("character_experience_modifiers", "kind = %d AND character_id IN (%s)" % (EVENT_KIND, ids))],
               "end EXP event %s for %d characters" % (src[len(EVENT_PREFIX):], len(off)),
               ["DELETE FROM character_experience_modifiers WHERE kind = %d AND source = %s AND character_id IN (%s)" % (EVENT_KIND, lit(src), ids)],
               ["character_experience_modifiers.*"], guard=["events"], applies="next_login", target="exp event",
               advanced=bool(b.get("advanced")), lock_cids=[x["id"] for x in off])
    r["applied"] = off
    r["skipped"] = [dict(x, why="online") for x in who if x["online"]]
    return r


# =========================================================================== event gifts (live for online characters)
@M.post("events/gift")
def gift(ctx):
    """POST {what: 'item'|'money', item_id, quantity, item_quality, item_grade, bound, silver, gold, bgold, target {...}} -
    per character through the shared save path: online + round-2 -> the running server applies it (the player sees it at
    once); offline -> guarded database write (next login); online on a stock server -> skipped with the reason."""
    b = ctx.body
    rows = _targets(ctx, b.get("target"))
    if not rows:
        raise Bad("no character matches this group")
    if len(rows) > 500:
        raise Bad("at most 500 characters per gift (%d match)" % len(rows))
    _need_writable(ctx)
    out = []
    if b.get("what") == "item":
        pid, meta = _item(ctx, b.get("item_id"))
        p = {"item": pid, "quantity": _int(b.get("quantity", 1), 1, 9999, "quantity")}
        for k, (lo, hi, src) in {"quality": (0, 20, "item_quality"), "grade": (0, 25, "item_grade"), "bound": (0, 1, "bound")}.items():
            if b.get(src) not in (None, ""):
                p[k] = _int(b.get(src), lo, hi, k)
        for c in rows:
            out.append(_one_gift(ctx, c, lambda c=c: ctx.bag_item(c["id"], "add", p)))
        what = "%s x%d" % (meta["name"], p["quantity"])
    elif b.get("what") == "money":
        add = {"Money": _int(b.get("silver", 0) or 0, 0, INT_MAX, "Silver"), "Stone": _int(b.get("gold", 0) or 0, 0, INT_MAX, "Gold"),
               "BindingGold": _int(b.get("bgold", 0) or 0, 0, INT_MAX, "B-Gold")}
        add = {k: v for k, v in add.items() if v}
        if not add:
            raise Bad("give some Silver, Gold or B-Gold")
        cur_key = {"Money": "silver", "Stone": "gold", "BindingGold": "bgold"}
        for c in rows:
            vals = {k: min(INT_MAX, int(c[cur_key[k]]) + v) for k, v in add.items()}
            exp = {k: c[cur_key[k]] for k in add}
            out.append(_one_gift(ctx, c, lambda c=c, vals=vals, exp=exp: ctx.set_character_fields(
                c["id"], vals, expected=exp, label="event gift " + ", ".join("%s +%s" % (
                    {"Money": "Silver", "Stone": "Gold", "BindingGold": "B-Gold"}[k], "{:,}".format(v)) for k, v in add.items()))))
        what = ", ".join("%s +%s" % ({"Money": "Silver", "Stone": "Gold", "BindingGold": "B-Gold"}[k], "{:,}".format(v)) for k, v in add.items())
    else:
        raise Bad("what must be 'item' or 'money'")
    return {"gift": what, "results": out, "live": sum(1 for x in out if x.get("mode") == "live"),
            "database": sum(1 for x in out if x.get("mode") == "database"), "skipped": sum(1 for x in out if not x.get("ok"))}


def _one_gift(ctx, c, fn):
    try:
        r = fn()
        return {"id": c["id"], "name": c["name"], "ok": True, "mode": r.get("mode"), "applies_text": r.get("applies_text"),
                "change": (r.get("change") or {}).get("id")}
    except common.OnlineRefused as e:
        return {"id": c["id"], "name": c["name"], "ok": False, "why": str(e), "online": True}
    except (Bad, Refused, common.Conflict, common.RebornError) as e:
        return {"id": c["id"], "name": c["name"], "ok": False, "why": str(e)}


# =========================================================================== read-only views
@M.get("bosses")
def bosses(ctx):
    d = _q(ctx, """json_build_object(
        'bosses', (SELECT coalesce(json_agg(to_jsonb(b) ORDER BY b.map_id), '[]') FROM gameplay_world_boss_definitions b WHERE
             b.revision = (SELECT revision FROM gameplay_content_publication WHERE family = 'gameplay')),
        'control', (SELECT coalesce(json_agg(to_jsonb(c) - 'death_token' ORDER BY c.map_id), '[]') FROM faction_area_experience_control c),
        'pending', (SELECT coalesce(json_agg(to_jsonb(p)), '[]') FROM gameplay_pending_world_boss_areas p WHERE p.revision =
             (SELECT revision FROM gameplay_content_publication WHERE family = 'gameplay')),
        'revision', (SELECT revision FROM gameplay_content_publication WHERE family = 'gameplay'), 'now', now())""")
    for x in d["bosses"] + d["control"] + d["pending"]:
        if "map_id" in x:
            x["map_name"] = _map_name(ctx, x["map_id"])
    d["why"] = common.GLOSSARY.rules("gameplay_world_boss_definitions").get("why")
    return d


@M.get("calendar")
def calendar(ctx):
    d = _q(ctx, """json_build_object(
        'realms', (SELECT coalesce(json_agg(json_build_object('id', s.id, 'name', s.name, 'enabled', s.enabled, 'host', s.ip_address,
             'game_port', s.game_port, 'time_zone', s.time_zone_id, 'time_zone_revision', s.time_zone_revision,
             'time_zone_updated_at', s.time_zone_updated_at, 'time_zone_updated_by', s.time_zone_updated_by,
             'local_now', to_char(now() AT TIME ZONE s.time_zone_id, 'YYYY-MM-DD HH24:MI:SS'),
             'local_dow', to_char(now() AT TIME ZONE s.time_zone_id, 'FMDay'),
             'utc_offset', to_char(now() AT TIME ZONE s.time_zone_id - now() AT TIME ZONE 'UTC', 'HH24:MI'),
             'next_reset_utc', ((date_trunc('day', now() AT TIME ZONE s.time_zone_id) + interval '1 day') AT TIME ZONE s.time_zone_id))
             ORDER BY s.id), '[]') FROM server s),
        'audit', (SELECT coalesce(json_agg(to_jsonb(a) ORDER BY 1 DESC), '[]') FROM (SELECT * FROM server_time_zone_audit ORDER BY 1 DESC LIMIT 20) a),
        'now_utc', now())""")
    d["why"] = "Calendar settings are pinned by game workers at startup. A saved change takes effect after a coordinated game restart."
    d["writable"] = bool(ctx.rb and ctx.rb.writable)
    d["zones"] = _q(ctx, "(SELECT json_agg(name ORDER BY name) FROM pg_timezone_names WHERE name LIKE '%/%' AND name NOT LIKE 'posix/%' AND name NOT LIKE 'right/%')")
    return d


def _calendar_zone(value):
    if not isinstance(value, str) or len(value) > 64 or not re.fullmatch(r"[A-Za-z0-9_+-]+(?:/[A-Za-z0-9_+-]+)+", value):
        raise Bad("Choose an IANA time zone, for example Australia/Brisbane")
    try:
        ZoneInfo(value)
    except (ZoneInfoNotFoundError, ValueError):
        raise Bad("Unknown time zone: %s" % value)
    return value


@M.post("calendar")
def save_calendar(ctx):
    _need_writable(ctx)
    b = ctx.body
    rid = _int(b.get("realm"), 1, INT_MAX, "realm")
    revision = _int(b.get("expected_revision"), 1, 9_007_199_254_740_991, "calendar revision")
    zone = _calendar_zone(b.get("time_zone"))
    if not _v(ctx, "EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = %s)" % lit(zone)):
        raise Bad("The database does not recognize that time zone")
    current = next((r for r in calendar(ctx)["realms"] if r["id"] == rid), None)
    if not current:
        raise NotFound("No realm #%d" % rid)
    if current["time_zone_revision"] != revision:
        raise common.Conflict("The realm calendar changed. Reload before saving.")
    if current["time_zone"] == zone:
        return {"unchanged": True, "applies": "after_restart", "calendar": calendar(ctx)}
    sql = "UPDATE server SET time_zone_id = %s, time_zone_revision = time_zone_revision + 1, time_zone_updated_at = clock_timestamp(), time_zone_updated_by = 'web:calendar' WHERE id = %d" % (lit(zone), rid)
    result = ctx.rows_write([common.scope("server", "id = %d" % rid, pk=["id"], counters=["time_zone_revision"], ignore=["time_zone_updated_at", "time_zone_updated_by"])],
        "realm #%d calendar: %s to %s" % (rid, current["time_zone"], zone), lambda before: [sql],
        fields=["server.time_zone_id"], advanced=True, applies="after_restart", target="realm #%d" % rid,
        expected=[{"table": "server", "where": {"id": rid}, "values": {"time_zone_revision": revision}}],
        no_undo="Calendar audit is append-only. To revert, select the previous time zone and save a new revision, then coordinate a game restart.")
    result["calendar"] = calendar(ctx)
    return result


@M.get("balances")
def balances(ctx):
    return _q(ctx, """json_build_object(
        'crier', json_build_object('revision', (SELECT revision FROM faction_crier_balance_settings WHERE setting_id = 1),
             'meta', (SELECT to_jsonb(r) FROM faction_crier_balance_revisions r WHERE r.revision = (SELECT revision FROM faction_crier_balance_settings WHERE setting_id = 1)),
             'tiers', (SELECT coalesce(json_agg(to_jsonb(t) ORDER BY t.minimum_level), '[]') FROM faction_crier_balance_tiers t WHERE
                  t.balance_revision = (SELECT revision FROM faction_crier_balance_settings WHERE setting_id = 1)),
             'options', (SELECT coalesce(json_agg(to_jsonb(o) ORDER BY o.sub_id), '[]') FROM faction_crier_balance_options o WHERE
                  o.balance_revision = (SELECT revision FROM faction_crier_balance_settings WHERE setting_id = 1))),
        'award', json_build_object('publication', (SELECT to_jsonb(p) FROM online_award_balance_publication p WHERE family = 'online-award'),
             'entries', (SELECT coalesce(json_agg(to_jsonb(e) ORDER BY e.reward_order), '[]') FROM online_award_balance_entries e WHERE
                  e.revision = (SELECT revision FROM online_award_balance_publication WHERE family = 'online-award'))),
        'why', 'These are sealed balance publications: the server checks them with a hash at start-up, and a change is a new '
               'published revision made by the server''s own settings store. This editor shows them and does not change them.')""")


@M.get("shops")
def shops(ctx):
    s = ctx.catalog.file("shops")
    cats = []
    for name, c in sorted((s.get("server_catalogues") or {}).items()):
        cats.append({"name": name, "label": re.sub(r"(?<=[a-z])(?=[A-Z])", " ", name), "currency": c.get("currency"),
                     "entries": c.get("entries") or []})
    return {"catalogues": cats, "client_items": len((s.get("client_shop_list") or {}).get("items") or []),
            "why": s.get("why_read_only") or "compiled into the server", "currencies": ctx.catalog.file("currencies")}


# ---- game service controls (start / stop / restart / backup / logs): one fixed root helper behind a unix socket
# (Development/server-control/control.py). The editor decides who may call it (an editor_admin sign-in, enforced for every
# request by editor_auth) and how: a stop or restart with players online needs the count the person saw AND an explicit
# acknowledgement, re-checked here against the database at POST time. Env names exist so the tests can use a stub helper.
CONTROL_SOCK = os.environ.get("GWED_CONTROL_SOCKET", "/run/gweditor-control.sock")
CONTROL_ROOT = os.environ.get("GWED_CONTROL_ROOT", "/run/gweditor-control")
CONTROL_TEST = os.environ.get("GWED_CONTROL_TEST") == "1"        # tests only: lets a non-live rig talk to a STUB helper
CONTROL_LOCK = threading.Lock()
CONTROL_ACTIONS = ("start", "stop", "restart", "backup", "logs")
DISRUPTIVE = ("stop", "restart")
_SECRETS = [(re.compile(r"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+"), "Bearer [hidden]"),
            (re.compile(r"(?i)(password|passwd|pwd|secret|token|api[_-]?key|apikey|authorization|bearer|connectionstring)([\"']?\s*[=:]\s*|\s+)(\"[^\"]*\"|'[^']*'|[^\s;,&]+)"), r"\1\2[hidden]"),
            (re.compile(r"(?i)\b(postgres(?:ql)?|mysql|mariadb|redis|amqp|https?)://([^/\s:@]+):([^@\s]+)@"), r"\1://\2:[hidden]@"),
            (re.compile(r"\b[A-Fa-f0-9]{32,}\b"), "[hidden]"), (re.compile(r"\b[A-Za-z0-9_\-]{40,}\b"), "[hidden]")]


def redact(text):
    """No secrets in anything the page shows (the helper redacts as well; this is the second layer)."""
    for rx, sub in _SECRETS:
        text = rx.sub(sub, text)
    return text


def _control_ok(ctx):
    rb = ctx.rb
    allowed = bool(rb and ((rb.live and rb.db == "godswar") or CONTROL_TEST))
    return allowed and Path(CONTROL_SOCK).exists()


def _connections():
    """Established TCP connections to the login (6999) and game (7000) ports, from /proc/net/tcp{,6}; None when unreadable."""
    n = 0
    seen = False
    for f in ("/proc/net/tcp", "/proc/net/tcp6"):
        try:
            rows = open(f).read().splitlines()[1:]
        except OSError:
            continue
        seen = True
        for r in rows:
            c = r.split()
            if len(c) > 3 and c[3] == "01" and c[1].rsplit(":", 1)[-1] in ("1B57", "1B58"):     # 6999, 7000
                n += 1
    return n if seen else None


def _players_online(ctx):
    """Accounts the game server may still hold, by the signals the editor's own online check uses: a character owned by a
    game session, a login presence token, or login_status <> 0 (login server / character select). Counts people at the login
    screen too, so a restart is never "nobody is online" while someone is signing in."""
    return int(_v(ctx, """(SELECT count(*) FROM accounts a WHERE a.login_status <> 0 OR a.login_presence_token IS NOT NULL
        OR EXISTS (SELECT 1 FROM character_base c WHERE c.account_id = a.id AND c.checkpoint_owner_id IS NOT NULL))"""))


def _who(ctx):
    """(count to confirm against, {accounts, connections}); the larger of the two signals is what the person must acknowledge."""
    accounts = _players_online(ctx)
    conns = _connections()
    return max(accounts, conns or 0), {"accounts": accounts, "connections": conns}


@M.get("server")
def server_settings(ctx):
    return {"realms":_q(ctx,"(SELECT json_agg(json_build_object('id',id,'name',name,'enabled',enabled) ORDER BY id) FROM server)"),
            "writable":bool(ctx.rb and ctx.rb.writable), "status":common.server_status(ctx.app),
            "control_available":_control_ok(ctx), "players_online":_who(ctx)[0], "players_detail":_who(ctx)[1],
            "jobs":_server_jobs()}


@M.get("server/status")
def server_status_view(ctx):
    """Read-only check: the service, the three listeners, readiness, and how many players are in the world."""
    st = common.server_status(ctx.app)
    ports = []
    for name, port in (("Login", 6999), ("Game", 7000), ("Management (local)", st.get("port") or 9090)):
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=2):
                ports.append({"name": name, "port": port, "open": True})
        except OSError:
            ports.append({"name": name, "port": port, "open": False})
    count, detail = _who(ctx)
    return {"status": st, "ports": ports, "players_online": count, "players_detail": detail, "control_available": _control_ok(ctx)}


@M.post("server/name")
def server_name(ctx):
    _need_writable(ctx)
    rid=_int(ctx.body.get("realm"),1,INT_MAX,"realm")
    if not _v(ctx,"EXISTS (SELECT 1 FROM server WHERE id = %d)"%rid):raise NotFound("No realm #%d"%rid)
    name=_text(ctx.body.get("name"),"server name",35,rx=r"^[ -~]+$")
    expected=ctx.body.get("expected")
    if not isinstance(expected,str): raise Bad("expected server name is required")
    return ctx.rows_write([common.scope("server","id = %d"%rid,pk=["id"],counters=[],ignore=["time_zone_revision","time_zone_id","time_zone_updated_at","time_zone_updated_by"])],
        "realm #%d display name: %s"%(rid,name),lambda before:[common.update_sql("server",{"id":rid},{"name":name},bump=False)],
        fields=["server.name"],advanced=True,applies="next_login",target="realm #%d"%rid,
        expected=[{"table":"server","where":{"id":rid},"values":{"name":expected}}])


def _server_jobs():
    root=Path(CONTROL_ROOT)
    try:
        files=sorted(root.glob("*.json"),key=lambda p:p.stat().st_mtime,reverse=True)[:10]
        return [{k:v for k,v in json.loads(p.read_text()).items() if k!="output"} for p in files if re.fullmatch(r"[a-f0-9]{32}\.json",p.name)]
    except (OSError,ValueError):return []


@M.get(r"server/job/([a-f0-9]{32})")
def server_job(ctx, job):
    try:doc=json.loads((Path(CONTROL_ROOT)/(job+".json")).read_text())
    except (OSError,ValueError):raise NotFound("No server-control job with that ID")
    if isinstance(doc.get("output"),str):doc["output"]=redact(doc["output"])
    if isinstance(doc.get("error"),str):doc["error"]=redact(doc["error"])
    return doc


@M.post("server/control")
def server_control(ctx):
    rb=_need_writable(ctx)
    if not _control_ok(ctx):raise Refused("Service controls are available only for the configured live game server")
    action=ctx.body.get("action")
    if action not in CONTROL_ACTIONS:raise Bad("Choose start, stop, restart, backup or logs")
    players,detail=_who(ctx)
    ack=ctx.body.get("acknowledge_players")
    if action in DISRUPTIVE and players>0:
        # The person must have seen the CURRENT number and ticked the box; otherwise nothing is sent.
        if ack is not True or ctx.body.get("players_seen")!=players:
            raise common.Conflict("%d player%s online now. Confirm again with this number to %s anyway; they will be disconnected."%(players,"" if players==1 else "s",action))
    request={"action":action}
    if action=="logs":request["lines"]=_int(ctx.body.get("lines",100),10,300,"log lines")
    job=uuid.uuid4().hex
    request["job"]=job
    if not CONTROL_LOCK.acquire(blocking=False):raise Refused("Another server operation is being sent; wait a moment")
    try:
        with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as connection:
            connection.settimeout(5)
            connection.connect(CONTROL_SOCK)
            connection.sendall((json.dumps(request)+"\n").encode())
            with connection.makefile("rb") as response:result=json.loads(response.readline(4096))
    except (OSError,ValueError) as e:raise Refused("Server control unavailable (%s)"%type(e).__name__)
    finally:CONTROL_LOCK.release()
    if result.get("error"):raise Refused(redact(str(result["error"])))
    actor=getattr(common.ACTOR,"name",None) or "unknown editor session"
    note="by %s; %d player%s online when requested%s"%(actor,players,"" if players==1 else "s",
        "; acknowledged" if action in DISRUPTIVE and players>0 else "")
    change={"engine":"operation","id":"op-"+job,"time":common.now_iso(),"database":rb.db,"action":"game service "+action,
            "character":"godswar251.service","cid":None,"sql":[],"backup":None,"diff":{},"verify":{},"output":"job "+job+" "+note,
            "actor":actor,"actor_note":"the editor session name; not independently verified","players_online":players,"players_detail":detail,
            "undone":False,"no_undo":"Use the opposite service control; player data is saved by the game shutdown path."}
    ctx.app.changes.append(change);ctx.app._save_change(change)
    return {"job":result,"players_online":players,"players_detail":detail,"change":common.public_change(change)}
