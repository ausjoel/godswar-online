"""Operations: the gw251 commands, a server resource monitor, the realm (server list) editor and the change-log
housekeeping, on one page.

* gw251 status / check run here, as the editor user (systemctl show, port probes, /readyz, one SELECT).
  start / stop / restart / logs / backup go through the existing root helper behind /run/gweditor-control.sock
  (world.server_control: player-count acknowledgement, change record). backups (list) and restore need helper v2
  (ops/server-control/gweditor-control.py); restore is refused unless NOBODY is online and the file name is typed.
* Monitor: /proc and the cgroup v2 files (world-readable; no root). A background sampler keeps one hour of history
  in memory (nothing is written). GWED_PROC_ROOT / GWED_CGROUP_ROOT point the tests at fixture trees.
* Realms: public.server rows, validated exactly like the server's RealmCatalogEntry / RealmCatalogSnapshot (one
  bad enabled row breaks the login screen for everybody), through rows_write (backup, change record, Undo).
* Change log: export everything, then archive (move, never delete) the records into web-changes/cleared-<time>/.
  Records still waiting for a restart stay, so Home's "pending restart" list is not lost.
"""
import collections
import datetime
import glob
import ipaddress
import json
import os
import re
import secrets
import shutil
import socket
import string
import subprocess
import threading
import time
import urllib.request
import uuid

from . import common
from . import world as W
from .common import Bad, Conflict, NotFound, Refused, lit

M = common.module(
    "operations", "Operations",
    icon={"sprite": 696},
    order=95,
    status="ready",
    description="gw251 commands (status, start, stop, restart, logs, check, backup, restore), resource monitor, realms, change log.",
    needs_character=False,
)

UNIT = "godswar251.service"
SYSTEMCTL = os.environ.get("GWED_SYSTEMCTL", "/usr/bin/systemctl")
PROC = os.environ.get("GWED_PROC_ROOT", "/proc")
CGROUP = os.environ.get("GWED_CGROUP_ROOT", "/sys/fs/cgroup")
READYZ_PORT = 9090
# service -> cgroup v2 path patterns under CGROUP (first match wins)
SERVICES = collections.OrderedDict([
    ("godswar251", ["system.slice/godswar251.service"]),
    ("postgres", ["system.slice/system-postgresql.slice/postgresql@*.service", "system.slice/postgresql@*.service",
                  "system.slice/postgresql.service"]),
    ("caddy", ["system.slice/caddy.service"]),
    ("gwsignup", ["system.slice/gwsignup.service"]),
    ("editor", ["system.slice/gweditor.service"]),
])
SAMPLE_SECONDS = 10
HISTORY = 360                       # one hour at 10 s
PLAYERS_EVERY = 3                   # count players every 3rd sample (30 s): one small SELECT
DUMP = re.compile(r"godswar251-(?:pre-restore-)?\d{4}-\d{2}-\d{2}-\d{6}\.sql\.gz")


# =============================================================== gw251 status / check (no root)
def _systemctl_show(unit=UNIT):
    props = "ActiveState,SubState,ActiveEnterTimestamp,ExecMainStartTimestamp,MainPID,NRestarts,Result"
    try:
        p = subprocess.run([SYSTEMCTL, "show", unit, "-p", props], capture_output=True, text=True, timeout=5)
    except (OSError, subprocess.TimeoutExpired) as e:
        return {"error": "systemctl unavailable (%s)" % type(e).__name__}
    out = {}
    for line in p.stdout.splitlines():
        k, _, v = line.partition("=")
        if k:
            out[k] = v
    if p.returncode and not out:
        out["error"] = (p.stderr or "systemctl show failed").strip()[:200]
    return out


def _port_open(port, host="127.0.0.1", timeout=2.0):
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def _readyz(port=READYZ_PORT):
    try:
        with urllib.request.urlopen("http://127.0.0.1:%d/readyz" % port, timeout=2) as r:
            return r.status == 200
    except Exception:       # noqa: BLE001 - any failure = not ready
        return False


def _players(ctx):
    try:
        return W._who(ctx)
    except Exception as e:   # noqa: BLE001 - the page still renders without the database
        return None, {"error": str(e)[:200]}


@M.get("overview")
def overview(ctx):
    count, detail = _players(ctx)
    rb = ctx.rb
    return {"unit": UNIT, "service": _systemctl_show(), "players_online": count, "players_detail": detail,
            "control_available": bool(rb) and W._control_ok(ctx), "writable": bool(rb and rb.writable),
            "database": rb.db if rb else None, "jobs": W._server_jobs(), "restore_rule":
            "Restore is refused while anyone is online or signing in. A pre-restore dump is taken first; on failure the old "
            "data is put back. Restoring an older dump also rolls back schema migrations; the game server re-applies newer "
            "ones at start, so restore only dumps taken from this server lineage."}


@M.get("check")
def check(ctx):
    """gw251 check, as the editor user: listeners, readiness, the unit, PostgreSQL, the realm rows, disk."""
    rows = []

    def add(name, ok, detail=""):
        rows.append({"name": name, "ok": bool(ok), "detail": detail})
    for name, port in (("Login port", 6999), ("Game port", 7000), ("Management (local)", READYZ_PORT)):
        is_open = _port_open(port)
        add("%s %d" % (name, port), is_open, "127.0.0.1:%d %s" % (port, "open" if is_open else "closed"))
    add("Game readiness (/readyz)", _readyz(), "http://127.0.0.1:%d/readyz" % READYZ_PORT)
    svc = _systemctl_show()
    add("%s active" % UNIT, svc.get("ActiveState") == "active", "%s/%s since %s" % (svc.get("ActiveState"), svc.get("SubState"),
                                                                                    svc.get("ActiveEnterTimestamp") or "?"))
    try:
        realms = ctx.need_rb().pg.json("(SELECT coalesce(json_agg(json_build_object('id',id,'name',name,'host',ip_address,"
                                       "'port',game_port,'enabled',enabled) ORDER BY id),'[]') FROM server)")
        add("PostgreSQL", True, "database %s answers" % ctx.rb.db)
        for r in realms:
            add("Realm %s %s" % (r["id"], r["name"]), True, "%s:%s enabled=%s" % (r["host"], r["port"], r["enabled"]))
    except Exception as e:   # noqa: BLE001
        add("PostgreSQL", False, str(e)[:200])
    d = disk_usage()
    for m in d:
        add("Disk %s" % m["mount"], m["used_pct"] < 90, "%.1f GB free of %.1f GB (%d%% used)" % (m["free"] / 1e9, m["total"] / 1e9, m["used_pct"]))
    return {"checks": rows, "ok": all(r["ok"] for r in rows), "time": common.now_iso()}


# =============================================================== feature switches (read-only)
SECRET_NAME = re.compile(r"PASSWORD|CONNECTION_STRING|CERTIFICATE|TOKEN|SECRET|KEY", re.I)


def parse_environment(text):
    """systemctl show -p Environment --value output -> [(name, value)] (values may be quoted by systemd)."""
    import shlex
    try:
        words = shlex.split(text or "")
    except ValueError:
        words = (text or "").split()
    out = []
    for w in words:
        k, sep, v = w.partition("=")
        if sep and re.fullmatch(r"[A-Z][A-Z0-9_]*", k):
            out.append((k, v))
    return out


