#!/usr/bin/env python3
"""Signed-in smoke test of the editor (gwadmin-web) on a scratch database. POLISH-REVIEW #16.

    python3 -I editor/tools/smoke.py                      # newest ~/GodsWar-Backups/vps/*.sql.gz -> scratch DB on the rig
    python3 -I editor/tools/smoke.py --db godswar_edsmoke_20261122 --keep-db   # reuse a scratch copy you made

What it does, in order:
  1. makes a scratch PostgreSQL database (gw_edsmoke_<stamp>) on the release-rig cluster (127.0.0.1:55496) from a
     Mac backup of live (plain pg_dump .sql.gz), or reuses --db. The only restore errors allowed are the ones the
     rig always gives (`role "postgres" does not exist`).
  2. adds a TEST editor administrator to that scratch database only (random name and password, never printed,
     never written to git; removed again at the end when the database is kept).
  3. starts the editor from this checkout like the live unit does (--account-auth --reborn-only --no-persistent,
     psql as the live role gweditor through PGOPTIONS, an HTTPS public origin) on 127.0.0.1:<port>, with
     backups, audit log, sessions and graphics output in a scratch folder. The live layout (tools/gwadmin.py beside
     tools/gwadmin-web) is rebuilt in that folder with symlinks; gwadmin.py comes from --gwadmin-dir.
  4. checks that an unsigned request is refused, signs in (the only POST), then GETs the shell, every module's
     js/css, every GET route of every module in the module registry (/api/m/modules) and the core JSON endpoints.
     Route parameters are filled from earlier answers (a real character id, guild id, quest id, ...); a route whose
     parameter cannot be found, or whose GET writes files, is listed as SKIP with the reason.
  5. fails on any 4xx/5xx, an empty body, a Python traceback in a body or in the editor's log, or a module that
     failed to import. Prints a table: route, status, time, ok.

It never clicks Save/Apply and never POSTs anything but the sign-in. Exit code 0 = all ok, 1 = failures, 2 = setup.
python3 standard library only. Needs psql/createdb/dropdb (Homebrew PostgreSQL 17) and gzip on PATH.
"""
import argparse
import base64
import datetime
import glob
import hashlib
import http.client
import json
import os
import re
import secrets
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
EDITOR = os.path.dirname(HERE)
REPO = os.path.dirname(EDITOR)
DB_PREFIX = "gw_edsmoke_"
PROTECTED = {"godswar", "postgres", "template0", "template1", "godswar_live_copy", "godswar_release", "godswar_editor_base"}
ORIGIN = "https://editor-smoke.invalid"
COOKIE = "__Host-gweditor"
TRACEBACK = re.compile(r"Traceback \(most recent call last\)")
# rig noise the restore may print; anything else is a failed restore
RESTORE_OK_ERRORS = (re.compile(r'role "postgres" does not exist'),)

# Answers that only say the scratch set-up has no game server (management port) or systemd: shown as RIG, not failed.
# Pass --mgmt PORT (a rig game server on the same database) to check those routes for real.
RIG_ONLY = re.compile(r"no game server management port is configured|needs the compatible title-grant update|"
                      r"does not support teleporter menus yet|systemctl unavailable")
# GET routes that need a query string to answer; filled from earlier answers (see Smoke.query)
DEFERRED = {("graphics", "model"), ("graphics", "still"), ("graphics", "mapimage"), ("graphics", "texture"),
            ("graphics", "atlas"), ("graphics", "effect")}
# parameter routes whose value only a game-server answer gives: (module, pattern) -> the answer it comes from
RIG_PARAM = {("npcs", r"teleport/menu/([A-Za-z0-9_]+)"): "npcs overview"}
# GET routes that are not plain reads, or need a value no answer gives: (module, pattern) -> reason
SKIP = {
    ("graphics", "convert"): "writes converted files into GW_GRAPHICS_OUT (a cache write)",
    ("world", r"server/job/([a-f0-9]{32})"): "needs a running server-control job id",
    ("operations", r"job/([a-f0-9]{32})"): "needs a running operations job id",
}


