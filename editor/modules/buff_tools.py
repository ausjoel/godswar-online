"""modules/buff_tools.py - B21 item 22 (the admin 2026-11-21): buffs for many players. Page: static/modules/buff_tools.js.

  * a buff picker sorted by effect: the same effect at different strengths (Bleeding 1-10, [Guild] Double EXP 1-10 ...)
    is ONE entry with a level picker;
  * apply one buff to everyone online, every character, or the characters ticked in a list of names;
  * for a set time (duration) OR until a fixed end time (an event buff);
  * remove buffs from the ticked characters, and clear buffs server-wide.

How the game keeps buffs (checked in the server source, 2026-11-21):
  * A buff lives in the game session only (ActiveRuntimeStatus with ExpiresAt = now + duration). Logging out ends it;
    nothing is stored. The server honours the duration exactly (GameSessionRegistry.PlayerStatusMutations).
  * pending_character_buffs (W4-B) queues a buff for the next world entry; its duration starts then.
  * Applying a status of a kind the character already has replaces it, unless the one it has is stronger (priority);
    no chat line or window is sent to the player.
So:
  * "for N seconds": online characters get it live now (each one through ctx.live_call: change log, no Undo because it
    expires); offline characters get a queued row (applied at their next login, the N seconds start then).
  * "until <time>": online characters get it live with the seconds left; an EVENT row (sqlite in the editor's backup
    folder) makes the editor's event job give it, with the seconds left, to every target that enters the world before
    that time (each new game session once). It ends exactly at that time for everyone, also after a relog. The job runs
    inside the editor (started with it, every 30 s); while the editor is stopped nobody new gets the buff.
  * Clear server-wide: every online character's removable buffs are removed (inspect -> remove with the server's token;
    mounts and permanent effects stay: the server owns them), every queued buff is cancelled and every running event
    is stopped (or it would give the buff straight back).
"""
import datetime
import json
import os
import sqlite3
import threading
import time
from contextlib import contextmanager

from . import common, buffs as B, login_awards as LA
from .common import Bad, NotFound

M = common.module(
    "buff_tools", "Buffs for many",
    icon={"sprite": 17},
    order=46,
    status="ready",
    description="Give or remove buffs for everyone online, every character or a list of names; for a time or until a "
                "fixed end (event buffs); clear buffs server-wide.",
    needs_character=False,
)

MAX_TARGETS = 500
JOB_SECONDS = 30
MAX_EVENT_DAYS = 60
BRISBANE = LA.BRISBANE


# =========================================================================== the buff picker, grouped by effect
def group_library(entries):
    """Merge same-effect entries at different strengths into one group with levels. Pure (unit-tested).
    Key: the family name + the set of effect codes + the status kind. Sorted by category, then effect, then name."""
    groups = {}
    for e in entries:
        codes = tuple(sorted({int(x.get("code")) for x in e.get("effects") or [] if x.get("code") is not None}))
        key = ((e.get("family_name") or e.get("name") or "Status #%s" % e["id"]), codes, e.get("kind"))
        groups.setdefault(key, []).append(e)
    out = []
    for (family, codes, kind), rows in groups.items():
        rows.sort(key=lambda r: (r.get("priority") or 0, sum(abs(float(x.get("value") or 0)) for x in r.get("effects") or []), r["id"]))
        effect = "; ".join(sorted({(x.get("label") or "").split(" (")[0] for x in rows[0].get("effects") or [] if x.get("label")})) \
            or (rows[0].get("note") or "")
        levels = [{"level": i + 1, "status_id": r["id"], "label": r.get("label") or r.get("name"),
                   "effects": [x.get("label") for x in r.get("effects") or []], "duration_s": r.get("duration_s"),
                   "applicable": r.get("apply_status_id") is not None, "icon": r.get("icon"), "note": r.get("note") or ""}
                  for i, r in enumerate(rows)]
        out.append({"key": "%s|%s|%s" % (family, ",".join(map(str, codes)), kind), "name": family,
                    "category": rows[0].get("category") or "Other", "effect": effect, "beneficial": bool(rows[0].get("beneficial", True)),
                    "icon": rows[0].get("icon"), "levels": levels, "applicable": sum(1 for x in levels if x["applicable"])})
    out.sort(key=lambda g: (g["category"], g["effect"].lower(), g["name"].lower()))
    return out