@M.get("features")
def features(ctx):
    """The GODSWAR_* switches the live unit is started with (systemd drop-ins). Read-only: changing one means editing a
    drop-in as root and restarting at zero players (see Development/w4 C research report)."""
    try:
        p = subprocess.run([SYSTEMCTL, "show", UNIT, "-p", "Environment", "-p", "DropInPaths"], capture_output=True, text=True, timeout=5)
    except (OSError, subprocess.TimeoutExpired) as e:
        return {"error": "systemctl unavailable (%s)" % type(e).__name__, "switches": [], "dropins": []}
    env, dropins = "", []
    for line in p.stdout.splitlines():
        k, _, v = line.partition("=")
        if k == "Environment":
            env = v
        elif k == "DropInPaths":
            dropins = [os.path.basename(x) for x in v.split()]
    rows = []
    for k, v in parse_environment(env):
        if not k.startswith("GODSWAR_"):
            continue
        rows.append({"name": k, "value": "[hidden]" if SECRET_NAME.search(k) else W.redact(v)[:200]})
    return {"switches": sorted(rows, key=lambda r: r["name"]), "dropins": dropins, "unit": UNIT,
            "note": "Set in /etc/systemd/system/godswar251.service.d/*.conf; a change needs root and a game restart."}


# =============================================================== helper calls (start/stop/restart/logs/backup/backups/restore)
def _helper(request):
    """One request to the root helper; returns its first reply line (the job record)."""
    if not W.CONTROL_LOCK.acquire(blocking=False):
        raise Refused("Another server operation is being sent; wait a moment")
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(5)
            connection.connect(W.CONTROL_SOCK)
            connection.sendall((json.dumps(request) + "\n").encode())
            with connection.makefile("rb") as response:
                result = json.loads(response.readline(4096))
    except (OSError, ValueError) as e:
        raise Refused("Server control unavailable (%s)" % type(e).__name__)
    finally:
        W.CONTROL_LOCK.release()
    if result.get("error"):
        msg = W.redact(str(result["error"]))
        if "invalid action" in msg and request.get("action") in ("backups", "restore"):
            msg += " - the installed helper is v1; install ops/server-control/gweditor-control.py (v2) for backups and restore"
        raise Refused(msg)
    return result


def _record(ctx, action, job, note, players, detail, extra=None):
    actor = getattr(common.ACTOR, "name", None) or "unknown editor session"
    change = {"engine": "operation", "id": "op-" + job, "time": common.now_iso(), "database": ctx.rb.db if ctx.rb else None,
              "action": action, "character": UNIT, "cid": None, "sql": [], "backup": None, "diff": {}, "verify": {},
              "output": "job %s by %s; %s" % (job, actor, note), "actor": actor, "players_online": players,
              "players_detail": detail, "module": "operations", "undone": False,
              "no_undo": "A server operation; use the opposite control (or restore the pre-restore dump)."}
    if extra:
        change.update(extra)
    ctx.app.changes.append(change)
    ctx.app._save_change(change)
    return common.public_change(change)


@M.post("control")
def control(ctx):
    action = (ctx.body or {}).get("action")
    if action in W.CONTROL_ACTIONS:
        return W.server_control(ctx)            # start / stop / restart / backup / logs (acknowledged player count)
    if action not in ("backups", "restore"):
        raise Bad("Choose status, check, start, stop, restart, logs, backup, backups or restore")
    rb = W._need_writable(ctx)
    if not W._control_ok(ctx):
        raise Refused("Service controls are available only for the configured live game server")
    job = uuid.uuid4().hex
    if action == "backups":
        return {"job": _helper({"action": "backups", "job": job})}
    name = ctx.body.get("file")
    if not isinstance(name, str) or not DUMP.fullmatch(name):
        raise Bad("Choose a backup file from the list")
    if ctx.body.get("confirm") != name:
        raise Bad("Type the backup file name exactly to confirm the restore")
    players, detail = W._who(ctx)
    if players:
        raise Refused("%d player%s online or signing in. A restore is allowed only at zero players (it replaces the whole "
                      "database); stop the server or wait." % (players, "" if players == 1 else "s"))
    result = _helper({"action": "restore", "job": job, "file": name})
    change = _record(ctx, "database restore from " + name, job, "restore of %s requested at 0 players" % name, players, detail,
                     {"database": rb.db})
    return {"job": result, "players_online": players, "change": change}


@M.get(r"job/([a-f0-9]{32})")
def job(ctx, job_id):
    return W.server_job(ctx, job_id)


# =============================================================== resource monitor
def _read(path, default=None):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return default


def cpu_times():
    """(busy, total) jiffies from the first line of /proc/stat."""
    first = (_read(os.path.join(PROC, "stat"), "") or "").split("\n", 1)[0].split()
    if not first or first[0] != "cpu":
        return None
    v = [int(x) for x in first[1:]]
    idle = v[3] + (v[4] if len(v) > 4 else 0)
    return sum(v) - idle, sum(v)


def ncpu():
    n = sum(1 for line in (_read(os.path.join(PROC, "stat"), "") or "").splitlines() if re.match(r"cpu\d+ ", line))
    return n or os.cpu_count() or 1


def meminfo():
    out = {}
    for line in (_read(os.path.join(PROC, "meminfo"), "") or "").splitlines():
        k, _, v = line.partition(":")
        parts = v.split()
        if parts and parts[0].isdigit():
            out[k] = int(parts[0]) * 1024
    if "MemTotal" not in out:
        return None
    total, avail = out["MemTotal"], out.get("MemAvailable", out.get("MemFree", 0))
    return {"total": total, "available": avail, "used": total - avail, "used_pct": round(100.0 * (total - avail) / total, 1),
            "swap_total": out.get("SwapTotal", 0), "swap_used": out.get("SwapTotal", 0) - out.get("SwapFree", 0)}


def loadavg():
    parts = (_read(os.path.join(PROC, "loadavg"), "") or "").split()
    return [float(x) for x in parts[:3]] if len(parts) >= 3 else None


def uptime():
    parts = (_read(os.path.join(PROC, "uptime"), "") or "").split()
    return float(parts[0]) if parts else None


def net_bytes():
    """{iface: (rx, tx)} without lo."""
    out = {}
    for line in (_read(os.path.join(PROC, "net", "dev"), "") or "").splitlines()[2:]:
        name, _, rest = line.partition(":")
        name, v = name.strip(), rest.split()
        if name and name != "lo" and len(v) >= 9:
            out[name] = (int(v[0]), int(v[8]))
    return out


def disk_usage(paths=("/", "/var/lib/postgresql", "/opt")):
    out, seen = [], set()
    for p in paths:
        try:
            st = os.stat(p)
            if st.st_dev in seen:
                continue
            seen.add(st.st_dev)
            v = os.statvfs(p)
        except OSError:
            continue
        total, free = v.f_blocks * v.f_frsize, v.f_bavail * v.f_frsize
        out.append({"mount": p, "total": total, "free": free, "used_pct": int(round(100.0 * (total - free) / total)) if total else 0})
    return out


def _cgroup_dir(patterns):
    for pat in patterns:
        hits = sorted(glob.glob(os.path.join(CGROUP, pat)))
        if hits:
            return hits[0]
    return None


