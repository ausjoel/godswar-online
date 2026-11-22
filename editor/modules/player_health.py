"""modules/player_health.py - "Player health". Page: static/modules/player_health.js. Wave 6 package A.

The owner only notices problems on his own machine. This page answers, for every player:
  * can this character get into the world right now?        (Login check; works before the W6A server is live)
  * did their recent logins work, and if not, what went wrong? (login records; start with the W6A server build)

Login check   modules/_login_check.py: the game server's dry run of a login (POST /admin/character/entry-check)
              merged with the bad rows the editor finds in the database itself. Each finding the editor can repair
              has a Fix button; the fix is an ordinary audited item removal (bag: ctx.bag_item, live when the
              character is online; elsewhere ctx.char_write with reborn.op_item), so it is in the change log with Undo.
Login records public.session_health (server migration 20261113_242): one row per connection, 30 days.
              modules/_session_health.py pairs the login-port and game-port rows into attempts and explains each.
              No full addresses are stored (a.b.c.0 only), no passwords, no chat, no packet contents.
"""
import datetime

from . import common, world as W, _login_check as LC, _session_health as SH
from .common import Bad, Conflict, NotFound

M = common.module(
    "player_health", "Player health",
    icon={"sprite": 1063},
    order=94,
    status="ready",
    description="For every player: whether their character can log in right now, how their recent logins went, and what "
                "went wrong in plain words, with a fix where the editor can make one.",
    needs_character=False,
)

TABLE = "session_health"
MAX_ROWS = 20000
TIMELINE = 30


def _q(ctx, sql):
    return W._q(ctx, sql)


def _records_state(ctx):
    """'ready', 'missing' (older game server) or 'no_grant' (the editor's database role may not read the table)."""
    if not W._v(ctx, "(to_regclass('public.%s') IS NOT NULL)" % TABLE):
        return "missing"
    return "ready" if W._v(ctx, "has_table_privilege(current_user, 'public.%s', 'SELECT')" % TABLE) else "no_grant"


RECORDS_NOTES = {
    "ready": "",
    "missing": "Login records start with the next game server update. The Login check works now.",
    "no_grant": "The game server is recording logins, but the editor has not been given the right to read them yet "
                "(run the editor grants that came with this update). The Login check works now.",
}


CHARACTERS_SQL = """(SELECT coalesce(json_agg(json_build_object('id', c.id, 'account_id', a.id, 'name', c.name,
      'username', a.username, 'level', c.fighter_job_lv, 'account_status', a.status,
      'online', c.checkpoint_owner_id IS NOT NULL, 'last_login', a.last_login_time) ORDER BY lower(c.name)), '[]')
    FROM character_base c JOIN accounts a ON a.id = c.account_id
    WHERE c.deleted_at IS NULL AND c.lifecycle_state = 'active' %s)"""


def _characters(ctx, where=""):
    return _q(ctx, CHARACTERS_SQL % where) or []


def _now():
    return datetime.datetime.now(datetime.timezone.utc)


def run_check(ctx, characters):
    """The login check for these characters: [{cid, character, can_enter, light, findings[...]}], red first."""
    if not characters:
        return [], {"server": False, "why": ""}
    caps = ctx.caps
    offered = LC.server_offers_check(caps)
    server = LC.server_results(common._http, caps.get("port"), [c["account_id"] for c in characters],
                               actor=getattr(common.ACTOR, "name", None) or "editor") if offered else None
    cids = [int(c["id"]) for c in characters]
    db = LC.database_findings(lambda sql: _q(ctx, sql), cids, self_healing=offered)
    rows = []
    if server and any(r.get("findings") for r in server.values()):
        rows = _q(ctx, "(SELECT coalesce(json_agg(json_build_object('row', id, 'cid', user_id, 'location', item_location, 'slot', "
                       "slot_index, 'item', prop_id)), '[]') FROM character_items WHERE user_id = ANY(ARRAY[%s]::int[]) "
                       "AND item_location IN (0, 1))" % ",".join(map(str, cids))) or []
    item_rows = {(int(r["cid"]), int(r["location"]), int(r["slot"])): int(r["row"]) for r in rows}
    names = {}
    for result in (server or {}).values():
        for f in result.get("findings") or []:
            if f.get("itemId"):
                names[int(f["itemId"])] = ctx.catalog.name("item", int(f["itemId"])) or "item %d" % int(f["itemId"])
    why = "" if server is not None else (
        "The running game build cannot do the full dry run yet (it arrives with the next server update). Until then "
        "this check finds the bad rows the editor knows about." if not offered else
        "The game server did not answer, so only the editor's own database check ran.")
    return LC.merge(characters, server, db, item_rows, names), {"server": server is not None, "why": why}