@M.get("library")
def library(ctx):
    lib = B.library(ctx)
    groups = group_library(lib["entries"])
    return {"groups": groups, "categories": sorted({g["category"] for g in groups}), "server_supported": lib["server_supported"],
            "entries": len(lib["entries"]), "applicable": lib["applicable"]}


# =========================================================================== characters
def characters(ctx, ids=None):
    """Active characters with whether each is in the world now (account presence token or a session owner)."""
    where = "c.lifecycle_state = 'active'"
    if ids is not None:
        if not ids:
            return []
        where += " AND c.id = ANY(ARRAY[%s]::int[])" % ",".join(str(int(i)) for i in ids)
    return ctx.need_rb().pg.json("""(SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name, 'level', c.fighter_job_lv,
        'camp', c.camp, 'profession', c.profession, 'online', (a.login_presence_token IS NOT NULL OR c.checkpoint_owner_id IS NOT NULL),
        'session', coalesce(a.login_presence_token::text, c.checkpoint_owner_id::text, '')) ORDER BY lower(c.name)), '[]')
        FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE %s)""" % where) or []


@M.get("characters")
def characters_view(ctx):
    rows = characters(ctx)
    for r in rows:
        r["class"] = common.CLASS_NAMES.get(r["profession"], str(r["profession"]))
        r.pop("session", None)
    return {"characters": rows}


def _targets(ctx, b):
    """(rows, label) for {target: online|all|selected, ids, camp, min_level, max_level}."""
    t = b.get("target")
    if t not in ("online", "all", "selected"):
        raise Bad("choose who: everyone online, every character or the ticked characters")
    if t == "selected":
        ids = b.get("ids")
        if not isinstance(ids, list) or not 1 <= len(ids) <= MAX_TARGETS:
            raise Bad("tick 1-%d characters" % MAX_TARGETS)
        rows = characters(ctx, sorted({common.to_int(i, 1, 2 ** 31 - 1, "character") for i in ids}))
        if len(rows) != len({int(i) for i in ids}):
            raise NotFound("a ticked character no longer exists")
        return rows, "%d ticked character(s)" % len(rows)
    rows = characters(ctx)
    camp = b.get("camp")
    lo = LA._int(b.get("min_level"), 1, 200, "minimum level", 1)
    hi = LA._int(b.get("max_level"), 1, 200, "maximum level", 200)
    if camp not in (None, "", "all"):
        camp = common.to_int(camp, 0, 1, "faction")
        rows = [r for r in rows if r["camp"] == camp]
    rows = [r for r in rows if lo <= int(r["level"]) <= hi]
    if t == "online":
        rows = [r for r in rows if r["online"]]
    label = ("everyone online" if t == "online" else "every character") + \
        ("" if camp in (None, "", "all") else " of " + common.CAMP_NAMES.get(camp, "?")) + \
        ("" if (lo, hi) == (1, 200) else " level %d-%d" % (lo, hi))
    return rows, label


# =========================================================================== apply
def _buff(ctx, status_id):
    cat = B.runtime_catalogue(ctx)
    if not cat["supported"]:
        raise Bad(cat["reason"])
    sid = common.to_int(status_id, 1, 2 ** 31 - 1, "buff")
    row = next((r for r in cat["buffs"] if r.get("statusId") == sid), None)
    if row is None:
        raise Bad("the running game server cannot apply status %d" % sid)
    if row.get("activation") in ("mount", "periodic-damage"):
        raise Bad("%s cannot be given to many players (%s)" % (row["name"], "it needs each player's own mount"
                  if row["activation"] == "mount" else "it is damage over time"))
    return row


def _live_apply(ctx, cid, name, status_id, seconds):
    """One live apply: {'result': applied|refused|offline|error, 'note'}."""
    try:
        r = ctx.live_call("character.buff_apply", name, {"statusId": int(status_id), "durationSeconds": int(seconds)}, cid=cid,
                          no_undo="A live buff ends by itself (or at logout); remove it on the Buffs for many page.")
    except (Bad, common.Conflict) as e:
        return {"result": "error", "note": str(e)}
    if r.get("offline"):
        return {"result": "offline", "note": "logged out meanwhile"}
    doc = r.get("result") or {}
    if isinstance(doc, dict) and doc.get("applied") is False:
        return {"result": "refused", "note": doc.get("reason") or doc.get("error") or "not applied"}
    return {"result": "applied", "note": ""}