def service_raw():
    """{service: {memory, usage_usec, pids, running}} from the cgroup v2 files (None when the cgroup is absent)."""
    out = {}
    for name, pats in SERVICES.items():
        d = _cgroup_dir(pats)
        if not d:
            out[name] = None
            continue
        usage = None
        for line in (_read(os.path.join(d, "cpu.stat"), "") or "").splitlines():
            if line.startswith("usage_usec "):
                usage = int(line.split()[1])
        mem = (_read(os.path.join(d, "memory.current"), "") or "").strip()
        pids = (_read(os.path.join(d, "pids.current"), "") or "").strip()
        events = _read(os.path.join(d, "cgroup.events"), "") or ""
        out[name] = {"memory": int(mem) if mem.isdigit() else None, "usage_usec": usage, "pids": int(pids) if pids.isdigit() else None,
                     "running": "populated 1" in events if events else (mem.isdigit() and int(mem) > 0)}
    return out


def raw_sample():
    return {"t": time.time(), "cpu": cpu_times(), "net": net_bytes(), "svc": service_raw()}


def derive(prev, cur, cores):
    """Rates between two raw samples: cpu %, per-service cpu % (of ONE core) and memory, network bytes/s."""
    dt = max(1e-3, cur["t"] - prev["t"]) if prev else None
    out = {"t": cur["t"], "time": datetime.datetime.fromtimestamp(cur["t"], datetime.timezone.utc).isoformat(timespec="seconds"),
           "cpu_pct": None, "net_rx": None, "net_tx": None, "services": {}}
    if prev and prev.get("cpu") and cur.get("cpu"):
        busy, total = cur["cpu"][0] - prev["cpu"][0], cur["cpu"][1] - prev["cpu"][1]
        out["cpu_pct"] = round(100.0 * busy / total, 1) if total > 0 else 0.0
    if prev:
        rx = sum(v[0] for v in cur["net"].values()) - sum(v[0] for k, v in prev["net"].items() if k in cur["net"])
        tx = sum(v[1] for v in cur["net"].values()) - sum(v[1] for k, v in prev["net"].items() if k in cur["net"])
        out["net_rx"], out["net_tx"] = max(0, int(rx / dt)), max(0, int(tx / dt))
    for name, s in cur["svc"].items():
        if s is None:
            out["services"][name] = None
            continue
        p = (prev or {}).get("svc", {}).get(name) if prev else None
        pct = None
        if p and p.get("usage_usec") is not None and s.get("usage_usec") is not None and s["usage_usec"] >= p["usage_usec"]:
            pct = round(100.0 * (s["usage_usec"] - p["usage_usec"]) / (dt * 1e6), 1)
        out["services"][name] = {"memory": s["memory"], "cpu_pct": pct, "pids": s["pids"], "running": s["running"]}
    return out


class Sampler:
    """One daemon thread per editor process; started by the first monitor request."""

    def __init__(self):
        self.lock = threading.Lock()
        self.history = collections.deque(maxlen=HISTORY)
        self.prev = None
        self.thread = None
        self.app = None
        self.players = None
        self.n = 0

    def tick(self):
        cur = raw_sample()
        row = derive(self.prev, cur, ncpu())
        self.prev = cur
        if self.app is not None and self.n % PLAYERS_EVERY == 0:
            try:
                self.players = W._who(common.Ctx(self.app, "GET", "", {}, {}))[0]
            except Exception:    # noqa: BLE001 - the database may be busy; keep the last count
                pass
        row["players"] = self.players
        mem, load = meminfo(), loadavg()
        row["mem_pct"] = mem["used_pct"] if mem else None
        row["load1"] = load[0] if load else None
        self.n += 1
        with self.lock:
            if row["cpu_pct"] is not None:
                self.history.append(row)
        return row

    def run(self):
        while True:
            try:
                self.tick()
            except Exception:    # noqa: BLE001 - a missing file must not kill the sampler
                pass
            time.sleep(SAMPLE_SECONDS)

    def ensure(self, app):
        self.app = app
        if self.thread is None:
            self.tick()                    # baseline for the first rates
            self.thread = threading.Thread(target=self.run, name="gw-ops-sampler", daemon=True)
            self.thread.start()


SAMPLER = Sampler()


@M.get("monitor")
def monitor(ctx):
    SAMPLER.ensure(ctx.app)
    with SAMPLER.lock:
        hist = list(SAMPLER.history)
    players, detail = _players(ctx)
    svc = _systemctl_show()
    up = None
    try:
        started = svc.get("ActiveEnterTimestamp")
        if started:
            # "Wed 2026-11-12 02:27:01 UTC"
            dt = datetime.datetime.strptime(" ".join(started.split()[1:3]), "%Y-%m-%d %H:%M:%S")
            up = max(0, int(time.time() - dt.replace(tzinfo=datetime.timezone.utc).timestamp()))
    except (ValueError, IndexError):
        pass
    return {"now": hist[-1] if hist else None, "history": hist, "interval": SAMPLE_SECONDS, "cores": ncpu(),
            "memory": meminfo(), "load": loadavg(), "uptime": uptime(), "disks": disk_usage(), "players_online": players,
            "players_detail": detail, "game_service": svc, "game_uptime": up, "services": list(SERVICES)}


# =============================================================== realms (public.server)
NAME_MAX, IDENT_LEN, HOST_MAX, MAX_ENABLED = 35, 25, 23, 16
_LABEL = r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"
_DNS = re.compile(r"^%s(?:\.%s)*$" % (_LABEL, _LABEL))
REALM_COLS = ["id", "name", "identifier", "ip_address", "game_port", "server_limit", "enabled", "recommended", "display_order"]
# B21 item 16: realm 2 (Dwargon, the placeholder row the 2026-08-20 migrations seeded) is no longer protected. Those
# migrations check its identity only BEFORE they run, and a fresh database seeds the row itself in the same migration, so
# an already-migrated database can lose it. Deleting it still needs: disabled, nothing referring to it, no game process,
# the name typed. Its id stays used (time-zone audit), so a new server gets the next id.
PROTECTED = {1: "Realm 1 is the home realm of every existing character and the schema migrations check its identity"}
PLACEHOLDER = {2: "Placeholder server the database set-up created (Dwargon). Players never see it while it is disabled. "
                  "Delete it if you do not want a second server, or set it up as one."}


def _printable(s, lo, hi, space):
    return isinstance(s, str) and lo <= len(s) <= hi and all((" " if space else "!") <= ch <= "~" for ch in s)


def validate_realm(v):
    """The server's RealmCatalogEntry rules; raises Bad. Returns a clean dict."""
    out = {}
    name = v.get("name")
    if not _printable(name, 1, NAME_MAX, True) or name != name.strip():
        raise Bad("Name: 1-35 printable ASCII characters, no leading or trailing spaces")
    out["name"] = name
    ident = v.get("identifier")
    if not _printable(ident, IDENT_LEN, IDENT_LEN, False):
        raise Bad("Identifier: exactly 25 printable ASCII characters without spaces")
    out["identifier"] = ident
    host = v.get("ip_address")
    if not _printable(host, 1, HOST_MAX, False):
        raise Bad("Address: an IPv4 address or DNS name of at most 23 characters")
    try:
        ipaddress.IPv4Address(host)
    except ValueError:
        if re.fullmatch(r"[0-9.]+", host) or not _DNS.match(host):
            raise Bad("Address: an IPv4 address or DNS name of at most 23 characters")
    out["ip_address"] = host
    for k, lo, hi, what in (("game_port", 1, 65535, "Game port"), ("server_limit", 1, 2147483647, "Player limit"),
                            ("display_order", 0, 2147483647, "Display order")):
        x = v.get(k)
        if isinstance(x, bool) or not isinstance(x, int) or not lo <= x <= hi:
            raise Bad("%s: a whole number %d..%d" % (what, lo, hi))
        out[k] = x
    for k in ("enabled", "recommended"):
        if not isinstance(v.get(k), bool):
            raise Bad("%s must be true or false" % k)
        out[k] = v[k]
    if out["recommended"] and not out["enabled"]:
        raise Bad("Only an enabled realm can be recommended")
    return out


