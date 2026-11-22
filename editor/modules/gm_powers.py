"""modules/gm_powers.py - "GM powers". Page: static/modules/gm_powers.js. Wave 5 package B.

the admin 2026-11-12: "Assess GM abilities and allow turning on and off for the server. Would be good to see what they can
and cant do and modify that."

Two separate questions, two separate places:

  WHO is a GM      character_base."GM" = 1 on a live character of an unbanned account, read again for EVERY command
                   (live runs GODSWAR_CHARACTER_GM_FLAGS=true). This page grants and revokes the flag; it applies at the
                   next command, no relog, no restart. With character flags off, the account allowlist
                   GODSWAR_DEVELOPER_ACCOUNT_IDS decides instead (a systemd drop-in: shown here, not editable here).
  WHAT a GM may do public.gm_power_policy (W5B server build): one row per switched-off power, polled every 5 seconds.
                   No row = allowed, so an empty table is the behaviour before this page existed. 'master' off refuses
                   every GM power. A switched-off command is refused by the SERVER (the GM is told, the attempt goes to
                   operator_log); it is not merely hidden.

The catalogue of powers comes from the running game (GET /admin/capabilities -> gmPowers), so the page lists exactly
what that build enforces. Before the W5B build runs, the page shows the inventory from data/gm_powers_inventory.json
(read-only; a test keeps it equal to the server source) and saves nothing.

Not switchable here, listed so the picture is complete (see ops/GM-POWERS.md): the per-gate GM rule (Gates page), the
rate gm.obey_gates (Server rates), GODSWAR_GM_MALL_FREE, and the loopback /admin endpoints the editor itself uses.
"""
import json
import os
import re
import subprocess

from . import common, world as W
from .common import Bad, Conflict, NotFound, Refused, lit

M = common.module(
    "gm_powers", "GM powers",
    icon={"sprite": 696},
    order=96,
    status="ready",
    description="Who is a GM, what a GM can do, and switches to turn each GM command or power off for the whole server "
                "(applies within 5 seconds, no restart).",
    needs_character=False,
)

TABLE = "gm_power_policy"
LOG = "gm_power_policy_log"
MASTER = "master"
UNIT = "godswar251.service"
SYSTEMCTL = os.environ.get("GWED_SYSTEMCTL", "/usr/bin/systemctl")
INVENTORY = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data", "gm_powers_inventory.json")
KEY = re.compile(r"^[a-z][a-z0-9_.]{0,47}$")
GROUPS = [("master", "Master switch"), ("moderation", "Moderation"), ("travel", "Teleport and instances"),
          ("self", "The GM's own powers"), ("items", "Items"), ("progression", "Levels and progress"),
          ("tools", "Tools"), ("bypass", "Rules a GM skips")]
ENV_KEYS = ("GODSWAR_DEVELOPER_COMMANDS_ENABLED", "GODSWAR_CHARACTER_GM_FLAGS", "GODSWAR_DEVELOPER_ACCOUNT_IDS", "GODSWAR_GM_MALL_FREE")
NOT_HERE = [
    {"what": "Each gate's own GM rule (may a GM skip this transporter, battlefield, instance or event)", "where": "Gates page", "link": "#/gates"},
    {"what": "gm.obey_gates: 1 makes every GM obey level ranges and opening times", "where": "Server rates page", "link": "#/rates"},
    {"what": "GM Mall prices: 1 silver each, or free with GODSWAR_GM_MALL_FREE=1", "where": "systemd drop-in (root, restart)", "link": None},
    {"what": "Whether GM commands exist at all (GODSWAR_DEVELOPER_COMMANDS_ENABLED) and whether the character flag or the "
             "account list decides (GODSWAR_CHARACTER_GM_FLAGS, GODSWAR_DEVELOPER_ACCOUNT_IDS)", "where": "systemd drop-ins (root, restart)", "link": None},
    {"what": "The editor's own live edits of online characters (items, level, money, teleport, kick, buffs, board posts) use "
             "the game's /admin endpoints on 127.0.0.1. They are an editor sign-in power, not a GM-character power, and are "
             "not affected by these switches", "where": "editor sign-in", "link": None},
    {"what": "/sort and /sortwh are player commands (everyone)", "where": "always on", "link": None},
    {"what": "A GM account cannot be banned with /ban; mutes and bans by a GM are written to operator_log", "where": "built in", "link": None},
]