@M.get("overview")
def overview(ctx):
    """Everything the page shows. ?days=1..30 (default 7)."""
    ctx.need_rb()
    days = ctx.qint("days", 7, 1, 30)
    now = _now()
    characters = _characters(ctx)
    by_account = {}
    for c in characters:
        by_account.setdefault(int(c["account_id"]), []).append(c)
    state = _records_state(ctx)
    ready = state == "ready"
    rows = []
    if ready:
        rows = _q(ctx, "(SELECT coalesce(json_agg(to_jsonb(s) ORDER BY s.id), '[]') FROM (SELECT * FROM %s WHERE started_at > now() - "
                       "make_interval(days => %d) ORDER BY id DESC LIMIT %d) s)" % (TABLE, days, MAX_ROWS)) or []
    checks, check_info = run_check(ctx, characters)
    check_by_cid = {r["cid"]: r for r in checks}

    per_account, unknown = {}, []
    for r in rows:
        if r.get("account_id") is None:
            unknown.append(r)
        else:
            per_account.setdefault(int(r["account_id"]), []).append(r)
    attempts_by_account, everything = {}, []
    for account_id, account_rows in per_account.items():
        attempts = SH.pair(account_rows)
        for a in attempts:
            a["explained"] = SH.explain(a, now)
            a["account_id"] = account_id
        attempts_by_account[account_id] = attempts
        everything += attempts
    stats = SH.summary(attempts_by_account, now)
    stats["unknown_name_attempts"] = len(unknown)
    hint = SH.what_changed(everything, SH.markers(rows))

    builds = {}
    players = []
    for account_id in sorted(set(by_account) | set(attempts_by_account)):
        chars = by_account.get(account_id) or []
        attempts = attempts_by_account.get(account_id) or []
        findings = [f for c in chars for f in (check_by_cid.get(int(c["id"])) or {}).get("findings", [])]
        explained = [a["explained"] for a in attempts]
        light = SH.status(explained, findings)
        build = next((a["login"]["client_build"] for a in reversed(attempts) if a["login"] and a["login"].get("client_build") is not None), None)
        if build is not None:
            builds[int(build)] = builds.get(int(build), 0) + 1
        last = attempts[-1] if attempts else None
        blocker = next((f for f in findings if f["severity"] == "blocks"), None) or next(iter(findings), None)
        headline = (blocker["title"] if blocker and light != "green" and (blocker["severity"] == "blocks" or not last or last["explained"]["level"] in ("ok", "info"))
                    else last["explained"]["title"] if last else "No logins in the last %d days" % days)
        players.append({
            "account_id": account_id,
            "username": chars[0]["username"] if chars else None,
            "characters": [{"id": c["id"], "name": c["name"], "level": c["level"], "online": c["online"]} for c in chars],
            "light": light, "headline": headline,
            "fix": (blocker["fix"]["label"] if blocker and blocker.get("fix") and headline == blocker["title"] else
                    last["explained"]["fix"] if last and headline == last["explained"]["title"] else ""),
            "last": SH.local_text(last["at"]) if last else None,
            "last_day": SH.local_day(last["at"]) if last else None,
            "build": int(build) if build is not None else None, "build_text": SH.build_text(build),
            "problems": sum(1 for e in explained if e["level"] in ("warn", "bad")),
            "logins": len(attempts),
            "days": sorted({SH.local_day(a["at"]) for a in attempts}),
            "timeline": [_attempt_view(a) for a in attempts[-TIMELINE:]][::-1],
        })
    newest = max(builds) if builds else None
    for p in players:
        p["build_behind"] = bool(newest is not None and p["build"] is not None and p["build"] < newest)
    order = {"red": 0, "amber": 1, "green": 2, "grey": 3}
    players.sort(key=lambda p: (order[p["light"]], (p["characters"][0]["name"] if p["characters"] else p["username"] or "").lower()))
    return {
        "records_ready": ready,
        "records_note": RECORDS_NOTES[state],
        "days": days, "now": SH.local_text(now), "stats": stats, "what_changed": hint,
        "players": players,
        "counts": {k: sum(1 for p in players if p["light"] == k) for k in order},
        "builds": [{"build": b, "text": SH.build_text(b), "accounts": n, "newest": b == newest} for b, n in sorted(builds.items(), reverse=True)],
        "build_note": "Every client release so far sends the same version number, so this column cannot tell our "
                      "releases apart yet.",
        "check": {"results": checks, "server": check_info["server"], "why": check_info["why"],
                  "blocked": sum(1 for r in checks if r["light"] == "red"), "hidden": sum(1 for r in checks if r["light"] == "amber"),
                  "checked": len(checks)},
        "writable": bool(getattr(ctx.need_rb(), "writable", False)),
    }


