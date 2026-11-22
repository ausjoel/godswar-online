"""modules/gm_console.py - "GM commands" page (B22 d51, the admin 2026-11-21: "Also allow gm server commands/window").
Page: static/modules/gm_console.js.

A command window in the editor that takes the game's own GM chat syntax (/announce, /kick, /where ...) and runs it
through what the editor can reach without a GM character in the world:
  * the game server's loopback management port (127.0.0.1:9090 on live; the same port the editor already uses for live
    edits): board post, kick, teleport, the login dry run, rates reload, /readyz;
  * the editor's own guarded database writes (ban / unban = the Accounts page's ban; give = the Inventory page's
    kit-bag add, live when the player is online, else a backed-up database write with Undo).

Why not every chat command: the server runs GM chat commands only from a GM CHARACTER's chat line in the world
(GameClientHandler.OperatorCommands.cs HandleOperatorCommandAsync: it needs the session's account and character, and
most commands act on that GM: /goto, /summon, /god, /speed, /walls, /buff on self, /mall, /bag ...). The management API
has no "run this chat line" endpoint, so those are listed as "in game only" with the reason (UNAVAILABLE below).

Safety:
  * every route sits behind the editor's sign-in (server.py answers 401 before any /api/m/ route) and POSTs need the
    CSRF token, like every other editor write;
  * a read-only editor refuses every command that changes something;
  * kick and ban need confirm: true (the page asks first);
  * the actor sent to the server is "web:<editor user>", so the server's own operator_log names who did it;
  * every command (also refused ones) is appended to gm-console-log.jsonl in the editor's backup folder: time, editor
    user, database, the command line, ok / refused and a short result. GET log shows it; nothing deletes it.
"""
import datetime
import json
import os
import re
import threading
import time

from . import common
from .common import Bad, Refused, NotFound

M = common.module(
    "gm_console", "GM commands",
    icon={"sprite": 696},
    order=97,
    status="ready",
    description="Run the game's GM commands from the editor: announce, who is online, where is a player, kick, ban, "
                "teleport, give an item. Every command is logged with who ran it.",
    needs_character=False,
)

LOG_NAME = "gm-console-log.jsonl"
_LOG_LOCK = threading.Lock()
NAME = re.compile(r"^[^\s@]{1,32}$")
MAX_GIVE = 99                 # the admin's rule: no stack above 99 (the bag add also splits by the item's own stack size)

# word -> (usage, what it does, writes?, needs confirm?)
COMMANDS = {
    "/help": ("/help", "List the commands this window can run.", False, False),
    "/status": ("/status", "Is the game server up, how many players are online, which live operations it offers.", False, False),
    "/online": ("/online", "Everyone online now, with level, class and map.", False, False),
    "/where": ("/where <name>", "Where a character is (map and position) and whether they are online.", False, False),
    "/check": ("/check <name>", "The server's own login dry run for that character's account: would the login work, and "
                                "which items it would hide.", False, False),
    "/announce": ("/announce <text>", "Post one line to every online player's board above the chat box, now.", True, False),
    "/kick": ("/kick <name> [reason]", "Disconnect an online player (the logout save runs as normal).", True, True),
    "/ban": ("/ban <name>", "Ban the character's account (login refused) and disconnect it if online.", True, True),
    "/unban": ("/unban <name>", "Lift the ban on the character's account.", True, False),
    "/move": ("/move <name> <map> <x> <z>", "Teleport an online player to a map and position.", True, False),
    "/give": ("/give <name> <item id> [count]", "Put an item in the player's bag (online: at once; offline: in the "
                                                "database, with Undo). Count 1-99.", True, False),
    "/reload": ("/reload rates", "Ask the running server to re-read the server rates now (it also does so every 5 seconds).", True, False),
}
ALIASES = {"/announcement": "/announce", "/who": "/online", "/players": "/online", "/teleport": "/move", "/commands": "/help"}
# GM chat commands that need a GM character in the world (they act on that GM or open its own windows)
UNAVAILABLE = [
    {"command": "/goto, /summon, /leave, /testrun", "why": "move the GM character itself to a player or into a run"},
    {"command": "/god, /walls, /speed, /power, /armor, /bufflock, /petlock, /powers", "why": "the GM character's own powers"},
    {"command": "/buff (on self), /mall, /bag, /bind, /itemset, /dupe, /craft", "why": "act on the GM's own bag, buffs or shop window"},
    {"command": "/gm, /panel", "why": "open the in-game GM panel"},
    {"command": "/instances", "why": "list the running instances of the GM's session (no management endpoint)"},
    {"command": "/mute, /unmute", "why": "the server keeps mutes in the GM session; there is no management endpoint for them yet"},
]
SEE_ALSO = [
    {"what": "Buffs for many players, event buffs and Clear buffs", "link": "#/buff_tools"},
    {"what": "Switch GM commands off for the whole server, and who is a GM", "link": "#/gm_powers"},
    {"what": "Money, level, items with every field, offline teleport", "link": "#/character"},
    {"what": "Scheduled notices and the board", "link": "#/events"},
]