def load_inventory():
    """The editor's read-only copy of the server's catalogue (used only while the running game has no gmPowers)."""
    try:
        with open(INVENTORY, encoding="utf-8") as f:
            return json.load(f)["powers"]
    except (OSError, ValueError, KeyError):
        return []


def _capabilities(ctx):
    """(doc | None, why not) from the running game."""
    port = common.mgmt_port(ctx.app)
    if not port:
        return None, "no game server management port is configured for this database"
    try:
        st, doc = common._http("GET", port, "/admin/capabilities", timeout=3.0)
    except Exception as e:  # noqa: BLE001
        return None, "the game server did not answer (%s)" % e
    if st != 200 or not isinstance(doc, dict):
        return None, "the game server has no /admin/capabilities (HTTP %s)" % st
    return doc, None


def _environment():
    """The GM-related switches the unit is started with (systemctl show; no root)."""
    from . import operations as O
    try:
        p = subprocess.run([SYSTEMCTL, "show", UNIT, "-p", "Environment", "--value"], capture_output=True, text=True, timeout=5)
    except (OSError, subprocess.TimeoutExpired):
        return None
    env = dict(O.parse_environment(p.stdout))
    return {k: env.get(k) for k in ENV_KEYS}


def _truthy(v):
    return str(v or "").strip().lower() in ("1", "true", "yes")


def _table_ready(ctx):
    return bool(W._v(ctx, "(to_regclass('public.%s') IS NOT NULL)" % TABLE))


def _parts():
    return [(TABLE, "true", "power_key")]


def _rows(ctx):
    return W._q(ctx, "(SELECT coalesce(json_object_agg(power_key, json_build_object('allowed', allowed, 'note', note, 'updated_at', updated_at, "
                     "'updated_by', updated_by)), '{}') FROM %s)" % TABLE) or {}


GMS_SQL = """(SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name, 'level', c.fighter_job_lv, 'gm', c."GM",
      'account_id', a.id, 'username', a.username, 'account_status', a.status, 'realm', c.server_id,
      'online', c.checkpoint_owner_id IS NOT NULL,
      'works', (c."GM" = 1 AND c.deleted_at IS NULL AND c.lifecycle_state = 'active' AND a.status = 0),
      'why_not', CASE WHEN c."GM" <> 1 THEN 'the flag is ' || c."GM" || ', the server wants exactly 1'
                      WHEN c.deleted_at IS NOT NULL OR c.lifecycle_state <> 'active' THEN 'the character is deleted'
                      WHEN a.status <> 0 THEN 'the account is blocked (status ' || a.status || ')' END)
      ORDER BY lower(c.name)), '[]')
    FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c."GM" <> 0)"""


def describe(caps, env, rows, table_ready):
    """The page's picture of WHAT a GM may do, from the server's catalogue (or the editor's inventory copy) + the rows."""
    gm = (caps or {}).get("gmPowers") if isinstance(caps, dict) else None
    from_server = isinstance(gm, dict) and isinstance(gm.get("powers"), list)
    powers = []
    for p in (gm["powers"] if from_server else load_inventory()):
        key = p.get("key")
        if not isinstance(key, str) or not KEY.match(key):
            continue
        row = (rows or {}).get(key)
        allowed = row["allowed"] if row else True
        powers.append({"key": key, "group": p.get("group") or "tools", "label": p.get("label") or key, "description": p.get("description") or "",
                       "risk": p.get("risk") or "normal", "allowed": bool(allowed),
                       "server_allowed": p.get("allowed") if from_server else None,      # what the running game applies now
                       "updated_at": (row or {}).get("updated_at"), "updated_by": (row or {}).get("updated_by")})
    master = next((p["allowed"] for p in powers if p["key"] == MASTER), True)
    commands_on = (caps or {}).get("gmCommandsEnabled") if from_server else (_truthy(env.get(ENV_KEYS[0])) if env else None)
    mode = (caps or {}).get("gmAuthority") or ("character_flag" if env and _truthy(env.get(ENV_KEYS[1])) else "account_allowlist" if env else None)
    pending = [p["key"] for p in powers if from_server and p["server_allowed"] is not None and p["server_allowed"] != p["allowed"]]
    why = None
    if not from_server:
        why = ("The running game build has no GM power switches yet (they arrive with the wave 5 server build). The list below "
               "is the inventory of what a GM can do; nothing can be switched until that build runs.")
    elif not table_ready:
        why = "The game has not created the table gm_power_policy yet; restart it once on the new build."
    return {"ready": bool(from_server and table_ready), "why_not_ready": why, "powers": powers, "master": bool(master),
            "groups": [{"key": k, "title": t} for k, t in GROUPS], "from_server": from_server,
            "commands_enabled": commands_on, "authority": mode, "pending": pending,
            "source": (gm or {}).get("source"), "loaded_at": (gm or {}).get("loadedAtUtc")}