def new_identifier():
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(IDENT_LEN))


# Runs inside the transaction after the write and again on Undo: the login screen reads exactly these rules.
REALM_GUARD = """DO $gwrealm$ BEGIN
 IF (SELECT count(*) FROM server WHERE enabled) > %d THEN RAISE EXCEPTION 'gwadmin-conflict: more than %d enabled realms (the login server refuses the list)'; END IF;
 IF (SELECT count(*) FROM server WHERE enabled AND recommended) > 1 THEN RAISE EXCEPTION 'gwadmin-conflict: more than one enabled realm is recommended'; END IF;
 IF (SELECT count(*) FROM server WHERE enabled) = 0 THEN RAISE EXCEPTION 'gwadmin-conflict: at least one realm must stay enabled (an empty list disconnects every login)'; END IF;
 IF EXISTS (SELECT 1 FROM server WHERE enabled AND (id < 1 OR id > 255)) THEN RAISE EXCEPTION 'gwadmin-conflict: enabled realm ids must be 1..255'; END IF;
END $gwrealm$;""" % (MAX_ENABLED, MAX_ENABLED)     # the ';' matters: reborn.execute_rows appends guards as they are (W5B rig)


FK_TABLES_SQL = """(SELECT coalesce(json_agg(json_build_object('schema', ns.nspname, 'table', cl.relname, 'column', a.attname)
      ORDER BY ns.nspname, cl.relname), '[]')
    FROM pg_constraint c JOIN pg_class cl ON cl.oid = c.conrelid JOIN pg_namespace ns ON ns.oid = cl.relnamespace
    JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
    WHERE c.contype = 'f' AND c.confrelid = 'public.server'::regclass AND array_length(c.conkey, 1) = 1)"""


def _references_sql(fk, rid):
    """count(*) of one referencing table for one realm (one statement per table, so a table the editor may not read
    makes only that count unknown)."""
    # to_jsonb: pg.json casts its expression to jsonb, and a bare bigint has no such cast (found on the rig, W5B: every
    # count failed, so every realm looked "used by ... could not be counted")
    return "to_jsonb((SELECT count(*) FROM %s.%s WHERE %s = %d))" % (common.ident(fk["schema"]), common.ident(fk["table"]), common.ident(fk["column"]), int(rid))


def references(rb, rid, fks=None):
    """[{table, rows}] with rows > 0, plus {table, rows: None, error} for tables that could not be counted."""
    out = []
    for fk in (fks if fks is not None else rb.pg.json(FK_TABLES_SQL)):
        name = "%s.%s" % (fk["schema"], fk["table"])
        try:
            n = int(rb.pg.json(_references_sql(fk, rid)))
        except Exception as e:   # noqa: BLE001 - e.g. permission denied for that table
            out.append({"table": name, "rows": None, "error": str(e)[:160]})
            continue
        if n:
            out.append({"table": name, "rows": n})
    return out


def _realm_rows(ctx):
    return ctx.need_rb().pg.json("(SELECT coalesce(json_agg(json_build_object(%s) ORDER BY display_order, id), '[]') FROM server)" %
                                 ",".join("'%s',%s" % (c, c) for c in REALM_COLS + ["time_zone_id"]))


def _realm_scope(rid):
    return common.scope("server", "id = %d" % rid, pk=["id"], counters=[],
                        ignore=["time_zone_revision", "time_zone_updated_at", "time_zone_updated_by"])


@M.get("realms")
def realms(ctx):
    rows = _realm_rows(ctx)
    rb = ctx.need_rb()
    fks = rb.pg.json(FK_TABLES_SQL)
    for r in rows:
        r["references"] = references(rb, r["id"], fks)
        r["protected"] = PROTECTED.get(r["id"])
        r["placeholder"] = PLACEHOLDER.get(r["id"]) if r["name"] == "Dwargon" and not r["enabled"] else None   # B21
        r["reachable"] = _port_open(r["game_port"], r["ip_address"], 1.0) if r["ip_address"] not in ("0.0.0.0", "") else False
    next_id = rb.pg.json("to_jsonb(greatest((SELECT coalesce(max(id),0) FROM server), (SELECT coalesce(max(realm_id),0) FROM server_time_zone_audit)) + 1)")
    procs = realm_processes()
    for r in rows:
        r["process"] = procs.get(r["id"])
        r["characters"] = realm_characters(ctx, r["id"])
    return {"realms": rows, "next_id": next_id, "new_identifier": new_identifier(), "writable": bool(rb.writable),
            "character_rule": CHARACTER_RULE, "control_available": W._control_ok(ctx),
            "limits": {"name": NAME_MAX, "identifier": IDENT_LEN, "host": HOST_MAX, "enabled": MAX_ENABLED, "ids": "1..255"},
            "how": ["The client's config.ini holds only the LOGIN server address (IP/PORT). After login the server sends the "
                    "list of ENABLED rows of this table (ordered by display order), re-read at every login: name, enable, "
                    "recommended and order changes show at the next login without a restart.",
                    "Each game process serves ONE realm (appsettings Game:WorldInstances:RealmId). An enabled realm with no game "
                    "process at its address and port disconnects whoever picks it. New realms start disabled.",
                    "A realm that characters, accounts, world bosses, criers, awards or holy suits refer to cannot be removed; "
                    "disable it instead. Realm ids are never reused (the time-zone audit keeps every id).",
                    "Client files: AddressConfig.ini is per-map monster/safe-area data and Channel.ini is a client setting; "
                    "neither is a server list."]}


def _realm_fields(values):
    return ["server.%s" % c for c in values]


@M.post("realm/save")
def realm_save(ctx):
    rb = W._need_writable(ctx)
    b = ctx.body or {}
    clean = validate_realm(b)
    rid = b.get("id")
    expected = b.get("expected")
    rows = {r["id"]: r for r in _realm_rows(ctx)}
    if expected is None:                                   # a new realm
        rid = W._int(rid, 1, 255, "realm id")
        nxt = rb.pg.json("to_jsonb(greatest((SELECT coalesce(max(id),0) FROM server), (SELECT coalesce(max(realm_id),0) FROM server_time_zone_audit)) + 1)")
        if rid < nxt:
            raise Conflict("Realm id %d is or was in use (time-zone audit keeps every id); use %d" % (rid, nxt))
        if clean["enabled"]:
            raise Bad("Add the realm disabled first; enable it once its game process answers at %s:%d" % (clean["ip_address"], clean["game_port"]))
        values = dict(id=rid, **clean)
        return ctx.rows_write([_realm_scope(rid)], "add realm #%d %s (%s:%d)" % (rid, clean["name"], clean["ip_address"], clean["game_port"]),
                              lambda before: [common.insert_sql("server", values)], fields=["server.*"], advanced=True,
                              guard_sql=[REALM_GUARD, "DO $gwid$ BEGIN IF (SELECT count(*) FROM server WHERE identifier = %s) <> 1 THEN "
                                         "RAISE EXCEPTION 'gwadmin-conflict: identifier already used by another realm'; END IF; END $gwid$;" % lit(clean["identifier"])],
                              applies="next_login", target="realm #%d" % rid)
    rid = W._int(rid, 1, 2147483647, "realm id")
    if rid not in rows:
        raise NotFound("No realm #%d" % rid)
    if not isinstance(expected, dict):
        raise Bad("expected (the row as shown) is required")
    if clean["identifier"] != rows[rid]["identifier"]:
        raise Refused("The identifier of an existing realm is fixed (the game process and the login redirect match it)")
    changes = {k: v for k, v in clean.items() if rows[rid].get(k) != v and k != "identifier"}
    if not changes:
        raise Bad("Nothing changed")
    moves = "ip_address" in changes or "game_port" in changes or ("enabled" in changes and clean["enabled"])
    if clean["enabled"] and moves and not _port_open(clean["game_port"], clean["ip_address"], 2.0) and b.get("acknowledge_unreachable") is not True:
        raise Conflict("Nothing answers at %s:%d. Every player who picks realm #%d at the next login would be disconnected; "
                       "confirm again to save anyway" % (clean["ip_address"], clean["game_port"], rid))
    exp = {k: expected.get(k) for k in changes}
    return ctx.rows_write([_realm_scope(rid)], "realm #%d: %s" % (rid, ", ".join("%s=%s" % kv for kv in changes.items())),
                          lambda before: [common.update_sql("server", {"id": rid}, changes, bump=False)],
                          fields=_realm_fields(changes), advanced=True, guard_sql=[REALM_GUARD], applies="next_login",
                          target="realm #%d" % rid, expected=[{"table": "server", "where": {"id": rid}, "values": exp}])