def _attempt_view(a):
    login, game, e = a["login"], a["game"], a["explained"]
    stages = dict((game or {}).get("stages") or {})
    reached = ("in the world" if "ui_ready" in stages else "entering the world" if "enter_requested" in stages else
               "character screen" if "character_screen" in stages else "game server" if game else
               "server list" if login and login.get("outcome") == "accepted" else "password check")
    return {"at": SH.local_text(a["at"]), "level": e["level"], "title": e["title"], "detail": e["detail"], "fix": e["fix"],
            "reached": reached, "duration": SH.duration_text((game or login)["duration_ms"]),
            "network": (game or login).get("network") or "",
            "build": SH.build_text(login.get("client_build")) if login else "",
            "character_id": (game or {}).get("character_id")}


@M.post("check")
def check(ctx):
    """Run the login check now. Body: {} = every character, or {"character_id": n}."""
    ctx.need_rb()
    cid = (ctx.body or {}).get("character_id")
    where = ""
    if cid is not None:
        where = "AND c.id = %d" % common.to_int(cid, 1, 2 ** 31 - 1, "character")
    characters = _characters(ctx, where)
    if cid is not None and not characters:
        raise NotFound("no such character")
    results, info = run_check(ctx, characters)
    return {"results": results, "server": info["server"], "why": info["why"], "checked": len(results),
            "blocked": sum(1 for r in results if r["light"] == "red"), "hidden": sum(1 for r in results if r["light"] == "amber")}


@M.get(r"character/(\d+)")
def character(ctx, cid):
    """The login check for one character (the character page shows it)."""
    ctx.need_rb()
    characters = _characters(ctx, "AND c.id = %d" % int(cid))
    if not characters:
        raise NotFound("no such character")
    results, info = run_check(ctx, characters)
    return dict(results[0], server=info["server"], why=info["why"])


@M.post("fix")
def fix(ctx):
    """Repair one finding. Body: {"character_id", "fix": {kind: "remove_item", row, location, slot, item}}.
    Only removes an item the login check still reports, at the place and with the id the page showed."""
    rb = ctx.need_rb()
    body = ctx.body or {}
    cid = common.to_int(body.get("character_id"), 1, 2 ** 31 - 1, "character")
    want = body.get("fix") if isinstance(body.get("fix"), dict) else {}
    if want.get("kind") != "remove_item":
        raise Bad("this finding has no automatic fix")
    characters = _characters(ctx, "AND c.id = %d" % cid)
    if not characters:
        raise NotFound("no such character")
    results, _ = run_check(ctx, characters)
    current = next((f["fix"] for f in results[0]["findings"] if f.get("fix") and f["fix"]["row"] == want.get("row")), None)
    if current is None or any(current[k] != want.get(k) for k in ("location", "slot", "item")):
        raise Conflict("the login check no longer reports that item there (it was fixed or moved); the page will reload")
    name = ctx.catalog.name("item", current["item"]) or "item %d" % current["item"]
    if current["location"] == common.R.LOC_BAG:
        result = ctx.bag_item(cid, "remove", {"slot": current["slot"]})
    else:
        op = common.R.op_item(rb, cid, {"location": current["location"], "slot": current["slot"], "item": None})
        result = ctx.char_write(cid, "remove %s (#%d) that stops the login" % (name, current["item"]), op, fields=["character_items.*"],
                                cross_bumps=False)
        result["mode"] = "database"
    after, _ = run_check(ctx, characters)
    result["check"] = after[0]
    result["removed"] = {"name": name, "where": LC.where_text(current["location"], current["slot"])}
    return result