@M.get("overview")
def overview(ctx):
    caps, why = _capabilities(ctx)
    env = _environment()
    table_ready = _table_ready(ctx)
    rows = _rows(ctx) if table_ready else {}
    d = describe(caps, env or {}, rows, table_ready)
    if caps is None and d["why_not_ready"]:
        d["why_not_ready"] = "%s: %s" % (why, d["why_not_ready"]) if not d["from_server"] else d["why_not_ready"]
    d["version"] = W._version(ctx, _parts()) if table_ready else None
    d["environment"] = env
    d["gms"] = W._q(ctx, GMS_SQL)
    ids = sorted({int(x) for x in re.findall(r"\d+", (env or {}).get(ENV_KEYS[2]) or "")})
    d["allowlist"] = (W._q(ctx, "(SELECT coalesce(json_agg(json_build_object('id', id, 'username', username, 'status', status) ORDER BY id), '[]') "
                                "FROM accounts WHERE id IN (%s))" % ", ".join(str(i) for i in ids)) if ids else [])
    d["who_rule"] = {
        "character_flag": "A GM is a character with the GM flag (below). It is checked again for every command, so a grant or a "
                          "revoke applies at once. The account list is not used.",
        "account_allowlist": "A GM is any character of an account on the allow list (a systemd drop-in). The GM flag below does "
                             "nothing while the server runs this way.",
    }.get(d["authority"], "The game server did not say how it decides who is a GM.")
    d["not_here"] = NOT_HERE
    d["writable"] = bool(ctx.rb and ctx.rb.writable)
    d["applies_text"] = "applies within 5 seconds, no restart; refused commands are refused by the game server and logged"
    return d


@M.post("save")
def save(ctx):
    """POST {changes: {power_key: true|false}, version}. true removes the row (= allowed, the default), false writes a
    row with allowed = false. Only keys of the running server's catalogue are accepted."""
    W._need_writable(ctx)
    b = ctx.body or {}
    caps, why = _capabilities(ctx)
    if caps is None:
        raise Refused("Not saved: %s" % why)
    if not _table_ready(ctx):
        raise Refused("Not saved: the table gm_power_policy does not exist yet (the wave 5 game build creates it at start-up)")
    d = describe(caps, {}, _rows(ctx), True)
    if not d["from_server"]:
        raise Refused("Not saved: the running game build does not check GM power switches yet, so a switch here would do "
                      "nothing. It arrives with the wave 5 server build")
    known = {p["key"]: p for p in d["powers"]}
    changes = b.get("changes")
    if not isinstance(changes, dict) or not changes:
        raise Bad("Nothing changed")
    off, on = [], []
    for key, allowed in changes.items():
        if key not in known:
            raise Bad("Unknown GM power %r" % key)
        if not isinstance(allowed, bool):
            raise Bad("%s must be on or off" % key)
        if known[key]["allowed"] != allowed:
            (on if allowed else off).append(key)
    if not off and not on:
        raise Bad("Nothing changed")
    stmts = [W._ver_guard(_parts(), b.get("version"), "the GM power switches")]
    for key in sorted(off):
        stmts.append(W._upsert(TABLE, ["power_key"], {"power_key": key, "allowed": False}, touch=False))
    for key in sorted(on):
        stmts.append("DELETE FROM %s WHERE power_key = %s" % (TABLE, lit(key)))
    what = []
    if off:
        what.append("OFF " + ", ".join(sorted(off)))
    if on:
        what.append("ON " + ", ".join(sorted(on)))
    scope = common.scope(TABLE, "true", pk=["power_key"], counters=[], ignore=["updated_at", "updated_by"])
    out = W._write(ctx, [scope], "GM powers: " + "; ".join(what), stmts, ["%s.*" % TABLE], applies="hot_reload", target="GM powers")
    out["off"], out["on"] = sorted(off), sorted(on)
    return out