@M.post("realm/delete")
def realm_delete(ctx):
    rb = W._need_writable(ctx)
    rid = W._int((ctx.body or {}).get("id"), 1, 2147483647, "realm id")
    if rid in PROTECTED:
        raise Refused(PROTECTED[rid])
    rows = {r["id"]: r for r in _realm_rows(ctx)}
    if rid not in rows:
        raise NotFound("No realm #%d" % rid)
    refs = references(rb, rid)
    if refs:
        raise Refused("Realm #%d is used by %s; disable it instead" % (rid, ", ".join(
            "%s (%s)" % (r["table"], r["rows"] if r["rows"] is not None else "could not be counted") for r in refs)))
    if rows[rid]["enabled"]:
        raise Refused("Disable realm #%d first (players may be choosing it now)" % rid)
    proc = realm_processes().get(rid)
    if proc and proc.get("set_up"):
        raise Refused("Realm #%d still has a game process set up (%s). Use Delete server: it takes the process down and closes "
                      "the port first" % (rid, proc.get("unit")))
    if (ctx.body or {}).get("confirm") != rows[rid]["name"]:
        raise Bad("Type the realm name to confirm")
    return ctx.rows_write([_realm_scope(rid)], "remove realm #%d %s" % (rid, rows[rid]["name"]),
                          lambda before: [common.delete_sql("server", {"id": rid})], fields=["server.*"], advanced=True,
                          guard_sql=[REALM_GUARD], applies="next_login", target="realm #%d" % rid,
                          expected=[{"table": "server", "where": {"id": rid}, "values": {"name": rows[rid]["name"], "enabled": False}}],
                          no_undo="A removed realm id cannot be re-created (its time-zone audit row stays); add a new realm instead.")


# =============================================================== guided "Add server" / "Delete server" (wave 5 B)
# What one more server (realm) needs, and who does it:
#   * a row in public.server (this module, audited, Undo)                     - added DISABLED
#   * its own game process, its ufw port, a readiness wait                    - the root helper v3 (realm_provision), which
#     starts the fixed oneshot godswar251-realm-admin@provision-<realm>-<port>.service; two numbers, nothing else
#   * the login handoff in PostgreSQL on the MAIN game process                - the script writes the drop-in; the admin restarts
#     the game once at zero players (never done for him)
#   * the provider firewall (Hosting by Design)                               - the admin; the script listens on the port for a
#     moment and the page connects from the admin's browser, so "reachable from outside" is measured, not assumed
#   * the row enabled                                                         - realm/provision/finish, only after all of it
# Realm 2 "Dwargon" is NOT a template: it is a real, disabled row that the schema migrations seeded. It is the natural
# first extra server, so the guide offers it first; new rows copy nothing. B21: it may now be deleted (its id is never
# reused).
REALM_STATE = os.environ.get("GWED_REALM_STATE", "/var/lib/godswar251-realms")
REALM_MIN, REALM_MAX, REALM_PORT_MIN, REALM_PORT_MAX = 2, 255, 7001, 7099
DEFAULT_LIMIT = 250
CHARACTER_RULE = ("Characters are never deleted with a server. 'Close server' stops its game process, closes its port and hides it "
                  "from the server list; its characters stay in the database untouched and are playable again when the server "
                  "is set up again. 'Delete server' also removes the row from the list, and is refused while any character, "
                  "account slot or other record still belongs to it (delete or wipe those characters first, one by one, in "
                  "the Accounts page).")
PROVIDER_RULE = ("The provider firewall (Hosting by Design panel) is outside this machine: the editor cannot open it. During set-up "
                 "the server listens on the new port for about 30 seconds; press 'Test from this computer' then. If the "
                 "connection arrives, the provider passes the port. If not, open inbound TCP on that port in the provider panel "
                 "and press 'Test again'.")


def _local_ipv4():
    """The source address this machine uses towards the internet (no packet is sent), or None."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("192.0.2.1", 9))
            return s.getsockname()[0]
    except OSError:
        return None


def detect_address(rows):
    """The address players connect to: this machine's own public IPv4, read from its network interface. Falls back to
    realm 1's address (with the reason) when the interface address is private (a NAT in front) or unreadable."""
    realm1 = next((r["ip_address"] for r in rows if r["id"] == 1), None)
    fixed = os.environ.get("GWED_PUBLIC_ADDRESS")           # for a machine behind a NAT, and for rigs; unset on live
    if fixed:
        try:
            validate_realm({"name": "x", "identifier": "x" * IDENT_LEN, "ip_address": fixed, "game_port": 1, "server_limit": 1,
                            "display_order": 0, "enabled": False, "recommended": False})
            return {"address": fixed, "source": "the editor setting GWED_PUBLIC_ADDRESS", "interface": None, "realm1": realm1,
                    "agrees_with_realm1": realm1 == fixed, "note": ""}
        except Bad:
            pass
    iface = _local_ipv4()
    public = False
    if iface:
        try:
            public = ipaddress.IPv4Address(iface).is_global
        except ValueError:
            iface = None
    if iface and public:
        return {"address": iface, "source": "this machine's network interface", "interface": iface, "realm1": realm1,
                "agrees_with_realm1": realm1 == iface,
                "note": "" if realm1 in (iface, None) else "Realm 1 is listed at %s, not %s; players of the new server will "
                        "connect to %s. If realm 1 uses a DNS name for the same machine, that is fine." % (realm1, iface, iface)}
    if realm1 and realm1 not in ("0.0.0.0", "127.0.0.1"):
        return {"address": realm1, "source": "realm 1's row", "interface": iface, "realm1": realm1, "agrees_with_realm1": True,
                "note": "This machine's own address is %s; the address of realm 1 is used instead." %
                        (("private (%s)" % iface) if iface else "not readable")}
    return {"address": None, "source": None, "interface": iface, "realm1": realm1, "agrees_with_realm1": False,
            "note": "No public address could be detected (interface %s, realm 1 at %s); set the address by hand in the "
                    "advanced form." % (iface or "unreadable", realm1 or "nothing")}