def _end_time(b):
    if b.get("until") in (None, ""):
        return None
    end = LA.brisbane_to_utc(b["until"], "End time")
    now = datetime.datetime.now(datetime.timezone.utc)
    if end <= now + datetime.timedelta(seconds=30):
        raise Bad("the end time must be at least a minute from now (Brisbane time)")
    if end > now + datetime.timedelta(days=MAX_EVENT_DAYS):
        raise Bad("an event buff can run for at most %d days" % MAX_EVENT_DAYS)
    return end


@M.post("apply")
def apply(ctx):
    """POST {status_id, target: online|all|selected, ids?, camp?, min_level?, max_level?,
             duration_seconds? | until: 'YYYY-MM-DD HH:MM' (Brisbane)}"""
    rb = ctx.need_rb()
    if not rb.writable:
        raise Bad("this editor connection is read-only")
    b = ctx.body
    buff = _buff(ctx, b.get("status_id"))
    end = _end_time(b)
    seconds = None
    if end is None:
        seconds = common.to_int(b.get("duration_seconds") if b.get("duration_seconds") not in (None, "") else buff["durationSeconds"],
                                1, 2 ** 32 - 1, "duration in seconds")
    rows, label = _targets(ctx, b)
    if not rows:
        raise Bad("nobody matches (%s)" % label)
    out = {"buff": buff["name"], "status_id": buff["statusId"], "targets": label, "live": [], "queued": 0, "event": None}
    now = time.time()
    for r in rows:
        if not r["online"]:
            continue
        left = seconds if end is None else int(end.timestamp() - now)
        res = _live_apply(ctx, r["id"], r["name"], buff["statusId"], left)
        out["live"].append(dict(res, id=r["id"], name=r["name"]))
    if end is None:
        offline = [r["id"] for r in rows if not r["online"]] if b.get("target") != "online" else []
        for i in range(0, len(offline), MAX_TARGETS):
            LA.queue_buff(ctx, offline[i:i + MAX_TARGETS], buff["statusId"], seconds)
            out["queued"] += len(offline[i:i + MAX_TARGETS])
    else:
        if b.get("target") == "online":
            ids = [r["id"] for r in rows]          # "online now": the event follows these characters through a relog
            scope = {"kind": "ids", "ids": ids}
        elif b.get("target") == "selected":
            scope = {"kind": "ids", "ids": [r["id"] for r in rows]}
        else:
            scope = {"kind": "all", "camp": b.get("camp") if b.get("camp") not in (None, "", "all") else None,
                     "min_level": LA._int(b.get("min_level"), 1, 200, "minimum level", 1),
                     "max_level": LA._int(b.get("max_level"), 1, 200, "maximum level", 200)}
        ev = STORE(ctx.app).add(buff["statusId"], buff["name"], end.timestamp(), scope, LA._actor())
        for x in out["live"]:
            if x["result"] == "applied":
                sess = next((r["session"] for r in rows if r["id"] == x["id"]), "")
                STORE(ctx.app).mark(ev, x["id"], sess, "applied")
        out["event"] = {"id": ev, "ends": LA.utc_to_brisbane(end)}
        JOB.ensure(ctx.app)
    out["applied"] = sum(1 for x in out["live"] if x["result"] == "applied")
    out.update(mode="live", applies="live", applies_text=common.APPLIES_TEXT["live"])
    return out


# =========================================================================== remove / clear
def _inspect(ctx, name):
    caps = ctx.caps
    if "character.buff_inspect" not in (caps.get("operations") or {}):
        raise Bad("the running game server cannot list a player's buffs (it needs the live buff tools update)")
    code, doc = common._http("POST", caps["port"], "/admin/buff/inspect", {"actor": "gwadmin-web", "character": name}, timeout=8)
    if code == 409 and isinstance(doc, dict) and doc.get("error") == "offline":
        return None
    if code != 200 or not isinstance(doc, dict) or not doc.get("ok"):
        raise common.Conflict("the live effects of %s are unavailable right now" % name)
    return doc