def _character_columns():
    g, _ = common.GLOSSARY._load()
    return list(((g.get("character_base") or {}).get("columns") or {}).keys())


@M.get("find")
def find(ctx):
    """GET ?q=<start of a character name>: up to 20 live characters, to grant the flag."""
    q = str((ctx.query or {}).get("q") or "").strip()
    if not 1 <= len(q) <= 32:
        raise Bad("Type the start of a character name")
    like = q.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
    return {"characters": W._q(ctx, """(SELECT coalesce(json_agg(x), '[]') FROM (SELECT c.id, c.name, c.fighter_job_lv AS level, c."GM" AS gm,
        a.id AS account_id, a.username, a.status AS account_status, c.checkpoint_owner_id IS NOT NULL AS online
        FROM character_base c JOIN accounts a ON a.id = c.account_id
        WHERE lower(c.name) LIKE lower(%s) AND c.deleted_at IS NULL AND c.lifecycle_state = 'active' ORDER BY lower(c.name) LIMIT 20) x)""" % lit(like))}


@M.post("grant")
def grant(ctx):
    """POST {character_id, gm: true|false, expected: <flag as shown>}: set or clear the GM flag of ONE character.
    Applies at that character's next GM command (the server reads the flag every time); the player may stay online.
    Only the GM column is written, checked against the value the page showed, and only that column is put back by Undo."""
    W._need_writable(ctx)
    b = ctx.body or {}
    cid = W._int(b.get("character_id"), 1, 2147483647, "character")
    want = b.get("gm")
    if not isinstance(want, bool):
        raise Bad("gm must be true or false")
    row = W._q(ctx, """(SELECT json_build_object('name', c.name, 'gm', c."GM", 'deleted', (c.deleted_at IS NOT NULL OR c.lifecycle_state <> 'active'),
        'account_id', a.id, 'username', a.username, 'account_status', a.status)
        FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.id = %d)""" % cid)
    if not row:
        raise NotFound("No character #%d" % cid)
    if b.get("expected") != row["gm"]:
        raise Conflict("The GM flag of %s changed since the page read it (it is %s now); look again" % (row["name"], row["gm"]))
    new = 1 if want else 0
    if row["gm"] == new:
        raise Bad("%s %s a GM already" % (row["name"], "is" if want else "is not"))
    if want and row["deleted"]:
        raise Refused("%s is a deleted character; the flag would do nothing" % row["name"])
    if want and row["account_status"] != 0:
        raise Refused("The account %s is blocked (status %s); unblock it first, a GM flag on it would do nothing" % (row["username"], row["account_status"]))
    cols = _character_columns()
    scope = common.scope("character_base", "id = %d" % cid, pk=["id"], counters=[], ignore=[c for c in cols if c not in ("id", "GM")])
    action = "%s GM rights: %s (account %s)" % ("grant" if want else "revoke", row["name"], row["username"])
    out = ctx.rows_write([scope], action, lambda before: [common.update_sql("character_base", {"id": cid}, {"GM": new}, bump=False)],
                         fields=["character_base.GM"], advanced=True, applies="now", target=row["name"],
                         expected=[{"table": "character_base", "where": {"id": cid}, "values": {"GM": row["gm"]}}])
    out["character"] = {"id": cid, "name": row["name"], "gm": new}
    return out


@M.get("log")
def log(ctx):
    """The last switch changes (database trigger log) and the last commands the game refused because of a switch."""
    out = {"changes": [], "refused": [], "notes": []}
    try:
        out["changes"] = W._q(ctx, "(SELECT coalesce(json_agg(x), '[]') FROM (SELECT at, power_key, operation, old_allowed, new_allowed, changed_by "
                                   "FROM %s ORDER BY id DESC LIMIT 50) x)" % LOG)
    except Exception as e:  # noqa: BLE001 - the table arrives with the server build; the grant may be missing
        out["notes"].append("switch log not readable: %s" % str(e)[:160])
    try:
        out["refused"] = W._q(ctx, "(SELECT coalesce(json_agg(x), '[]') FROM (SELECT at, actor_character, command, detail FROM operator_log "
                                   "WHERE detail LIKE 'refused:%' ORDER BY at DESC LIMIT 50) x)")
    except Exception as e:  # noqa: BLE001
        out["notes"].append("operator_log not readable: %s" % str(e)[:160])
    return out