def realm_processes():
    """{realm id: what godswar251-realm last did for it + whether its unit is active} from the script's own records
    (world-readable JSON under REALM_STATE; nothing here needs root)."""
    out = {}
    for path in sorted(glob.glob(os.path.join(REALM_STATE, "realm-*.json"))):
        m = re.fullmatch(r"realm-(\d{1,3})\.json", os.path.basename(path))
        if not m or os.path.islink(path):
            continue
        try:
            with open(path, encoding="utf-8") as f:
                doc = json.load(f)
        except (OSError, ValueError):
            continue
        if not isinstance(doc, dict):
            continue
        rid = int(m.group(1))
        rec = {k: doc.get(k) for k in ("state", "action", "port", "steps", "outside", "error", "code", "started_at", "finished_at",
                                       "ufw", "unit", "log", "management_port")}
        rec["set_up"] = doc.get("action") == "provision" and doc.get("state") in ("succeeded", "running")
        svc = _systemctl_show("godswar251-realm@%d.service" % rid) if rec["set_up"] else {}
        rec["active"] = svc.get("ActiveState")
        if rec["set_up"] and doc.get("state") == "succeeded" and rec["active"] not in (None, "active", "activating"):
            rec["problem"] = "the process is set up but not running (%s)" % rec["active"]
        out[rid] = rec
    return out


def _realm_connections(port):
    """Established TCP connections to one local port, from /proc/net/tcp{,6}; None when unreadable."""
    want, n, seen = "%04X" % int(port), 0, False
    for name in ("tcp", "tcp6"):
        rows = _read(os.path.join(PROC, "net", name))
        if rows is None:
            continue
        seen = True
        for r in rows.splitlines()[1:]:
            c = r.split()
            if len(c) > 3 and c[3] == "01" and c[1].rsplit(":", 1)[-1].upper() == want:
                n += 1
    return n if seen else None


def realm_players(ctx, rid, port):
    """(count, detail) of who is in ONE realm now: characters a game session owns + open connections to its port."""
    owned = int(ctx.need_rb().pg.json("to_jsonb((SELECT count(*) FROM character_base WHERE server_id = %d AND checkpoint_owner_id IS NOT NULL))" % int(rid)))
    conns = _realm_connections(port)
    return max(owned, conns or 0), {"characters_in_world": owned, "connections": conns}


def realm_characters(ctx, rid):
    return int(ctx.need_rb().pg.json("to_jsonb((SELECT count(*) FROM character_base WHERE server_id = %d))" % int(rid)))


def suggest_port(rows, procs, own=None):
    """The lowest port of the realm range that no row, no set-up realm and no listener uses. own = a realm id whose
    own port may be suggested again."""
    used = {r["game_port"] for r in rows if r["id"] != own}
    used |= {p.get("port") for rid, p in procs.items() if rid != own and p.get("set_up")}
    keep = next((r["game_port"] for r in rows if r["id"] == own), None)
    if keep and REALM_PORT_MIN <= keep <= REALM_PORT_MAX and keep not in used:
        return keep
    for port in range(REALM_PORT_MIN, REALM_PORT_MAX + 1):
        if port not in used and not _port_open(port, "127.0.0.1", 0.3):
            return port
    return None


def _handoff(ctx):
    """How the RUNNING main game keeps login handoffs: {'store': 'memory'|'postgres'|None, ...} from /admin/capabilities."""
    try:
        st, doc = common._http("GET", common.mgmt_port(ctx.app), "/admin/capabilities", timeout=2.0)
        h = doc.get("legacyLoginHandoff") if st == 200 and isinstance(doc, dict) else None
    except Exception:    # noqa: BLE001 - no management port on a copy, or the game is down
        h = None
    store = (h or {}).get("store")
    return {"store": store, "shared": store == "postgres", "known": h is not None,
            "text": {"postgres": "The running game shares login handoffs through PostgreSQL: a second server can take logins.",
                     "memory": "The running game keeps login handoffs in memory. Set-up prepares the switch; then the game must be "
                               "restarted once at zero players (Game server tab) before a second server can take logins."
                     }.get(store, "The running game did not say how it keeps login handoffs (older build, or it is down).")}


def _realm_port(v, what="Game port"):
    if isinstance(v, bool) or not isinstance(v, int) or not REALM_PORT_MIN <= v <= REALM_PORT_MAX:
        raise Bad("%s: a whole number %d..%d (the range the root helper may open)" % (what, REALM_PORT_MIN, REALM_PORT_MAX))
    return v


@M.get("realm/setup")
def realm_setup(ctx):
    """Everything the guided 'Add server' form shows before anything is changed: detected address, a free port, the rows
    that can be set up, the handoff state, what is already running."""
    rb = ctx.need_rb()
    rows = _realm_rows(ctx)
    procs = realm_processes()
    next_id = rb.pg.json("to_jsonb(greatest((SELECT coalesce(max(id),0) FROM server), (SELECT coalesce(max(realm_id),0) FROM server_time_zone_audit)) + 1)")
    reuse = [{"id": r["id"], "name": r["name"], "game_port": r["game_port"], "ip_address": r["ip_address"], "characters": realm_characters(ctx, r["id"]),
              "suggested_port": suggest_port(rows, procs, own=r["id"])}
             for r in rows if not r["enabled"] and REALM_MIN <= r["id"] <= REALM_MAX and not (procs.get(r["id"]) or {}).get("set_up")]
    return {"address": detect_address(rows), "suggested_port": suggest_port(rows, procs), "port_range": [REALM_PORT_MIN, REALM_PORT_MAX],
            "next_id": next_id if next_id <= REALM_MAX else None, "reuse": reuse, "default_limit": DEFAULT_LIMIT,
            "handoff": _handoff(ctx), "processes": procs, "control_available": W._control_ok(ctx), "writable": bool(rb.writable),
            "enabled_now": sum(1 for r in rows if r["enabled"]), "max_enabled": MAX_ENABLED,
            "template_note": "Dwargon (realm 2) is not a template. It is a placeholder server row the database set-up created. "
                             "Set it up as your second server (rename it if you like), or delete it in the Realms table and add "
                             "new servers instead. Its number is never reused. A new server copies nothing from it.",
            "provider_rule": PROVIDER_RULE, "character_rule": CHARACTER_RULE,
            "what_happens": ["The row is added disabled (or the existing disabled row is used).",
                             "A second game process is created for it: its own systemd unit, realm id, ports and log file; the same "
                             "game build, feature switches and database as realm 1.",
                             "Its port is opened in this machine's firewall (ufw).",
                             "The port is tested from your browser (the provider firewall).",
                             "The process is started and waited for; only then is the server shown in the server list."]}


def _send_realm_job(ctx, action, rid, port=None):
    if not W._control_ok(ctx):
        raise Refused("Server set-up is available only for the configured live game server (the root helper is not there)")
    job = uuid.uuid4().hex
    request = {"action": action, "job": job, "realm": rid}
    if port is not None:
        request["port"] = port
    try:
        return _helper(request)
    except Refused as e:
        if "invalid action" in str(e):
            raise Refused("The installed root helper is older than v3 and cannot set up servers. Install "
                          "ops/server-control (INSTALL.md, 'Helper v3'), then try again")
        raise