def remove_from(ctx, row, status_ids=None):
    """Remove the removable buffs (all, or only these status ids) from one online character. {removed, skipped, note}."""
    out = {"id": row["id"], "name": row["name"], "removed": [], "skipped": [], "note": ""}
    for attempt in range(2):
        try:
            doc = _inspect(ctx, row["name"])
        except (Bad, common.Conflict) as e:
            out["note"] = str(e)
            return out
        if doc is None:
            out["note"] = "offline"
            return out
        todo = [x for x in doc.get("buffs") or [] if status_ids is None or int(x["statusId"]) in status_ids]
        changed = False
        for x in todo:
            nm = (common.CATALOG.entry("status", x["statusId"]) or {}).get("name") or "Status #%s" % x["statusId"]
            if not x.get("removable"):
                if nm not in out["skipped"]:
                    out["skipped"].append(nm)
                continue
            try:
                ctx.live_call("character.buff_remove", row["name"], {"epoch": doc["epoch"], "revision": x["revision"],
                              "kind": x["kind"], "statusId": x["statusId"]}, cid=row["id"],
                              no_undo="This removed a live session effect; give it again if needed.")
                out["removed"].append(nm)
            except common.Conflict:
                changed = True                      # the effect changed meanwhile: read again once
            except Bad as e:
                out["note"] = str(e)
        if not changed:
            break
    return out


@M.post("remove")
def remove(ctx):
    """POST {ids: [...], status_ids?: [...]} - remove buffs from the ticked online characters (offline ones have none)."""
    if not ctx.need_rb().writable:
        raise Bad("this editor connection is read-only")
    ids = ctx.body.get("ids")
    if not isinstance(ids, list) or not 1 <= len(ids) <= MAX_TARGETS:
        raise Bad("tick 1-%d characters" % MAX_TARGETS)
    sids = ctx.body.get("status_ids")
    sids = None if not sids else {common.to_int(s, 1, 2 ** 31 - 1, "buff") for s in sids}
    rows = characters(ctx, [common.to_int(i, 1, 2 ** 31 - 1, "character") for i in ids])
    res = [remove_from(ctx, r, sids) if r["online"] else {"id": r["id"], "name": r["name"], "removed": [], "skipped": [],
                                                           "note": "offline: buffs end at logout"} for r in rows]
    cancelled = 0
    if ctx.body.get("cancel_queued", True) and LA._ready(ctx):
        cancelled = _cancel_queued(ctx, [r["id"] for r in rows], sids)
    return {"characters": res, "removed": sum(len(r["removed"]) for r in res), "queued_cancelled": cancelled,
            "mode": "live", "applies": "live", "applies_text": common.APPLIES_TEXT["live"]}


def _cancel_queued(ctx, cids=None, sids=None):
    where = "applied_at IS NULL"
    if cids is not None:
        if not cids:
            return 0
        where += " AND character_id IN (%s)" % ",".join(str(int(c)) for c in cids)
    if sids:
        where += " AND status_id IN (%s)" % ",".join(str(int(s)) for s in sids)
    n = ctx.need_rb().pg.json("(SELECT to_jsonb(count(*)) FROM pending_character_buffs WHERE %s)" % where) or 0
    if not n:
        return 0
    LA.W._write(ctx, [common.scope("pending_character_buffs", where, pk=["id"])], "cancelled %d queued buff(s)" % n,
                ["UPDATE pending_character_buffs SET applied_at = now(), result = 'cancelled in the editor' WHERE %s" % where],
                ["pending_character_buffs.*"], applies="now", target="queued buffs")
    return int(n)


@M.post("clear")
def clear(ctx):
    """POST {confirm: 'CLEAR'} - every online character's removable buffs, every queued buff, every running event."""
    if not ctx.need_rb().writable:
        raise Bad("this editor connection is read-only")
    if ctx.body.get("confirm") != "CLEAR":
        raise Bad("type CLEAR to confirm")
    stopped = STORE(ctx.app).stop_all(LA._actor())
    rows = [r for r in characters(ctx) if r["online"]]
    res = [remove_from(ctx, r) for r in rows]
    cancelled = _cancel_queued(ctx) if LA._ready(ctx) else 0
    return {"characters": res, "online": len(rows), "removed": sum(len(r["removed"]) for r in res),
            "skipped": sorted({s for r in res for s in r["skipped"]}), "queued_cancelled": cancelled, "events_stopped": stopped,
            "mode": "live", "applies": "live", "applies_text": common.APPLIES_TEXT["live"]}