# ------------------------------------------------------------------------------------------------ helpers
def find_gwadmin_dir():
    """The folder holding gwadmin.py (the editor imports ../gwadmin.py; it is not in git)."""
    env = os.environ.get("GWADMIN_TOOLS")
    cands = [env] if env else []
    cands.append(os.path.dirname(EDITOR))
    d = REPO
    for _ in range(8):                     # .../Godswar/GodsWar-VPS/tools, from the repo or any worktree inside it
        cands.append(os.path.join(d, "GodsWar-VPS", "tools"))
        d = os.path.dirname(d)
    for c in cands:
        if c and os.path.isfile(os.path.join(c, "gwadmin.py")):
            return c
    return None


def find_graphics_out():
    """The project's prebuilt graphics folder (Development/w4/graphics-out), like GW_GRAPHICS_OUT on live. Read only:
    the one GET that writes there (graphics/convert) is skipped."""
    d = REPO
    for _ in range(8):
        c = os.path.join(d, "Development", "w4", "graphics-out")
        if os.path.isfile(os.path.join(c, "web", "index.json")):
            return c
        d = os.path.dirname(d)
    return None


def newest_backup():
    files = sorted(glob.glob(os.path.expanduser("~/GodsWar-Backups/vps/godswar251-*.sql.gz")))
    return files[-1] if files else None


def password_record(password, iterations=100_000):
    """The server's verifier format (editor_auth.verify_password): PBKDF2-SHA256 over the md5 hex of the password."""
    salt = secrets.token_bytes(16)
    legacy = hashlib.md5(password.encode("ascii")).hexdigest().encode("ascii")
    dk = hashlib.pbkdf2_hmac("sha256", legacy, salt, iterations, 32)
    return "gws$pbkdf2-sha256$v1$%d$%s$%s" % (iterations, base64.b64encode(salt).decode(), base64.b64encode(dk).decode())


class PG:
    def __init__(self, a):
        self.base = ["-h", a.pg_host, "-p", str(a.pg_port), "-U", a.pg_user]

    def run(self, tool, *args, **kw):
        return subprocess.run([tool] + self.base + list(args), capture_output=True, text=True, **kw)

    def sql(self, db, sql):
        p = self.run("psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-d", db, "-c", sql)
        if p.returncode:
            raise RuntimeError("psql: %s" % p.stderr.strip()[:400])
        return p.stdout.strip()


def lit(s):
    return "'" + str(s).replace("'", "''") + "'"


def first(obj, keys, pred=None, depth=0):
    """First value of any of `keys` in a JSON answer (depth-first), optionally matching pred."""
    if depth > 6:
        return None
    if isinstance(obj, dict):
        for k in keys:
            if k in obj and obj[k] not in (None, "", [], {}) and not isinstance(obj[k], (dict, list)) and (pred is None or pred(obj[k])):
                return obj[k]
        for v in obj.values():
            r = first(v, keys, pred, depth + 1)
            if r is not None:
                return r
    elif isinstance(obj, list):
        for v in obj[:200]:
            r = first(v, keys, pred, depth + 1)
            if r is not None:
                return r
    return None


def isint(v):
    return isinstance(v, int) and not isinstance(v, bool) or (isinstance(v, str) and v.isdigit())


# ------------------------------------------------------------------------------------------------ HTTP
class Client:
    def __init__(self, port, timeout):
        self.port, self.timeout, self.cookie = port, timeout, None

    def request(self, method, path, body=None, headers=None, auth=True):
        h = {"Host": "127.0.0.1:%d" % self.port}
        if auth and self.cookie:
            h["Cookie"] = "%s=%s" % (COOKIE, self.cookie)
        h.update(headers or {})
        c = http.client.HTTPConnection("127.0.0.1", self.port, timeout=self.timeout)   # never follows redirects
        t = time.monotonic()
        try:
            c.request(method, path, body=body, headers=h)
            r = c.getresponse()
            data = r.read()
            return r.status, dict((k.lower(), v) for k, v in r.getheaders()), data, time.monotonic() - t
        finally:
            c.close()

    def get(self, path, auth=True):
        return self.request("GET", path, auth=auth)