@M.post("realm/provision")
def realm_provision(ctx):
    """One click of the guide. POST {mode: 'new'|'existing', id, name, port, server_limit}.
    new: adds the row disabled at the DETECTED address (not typed). existing: a disabled row (for example Dwargon), moved
    to the detected address and the chosen port when they differ. Then the root helper sets the process up. The row is
    enabled later by realm/provision/finish."""
    rb = W._need_writable(ctx)
    if not W._control_ok(ctx):                              # before anything is written
        raise Refused("Server set-up is available only for the configured live game server (the root helper is not there)")
    b = ctx.body or {}
    rows = {r["id"]: r for r in _realm_rows(ctx)}
    procs = realm_processes()
    port = _realm_port(b.get("port"))
    address = detect_address(list(rows.values()))["address"]
    if not address:
        raise Refused("No public address could be detected for this machine; add the row by hand in the advanced form first")
    mode = b.get("mode")
    if mode not in ("new", "existing"):
        raise Bad("mode must be 'new' or 'existing'")
    limit = b.get("server_limit", DEFAULT_LIMIT)
    out = {}
    if mode == "new":
        nxt = rb.pg.json("to_jsonb(greatest((SELECT coalesce(max(id),0) FROM server), (SELECT coalesce(max(realm_id),0) FROM server_time_zone_audit)) + 1)")
        if b.get("id") != nxt:
            raise Conflict("The next free server number is %d now; look again and confirm" % nxt)
        rid = nxt
        if not REALM_MIN <= rid <= REALM_MAX:
            raise Refused("Server numbers go up to %d; none is left" % REALM_MAX)
    else:
        rid = W._int(b.get("id"), REALM_MIN, REALM_MAX, "server number")
        if rid not in rows:
            raise NotFound("No realm #%d" % rid)
        if rows[rid]["enabled"]:
            raise Refused("Realm #%d is enabled: players may be in it. Close it first; set-up restarts its process" % rid)
        players, _ = realm_players(ctx, rid, rows[rid]["game_port"])
        if players:
            raise Refused("%d player%s still in realm #%d; wait until it is empty" % (players, "" if players == 1 else "s", rid))
    taken = [r for r in rows.values() if r["game_port"] == port and r["id"] != rid]
    if taken:
        raise Conflict("Port %d is the port of realm #%d %s" % (port, taken[0]["id"], taken[0]["name"]))
    if any(p.get("set_up") and p.get("port") == port for other, p in procs.items() if other != rid):
        raise Conflict("Port %d belongs to another server's game process" % port)
    mine = procs.get(rid) or {}
    if not (mine.get("set_up") and mine.get("port") == port) and _port_open(port, "127.0.0.1", 0.5):
        raise Conflict("Something already listens on port %d on this machine; choose another" % port)
    if mode == "new":
        clean = validate_realm({"name": b.get("name"), "identifier": new_identifier(), "ip_address": address, "game_port": port,
                                "server_limit": limit, "display_order": max([r["display_order"] for r in rows.values()] + [0]) + 1,
                                "enabled": False, "recommended": False})
        values = dict(id=rid, **clean)
        zone = rows.get(1, {}).get("time_zone_id")          # event times follow realm 1's clock, not the column default (UTC)
        if zone:
            values["time_zone_id"] = zone
        out["row"] = ctx.rows_write([_realm_scope(rid)], "add server #%d %s (%s:%d), disabled until its game process runs" %
                                    (rid, clean["name"], address, port),
                                    lambda before: [common.insert_sql("server", values)], fields=["server.*"], advanced=True,
                                    guard_sql=[REALM_GUARD], applies="next_login", target="realm #%d" % rid)
    else:
        row = rows[rid]
        clean = validate_realm(dict({k: row[k] for k in ("identifier", "display_order", "enabled", "recommended")},
                                    name=b.get("name", row["name"]), ip_address=address, game_port=port,
                                    server_limit=limit if "server_limit" in b else row["server_limit"]))
        changes = {k: v for k, v in clean.items() if row.get(k) != v}
        if changes:
            out["row"] = ctx.rows_write([_realm_scope(rid)], "server #%d: %s" % (rid, ", ".join("%s=%s" % kv for kv in changes.items())),
                                        lambda before: [common.update_sql("server", {"id": rid}, changes, bump=False)],
                                        fields=_realm_fields(changes), advanced=True, guard_sql=[REALM_GUARD], applies="next_login",
                                        target="realm #%d" % rid,
                                        expected=[{"table": "server", "where": {"id": rid}, "values": {k: row.get(k) for k in changes}}])
    result = _send_realm_job(ctx, "realm_provision", rid, port)
    out["change"] = _record(ctx, "set up game process for server #%d on port %d" % (rid, port), result["job"],
                            "realm_provision realm=%d port=%d address=%s" % (rid, port, address), 0, {},
                            {"character": "godswar251-realm@%d.service" % rid,
                             "no_undo": "Use Close server (it stops the process and closes the port)."})
    out.update(job=result, realm_id=rid, port=port, address=address, name=clean["name"],
               knock={"host": address, "port": port, "url": "http://%s:%d/" % (address, port)})
    return out


def _realm_job_done(ctx, job_id, action, rid):
    if not isinstance(job_id, str) or not re.fullmatch(r"[a-f0-9]{32}", job_id):
        raise Bad("job is required")
    doc = W.server_job(ctx, job_id)
    if doc.get("action") != action or doc.get("realm_id") != rid:
        raise Bad("That job is not the %s of realm #%d" % (action.replace("realm_", ""), rid))
    if doc.get("state") == "running":
        raise Conflict("The job is still running; wait for it")
    if doc.get("state") != "succeeded":
        raise Refused("The job did not succeed: %s" % (doc.get("error") or doc.get("state")))
    return doc


@M.post("realm/provision/finish")
def realm_provision_finish(ctx):
    """POST {id, job, acknowledge_unreachable?}: show the server in the server list (enable its row), but only when the
    set-up job succeeded, its process answers, the main game shares login handoffs, and the port was seen from outside
    (or the admin confirms he opened the provider firewall and wants it listed anyway)."""
    W._need_writable(ctx)
    b = ctx.body or {}
    rid = W._int(b.get("id"), REALM_MIN, REALM_MAX, "server number")
    doc = _realm_job_done(ctx, b.get("job"), "realm_provision", rid)
    rows = {r["id"]: r for r in _realm_rows(ctx)}
    if rid not in rows:
        raise NotFound("No realm #%d" % rid)
    row = rows[rid]
    if row["game_port"] != doc.get("port"):
        raise Conflict("Realm #%d is listed on port %d but its process was set up on %s; set it up again" % (rid, row["game_port"], doc.get("port")))
    if row["enabled"]:
        return {"already": True, "realm": row}
    if not _port_open(row["game_port"], row["ip_address"], 2.0):
        raise Refused("Nothing answers at %s:%d from this machine; the process is not running. Read its log (%s)" %
                      (row["ip_address"], row["game_port"], (doc.get("realm") or {}).get("log") or "realm log"))
    handoff = _handoff(ctx)
    if not handoff["shared"]:
        raise Refused("Not listed yet: " + handoff["text"])
    outside = ((doc.get("realm") or {}).get("outside") or {}).get("result")
    if outside != "seen" and b.get("acknowledge_unreachable") is not True:
        raise Conflict("No connection from outside reached port %d during the test, so the provider firewall may still block "
                       "it. Players who pick this server would be disconnected. Open inbound TCP %d in the Hosting by Design "
                       "panel and press Test again, or confirm that you opened it and list the server anyway"
                       % (row["game_port"], row["game_port"]))
    res = ctx.rows_write([_realm_scope(rid)], "server #%d %s: shown in the server list (enabled)%s" %
                         (rid, row["name"], "" if outside == "seen" else "; outside test NOT passed, listed on the operator's confirmation"),
                         lambda before: [common.update_sql("server", {"id": rid}, {"enabled": True}, bump=False)],
                         fields=["server.enabled"], advanced=True, guard_sql=[REALM_GUARD], applies="next_login", target="realm #%d" % rid,
                         expected=[{"table": "server", "where": {"id": rid}, "values": {"enabled": False}}])
    res["outside"] = outside
    return res