# =========================================================================== event buffs (fixed end time)
class Store:
    """Event buffs in sqlite beside the editor's backups (the game database has no place for them)."""

    def __init__(self, path):
        self.path = path
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with self.db() as c:
            c.execute("CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, status_id INTEGER NOT NULL, "
                      "name TEXT NOT NULL, ends REAL NOT NULL, scope TEXT NOT NULL, created REAL NOT NULL, created_by TEXT, "
                      "stopped REAL, stopped_by TEXT)")
            c.execute("CREATE TABLE IF NOT EXISTS given (event INTEGER NOT NULL, cid INTEGER NOT NULL, session TEXT NOT NULL, "
                      "at REAL NOT NULL, result TEXT, PRIMARY KEY (event, cid, session))")
        try:
            os.chmod(path, 0o600)
        except OSError:
            pass

    @contextmanager
    def db(self):
        c = sqlite3.connect(self.path, timeout=10)
        try:
            yield c
            c.commit()
        finally:
            c.close()

    def add(self, status_id, name, ends, scope, by):
        with self.db() as c:
            return c.execute("INSERT INTO events (status_id, name, ends, scope, created, created_by) VALUES (?,?,?,?,?,?)",
                             (int(status_id), name, float(ends), json.dumps(scope), time.time(), by)).lastrowid

    def mark(self, event, cid, session, result):
        with self.db() as c:
            c.execute("INSERT OR REPLACE INTO given (event, cid, session, at, result) VALUES (?,?,?,?,?)",
                      (int(event), int(cid), str(session), time.time(), result))

    def given(self, event):
        with self.db() as c:
            return {(r[0], r[1]) for r in c.execute("SELECT cid, session FROM given WHERE event = ?", (int(event),))}

    def running(self, now=None):
        now = time.time() if now is None else now
        with self.db() as c:
            rows = c.execute("SELECT id, status_id, name, ends, scope, created, created_by FROM events WHERE stopped IS NULL "
                             "AND ends > ? ORDER BY ends", (now,)).fetchall()
        return [{"id": r[0], "status_id": r[1], "name": r[2], "ends": r[3], "scope": json.loads(r[4]), "created": r[5],
                 "created_by": r[6]} for r in rows]

    def recent(self, limit=30):
        with self.db() as c:
            rows = c.execute("SELECT e.id, e.status_id, e.name, e.ends, e.scope, e.created, e.created_by, e.stopped, e.stopped_by, "
                             "(SELECT count(*) FROM given g WHERE g.event = e.id AND g.result = 'applied') FROM events e "
                             "ORDER BY e.id DESC LIMIT ?", (limit,)).fetchall()
        return [{"id": r[0], "status_id": r[1], "name": r[2], "ends": r[3], "scope": json.loads(r[4]), "created": r[5],
                 "created_by": r[6], "stopped": r[7], "stopped_by": r[8], "given": r[9]} for r in rows]

    def stop(self, event, by):
        with self.db() as c:
            return c.execute("UPDATE events SET stopped = ?, stopped_by = ? WHERE id = ? AND stopped IS NULL",
                             (time.time(), by, int(event))).rowcount

    def stop_all(self, by):
        with self.db() as c:
            return c.execute("UPDATE events SET stopped = ?, stopped_by = ? WHERE stopped IS NULL AND ends > ?",
                             (time.time(), by, time.time())).rowcount


_STORES = {}


def STORE(app):
    path = os.path.join(app.a.backup_dir, "event-buffs.sqlite3")
    if path not in _STORES:
        _STORES[path] = Store(path)
    return _STORES[path]


def in_scope(scope, row):
    if scope.get("kind") == "ids":
        return int(row["id"]) in {int(i) for i in scope.get("ids") or []}
    if scope.get("camp") is not None and int(row["camp"]) != int(scope["camp"]):
        return False
    return int(scope.get("min_level") or 1) <= int(row["level"]) <= int(scope.get("max_level") or 200)


def due(events, rows, given_of, now):
    """[(event, row, seconds_left)] still to give: online, in scope, this game session not given yet. Pure (unit-tested)."""
    out = []
    for ev in events:
        left = int(ev["ends"] - now)
        if left < 5:
            continue
        done = given_of(ev["id"])
        for r in rows:
            if r["online"] and r.get("session") and in_scope(ev["scope"], r) and (int(r["id"]), str(r["session"])) not in done:
                out.append((ev, r, left))
    return out