# ------------------------------------------------------------------------------------------------ the run
class Smoke:
    def __init__(self, a):
        self.a = a
        self.pg = PG(a)
        self.rows = []           # (route, status, seconds, ok, note)
        self.created_db = None
        self.admin = None
        self.proc = None
        self.work = a.workdir or tempfile.mkdtemp(prefix="gw-edsmoke-")
        os.makedirs(self.work, exist_ok=True)
        self.log_path = os.path.join(self.work, "editor.log")
        self.answers = {}        # "module pattern" -> parsed JSON (for parameter values)

    # -------------------------------------------------------------- setup
    def make_db(self):
        a = self.a
        if a.db:
            if a.db in PROTECTED:
                raise SystemExit("refusing to use %s: not a scratch copy" % a.db)
            if self.pg.run("psql", "-X", "-At", "-d", a.db, "-c", "SELECT 1").returncode:
                raise SystemExit("cannot reach database %s" % a.db)
            return a.db
        dump = a.dump or newest_backup()
        if not dump or not os.path.isfile(dump):
            raise SystemExit("no backup found (pass --dump PATH.sql.gz)")
        name = DB_PREFIX + datetime.datetime.now().strftime("%Y%m%d_%H%M%S") + "_%d" % os.getpid()
        p = self.pg.run("createdb", name)
        if p.returncode:
            raise SystemExit("createdb %s: %s" % (name, p.stderr.strip()))
        self.created_db = name
        print("restoring %s into %s ..." % (os.path.basename(dump), name), flush=True)
        t = time.monotonic()
        with open(os.path.join(self.work, "restore.err"), "w") as err:
            gz = subprocess.Popen(["gzip", "-dc", dump], stdout=subprocess.PIPE)
            ps = subprocess.run(["psql"] + self.pg.base + ["-X", "-q", "-d", name, "-v", "ON_ERROR_STOP=0"],
                                stdin=gz.stdout, stdout=subprocess.DEVNULL, stderr=err)
            gz.stdout.close()
            gz.wait()
        errs = [l for l in open(os.path.join(self.work, "restore.err")) if "ERROR:" in l]
        bad = [l for l in errs if not any(rx.search(l) for rx in RESTORE_OK_ERRORS)]
        if gz.returncode or ps.returncode or bad:
            raise SystemExit("restore failed (%d unexpected errors, see %s/restore.err): %s" % (
                len(bad), self.work, (bad[:1] or ["exit %s/%s" % (gz.returncode, ps.returncode)])[0].strip()))
        print("restored in %.0f s (%d expected rig errors)" % (time.monotonic() - t, len(errs)), flush=True)
        return name

    def add_admin(self, db):
        user = "smoke" + secrets.token_hex(4)
        pw = secrets.token_urlsafe(18)[:24]
        rec = password_record(pw)
        aid = int(self.pg.sql(db, "SELECT coalesce(max(id),0)+1 FROM accounts"))
        self.pg.sql(db, "INSERT INTO accounts (id, username, password, status, editor_admin) VALUES (%d, %s, %s, 0, true)" % (
            aid, lit(user), lit(rec)))
        self.admin = (db, aid, user, pw)

    def drop_admin(self):
        if self.admin:
            db, aid, user, _ = self.admin
            try:
                self.pg.sql(db, "DELETE FROM accounts WHERE id = %d AND username = %s" % (aid, lit(user)))
            except RuntimeError as e:
                print("note: test admin not removed: %s" % e, file=sys.stderr)

    def start_editor(self, db):
        a = self.a
        gwadmin = a.gwadmin_dir or find_gwadmin_dir()
        if not gwadmin or not os.path.isfile(os.path.join(gwadmin, "gwadmin.py")):
            raise SystemExit("gwadmin.py not found: pass --gwadmin-dir (the folder of GodsWar-VPS/tools/gwadmin.py)")
        tools = os.path.join(self.work, "tools")
        os.makedirs(tools, exist_ok=True)
        for name, target in (("gwadmin.py", os.path.join(gwadmin, "gwadmin.py")), ("catalog", os.path.join(gwadmin, "catalog")),
                             ("gwadmin-web", os.path.abspath(a.editor_dir))):
            link = os.path.join(tools, name)
            if os.path.lexists(link):
                os.unlink(link)          # our own symlink in our own scratch folder
            if os.path.exists(target):
                os.symlink(target, link)
        for d in ("backups", "graphics-out"):
            os.makedirs(os.path.join(self.work, d), exist_ok=True)
        token = os.path.join(self.work, "bootstrap-token")
        with open(os.open(token, os.O_CREAT | os.O_WRONLY | os.O_TRUNC, 0o600), "w") as f:
            f.write(secrets.token_urlsafe(48))
        psql = "psql -h %s -p %d -U %s" % (a.pg_host, a.pg_port, a.pg_user)
        cmd = [sys.executable, "-I", os.path.join(tools, "gwadmin-web", "server.py"),
               "--account-auth", "--bootstrap-token-file", token, "--reborn-only", "--no-persistent", "--db-suffix", "_test",
               "--reborn-db", db, "--reborn-psql", psql, "--port", str(a.port), "--public-origin", ORIGIN,
               "--backup-dir", os.path.join(self.work, "backups"), "--audit-log", os.path.join(self.work, "audit.log")]
        if a.mgmt:
            cmd += ["--rates-mgmt", str(a.mgmt)]
        self.gfx = a.graphics_out or find_graphics_out() or os.path.join(self.work, "graphics-out")
        env = dict(os.environ, GW_GRAPHICS_OUT=self.gfx)
        if a.role:
            env["PGOPTIONS"] = "-c role=%s" % a.role
        self.log = open(self.log_path, "w")
        self.proc = subprocess.Popen(cmd, cwd=self.work, stdout=self.log, stderr=subprocess.STDOUT, env=env,
                                     start_new_session=True)
        c = Client(a.port, 5)
        deadline = time.monotonic() + a.start_timeout
        while time.monotonic() < deadline:
            if self.proc.poll() is not None:
                raise SystemExit("the editor exited (%s) at start; log: %s\n%s" % (self.proc.returncode, self.log_path, self.tail()))
            try:
                if c.get("/login", auth=False)[0] == 200:
                    return
            except OSError:
                pass
            time.sleep(0.5)
        raise SystemExit("the editor did not answer on port %d within %d s; log: %s" % (a.port, a.start_timeout, self.log_path))

    def tail(self, n=30):
        try:
            return "".join(open(self.log_path, errors="replace").readlines()[-n:])
        except OSError:
            return ""

    def stop(self):
        if self.proc and self.proc.poll() is None:
            os.killpg(self.proc.pid, signal.SIGTERM)
            try:
                self.proc.wait(10)
            except subprocess.TimeoutExpired:
                os.killpg(self.proc.pid, signal.SIGKILL)
        if not (self.created_db and not self.a.keep_db):
            self.drop_admin()                              # a kept or reused database loses the test admin again
        self.admin = None
        if self.created_db and not self.a.keep_db:
            assert self.created_db.startswith(DB_PREFIX) and self.created_db not in PROTECTED
            p = self.pg.run("dropdb", self.created_db)     # only the database this run created
            if p.returncode:
                print("note: dropdb %s failed: %s" % (self.created_db, p.stderr.strip()), file=sys.stderr)
        elif self.created_db:
            print("kept scratch database %s (drop it with: dropdb -h %s -p %d %s)" % (
                self.created_db, self.a.pg_host, self.a.pg_port, self.created_db))

    # -------------------------------------------------------------- checks
    def rig_only(self, note):
        """A 4xx that only says the copy has no game server. With --mgmt there is one, so it is a real failure."""
        return not self.a.mgmt and bool(RIG_ONLY.search(note or ""))

    def db_writes(self, db):
        """Rows inserted/updated/deleted in the scratch database so far (pg_stat_user_tables)."""
        return int(self.pg.sql(db, "SELECT coalesce(sum(n_tup_ins + n_tup_upd + n_tup_del), 0) FROM pg_stat_user_tables"))

    def record(self, route, status, secs, ok, note=""):
        self.rows.append((route, status, secs, ok, note))

    def check(self, c, path, label=None, want=200, json_body=None, key=None):
        label = label or path
        try:
            st, hdr, body, secs = c.get(path)
        except OSError as e:
            self.record(label, "ERR", 0.0, False, "%s: %s" % (type(e).__name__, e))
            return None
        note, ok = "", st == want
        text = body.decode("utf-8", "replace")
        if not body:
            ok, note = False, "empty body"
        elif TRACEBACK.search(text):
            ok, note = False, "Python traceback in the body"
        elif st >= 400:
            try:
                note = str(json.loads(text).get("error"))[:160]
            except (ValueError, AttributeError):
                note = text[:160].replace("\n", " ")
            if st < 500 and self.rig_only(note):
                st, ok, note = "RIG", True, "%s (HTTP %d)" % (note, st)
        doc = None
        ctype = hdr.get("content-type", "")
        if ok and (json_body or ctype.startswith("application/json")):
            try:
                doc = json.loads(text)
            except ValueError:
                ok, note = False, "body is not JSON"
        if ok and st == 200 and isinstance(doc, dict) and doc.get("error"):
            note = "200 with error: %s" % str(doc["error"])[:140]       # shown, not failed: many pages explain rig gaps
        self.record(label, st, secs, ok, note)
        if key and doc is not None and st == 200:
            self.answers[key] = doc
        return doc

    def run(self):
        a = self.a
        db = self.make_db()
        self.add_admin(db)
        self.start_editor(db)
        c = self.client = Client(a.port, a.timeout)
        time.sleep(1.5)                     # let the start-up and test-admin statistics reach pg_stat
        writes0 = self.db_writes(db)

        # unsigned requests are refused
        st, hdr, _, secs = c.get("/api/meta", auth=False)
        self.record("unsigned GET /api/meta", st, secs, st == 401, "" if st == 401 else "expected 401")
        st, hdr, _, secs = c.get("/", auth=False)
        loc = hdr.get("location", "")
        self.record("unsigned GET /", st, secs, st == 303 and loc.startswith("/login"), "-> " + loc[:40])
        self.check(c, "/login", "GET /login (page)")

        # sign in: the only POST
        _, _, user, pw = self.admin
        st, hdr, _, secs = c.request("POST", "/auth/login", body=json.dumps({"username": user, "password": pw}),
                                     headers={"Origin": ORIGIN, "Content-Type": "application/json"}, auth=False)
        m = re.search(r"%s=([^;]+)" % re.escape(COOKIE), hdr.get("set-cookie", ""))
        self.record("POST /auth/login (test admin)", st, secs, st == 303 and bool(m), "" if m else "no session cookie")
        if not m:
            return
        c.cookie = m.group(1)
        sess = self.check(c, "/auth/session", "GET /auth/session")
        if sess and sess.get("username") != user:
            self.rows[-1] = self.rows[-1][:3] + (False, "session is not the test admin")
        st, _, body, secs = c.get("/api/reborn/status")       # the editor answering is the one this run started
        try:
            seen = json.loads(body).get("db") or (json.loads(body).get("identity") or {}).get("db")
        except (ValueError, AttributeError):
            seen = None
        self.record("editor is on the scratch database", st, secs, seen == db, "" if seen == db else "reborn status names %r" % seen)
        if seen != db:
            return

        # pages and core endpoints
        for path in ("/", "/static/shell.js", "/static/shell.css", "/static/session.js", "/static/progress.html",
                     "/static/build-analysis.html", "/static/content-audit.html", "/static/avatars.html"):
            if path.startswith("/static/") and not os.path.exists(os.path.join(a.editor_dir, "static", path[8:])):
                continue
            self.check(c, path)
        for path in ("/api/meta", "/api/m/status", "/api/m/capabilities", "/api/m/catalog", "/api/m/catalog/labels",
                     "/api/m/glossary", "/api/m/pending", "/api/m/changes", "/api/changes", "/api/reborn/status",
                     "/api/reborn/catalog/maps", "/api/reborn/catalog/attributes", "/api/rates"):
            self.check(c, path, json_body=True)
        reg = self.check(c, "/api/m/modules", "GET /api/m/modules (registry)", json_body=True)
        if not reg:
            return
        if reg.get("load_errors"):
            for name, err in sorted(reg["load_errors"].items()):
                self.record("module %s import" % name, "-", 0.0, False, err[:160])
        chars = self.check(c, "/api/m/characters", json_body=True) or []
        pick = max(chars, key=lambda r: (r.get("pets") or 0, r.get("level") or 0), default=None) if isinstance(chars, list) else None
        cid = pick["id"] if pick else None
        aid = pick["account_id"] if pick else None
        if cid is not None:
            self.check(c, "/api/reborn/character/%d" % cid, "/api/reborn/character/<cid>", json_body=True)
            self.check(c, "/api/m/online/%d" % cid, "/api/m/online/<cid>", json_body=True)
        if a.selftest:
            self.check(c, "/api/reborn/selftest", json_body=True)

        mods = sorted(reg.get("modules", []), key=lambda m: (m.get("order", 100), m["name"]))
        if a.only:
            mods = [m for m in mods if m["name"] in a.only]
        for mod in mods:
            for asset in ("js", "css"):
                if mod.get(asset):
                    self.check(c, mod[asset])
        # plain routes first (their answers give the parameter values), then the ones with parameters
        routes = [(mod["name"], r.split(" ", 1)[1]) for mod in mods for r in mod.get("routes", []) if r.startswith("GET ")]
        plain = [r for r in routes if "(" not in r[1]]
        param = [r for r in routes if "(" in r[1]]
        plain = [r for r in plain if r not in DEFERRED] + [r for r in plain if r in DEFERRED]
        for name, pat in plain:
            if (name, pat) in SKIP:
                self.record("/api/m/%s/%s" % (name, pat), "SKIP", 0.0, True, SKIP[(name, pat)])
                continue
            q = self.query(name, pat)
            if q is None:
                self.record("/api/m/%s/%s" % (name, pat), "SKIP", 0.0, True, "no value for the query string in the scratch data")
                continue
            self.check(c, "/api/m/%s/%s%s" % (name, pat, q), key="%s %s" % (name, pat))
        # parameter routes whose value comes from another parameter route's answer go last
        param = [r for r in param if r != ("gearstats", r"options/(\d+)")] + [r for r in param if r == ("gearstats", r"options/(\d+)")]
        for name, pat in param:
            label = "/api/m/%s/%s" % (name, pat)
            if (name, pat) in SKIP:
                self.record(label, "SKIP", 0.0, True, SKIP[(name, pat)])
                continue
            path = self.fill(name, pat, cid, aid)
            if path is None and not a.mgmt and RIG_PARAM.get((name, pat)) and RIG_PARAM[(name, pat)] not in self.answers:
                self.record(label, "RIG", 0.0, True, "its value comes from /api/m/%s, which needs the game server" %
                            RIG_PARAM[(name, pat)].replace(" ", "/"))
                continue
            if path is None:
                self.record(label, "SKIP", 0.0, True, "no value for the route parameter in the scratch data")
                continue
            self.check(c, "/api/m/%s/%s" % (name, path), label=label + "  [" + path + "]", key="%s %s" % (name, pat))

        # nothing was written: the database, the change log and the audit log
        time.sleep(1.5)
        delta = self.db_writes(db) - writes0
        self.record("database rows written during the walk", delta, 0.0, delta == 0,
                    "" if delta == 0 else "a GET wrote rows (pg_stat_user_tables)")
        changes = os.path.join(self.work, "backups", "web-changes")
        n_changes = len(os.listdir(changes)) if os.path.isdir(changes) else 0
        audit = os.path.join(self.work, "audit.log")
        n_audit = os.path.getsize(audit) if os.path.exists(audit) else 0
        self.record("editor change log and audit log", n_changes + n_audit, 0.0, n_changes == 0 and n_audit == 0,
                    "" if not (n_changes or n_audit) else "%d change files, audit log %d bytes" % (n_changes, n_audit))

        # what the editor printed: a caught exception still prints its traceback
        time.sleep(0.5)
        log = open(self.log_path, errors="replace").read()
        n = len(TRACEBACK.findall(log))
        self.record("editor log: tracebacks", n, 0.0, n == 0, "see %s" % self.log_path if n else "")

    # -------------------------------------------------------------- route parameters
    def query(self, name, pat):
        """Query strings for routes that need one to answer (a read-only lookup)."""
        if name == "library" and pat == "search":
            return "?q=sword"
        if name == "account_tools" and pat == "search":
            return "?q=a"
        if name == "gm_powers" and pat == "find":
            return "?q=a"
        ans = self.answers
        enc = lambda **kw: "?" + urllib.parse.urlencode(kw)
        if name == "graphics" and pat in ("model", "still"):
            for kind in ("monster", "npc", "pet", "ride"):
                d = os.path.join(self.gfx, "models", kind)
                glbs = sorted(f for f in os.listdir(d) if f.endswith(".glb")) if os.path.isdir(d) else []
                for f in glbs:
                    if pat == "model" or os.path.isfile(os.path.join(self.gfx, "stills", kind, f[:-4] + ".png")):
                        return enc(glb="%s/%s" % (kind, f))
            return None
        if name == "graphics" and pat == "mapimage":
            d = os.path.join(self.gfx, "maps")
            ids = sorted(int(f.split("-")[0]) for f in os.listdir(d) if re.match(r"^\d+-terrain\.png$", f)) if os.path.isdir(d) else []
            return enc(id=ids[0], kind="terrain") if ids else None
        if name == "graphics" and pat == "effect":
            v = first(ans.get("graphics effects"), ("path",), lambda s: isinstance(s, str) and s.lower().endswith(".gwm"))
            return enc(path=v) if v else None
        if name == "graphics" and pat in ("texture", "atlas"):
            web = os.path.join(self.gfx, "web")      # the prebuilt index the Textures tab lists
            try:
                doc = json.load(open(os.path.join(web, "pictures.json" if pat == "texture" else "atlas.json")))
            except (OSError, ValueError):
                return None
            if pat == "atlas":
                names = sorted((doc.get("ui") or {}))
                return enc(path="UI/" + names[0]) if names else None
            for rel, rec in sorted(doc.items()):
                if not rel.lower().endswith(".gwm") and (rec or {}).get("files"):
                    return enc(path=rel)
            return None
        return ""

    def fill(self, name, pat, cid, aid):
        ans = self.answers
        groups = re.findall(r"\(([^()]*)\)", pat)
        vals = []
        for i, g in enumerate(groups):
            v = None
            prefix = pat[:pat.index("(" + g + ")")]
            if g.startswith(r"\d"):
                if name == "accounts" or "account/" in prefix:
                    v = aid
                elif name == "guild":
                    v = first(ans.get("guild overview"), ("guild_id", "id", "consortia_id"), isint)
                elif name == "world" and prefix == "quest/":
                    v = first(ans.get("world quests"), ("quest_id", "id"), isint)
                elif name == "world" and prefix == "gm/map/":
                    v = first(ans.get("world gm/maps"), ("map", "map_id", "id"), isint)
                elif name == "world" and prefix == "npc/templates/":
                    v = first(ans.get("world npc"), ("template_id", "template", "npc_id", "id"), isint)
                elif name == "login_awards" and prefix.startswith("award/"):
                    v = first(ans.get("login_awards overview"), ("award_id", "id"), isint)
                elif name == "gearstats" and prefix == "options/":
                    v = first(ans.get(r"inventory state/(\d+)"), ("prop_id", "item_id"), isint)
                elif name == "operations" and prefix.startswith("realm/process/"):
                    v = first(ans.get("operations realms"), ("id",), isint)
                elif name == "character" and prefix == "map/":
                    v = first(ans.get("curves maps"), ("map", "id"), isint)
                else:
                    v = cid
            elif name == "library" and g == "[a-z_]+":
                tabs = [t for t in ((ans.get("library tabs") or {}).get("tabs") or []) if t.get("type") and t.get("count")]
                v = tabs[0]["type"] if tabs else None
                hit = None
                if v:
                    try:                     # the catalogue index the Library page lists (read-only)
                        st, _, body, _ = self.client.get("/api/m/catalog/" + v)
                        rows = json.loads(body).get("rows") or [] if st == 200 else []
                        hit = rows[0][0] if rows else None
                    except (OSError, ValueError, IndexError, TypeError):
                        hit = None
                vals += [v, hit]
                break
            elif name == "npcs" and prefix.startswith("teleport/menu/"):
                v = first(ans.get("npcs overview"), ("menu", "teleport_menu", "key"), lambda s: isinstance(s, str) and re.match(r"^[A-Za-z0-9_]+$", s))
            elif name == "npc_dialogue" and prefix.startswith("revision/"):
                v = first(ans.get("npc_dialogue overview"), ("revision", "sha256", "hash"), lambda s: isinstance(s, str) and re.match(r"^[0-9A-F]{64}$", s))
            elif name == "world" and prefix.startswith("loot/monster/"):
                v = first(ans.get("world loot"), ("monster", "monster_id", "key", "id"), lambda s: re.match(r"^[A-Za-z0-9_\-]{1,64}$", str(s)))
            vals.append(v)
        if any(v is None for v in vals):
            return None
        it = iter(str(v) for v in vals)
        return re.sub(r"\((?:[^()]*)\)", lambda _: urllib.parse.quote(next(it), safe=""), pat)

    # -------------------------------------------------------------- report
    def report(self):
        w = max([len(r[0]) for r in self.rows] + [5])
        w = min(w, 96)
        print("\n%-*s  %6s  %7s  %s" % (w, "route", "status", "time", "ok"))
        print("-" * (w + 24))
        for route, st, secs, ok, note in self.rows:
            print("%-*s  %6s  %6.2fs  %s%s" % (w, route[:w], st, secs, "ok" if ok else "FAIL", ("  " + note) if note else ""))
        fails = [r for r in self.rows if not r[3]]
        skips = [r for r in self.rows if r[1] == "SKIP"]
        rig = [r for r in self.rows if r[1] == "RIG"]
        print("-" * (w + 24))
        print("%d checks: %d ok, %d failed, %d skipped, %d rig-only (need a game server: --mgmt). Editor log: %s" % (
            len(self.rows), len(self.rows) - len(fails) - len(skips) - len(rig), len(fails), len(skips), len(rig), self.log_path))
        if self.a.json:
            with open(self.a.json, "w") as f:
                json.dump([{"route": r[0], "status": r[1], "seconds": round(r[2], 3), "ok": r[3], "note": r[4]} for r in self.rows],
                          f, indent=1)
        return 1 if fails else 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dump", help="plain pg_dump .sql.gz to restore (default: newest ~/GodsWar-Backups/vps/godswar251-*.sql.gz)")
    p.add_argument("--db", help="reuse this scratch database instead of restoring (never a live/template name)")
    p.add_argument("--keep-db", action="store_true", help="keep the database this run restored")
    p.add_argument("--pg-host", default="127.0.0.1")
    p.add_argument("--pg-port", type=int, default=55496, help="PostgreSQL port (default: the release rig 55496)")
    p.add_argument("--pg-user", default=os.environ.get("USER") or "postgres")
    p.add_argument("--role", default="gweditor", help="SET ROLE for the editor's psql, like live (default gweditor; '' = login role)")
    p.add_argument("--port", type=int, default=18790, help="editor HTTP port on 127.0.0.1 (>= 10000)")
    p.add_argument("--editor-dir", default=EDITOR, help="the editor tree to start (default: this checkout's editor/)")
    p.add_argument("--gwadmin-dir", help="folder with gwadmin.py and catalog/ (default: $GWADMIN_TOOLS or GodsWar-VPS/tools)")
    p.add_argument("--mgmt", type=int, help="management port of a rig game server on the same database (--rates-mgmt); "
                   "without it the routes that need the game server show as RIG")
    p.add_argument("--graphics-out", help="GW_GRAPHICS_OUT for the editor (default: the project's Development/w4/graphics-out)")
    p.add_argument("--workdir", help="scratch folder for logs, backups, sessions (default: a new temp folder)")
    p.add_argument("--timeout", type=float, default=120, help="seconds per request")
    p.add_argument("--start-timeout", type=float, default=90)
    p.add_argument("--selftest", action="store_true", help="also GET /api/reborn/selftest (read-only, slow)")
    p.add_argument("--only", nargs="*", help="only these modules")
    p.add_argument("--json", help="also write the table as JSON here")
    a = p.parse_args(argv)
    if a.port < 10000:
        p.error("--port must be 10000 or higher")
    s = Smoke(a)
    try:
        s.run()
    except SystemExit as e:
        s.stop()
        print("smoke setup failed: %s" % e, file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        s.stop()
        return 2
    s.stop()
    s.drop_admin()
    return s.report()


if __name__ == "__main__":
    sys.exit(main())