@M.post("realm/close")
def realm_close(ctx):
    """POST {id, confirm: <name>, delete: bool}: 'Close server' (delete false) or the first half of 'Delete server'.
    Refused with anyone in the realm. Disables the row, then the root helper stops and removes the process and closes
    the port. Characters are database rows and are not touched; with delete = true the row itself is removed afterwards
    by realm/delete, which is refused while anything still belongs to the realm."""
    rb = W._need_writable(ctx)
    b = ctx.body or {}
    rid = W._int(b.get("id"), 1, 2147483647, "server number")
    if rid == 1:
        raise Refused(PROTECTED[1] + "; it cannot be closed or deleted")
    rows = {r["id"]: r for r in _realm_rows(ctx)}
    if rid not in rows:
        raise NotFound("No realm #%d" % rid)
    row = rows[rid]
    delete = b.get("delete") is True
    refs = references(rb, rid)
    if delete:
        if rid in PROTECTED:
            raise Refused(PROTECTED[rid] + ". Use Close server instead (the row stays, disabled)")
        if refs:
            raise Refused("Server #%d %s still has %s. %s" % (rid, row["name"], ", ".join(
                "%s %s" % (r["rows"] if r["rows"] is not None else "an unknown number of", r["table"].replace("public.", "")) for r in refs),
                CHARACTER_RULE))
    players, detail = realm_players(ctx, rid, row["game_port"])
    if players:
        raise Refused("%d player%s in server #%d now (%s). It can be closed only when it is empty: nobody is ever disconnected "
                      "by this page" % (players, "" if players == 1 else "s", rid,
                                        ", ".join("%s: %s" % (k.replace("_", " "), v) for k, v in detail.items() if v is not None)))
    if b.get("confirm") != row["name"]:
        raise Bad("Type the server name to confirm")
    out = {"realm_id": rid, "delete": delete, "characters": realm_characters(ctx, rid), "character_rule": CHARACTER_RULE}
    if row["enabled"]:
        out["row"] = ctx.rows_write([_realm_scope(rid)], "server #%d %s: hidden from the server list (disabled)" % (rid, row["name"]),
                                    lambda before: [common.update_sql("server", {"id": rid}, {"enabled": False, "recommended": False}, bump=False)],
                                    fields=["server.enabled", "server.recommended"], advanced=True, guard_sql=[REALM_GUARD],
                                    applies="next_login", target="realm #%d" % rid,
                                    expected=[{"table": "server", "where": {"id": rid}, "values": {"enabled": True}}])
    if rid > REALM_MAX or not W._control_ok(ctx):
        out["job"] = None           # nothing the helper could have set up (a copy database, or an id beyond its range)
        return out
    if delete and not row["enabled"] and not (realm_processes().get(rid) or {}).get("set_up"):
        out["job"] = None           # B21: a row that never had a game process (the Dwargon placeholder): nothing to take down
        return out
    result = _send_realm_job(ctx, "realm_remove", rid)
    out["job"] = result
    out["change"] = _record(ctx, "take down game process of server #%d" % rid, result["job"],
                            "realm_remove realm=%d; %d characters stay in the database" % (rid, out["characters"]), 0, detail,
                            {"character": "godswar251-realm@%d.service" % rid,
                             "no_undo": "Set the server up again (Add server -> use the existing row)."})
    return out


@M.get(r"realm/process/(\d{1,3})")
def realm_process(ctx, rid):
    """The set-up record of one realm while the job runs (the page watches for outside.result == 'listening')."""
    return realm_processes().get(int(rid)) or {"state": None}


# =============================================================== change log: export, then archive
def _change_files(app):
    return sorted(glob.glob(os.path.join(app.changes_dir, "change-*.json")))


def _keep(c):
    """Records that must stay: changes still waiting for a game restart (Home lists them)."""
    return c.get("applies") in ("after_restart", "startup") and not c.get("undone") and not c.get("undo_of")


def _load_all(app):
    out = []
    for f in _change_files(app):
        try:
            with open(f, encoding="utf-8") as fh:
                out.append((f, json.load(fh)))
        except (OSError, ValueError):
            out.append((f, None))
    return out


@M.get("changes/summary")
def changes_summary(ctx):
    items = _load_all(ctx.app)
    recs = [c for _, c in items if c]
    keep = [c for c in recs if _keep(c)]
    times = sorted(c.get("time") or "" for c in recs)
    return {"files": len(items), "records": len(recs), "in_memory": len(ctx.app.changes), "keep": len(keep),
            "keep_list": [{"id": c.get("id"), "action": c.get("action"), "time": c.get("time")} for c in keep][:50],
            "oldest": times[0] if times else None, "newest": times[-1] if times else None,
            "archives": sorted(os.path.basename(p) for p in glob.glob(os.path.join(ctx.app.changes_dir, "cleared-*")))}


@M.get("changes/export")
def changes_export(ctx):
    """Every change record on disk, complete (before/after images included), for download."""
    return {"exported_at": common.now_iso(), "database": ctx.rb.db if ctx.rb else None,
            "changes": [c for _, c in _load_all(ctx.app) if c]}


@M.post("changes/clear")
def changes_clear(ctx):
    b = ctx.body or {}
    if b.get("confirm") != "CLEAR":
        raise Bad("Type CLEAR to confirm")
    app = ctx.app
    items = _load_all(app)
    if b.get("records_seen") != len([c for _, c in items if c]):
        raise Conflict("The change log changed since you opened it; look again and confirm")
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d-%H%M%S")
    folder = os.path.join(app.changes_dir, "cleared-" + stamp)
    os.makedirs(folder, exist_ok=False)
    # 1. the export first (one file with everything), 2. then move the records; nothing is deleted
    export = os.path.join(folder, "export-%s.json" % stamp)
    with open(export, "w", encoding="utf-8") as f:
        json.dump({"exported_at": common.now_iso(), "database": ctx.rb.db if ctx.rb else None,
                   "changes": [c for _, c in items if c]}, f, ensure_ascii=False, default=str)
    moved, kept = 0, []
    for path, c in items:
        if c and _keep(c):
            kept.append(c.get("id"))
            continue
        shutil.move(path, os.path.join(folder, os.path.basename(path)))
        moved += 1
    keep_ids = set(kept)
    app.changes[:] = [c for c in app.changes if c.get("id") in keep_ids]
    actor = getattr(common.ACTOR, "name", None) or "unknown editor session"
    rec = {"engine": "operation", "id": "op-clear-" + stamp, "time": common.now_iso(), "database": ctx.rb.db if ctx.rb else None,
           "action": "change log cleared: %d records archived" % moved, "character": "change log", "cid": None, "sql": [],
           "backup": export, "diff": {}, "verify": {}, "output": "archived to %s by %s; %d kept (waiting for a restart)" % (folder, actor, len(kept)),
           "module": "operations", "undone": False, "no_undo": "The records are in %s; copy them back into web-changes and restart the editor to restore them." % folder}
    app.changes.append(rec)
    app._save_change(rec)
    return {"archived": moved, "kept": len(kept), "folder": folder, "export": export, "change": common.public_change(rec)}