class Job:
    """Every JOB_SECONDS: give each running event buff to the targets that entered the world since (once per session)."""

    def __init__(self):
        self.app, self.thread, self.lock = None, None, threading.Lock()
        self.last = {"at": None, "given": 0, "error": None}

    def ensure(self, app):
        with self.lock:
            self.app = app
            if self.thread is None and getattr(app, "reborn", None):
                self.thread = threading.Thread(target=self.run, name="gw-event-buffs", daemon=True)
                self.thread.start()

    def run(self):
        while True:
            try:
                self.tick()
            except Exception as e:      # noqa: BLE001 - one bad tick must not stop the job
                self.last["error"] = "%s: %s" % (type(e).__name__, str(e)[:200])
            time.sleep(JOB_SECONDS)

    def tick(self):
        app = self.app
        store = STORE(app)
        events = store.running()
        self.last.update(at=time.time(), error=None)
        if not events or not app.reborn or not app.reborn.writable:
            return 0
        ctx = common.Ctx(app, "POST", "buff_tools/job", {}, {}, M)
        rows = characters(ctx)
        n = 0
        cache = {}
        for ev, r, left in due(events, rows, lambda e: cache[e] if e in cache else cache.setdefault(e, store.given(e)), time.time()):
            doc = None
            try:
                doc = _inspect(ctx, r["name"])
            except (Bad, common.Conflict):
                continue                        # changing maps / logging in: the next tick tries again
            if doc is None:
                continue
            have = next((x for x in doc.get("buffs") or [] if int(x["statusId"]) == int(ev["status_id"])), None)
            if have and int(have.get("remainingSeconds") or 0) >= left - JOB_SECONDS * 2:
                store.mark(ev["id"], r["id"], r["session"], "had it")
                continue
            res = _live_apply(ctx, r["id"], r["name"], ev["status_id"], left)
            if res["result"] in ("applied", "refused"):
                store.mark(ev["id"], r["id"], r["session"], res["result"])
                n += res["result"] == "applied"
        self.last["given"] += n
        return n


JOB = Job()


@M.get("events")
def events(ctx):
    JOB.ensure(ctx.app)
    st = STORE(ctx.app)
    now = time.time()
    rows = st.recent()
    for e in rows:
        e["ends_text"] = datetime.datetime.fromtimestamp(e["ends"], BRISBANE).strftime("%Y-%m-%d %H:%M")
        e["state"] = "stopped" if e["stopped"] else "running" if e["ends"] > now else "ended"
        e["left_seconds"] = max(0, int(e["ends"] - now)) if e["state"] == "running" else 0
        sc = e["scope"]
        e["who"] = ("%d named character(s)" % len(sc.get("ids") or [])) if sc.get("kind") == "ids" else \
            "every character" + ("" if sc.get("camp") is None else " of " + common.CAMP_NAMES.get(int(sc["camp"]), "?")) + \
            ("" if (sc.get("min_level"), sc.get("max_level")) in ((1, 200), (None, None)) else " level %s-%s" % (sc.get("min_level"), sc.get("max_level")))
    last = dict(JOB.last)
    last["at_text"] = datetime.datetime.fromtimestamp(last["at"], BRISBANE).strftime("%H:%M:%S") if last.get("at") else None
    idle = None if ctx.need_rb().writable else "the editor is read-only, so the event job gives nothing (open it in Edit mode)"
    return {"events": rows, "job": last, "job_running": JOB.thread is not None, "job_idle": idle, "every_seconds": JOB_SECONDS,
            "now": datetime.datetime.now(BRISBANE).strftime("%Y-%m-%d %H:%M"), "time_zone": "Brisbane (AEST, UTC+10)"}


@M.post(r"events/(\d+)/stop")
def event_stop(ctx, event):
    """Stop giving the buff to new sessions; with remove=true also remove it from everyone online now."""
    n = STORE(ctx.app).stop(int(event), LA._actor())
    if not n:
        raise Bad("that event is not running")
    out = {"stopped": True}
    if ctx.body.get("remove"):
        ev = next((e for e in STORE(ctx.app).recent(500) if e["id"] == int(event)), None)
        rows = [r for r in characters(ctx) if r["online"] and in_scope(ev["scope"], r)]
        res = [remove_from(ctx, r, {int(ev["status_id"])}) for r in rows]
        out.update(removed=sum(len(r["removed"]) for r in res), characters=res)
    return out