# ------------------------------------------------------------------------------------------------ the log
def _log_path(app):
    d = getattr(getattr(app, "a", None), "backup_dir", None)
    if not d:
        return None
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, LOG_NAME)


def log_entry(app, command, ok, result):
    rb = getattr(app, "reborn", None)
    rec = {"time": common.now_iso(), "user": getattr(common.ACTOR, "name", None) or "editor",
           "database": getattr(rb, "db", None), "command": str(command)[:300], "ok": bool(ok), "result": str(result)[:500]}
    p = _log_path(app)
    if p:
        with _LOG_LOCK, open(p, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
    return rec


def read_log(app, limit=200):
    p = _log_path(app)
    if not p or not os.path.exists(p):
        return []
    with _LOG_LOCK, open(p, encoding="utf-8") as f:
        lines = f.readlines()[-limit:]
    out = []
    for ln in lines:
        try:
            out.append(json.loads(ln))
        except ValueError:
            continue
    return out[::-1]


# ------------------------------------------------------------------------------------------------ parsing
def parse(line):
    """'/kick Bob afk' -> ('/kick', ['Bob', 'afk'], 'Bob afk'). Pure (the unit test calls it)."""
    if not isinstance(line, str) or not line.strip():
        raise Bad("type a command, for example /online")
    line = line.strip()
    if not line.startswith("/"):
        line = "/" + line
    word, _, rest = line.partition(" ")
    word = ALIASES.get(word.lower(), word.lower())
    rest = rest.strip()
    if word not in COMMANDS:
        known = [u["command"] for u in UNAVAILABLE if word in [w.strip().split(" ")[0] for w in u["command"].split(",")]]
        if known:
            raise Bad("%s works only in game, typed by a GM character: it needs that GM in the world. Use /help for "
                      "what this window can run." % word)
        raise Bad("unknown command %s; /help lists what this window can run" % word)
    return word, rest.split(), rest


def _name(args, usage):
    if not args or not NAME.match(args[0].lstrip("@")):
        raise Bad("usage: %s" % usage)
    return args[0].lstrip("@")


def _num(v, lo, hi, what, cast=int):
    try:
        n = cast(v)
    except (TypeError, ValueError):
        raise Bad("%s must be a number" % what)
    if not lo <= n <= hi:
        raise Bad("%s must be %s-%s" % (what, lo, hi))
    return n


# ------------------------------------------------------------------------------------------------ helpers
def _char(rb, name):
    r = rb.pg.json("""(SELECT row_to_json(q) FROM (SELECT c.id, c.name, c.account_id, a.username, a.status AS account_status,
        c.fighter_job_lv AS level, c.profession, c.camp, c."Map" AS map, c."Pos_X" AS x, c."Pos_Z" AS z,
        (c.checkpoint_owner_id IS NOT NULL OR a.login_presence_token IS NOT NULL) AS in_world, a.login_status
        FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE lower(c.name) = lower(%s)
        ORDER BY (c.lifecycle_state = 'active') DESC NULLS LAST, c.id LIMIT 1) q)""" % common.lit(name))
    if not r:
        raise NotFound("no character called %s" % name)
    return r


def _map_name(mp):
    try:
        n = common.CATALOG.name("map", int(mp))
    except Exception:          # noqa: BLE001
        n = None
    return n or "map %s" % mp


def _online(rb):
    """Characters in the world (a game session owns them) plus accounts that are logged in with nobody in the world yet
    (character select): the same three signals the editor uses everywhere."""
    return rb.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.name), '[]') FROM (
        SELECT c.id, c.name, c.fighter_job_lv AS level, c.profession, c.camp, c."Map" AS map, a.username, a.id AS account_id
        FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.checkpoint_owner_id IS NOT NULL
        UNION ALL
        SELECT NULL, '(' || a.username || ', character select)', NULL, NULL, NULL, NULL, a.username, a.id FROM accounts a
        WHERE (a.login_status <> 0 OR a.login_presence_token IS NOT NULL)
          AND NOT EXISTS (SELECT 1 FROM character_base c WHERE c.account_id = a.id AND c.checkpoint_owner_id IS NOT NULL)) r)""") or []


def _mgmt(ctx):
    port = common.mgmt_port(ctx.app)
    if not port:
        raise Bad("no game server management port is configured for this database")
    return port


def _actor():
    return ("web:" + (getattr(common.ACTOR, "name", None) or "editor"))[:64]


def _op(ctx, name):
    caps = common.capabilities(ctx.app)
    o = (caps.get("operations") or {}).get(name)
    if not o:
        raise Refused("the running game server does not offer this (%s); it needs a newer server release" % name)
    return caps, o


def _kick(ctx, c, reason):
    """POST the kick, retrying while the server answers busy (entering the world, changing maps) or offline although
    the database still shows the character online (the session is still being set up). -> (status, doc)."""
    caps, o = _op(ctx, "character.kick")
    for i in range(6):
        st, doc = common._http("POST", caps["port"], o["path"], {"actor": _actor(), "character": c["name"], "reason": reason}, timeout=8.0)
        err = doc.get("error") if isinstance(doc, dict) else None
        if not (st == 409 and (err == "busy" or (err == "offline" and common.online_info(ctx.app, c["id"])["online"]))):
            break
        time.sleep(1.0 + i)
    return st, doc


# ------------------------------------------------------------------------------------------------ the commands
def run(ctx, line, confirm=False):
    word, args, rest = parse(line)
    usage, _what, writes, needs_confirm = COMMANDS[word]
    rb = ctx.need_rb()
    if writes and not rb.writable:
        raise Refused("this editor is read-only for this database: %s changes the game" % word)
    if needs_confirm and confirm is not True:
        raise Bad("%s needs confirming (the page asks first)" % word)

    if word == "/help":
        return {"lines": ["%s - %s" % (u, w) for u, w, _x, _c in COMMANDS.values()] +
                         ["In game only: %s (%s)" % (u["command"], u["why"]) for u in UNAVAILABLE]}
    if word == "/status":
        st = common.server_status(ctx.app)
        caps = common.capabilities(ctx.app)
        on = _online(rb)
        return {"lines": ["Game server: %s%s" % (st.get("state"), (" (service %s)" % st["service"]) if st.get("service") else ""),
                          "Online now: %d" % len(on),
                          "Live operations: %s" % (", ".join(sorted(caps.get("operations") or {})) or "none")]}
    if word == "/online":
        on = _online(rb)
        if not on:
            return {"lines": ["Nobody is online."], "rows": []}
        return {"lines": ["%d online:" % len(on)] + [r["name"] if r["id"] is None else "%s (Lv %s %s, %s) - %s" % (
            r["name"], r["level"], common.CLASS_NAMES.get(r["profession"], r["profession"]),
            common.CAMP_NAMES.get(r["camp"], r["camp"]), _map_name(r["map"])) for r in on], "rows": on}
    if word == "/where":
        c = _char(rb, _name(args, usage))
        return {"lines": ["%s is %s: %s at %s, %s" % (c["name"], "online" if c["in_world"] else "offline", _map_name(c["map"]),
                                                       int(c["x"] or 0), int(c["z"] or 0))], "character": c}
    if word == "/check":
        c = _char(rb, _name(args, usage))
        st, doc = common._http("POST", _mgmt(ctx), "/admin/character/entry-check",
                               {"actor": _actor(), "accountIds": [int(c["account_id"])]}, timeout=15.0)
        if st != 200:
            raise Bad("the server did not run the dry run (HTTP %s: %s)" % (st, doc))
        lines = ["Login dry run for account %s:" % c["username"]]
        for x in (doc.get("results") if isinstance(doc, dict) else None) or []:
            f = x.get("findings") or []
            lines.append("%s: %s%s" % (x.get("character"), "can log in" if x.get("canEnter") else "LOGIN WOULD FAIL",
                                       (", %d finding(s): %s" % (len(f), "; ".join(str(i.get("message") or i) for i in f[:10]))) if f else ", nothing hidden"))
        return {"lines": lines, "result": doc}
    if word == "/announce":
        from . import events as EV
        text = EV.check_notice_text(rest, "the announcement")
        st, doc = common._http("POST", _mgmt(ctx), "/admin/board/post", {"actor": _actor(), "text": text}, timeout=8.0)
        if st != 200 or not isinstance(doc, dict) or not doc.get("ok"):
            raise Bad("the running game server did not take the post (HTTP %s: %s)" % (st, doc))
        return {"lines": ["Posted to %s online player(s): %s" % (doc.get("delivered", 0), text)]}
    if word == "/kick":
        c = _char(rb, _name(args, usage))
        reason = " ".join(args[1:])[:100] or "disconnected by an admin"
        info = common.online_info(ctx.app, c["id"])
        if not info["online"]:
            return {"lines": ["%s is not online" % c["name"]]}
        st, doc = _kick(ctx, c, reason)
        if st != 200:
            raise Bad("kick refused (HTTP %s): %s" % (st, doc.get("error") if isinstance(doc, dict) else doc))
        return {"lines": ["%s was disconnected (%s)" % (c["name"], reason)]}
    if word in ("/ban", "/unban"):
        c = _char(rb, _name(args, usage))
        sub = common.Ctx(ctx.app, "POST", "accounts/%d/ban" % c["account_id"], {}, {"banned": word == "/ban"},
                         common.REGISTRY.get("accounts"))
        from . import accounts as AC
        r = AC.ban(sub, str(c["account_id"]))
        lines = ["Account %s (%s): %s" % (c["username"], c["name"], r.get("note") or ("banned" if word == "/ban" else "ban lifted"))]
        if word == "/ban" and common.online_info(ctx.app, c["id"])["online"]:
            # the Accounts route kicks once; right after a login or a map change the server may answer busy / offline
            st, doc = _kick(ctx, c, "banned")
            lines.append("%s disconnected" % c["name"] if st == 200 else
                         "%s is still online (kick HTTP %s); the ban stops the next login" % (c["name"], st))
        else:
            lines += ["kicked %s" % k for k in r.get("kicked") or []]
        return {"lines": lines}
    if word == "/move":
        if len(args) != 4:
            raise Bad("usage: %s" % usage)
        c = _char(rb, _name(args, usage))
        mp = _num(args[1], 0, 255, "map")
        x, z = _num(args[2], -100000, 100000, "x", float), _num(args[3], -100000, 100000, "z", float)
        info = common.online_info(ctx.app, c["id"])
        if not info["online"]:
            raise Bad("%s is offline: set the map and position on the character page instead" % c["name"])
        r = ctx.live_call("character.move", c["name"], {"map": mp, "x": x, "z": z}, cid=c["id"])
        if not r.get("ok"):
            raise Bad("%s went offline; nothing moved" % c["name"])
        return {"lines": ["%s moved to %s at %s, %s" % (c["name"], _map_name(mp), x, z)]}
    if word == "/give":
        if len(args) not in (2, 3):
            raise Bad("usage: %s" % usage)
        c = _char(rb, _name(args, usage))
        item = _num(args[1], 1, 2 ** 31 - 1, "item id")
        qty = _num(args[2], 1, MAX_GIVE, "count") if len(args) == 3 else 1
        r = ctx.bag_item(c["id"], "add", {"item": item, "quantity": qty})
        return {"lines": ["%s: %s" % (c["name"], (r.get("change") or {}).get("action") or "item added (%s)" % r.get("mode", "done"))]}
    if word == "/reload":
        if rest.lower() != "rates":
            raise Bad("usage: %s" % usage)
        st, doc = common._http("POST", _mgmt(ctx), "/rates/reload", None, timeout=8.0)
        if st != 200:
            raise Bad("the server did not reload the rates (HTTP %s: %s)" % (st, doc))
        d = doc if isinstance(doc, dict) else {}
        return {"lines": ["Server rates re-read: revision %s%s" % (d.get("revision", "?"), (", error: %s" % d["lastError"]) if d.get("lastError") else "")]}
    raise Bad("unknown command %s" % word)


# ------------------------------------------------------------------------------------------------ routes
@M.get("info")
def info(ctx):
    rb = ctx.app.reborn
    caps = common.capabilities(ctx.app)
    return {"commands": [{"word": w, "usage": u, "what": t, "writes": wr, "confirm": cf} for w, (u, t, wr, cf) in COMMANDS.items()],
            "unavailable": UNAVAILABLE, "see_also": SEE_ALSO, "writable": bool(rb and rb.writable),
            "server": common.server_status(ctx.app), "live": bool(caps.get("available")),
            "operations": sorted(caps.get("operations") or {}), "user": getattr(common.ACTOR, "name", None)}


@M.get("log")
def log(ctx):
    try:
        limit = max(1, min(1000, int(ctx.q("limit") or 200)))
    except ValueError:
        limit = 200
    return {"log": read_log(ctx.app, limit)}


@M.post("run")
def run_route(ctx):
    """POST /api/m/gm_console/run {command: "/kick Bob afk", confirm?: true} -> {lines: [...], entry}."""
    line = ctx.body.get("command")
    try:
        out = run(ctx, line, ctx.body.get("confirm") is True)
    except (Bad, Refused, NotFound, common.Conflict, common.OnlineRefused) as e:
        log_entry(ctx.app, line, False, e)
        raise
    except Exception as e:     # noqa: BLE001 - logged, then the normal error path
        log_entry(ctx.app, line, False, "failed: %s" % e)
        raise
    out["entry"] = log_entry(ctx.app, line, True, " / ".join(out.get("lines") or [])[:500])
    return out
