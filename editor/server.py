#!/usr/bin/env python3
"""gwadmin-web/server.py - local admin web UI for GodsWar 1.0 characters.

A character sheet laid out like the game's own windows (equipment paperdoll,
bag pages, warehouse, skills, quests) in which everything is editable, on top
of ../gwadmin.py: every decode/encode of the packed character_kitbag /
user_magic / user_skill / user_quest strings is gwadmin's, every write goes
through gwadmin's own command functions or codecs, and every write refuses
when the character looks online (gwadmin.ensure_offline).

    python3 -I server.py --db-suffix _test --port 8787          # test DBs
    python3 -I server.py --live --port 8787                      # LIVE (red banner)

python3 standard library only (http.server + threading), drives the
mariadb/mysql command line client exactly like gwadmin/gw.  Binds 127.0.0.1
only.  Reach it from the Mac with `ssh -L 8787:127.0.0.1:8787 vps` (server on
the VPS) or run it on the Mac with `ssh -L 3306:127.0.0.1:3306 vps`.

Every write: JSON dump of the character's 7 rows BEFORE the write (backup
dir), the exact SQL sent, a re-read + re-decode + re-encode check AFTER, and
an UNDO that restores the before-rows column by column and proves the result
is byte-identical.  /selftest decodes + re-encodes every character in the DB.
"""
import argparse
import datetime
import glob
import io
import json
import os
import queue
import re
import secrets
import signal
import subprocess
import sys
import tempfile
import threading
import traceback
import urllib.parse
from contextlib import redirect_stdout
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.dirname(HERE)
sys.path.insert(0, TOOLS)
sys.path.insert(0, HERE)          # python3 -I drops the script directory from sys.path
import editor_auth            # noqa: E402
import gwadmin as gw          # noqa: E402  (../gwadmin.py, unchanged)
try:
    from reborn import RebornError   # noqa: E402
except Exception:                     # pragma: no cover
    class RebornError(Exception):
        pass

try:
    import reborn as reborn_mod   # noqa: E402  (optional 2.51 adapter)
except Exception:                  # pragma: no cover
    reborn_mod = None
try:
    import rates as rates_mod     # noqa: E402  (Server rates page, Polish RATES patch)
except Exception:                  # pragma: no cover
    rates_mod = None
try:
    import modules as gwmods      # noqa: E402  (module editors under /api/m/..., MODULES.md)
except Exception:                  # pragma: no cover
    traceback.print_exc()
    gwmods = None

VERSION = "1.0"
# bag window, GodsWar-1.0/Client/UI/XML/ItemUI.xml: Money label "Silver", Bijou (Stone) label "Gold"
CURRENCY = {"Money": "Silver", "Stone": "Gold"}
# equipment paperdoll, ItemUI.xml Head/Amulet/... Rectangle x,y -> column (42,123,204) / row (73,115,157,199,241)
PAPERDOLL = [
    [None, "Head", "Amulet"],
    ["Cuff", "Armor", "Ring1"],
    ["Glove", "Girdle", "Ring2"],
    [None, "Leggins", None],
    ["MainHand", "Shoes", "OffHand"],
]
# empty-slot silhouettes (ItemUI.xml BtnTopPos in icon.gwo)
SLOT_ICON = {"Head": "0,432", "Amulet": "0,684", "Glove": "0,576", "Armor": "0,360", "Cuff": "0,504",
             "Girdle": "0,648", "Shoes": "0,612", "Leggins": "0,504", "Ring1": "504,648", "Ring2": "504,648",
             "MainHand": "0,140", "OffHand": "0,140"}
BAG_COLS, BAG_ROWS = 6, 4            # ItemUI.xml Item0Bk..Item23Bk: 6 x 4 cells of 42 px
STORAGE_COLS, STORAGE_ROWS = 5, 8    # StorageUI.xml C0..C39: 5 x 8 cells
STORAGE_CELLS = STORAGE_COLS * STORAGE_ROWS
# character_base columns the sheet may edit, with validation (gwadmin commands are used where they exist)
BASE_EDITABLE = {
    "fighter_job_lv": ("level", 1, gw.PLAYER_MAXLV), "fighter_job_exp": ("exp", 0, 2 ** 32 - 1),
    "Money": ("money", 0, 2 ** 32 - 1), "Stone": ("stone", 0, 2 ** 32 - 1),
    "SkillPoint": ("skill points", 0, 2 ** 32 - 1), "SkillExp": ("skill exp", 0, 2 ** 32 - 1),
    "GM": ("GM flag", 0, 1), "curHP": ("HP", 0, 2 ** 31 - 1), "curMP": ("MP", 0, 2 ** 31 - 1),
    "MaxHP": ("max HP", 0, 2 ** 31 - 1), "MaxMP": ("max MP", 0, 2 ** 31 - 1),
    "status": ("status", 0, 2 ** 31 - 1), "belief": ("belief", 0, 255), "prestige": ("prestige", 0, 2 ** 31 - 1),
    "earl_rank": ("earl rank", 0, 2 ** 31 - 1), "consortia": ("guild id", 0, 2 ** 31 - 1),
    "consortia_job": ("guild job", 0, 6), "consortia_contribute": ("guild contribution", 0, 2 ** 31 - 1),
    "store_num": ("storage slots", 0, gw.STORAGE_READ_SLOTS), "bag_num": ("bags", 1, gw.MAX_BAGS),
    "hair_style": ("hair", 0, 255), "face_shap": ("face", 0, 255), "mutetime": ("online minutes", 0, 2 ** 31 - 1),
    "Map": ("map", 0, 255), "Pos_X": ("X", -239.99, 239.99), "Pos_Z": ("Z", -239.99, 239.99),
    "gender": ("gender", None, None), "name": ("name", None, None),
}
ITEM_TABLES = {"character_base": "id", "character_kitbag": "user_id", "user_magic": "user_id",
               "user_skill": "user_id", "user_quest": "id", "user_relation": "user_id", "user_altar": "user_id"}
PACKED = {("character_kitbag", "kitbag_1"): "bag", ("character_kitbag", "kitbag_2"): "bag",
          ("character_kitbag", "kitbag_3"): "bag", ("character_kitbag", "kitbag_4"): "bag",
          ("character_kitbag", "storage"): "storage", ("character_kitbag", "equip"): "equip",
          ("user_magic", "magic_info"): "pairs", ("user_skill", "skill_info"): "pairs",
          ("user_quest", "allqueststatus"): "allquest"}


class Bad(Exception):
    """A client error (400)."""


class PersistentMySQL:
    """One long-lived mariadb/mysql client process instead of one per query.

    gwadmin.DB spawns the client for every statement (fine on the VPS: 45 ms;
    through an ssh tunnel from the Mac every spawn costs ~1.2 s = 4 round
    trips of handshake).  This feeds the same client the statements on stdin
    in batch mode (-N -B, so rows are tab separated with the same NULL and
    \\t \\n \\\\ escapes, and gwadmin.unescape() applies unchanged) with -vv,
    whose "N rows in set" / "Empty set" / "Query OK" trailer marks the end of
    each result at no extra round trip; stderr is merged so a failing
    statement's "ERROR ... at line N" (N = the input line we sent it on) is
    seen too.  USE is sent only when the database changes.  One statement per
    input line; a statement that stalls for `timeout` seconds kills the client
    (it is restarted on the next call).
    """
    TERM = re.compile(r"^(\d+ rows? in set|Empty set|Query OK)")

    def __init__(self, base):
        self.cmd = list(base) + ["--force", "--unbuffered", "-vv"]
        self.p = None
        self.q = None
        self.pending = []
        self.lock = threading.Lock()
        self.line = 0
        self.db = None

    def _start(self):
        self.p = subprocess.Popen(self.cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, bufsize=0)
        self.q = queue.Queue()
        self.pending = []
        self.line = 0
        self.db = None
        threading.Thread(target=self._reader, args=(self.p, self.q), daemon=True).start()

    @staticmethod
    def _reader(p, q):
        for line in iter(p.stdout.readline, b""):
            q.put(line.decode("utf-8", "replace").rstrip("\r\n"))
        q.put(None)

    def close(self):
        if self.p and self.p.poll() is None:
            try:
                self.p.stdin.close()
                self.p.terminate()
            except OSError:
                pass
        self.p = None

    def _get(self, timeout):
        if self.pending:
            return self.pending.pop(0)
        try:
            return self.q.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError

    @staticmethod
    def _nstatements(sql):
        n, quote, i = 1, None, 0
        while i < len(sql):
            c = sql[i]
            if quote:
                if c == "\\":
                    i += 1
                elif c == quote:
                    quote = None
            elif c in "'\"`":
                quote = c
            elif c == ";" and sql[i + 1:].strip():
                n += 1
            i += 1
        return n

    def _send(self, text):
        self.line += 1
        try:
            self.p.stdin.write((text + "\n").encode("utf-8"))
            self.p.stdin.flush()
        except (OSError, ValueError) as e:
            self.close()
            raise gw.Fail("mysql client pipe broken (%s); retry" % e)
        return self.line

    def _collect(self, lineno, nterm, timeout, also=()):
        """Rows of the statement sent on input line `lineno` (nterm result trailers expected);
        an ERROR for input line `lineno` or any line in `also` (the USE before it) fails it."""
        rows, seen = [], 0
        while seen < nterm:
            try:
                l = self._get(timeout)
            except TimeoutError:
                self.close()
                raise gw.Fail("mysql client did not answer within %ds (client killed; it restarts on the next call)" % timeout)
            if l is None:
                self.close()
                raise gw.Fail("mysql client exited: %s" % " | ".join(x for x in rows if x.strip())[:600])
            if l == "--------------":                 # -vv statement echo: dashes, statement, dashes, blank
                for _ in range(3):
                    try:
                        self._get(timeout)
                    except TimeoutError:
                        break
                continue
            if l.startswith("ERROR "):
                m = re.search(r" at line (\d+):", l)
                if m and int(m.group(1)) != lineno and int(m.group(1)) not in also:
                    continue                      # a stale error of an earlier statement
                self._drain_blank()
                raise gw.Fail("mysql client failed: %s" % l)
            if self.TERM.match(l):
                seen += 1
                self._drain_info()
                continue
            if gw.ADVISORY.search(l) or l == "Bye":
                continue
            rows.append(l)
        return rows

    def _resync(self, timeout):
        """After an error the client may still print output of the statement that followed a failed
        USE; discard everything up to a marker SELECT (one extra round trip, errors only)."""
        if self.p is None or self.p.poll() is not None:
            return
        mark = "GWADMINWEB-SYNC-%d" % (self.line + 1)
        try:
            self._send("SELECT '%s';" % mark)
            while True:
                l = self._get(timeout)
                if l is None:
                    self.close()
                    return
                if l == mark:
                    while not self.TERM.match(l):      # its "1 row in set" trailer
                        l = self._get(timeout)
                        if l is None:
                            self.close()
                            return
                    self._drain_info()
                    return
        except TimeoutError:
            self.close()

    def _drain_blank(self):
        try:
            l = self._get(1.0)
        except TimeoutError:
            return
        if l != "":
            self.pending.insert(0, l)

    def _drain_info(self):
        """After a trailer -vv prints the statement info ("Rows matched: 1  Changed: 1  Warnings: 0",
        "Records: ..." ) when there is one, then a blank line: drop up to the blank."""
        for _ in range(4):
            try:
                l = self._get(2.0)
            except TimeoutError:
                return
            if l is None:
                self.pending.insert(0, l)
                return
            if l == "":
                return
            if l.startswith("--------------") or l.startswith("ERROR "):
                self.pending.insert(0, l)
                return

    def exec(self, sql, db, timeout=120):
        with self.lock:
            if self.p is None or self.p.poll() is not None:
                self._start()
            sql1 = sql.replace("\n", " ").replace("\r", " ").strip().rstrip(";")
            also = ()
            if db != self.db:
                # USE prints nothing in batch mode (no trailer); an unknown database shows up as an
                # ERROR for its line while the next statement's output is collected
                also = (self._send("USE `%s`;" % db),)
                self.db = db
            n = self._send(sql1 + ";")
            try:
                lines = self._collect(n, self._nstatements(sql1), timeout, also)
            except gw.Fail:
                self.db = None
                self._resync(timeout)
                raise
            return [[gw.unescape(c) for c in l.split("\t")] for l in lines]


# ------------------------------------------------------------ app state
class App:
    def __init__(self, a):
        self.a = a
        self.lock = threading.RLock()
        self.ctx = gw.Ctx()
        self.ctx.db = gw.DB(a)
        self.ctx.cat = gw.Catalog(a.catalog)
        self.ctx.force = False
        self.ctx.backup_dir = a.backup_dir
        self.ctx.gamelog = None
        self.gamelog_src = a.gamelog
        if self.gamelog_src is None:
            for cand in (["/opt/godswar/logs/gameserver.log"] if self.ctx.db.live else
                         [os.path.expanduser("~/gw-linux/run/gameserver/run.log")]):
                if os.path.exists(cand):
                    self.gamelog_src = cand
        self.csrf = secrets.token_urlsafe(24)
        self.changes = []
        self.changes_dir = os.path.join(a.backup_dir, "web-changes")
        os.makedirs(self.changes_dir, exist_ok=True)
        self._load_changes()
        self.reborn = None
        if reborn_mod and not a.no_reborn:
            rdb = a.reborn_db or "godswar"
            # a plain database name only: a conninfo string ('dbname=godswar', 'service=...') must not get past
            # the live gate below as "some other database"
            if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", rdb):
                raise SystemExit("gwadmin-web: --reborn-db must be a plain database name (letters, digits, _), got %r" % rdb)
            try:
                rb = reborn_mod.Reborn(a.reborn_psql, rdb, writable=False, live=True,
                                       backup_dir=a.backup_dir, log_path=None, allow_force=a.allow_force)
                # the live gate is decided on what the configured psql REALLY reaches, not on the argument text
                try:
                    ident = rb.identity()
                    actual = ident.get("db")
                except reborn_mod.RebornError as e:
                    ident, actual = {"error": str(e)[:200]}, None
                live_rb = rdb == "godswar" or actual == "godswar"
                if actual is not None and actual != rdb:
                    raise SystemExit("gwadmin-web: --reborn-db %s but psql reaches database %r; refusing to start" % (rdb, actual))
                # written only with an explicit --reborn-db godswar AND --live; any other (verified) name is a copy.
                # An unreachable database starts read-only (nothing could be verified).
                writable = actual is not None and ((not live_rb) or (a.live and a.reborn_db == "godswar"))
                rlog = a.reborn_log
                if rlog is None and live_rb:
                    rlog = "/opt/godswar251/logs/server.log"
                rb.writable, rb.live, rb.log_path, rb.identity_info = writable, live_rb, rlog or None, ident
                self.reborn = rb
            except SystemExit:
                raise
            except Exception as e:      # pragma: no cover
                print("reborn adapter disabled: %s" % e, file=sys.stderr)
        # Server rates page: same psql adapter, database and live gate as the reborn sheet. The management port is
        # the loopback port of the game server that uses that database (live godswar251: 9090).
        # the module editors (MODULES.md) also dump the per-character daily / buff / guild-member tables
        if self.reborn and self.reborn.identity_info.get("db"):
            try:
                self.reborn.enable_extra_dump_tables()
            except Exception as e:      # noqa: BLE001
                print("note: extra dump tables not enabled: %s" % e, file=sys.stderr)
        self.module_errors = gwmods.load_all() if gwmods else {"modules": "modules/ failed to import (see the log)"}
        self.rates = None
        if rates_mod and self.reborn:
            mgmt = a.rates_mgmt if a.rates_mgmt is not None else (9090 if self.reborn.live else None)
            self.rates = rates_mod.Rates(self.reborn, mgmt or None)
        # intercept gwadmin's writes to record the exact SQL per change
        self._orig_write = self.ctx.db.write
        self.ctx.db.write = self._logged_write
        self._sql_log = None
        self.fast = None
        if not a.no_persistent:
            self.fast = PersistentMySQL(self.ctx.db.base)
            self.ctx.db._exec = self._fast_exec
        self._gamelog_cache = (0.0, None)
        # SHOW COLUMNS once per table (gwadmin asks before every row read; the schema does not change)
        self._orig_columns = self.ctx.db.columns
        self._colcache = {}
        self.ctx.db.columns = self._cached_columns
        self.started = datetime.datetime.now().isoformat(timespec="seconds")
        self.auth = None
        self.bootstrap_token = None
        if getattr(a, "account_auth", False):
            if not self.reborn or not a.public_origin or not a.public_origin.startswith("https://"):
                raise SystemExit("account authentication requires a Reborn database and HTTPS public origin")
            if not a.bootstrap_token_file:
                raise SystemExit("account authentication requires --bootstrap-token-file")
            with open(a.bootstrap_token_file) as f:
                self.bootstrap_token = f.read().strip()
            if len(self.bootstrap_token) < 40:
                raise SystemExit("bootstrap token must be at least 40 characters")
            recovery_record = None
            if a.recovery_verifier_file:
                with open(a.recovery_verifier_file) as f:
                    recovery_record = f.read().strip()
            self.auth = editor_auth.Auth(lambda **kw: editor_auth.account_lookup(self.reborn, **kw), recovery_record=recovery_record, session_file=os.path.join(a.backup_dir,"editor-sessions.sqlite3"))
            # Fail closed at start-up if the separately applied schema migration is absent.
            self.reborn.pg.json("(SELECT to_jsonb(count(editor_admin)) FROM accounts)")


    # ---- SQL capture
    def _logged_write(self, sql, db=None, note=""):
        if self._sql_log is not None:
            self._sql_log.append({"db": db or self.ctx.db.game, "sql": sql, "note": note})
        return self._orig_write(sql, db, note)

    def _fast_exec(self, sql, db):
        if self.ctx.db.verbose:
            sys.stderr.write("SQL[%s]> %s\n" % (db, sql if len(sql) < 600 else sql[:600] + "..."))
        return self.fast.exec(sql, db)

    def _cached_columns(self, table, db=None):
        key = (db or self.ctx.db.game, table)
        if key not in self._colcache:
            self._colcache[key] = self._orig_columns(table, db)
        return self._colcache[key]

    # ---- gamelog (local file, or ssh://host/path fetched on demand)
    def refresh_gamelog(self):
        src = self.gamelog_src
        if not src:
            self.ctx.gamelog = None
            return
        m = re.match(r"^ssh://([^/]+)(/.*)$", src)
        if not m:
            self.ctx.gamelog = src
            return
        host, path = m.group(1), m.group(2)
        if path.startswith("/~/"):
            path = path[1:]
        import time
        t, cached = self._gamelog_cache
        if cached and time.time() - t < self.a.gamelog_cache:
            self.ctx.gamelog = cached
            return
        try:
            # only the tail: gamelog_state needs the LAST entry/exit event for the name, and a busy
            # test stack's run.log grows without bound (a whole-file cat made a tunnel-mode write take 18 s)
            self._ssh_host, self._ssh_ctl = host, os.path.join(tempfile.gettempdir(), "gwadmin-web-ssh-%d" % os.getuid())
            p = subprocess.run(["ssh", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ControlMaster=auto",
                                "-o", "ControlPath=" + self._ssh_ctl, "-o", "ControlPersist=120", host,
                                "tail -c %d %s" % (self.a.gamelog_tail, path)],
                               capture_output=True, timeout=60)
            if p.returncode != 0:
                raise OSError(p.stderr.decode("utf-8", "replace").strip())
            tmp = os.path.join(tempfile.gettempdir(), "gwadmin-web-gamelog-%d.log" % os.getpid())
            with open(tmp, "wb") as f:
                f.write(p.stdout)
            self._gamelog_tmp = tmp
            self.ctx.gamelog = tmp
            self._gamelog_cache = (time.time(), tmp)
            self.gamelog_error = None
        except (OSError, subprocess.SubprocessError) as e:
            self.ctx.gamelog = None
            self.gamelog_error = "cannot fetch %s: %s" % (src, e)

    def close(self):
        """Shutdown: stop the persistent mysql client, close the ssh ControlMaster (it would otherwise
        outlive us for ControlPersist seconds) and drop the fetched log copy."""
        if self.fast:
            self.fast.close()
        ctl = getattr(self, "_ssh_ctl", None)
        if ctl and os.path.exists(ctl):
            try:
                subprocess.run(["ssh", "-o", "ControlPath=" + ctl, "-O", "exit", self._ssh_host], capture_output=True, timeout=10)
            except (OSError, subprocess.SubprocessError):
                pass
        tmp = getattr(self, "_gamelog_tmp", None)
        if tmp:
            try:
                os.remove(tmp)
            except OSError:
                pass

    def online_state(self, char):
        self.refresh_gamelog()
        st, detail = gw.gamelog_state(self.ctx.gamelog, char["name"])
        acc = self.ctx.db.row("accounts_info", "accounts=%s" % gw.q(char["accounts"]), self.ctx.db.acc) or {}
        return {"gamelog": st, "gamelog_detail": detail, "login_status": acc.get("login_status"),
                "online": st == "online" or str(acc.get("login_status")) == "1"}

    # ---- change log persistence
    def _load_changes(self):
        files = sorted(glob.glob(os.path.join(self.changes_dir, "change-*.json")),
                       key=lambda p: re.sub(r"^change-(rb|rt|rw|lv)-", "change-", os.path.basename(p)))
        for f in files[-300:]:
            try:
                with open(f, encoding="utf-8") as fh:
                    self.changes.append(json.load(fh))
            except (OSError, ValueError):
                pass

    def record_reborn_change(self, c):
        self.changes.append(c)
        self._save_change(c)

    def _save_change(self, c):
        path = os.path.join(self.changes_dir, "change-%s.json" % c["id"])
        with open(path, "w", encoding="utf-8") as f:
            json.dump(c, f, ensure_ascii=False, indent=1, default=str)

    # ---- rows
    def raw_rows(self, cid):
        return {t: self.ctx.db.row(t, "%s=%d" % (k, cid)) for t, k in ITEM_TABLES.items()}

    def find_char(self, spec):
        try:
            return gw.find_char(self.ctx, spec)
        except gw.Fail as e:
            raise Bad(str(e))

    # ---- the write wrapper: dump before, run, verify after, record
    def write(self, cid, action, fn, char=None):
        with self.lock:
            self.refresh_gamelog()          # ensure_offline reads ctx.gamelog
            char = char or self.find_char("#%d" % cid)
            # the online check comes BEFORE the dump (the operations repeat it, cheaply) so a refused
            # write leaves no orphan backup file; the same for any refusal that ran no SQL (below)
            out = io.StringIO()
            try:
                with redirect_stdout(out):
                    gw.ensure_offline(self.ctx, char, action)
            except gw.Fail as e:
                raise Bad(str(e))
            before = self.raw_rows(cid)
            stamp = datetime.datetime.now()
            backup = os.path.join(gw.backup_dir(self.ctx), "%s-%s-web.json" % (char["name"], stamp.strftime("%Y%m%d-%H%M%S-%f")))
            with open(backup, "w", encoding="utf-8") as f:
                json.dump({"gwadmin": gw.VERSION, "dumped": stamp.isoformat(timespec="seconds"), "database": self.ctx.db.game,
                           "character": char["name"], "id": cid, "tables": before}, f, ensure_ascii=False, indent=1, default=str)
            self._sql_log = []
            out = io.StringIO()
            try:
                with redirect_stdout(out):
                    result = fn(char)
            except (gw.Fail, Bad) as e:
                if not self._sql_log:           # refused before any statement ran: no backup to keep
                    try:
                        os.remove(backup)
                    except OSError:
                        pass
                self._sql_log = None
                raise Bad(str(e))
            finally:
                sql = self._sql_log or []
                self._sql_log = None
            after = self.raw_rows(cid)
            diff = row_diff(before, after)
            verify = verify_roundtrip(after, diff)
            c = {"id": stamp.strftime("%Y%m%d-%H%M%S-%f"), "time": stamp.isoformat(timespec="seconds"),
                 "database": self.ctx.db.game, "character": char["name"], "cid": cid, "action": action,
                 "sql": sql, "output": out.getvalue().strip(), "backup": backup, "diff": diff, "verify": verify,
                 "before": {t: r for t, r in before.items()}, "undone": False}
            self.changes.append(c)
            self._save_change(c)
            return {"change": public_change(c), "result": result}

    def undo(self, change_id, full=False):
        """Undo a change: put back the columns that write changed (default), or with `full`
        every column of all 7 rows from the dump taken before it (also reverting whatever the
        GameServer saved since, e.g. after a login: curHP/MaxHP/LastLogin_time/mutetime/quests)."""
        with self.lock:
            c = next((x for x in self.changes if x["id"] == change_id), None)
            if not c:
                raise Bad("unknown change %s" % change_id)
            if c.get("undone") and not full:
                raise Bad("change %s is already undone ('Restore dump' can still put its pre-write dump back)" % change_id)
            if c["database"] != self.ctx.db.game:
                raise Bad("change %s belongs to database %s, this server uses %s" % (change_id, c["database"], self.ctx.db.game))
            cid = c["cid"]
            char = self.find_char("#%d" % cid)
            before = c["before"]
            scope = c["diff"]
            if full:
                now0 = self.raw_rows(cid)
                scope = {t: sorted(set((before.get(t) or {}).keys()) | set((now0.get(t) or {}).keys()))
                         for t in ITEM_TABLES if before.get(t) != now0.get(t)}

            def fn(char):
                try:
                    gw.ensure_offline(self.ctx, char, "undo")
                except gw.Fail as e:
                    raise Bad(str(e))
                now = self.raw_rows(cid)
                for t, cols in scope.items():
                    key = ITEM_TABLES[t]
                    brow, nrow = before.get(t), now.get(t)
                    if brow is None and nrow is not None:
                        self.ctx.db.write("DELETE FROM `%s` WHERE `%s`=%d" % (t, key, cid), note="undo %s" % change_id)
                        continue
                    if brow is not None and nrow is None:
                        cs = list(brow)
                        self.ctx.db.write("INSERT INTO `%s` (%s) VALUES (%s)" % (
                            t, ",".join("`%s`" % x for x in cs), ",".join(gw.q(brow[x]) for x in cs)), note="undo %s" % change_id)
                        continue
                    sets = ["`%s`=%s" % (col, gw.q(brow.get(col))) for col in cols if brow.get(col) != nrow.get(col)]
                    if sets:
                        self.ctx.db.write("UPDATE `%s` SET %s WHERE `%s`=%d" % (t, ", ".join(sets), key, cid), note="undo %s" % change_id)
                restored = self.raw_rows(cid)
                check = ITEM_TABLES if full else c["diff"]
                identical = {t: restored.get(t) == before.get(t) for t in check}
                for t in check:
                    if not identical[t]:
                        print("%s still differs from the pre-write dump in %s (columns the GameServer saved since; "
                              "'Restore dump' puts those back too)" % (t, row_diff({t: before.get(t)}, {t: restored.get(t)})[t]))
                return {"identical": identical, "all_identical": all(identical.values()), "full": full}

            r = self.write(cid, "%s of %s (%s)" % ("restore dump" if full else "undo", c["id"], c["action"]), fn, char)
            c["undone"] = True
            self._save_change(c)
            r["undo_of"] = c["id"]
            return r

    # ---- reads
    def sheet(self, cid):
        char = self.find_char("#%d" % cid)
        d = gw.decode_character(self.ctx, char)
        cat = self.ctx.cat
        b = d["base"]
        store_num = int(b.get("store_num") or 0)
        bags = {}
        for i in range(1, gw.MAX_BAGS + 1):
            slots = d["bags"][i]
            bags[i] = None if slots is None else [self.cell(x) for x in slots]
        storage = [self.cell(x) for x in d["storage"]]
        storage += [None] * (max(STORAGE_CELLS, len(storage)) - len(storage))
        online = self.online_state(char)
        sheet = {
            "base": b, "account": d["account"], "online": online,
            "class": d["class"], "camp_name": d["camp_name"], "map_name": d["map_name"],
            "equip": [{"slot": s, "icon": SLOT_ICON[s], "item": self.cell(d["equip"][i])} for i, s in enumerate(gw.EQUIP_SLOTS)],
            "paperdoll": PAPERDOLL, "bag_cols": BAG_COLS, "bag_rows": BAG_ROWS,
            "bags": bags, "bag_num": int(b.get("bag_num") or 1),
            "storage": storage, "storage_money": d["storage_money"], "storage_cols": STORAGE_COLS, "storage_rows": STORAGE_ROWS,
            "store_num": store_num, "storage_write_slots": gw.STORAGE_WRITE_SLOTS, "storage_read_slots": gw.STORAGE_READ_SLOTS,
            "active_skills": [{"id": i, "value": v, "name": cat.skill_name(i), "meta": cat.skills.get(str(i))} for i, v in d["active_skills"]],
            "passive_skills": [{"id": i, "value": v, "name": cat.skill_name(i, True), "meta": cat.passive.get(str(i))} for i, v in d["passive_skills"]],
            "quests_done": [{"id": qid, "title": (cat.quests.get(str(qid)) or {}).get("title", "")} for qid in d["quests_done"]],
            "quest_words": d["quest_words"],
            "quests_active": [dict(r, title=(cat.quests.get(str(r["quest"])) or {}).get("title", ""), status_name=gw.QUEST_STATUS.get(r["status"], "?")) for r in d["quests_active"]],
            "quests_timed": [dict(r, title=(cat.quests.get(str(r["quest"])) or {}).get("title", ""), status_name=gw.QUEST_STATUS.get(r["status"], "?")) for r in d["quests_timed"]],
            "friends": d["friends"], "blacklist": d["blacklist"], "altar": d["altar"],
            "raw": {t: r for t, r in d["raw"].items()},
            "currency": CURRENCY, "classes": gw.CLASSES, "camps": gw.CAMPS, "consortia_jobs": gw.CONSORTIA_JOBS,
            "equip_slots": gw.EQUIP_SLOTS, "quest_status": gw.QUEST_STATUS,
            "pets": {"available": False, "note": "not available on the 1.0 server: the 2008 GameServer has no pet code, tables or columns (CLAUDE.md key fact). Pets exist only on the 2.51 / reborn server."},
            "mounts": {"available": False, "note": "not available on the 1.0 server: the 2008 GameServer has no mount code, tables or columns. Mounts exist only on the 2.51 / reborn server."},
        }
        return sheet

    def cell(self, it):
        if it is None:
            return None
        meta = self.ctx.cat.item(it["base"]) or {}
        aps = [a for a in it["append"] if a != -1]
        return {
            "base": it["base"], "name": self.ctx.cat.item_name(it["base"]), "known": bool(meta),
            "icon": meta.get("icon") or "", "type": meta.get("type", ""), "group": meta.get("group", ""),
            "playlv": meta.get("playlv", 0), "maxstack": max(1, int(meta.get("overlap") or 1)), "desc": meta.get("desc", ""),
            "count": it["overlap"], "binding": it["binding"], "baselv": it["baselv"], "appendlv": it["appendlv"],
            "append": it["append"], "append_names": [((self.ctx.cat.appends.get(str(a)) or {}).get("tag") or "?") for a in aps],
            "cdkey": it["cdkey"], "nfields": it.get("nfields"),
            "quality": quality_of(it, meta),
        }

    def characters(self, qtext="", account=""):
        where = []
        if account:
            where.append("accounts=%s" % gw.q(account))
        if qtext:
            where.append("(name LIKE %s OR accounts LIKE %s OR id=%s)" % (gw.q("%" + qtext + "%"), gw.q("%" + qtext + "%"), gw.q(qtext)))
        rows = self.ctx.db.query("SELECT id,accounts,name,gender,GM,camp,profession,fighter_job_lv,Map,Money,Stone,LastLogin_time "
                                 "FROM character_base %s ORDER BY accounts,id" % (("WHERE " + " AND ".join(where)) if where else ""))
        acc = {r[0]: r[1] for r in self.ctx.db.query("SELECT accounts,login_status FROM accounts_info", self.ctx.db.acc)}
        self.refresh_gamelog()
        out = []
        for r in rows:
            cid, a, name, gender, gm, camp, prof, lv, mp, money, stone, last = r
            st, _ = gw.gamelog_state(self.ctx.gamelog, name)
            out.append({"id": int(cid), "account": a, "name": name, "gender": gender, "gm": gm == "1",
                        "camp": gw.CAMPS.get(int(camp), camp), "class": gw.CLASSES.get(int(prof), prof), "level": int(lv),
                        "map": self.ctx.cat.map_name(mp), "map_id": int(mp), "money": money, "stone": stone, "last_login": last,
                        "online": st == "online", "login_status": acc.get(a)})
        return out

    def accounts(self, qtext=""):
        where = ("WHERE a.accounts LIKE %s" % gw.q("%" + qtext + "%")) if qtext else ""
        rows = self.ctx.db.query(
            "SELECT a.id,a.accounts,a.login_status,a.accounts_status,a.last_login_time,a.last_login_IP,"
            "(SELECT COUNT(*) FROM `%s`.character_base b WHERE b.accounts=a.accounts),IFNULL(g.IsDel,'') "
            "FROM accounts_info a LEFT JOIN gamearea_info g ON g.PassName=a.accounts %s ORDER BY a.id" % (self.ctx.db.game, where), self.ctx.db.acc)
        return [{"id": r[0], "account": r[1], "login_status": r[2], "status": r[3], "last_login": r[4], "last_ip": r[5],
                 "chars": int(r[6] or 0), "isdel": r[7]} for r in rows]

    # ---- catalog
    def item_search(self, text, limit=40):
        text = (text or "").strip()
        if not text:
            hits = list(self.ctx.cat.items.values())
        elif text.isdigit():
            hits = [v for v in self.ctx.cat.items.values() if str(v["id"]).startswith(text)]
            exact = self.ctx.cat.item(int(text))
            if exact and exact not in hits:
                hits.insert(0, exact)
        else:
            hits = self.ctx.cat.find_items(text)
            t = text.lower()
            hits.sort(key=lambda v: (0 if v["name"].lower().startswith(t) else 1, v["playlv"], v["name"]))
        return [{"id": v["id"], "name": v["name"], "type": v["type"], "group": v["group"], "playlv": v["playlv"],
                 "overlap": v["overlap"], "icon": v["icon"], "class": v["class"], "desc": v["desc"]} for v in hits[:limit]]

    # ---- selftest
    def selftest(self):
        rows = self.ctx.db.query("SELECT id,name,camp FROM character_base ORDER BY id")
        report = {"database": self.ctx.db.game, "characters": len(rows), "columns_checked": 0, "identical": 0,
                  "differences": [], "notes": [
                      "Checked: kitbag_1..4 (decode_bag/encode_bag), storage (decode_storage/encode_storage), equip (decode_equip/encode_equip), "
                      "user_magic.magic_info and user_skill.skill_info (decode_pairs/encode_pairs), user_quest.allqueststatus (decode_allquest/encode_allquest).",
                      "user_quest.activestatus/timequest are never re-encoded by gwadmin (entries are kept verbatim and only filtered), so they are not part of this test.",
                      "A difference means gwadmin's writer would not reproduce the server's bytes for that column; the server would still parse both (CreatePropFromDB), but look before editing that character."]}
        packed = {}
        for t, key in (("character_kitbag", "user_id"), ("user_magic", "user_id"), ("user_skill", "user_id"), ("user_quest", "id")):
            cols = [c for (tt, c) in PACKED if tt == t]
            for r in self.ctx.db.query("SELECT `%s`,%s FROM `%s`" % (key, ",".join("`%s`" % c for c in cols), t)):
                packed[(t, int(r[0]))] = dict(zip(cols, r[1:]))
        for cid, name, camp in rows:
            cid = int(cid)
            for (t, col), kind in PACKED.items():
                row = packed.get((t, cid))
                if not row or row.get(col) is None:
                    continue
                s = row[col]
                try:
                    enc = reencode(kind, s, int(camp))
                except Exception as e:      # noqa: BLE001
                    enc = "DECODE ERROR: %s" % e
                report["columns_checked"] += 1
                if enc == s:
                    report["identical"] += 1
                else:
                    report["differences"].append({"id": cid, "name": name, "table": t, "column": col,
                                                  "stored": s, "reencoded": enc, "first_diff_at": first_diff(s, enc)})
        report["ok"] = not report["differences"]
        return report


def reencode(kind, s, camp):
    if kind == "bag":
        return gw.encode_bag(gw.decode_bag(s))
    if kind == "storage":
        slots, money = gw.decode_storage(s)
        return gw.encode_storage(slots, money)
    if kind == "equip":
        return gw.encode_equip(gw.decode_equip(s))
    if kind == "pairs":
        return gw.encode_pairs(gw.decode_pairs(s))
    if kind == "allquest":
        done, words = gw.decode_allquest(s, camp)
        return gw.encode_allquest(done, camp, len(words))
    raise ValueError(kind)


def first_diff(a, b):
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y:
            return i
    return min(len(a), len(b)) if len(a) != len(b) else -1


def row_diff(before, after):
    """{table: [changed columns]} (a whole row appearing/disappearing lists all its columns)."""
    out = {}
    for t in ITEM_TABLES:
        b, a = before.get(t), after.get(t)
        if b == a:
            continue
        cols = sorted(set((b or {}).keys()) | set((a or {}).keys()))
        out[t] = [c for c in cols if (b or {}).get(c) != (a or {}).get(c)]
    return out


def verify_roundtrip(after, diff):
    """Re-decode + re-encode every packed column the write touched."""
    res = {}
    camp = int((after.get("character_base") or {}).get("camp") or 0)
    for t, cols in diff.items():
        for col in cols:
            kind = PACKED.get((t, col))
            if not kind:
                continue
            s = (after.get(t) or {}).get(col)
            if s is None:
                res["%s.%s" % (t, col)] = "NULL"
                continue
            try:
                res["%s.%s" % (t, col)] = "round-trips" if reencode(kind, s, camp) == s else "DIFFERS after re-encode"
            except Exception as e:      # noqa: BLE001
                res["%s.%s" % (t, col)] = "decode error: %s" % e
    return res


def quality_of(it, meta):
    """The 1.0 items have no quality tier; derive a display grade from forge levels / appends."""
    aps = [a for a in it["append"] if a != -1]
    lv = max(it["baselv"], it["appendlv"])
    if not meta:
        return "unknown"
    if aps and lv > 3:
        return "epic"
    if aps:
        return "rare"
    if lv > 1:
        return "forged"
    return "normal"


def public_change(c):
    return {k: v for k, v in c.items() if k not in ("before", "after")}


# ------------------------------------------------------------- writes
def item_from_payload(app, p, slot_hint=None):
    """Validate an item edit payload -> gwadmin item dict."""
    base = gw.num(p.get("base"), 0, 10 ** 7, "item id")
    meta = app.ctx.cat.item(base)
    force = bool(p.get("force"))
    if not meta and not force:
        raise Bad("item id %d is not in ItemBaseAttribute.xml (catalog); tick 'force' to write it anyway" % base)
    maxstack = max(1, int((meta or {}).get("overlap") or 1))
    count = gw.num(p.get("count", 1), 1, 10 ** 6, "count")
    if count > maxstack and not force:
        raise Bad("%s stacks to %d at most (Overlap); tick 'force' to exceed it" % ((meta or {}).get("name", base), maxstack))
    binding = gw.num(p.get("binding", 0), 0, 1, "binding")
    baselv = gw.num(p.get("baselv", 1), 0, 255, "BaseLV")
    appendlv = gw.num(p.get("appendlv", 1), 0, 255, "AppendLV")
    cdkey = gw.num(p.get("cdkey", 0), 0, 2 ** 63 - 1, "CdKey")
    append = []
    for a in (p.get("append") or []):
        if a in ("", None, -1, "-1"):
            append.append(-1)
        else:
            a = gw.num(a, 0, 10 ** 6, "append id")
            if str(a) not in app.ctx.cat.appends and not force:
                raise Bad("append attribute %d is not in ItemAppendAttribute.xml; tick 'force' to use it" % a)
            append.append(a)
    append = (append + [-1] * gw.MAX_APPEND)[:gw.MAX_APPEND]
    it = gw.new_item(base, count, binding, baselv, appendlv, [a for a in append])
    it["append"] = append
    it["cdkey"] = cdkey
    return it, meta


def load_container(app, kb, container, bag_num):
    """-> (column, slots, extra, label).  container: bag1..bag4 | storage | equip"""
    m = re.match(r"^bag([1-4])$", container or "")
    if m:
        n = int(m.group(1))
        col = "kitbag_%d" % n
        s = kb.get(col)
        if s is None:
            if n > bag_num:
                raise Bad("bag %d is not activated for this character (bag_num=%d, column NULL); set 'bags' to %d in the stats panel first" % (n, bag_num, n))
            s = ""
        return col, gw.decode_bag(s), None, "bag %d" % n
    if container == "storage":
        slots, money = gw.decode_storage(kb.get("storage") or "")
        if len(slots) < STORAGE_CELLS:
            slots = slots + [None] * (STORAGE_CELLS - len(slots))
        return "storage", slots, money, "storage"
    if container == "equip":
        return "equip", gw.decode_equip(kb.get("equip") or ""), None, "equipment"
    raise Bad("unknown container %r" % container)


def human_slot(container, slot):
    """Slots are 0-based in the JSON API (like the packed strings), 1-based wherever a person reads
    them (dialog titles, change-log actions, error texts, gwadmin.py's CLI)."""
    if container == "equip":
        return gw.EQUIP_SLOTS[slot] if 0 <= slot < len(gw.EQUIP_SLOTS) else "slot %d" % (slot + 1)
    return "slot %d" % (slot + 1)


def check_slot(app, col, slots, slot, item, meta, store_num, force):
    if slot < 0 or slot >= len(slots):
        raise Bad("slot %d is outside %s (1..%d)" % (slot + 1, col, len(slots)))
    if col == "equip" and item is not None:
        name = gw.EQUIP_SLOTS[slot]
        allowed = gw.TYPE_TO_SLOTS.get((meta or {}).get("type", ""), [])
        if name not in allowed and not force:
            raise Bad("%s is type %r which goes in %s, not %s (tick 'force' to override)" % (
                (meta or {}).get("name", item["base"]), (meta or {}).get("type"), allowed or "no equipment slot", name))
    if col == "storage" and item is not None:
        if slot >= gw.STORAGE_WRITE_SLOTS and not force:
            raise Bad("storage slot %d: the GameServer only writes back slots 1..%d (PackStorageInfo, MAX_STORAGE_GRID>>1); an item there is lost at the next save. Tick 'force' if you really want it." % (slot + 1, gw.STORAGE_WRITE_SLOTS))


def trim_storage(slots):
    """Drop trailing empty entries beyond what the server would write (keeps at least 18 like SP_INSERT_ROLE)."""
    n = len(slots)
    while n > 18 and slots[n - 1] is None:
        n -= 1
    return slots[:n]


def op_item(app, cid, p):
    container, slot = p.get("container"), gw.num(p.get("slot"), 0, 99, "slot")
    force = bool(p.get("force"))
    item = None
    meta = None
    if p.get("item") is not None:
        item, meta = item_from_payload(app, dict(p["item"], force=force))

    def fn(char):
        gw.ensure_offline(app.ctx, char, "item edit")
        kb = gw.kitbag_row(app.ctx, cid)
        col, slots, extra, label = load_container(app, kb, container, int(char["bag_num"]))
        check_slot(app, col, slots, slot, item, meta, int(char["store_num"]), force)
        old = slots[slot]
        slots[slot] = item
        if col == "storage":
            slots[:] = trim_storage(slots)
        gw.save_target(app.ctx, cid, col, slots, extra, "web %s %s" % (label, human_slot(container, slot)))
        return {"container": container, "slot": slot, "old": app.cell(old), "new": app.cell(item)}

    what = "delete" if item is None else ("set %s x%d in" % (app.ctx.cat.item_name(item["base"]), item["overlap"]))
    return app.write(cid, "%s %s %s" % (what, container, human_slot(container, slot)), fn)


def encode_target(col, slots, extra):
    """gwadmin.save_target's encoder choice, without the write (one statement can then carry two columns)."""
    if col == "storage":
        return gw.encode_storage(slots, extra)
    if col == "equip":
        return gw.encode_equip(slots)
    return gw.encode_bag(slots)


def op_move(app, cid, p):
    src, dst = p.get("from") or {}, p.get("to") or {}
    force = bool(p.get("force"))
    sc, ss = src.get("container"), gw.num(src.get("slot"), 0, 99, "from slot")
    dc, ds = dst.get("container"), gw.num(dst.get("slot"), 0, 99, "to slot")
    if (sc, ss) == (dc, ds):
        raise Bad("source and destination are the same slot")

    def fn(char):
        gw.ensure_offline(app.ctx, char, "move")
        kb = gw.kitbag_row(app.ctx, cid)
        bag_num, store_num = int(char["bag_num"]), int(char["store_num"])
        scol, sslots, sextra, slabel = load_container(app, kb, sc, bag_num)
        if sc == dc:
            dcol, dslots, dextra, dlabel = scol, sslots, sextra, slabel
        else:
            dcol, dslots, dextra, dlabel = load_container(app, kb, dc, bag_num)
        if ss >= len(sslots) or sslots[ss] is None:
            raise Bad("%s %s is empty" % (slabel, human_slot(sc, ss)))
        moving = sslots[ss]
        other = dslots[ds] if ds < len(dslots) else None
        mmeta = app.ctx.cat.item(moving["base"])
        ometa = app.ctx.cat.item(other["base"]) if other else None
        check_slot(app, dcol, dslots, ds, moving, mmeta, store_num, force)
        check_slot(app, scol, sslots, ss, other, ometa, store_num, force)
        if dcol == "equip" or scol == "equip":
            # equipment entries are always 1 / bound (PackEquipInfo writes 1,1)
            if dcol == "equip" and moving["overlap"] != 1 and not force:
                raise Bad("only a single item can be worn (stack of %d); split it first or tick 'force'" % moving["overlap"])
        dslots[ds], sslots[ss] = moving, other
        if scol == "storage":
            sslots[:] = trim_storage(sslots)
        if dcol == "storage" and dcol != scol:
            dslots[:] = trim_storage(dslots)
        note = "web move %s %s -> %s %s" % (slabel, human_slot(sc, ss), dlabel, human_slot(dc, ds))
        if dcol == scol:
            gw.save_target(app.ctx, cid, scol, sslots, sextra, note)
        else:
            # both columns in ONE statement: a killed client / dropped tunnel between two UPDATEs
            # would otherwise leave the item removed from the source and never added to the destination
            app.ctx.db.write("UPDATE character_kitbag SET `%s`=%s, `%s`=%s WHERE user_id=%d" % (
                scol, gw.q(encode_target(scol, sslots, sextra)), dcol, gw.q(encode_target(dcol, dslots, dextra)), cid), note=note)
        return {"moved": app.cell(moving), "swapped_with": app.cell(other)}

    return app.write(cid, "move %s %s -> %s %s" % (sc, human_slot(sc, ss), dc, human_slot(dc, ds)), fn)


def op_storage_money(app, cid, p):
    money = gw.num(p.get("money"), 0, 2 ** 31 - 1, "storage money")

    def fn(char):
        gw.cmd_set_money(app.ctx, argparse.Namespace(name="#%d" % cid, money=None, stone=None, storage_money=money))
        return {"storage_money": money}

    return app.write(cid, "storage money -> %d" % money, fn)


def op_base(app, cid, p):
    fields = p.get("fields") or {}
    if not fields:
        raise Bad("nothing to set")
    unknown = [k for k in fields if k not in BASE_EDITABLE]
    if unknown:
        raise Bad("not editable here: %s" % ", ".join(unknown))

    def fn(char):
        gw.ensure_offline(app.ctx, char, "stats edit")
        name = "#%d" % cid
        plain = {}
        done = []
        for k, v in fields.items():
            label, lo, hi = BASE_EDITABLE[k]
            if k == "fighter_job_lv":
                gw.cmd_set_level(app.ctx, argparse.Namespace(name=name, level=v, exp=fields.get("fighter_job_exp", char["fighter_job_exp"])))
            elif k == "fighter_job_exp":
                if "fighter_job_lv" in fields:
                    continue
                gw.cmd_set_exp(app.ctx, argparse.Namespace(name=name, exp=v))
            elif k in ("Money", "Stone"):
                if k == "Stone" and "Money" in fields:
                    continue
                gw.cmd_set_money(app.ctx, argparse.Namespace(name=name, money=fields.get("Money"), stone=fields.get("Stone"), storage_money=None))
            elif k == "SkillPoint":
                gw.cmd_set_skillpoints(app.ctx, argparse.Namespace(name=name, points=v, skillexp=fields.get("SkillExp")))
            elif k == "SkillExp":
                if "SkillPoint" in fields:
                    continue
                gw.cmd_set_skillpoints(app.ctx, argparse.Namespace(name=name, points=char["SkillPoint"], skillexp=v))
            elif k in ("Map", "Pos_X", "Pos_Z"):
                if "pos" in done:
                    continue
                done.append("pos")
                gw.cmd_set_pos(app.ctx, argparse.Namespace(name=name, map=fields.get("Map", char["Map"]),
                                                           x=fields.get("Pos_X", char["Pos_X"]), z=fields.get("Pos_Z", char["Pos_Z"])))
            elif k == "GM":
                gw.cmd_set_gm(app.ctx, argparse.Namespace(name=name, flag=v))
            elif k == "bag_num":
                gw.cmd_set_bags(app.ctx, argparse.Namespace(name=name, count=v))
            elif k == "name":
                gw.cmd_rename(app.ctx, argparse.Namespace(name=name, new_name=str(v)))
            elif k == "gender":
                if v not in ("male", "female"):
                    raise Bad("gender must be male or female")
                plain["gender"] = gw.q(v)
            else:
                n = gw.num(v, lo, hi, label)
                plain[k] = n
        if plain:
            gw.update_base(app.ctx, char, plain, "web stats %s" % char["name"])
            print("%s: %s" % (char["name"], ", ".join("%s=%s" % kv for kv in plain.items())))
        return {"fields": fields}

    return app.write(cid, "stats " + ", ".join("%s=%s" % kv for kv in fields.items()), fn)


def op_skill(app, cid, p):
    kind = p.get("kind")
    if kind not in ("active", "passive"):
        raise Bad("kind must be active or passive")
    action = p.get("action")
    if action not in ("add", "remove"):
        raise Bad("action must be add or remove")
    sid = gw.num(p.get("id"), 0, 10 ** 6, "skill id")

    def fn(char):
        gw.cmd_skill(app.ctx, argparse.Namespace(action=action, name="#%d" % cid, id=sid, passive=(kind == "passive"),
                                                 value=p.get("value"), force=bool(p.get("force"))))
        return {}

    return app.write(cid, "%s %s skill %d%s" % (action, kind, sid, (" = %s" % p.get("value")) if p.get("value") not in (None, "") else ""), fn)


def op_quest(app, cid, p):
    qid = gw.num(p.get("quest"), 0, 2 * gw.QUEST_COUNT - 1, "quest id")
    state = p.get("state")
    if state not in ("done", "none"):
        raise Bad("state must be done or none")

    def fn(char):
        gw.cmd_quest(app.ctx, argparse.Namespace(name="#%d" % cid, quest=qid, state=state, drop_active=bool(p.get("drop_active"))))
        return {}

    return app.write(cid, "quest %d -> %s%s" % (qid, state, " (dropped from active)" if p.get("drop_active") else ""), fn)


def op_quest_entries(app, cid, p):
    """Replace user_quest.activestatus / timequest with the given raw entries (verbatim, like gwadmin keeps them)."""
    col = p.get("column")
    if col not in ("activestatus", "timequest"):
        raise Bad("column must be activestatus or timequest")
    entries = p.get("entries")
    if not isinstance(entries, list):
        raise Bad("entries must be a list of strings")
    for e in entries:
        if not re.match(r"^[0-9,\-]*$", e or ""):
            raise Bad("entry %r: only digits, commas and minus signs are allowed" % e)

    def fn(char):
        gw.ensure_offline(app.ctx, char, "quest entries")
        row = app.ctx.db.row("user_quest", "id=%d" % cid)
        if not row:
            raise Bad("no user_quest row")
        s = "".join("[%s]#" % e for e in entries)
        app.ctx.db.write("UPDATE user_quest SET `%s`=%s WHERE id=%d" % (col, gw.q(s), cid), note="web quest entries")
        print("%s: %s <- %d entries" % (char["name"], col, len(entries)))
        return {}

    return app.write(cid, "quest %s rewritten (%d entries)" % (col, len(entries)), fn)


def op_account(app, p):
    """Account-level toggles (accounts DB): set-online 0/1, set-status, ban/unban, set-password."""
    sub, account = p.get("sub"), p.get("account")
    if sub not in ("set-online", "set-status", "disable", "ban", "unban", "set-password"):
        raise Bad("unknown account action")
    ns = argparse.Namespace(subcmd=sub, account=account, value=p.get("value"), reason=p.get("reason"), by=p.get("by") or "gwadmin-web",
                            password=p.get("value"))
    out = io.StringIO()
    with app.lock:
        app._sql_log = []
        try:
            with redirect_stdout(out):
                gw.cmd_account(app.ctx, ns)
        except gw.Fail as e:
            raise Bad(str(e))
        finally:
            sql = app._sql_log or []
            app._sql_log = None
        c = {"id": datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f"), "time": datetime.datetime.now().isoformat(timespec="seconds"),
             "database": app.ctx.db.acc, "character": account, "cid": None, "action": "account %s %s" % (sub, account),
             "sql": sql, "output": out.getvalue().strip(), "backup": None, "diff": {}, "verify": {}, "before": {}, "undone": True,
             "no_undo": "account writes have no undo (accounts DB)"}
        app.changes.append(c)
        app._save_change(c)
    return {"change": public_change(c)}


# --------------------------------------------------------------- HTTP
class Handler(BaseHTTPRequestHandler):
    app = None
    server_version = "gwadmin-web/" + VERSION

    def log_message(self, fmt, *args):
        if self.app.a.verbose:
            sys.stderr.write("%s - %s\n" % (self.address_string(), fmt % args))

    # ---- helpers
    def auth_redirect(self, location, token=None, clear=False):
        self.send_response(303)
        self.send_header("Location", location)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", "0")
        if token is not None or clear:
            self.send_header("Set-Cookie", "%s=%s; Path=/; Secure; HttpOnly; SameSite=Strict; Max-Age=%d" %
                             (editor_auth.COOKIE, token or "", 0 if clear else editor_auth.TTL))
        self.end_headers()

    def auth_request(self, method, path):
        auth = getattr(self.app, "auth", None)
        if auth is None:
            return False
        if method == "GET" and path in ("/login", "/static/login.css", "/static/login.js"):
            name = "login.html" if path == "/login" else path.rsplit("/", 1)[-1]
            mime = {"html": "text/html", "css": "text/css", "js": "application/javascript"}[name.rsplit(".", 1)[-1]]
            self.send_file(os.path.join(HERE, "static", name), mime + "; charset=utf-8")
            return True
        if method == "GET" and path == "/auth/bootstrap":
            supplied = self.headers.get("X-Editor-Bootstrap") or ""
            if not supplied or not secrets.compare_digest(supplied, self.app.bootstrap_token):
                self.send_json({"error": "owner recovery requires the authenticated proxy"}, 403)
            else:
                self.auth_redirect("/", auth.issue(bootstrap=True))
            return True
        if method == "POST" and path in ("/auth/login", "/auth/recovery"):
            if not valid_origin(self.headers.get("Origin"), self.app.a.public_origin) or self.headers.get_content_type() != "application/json":
                self.send_json({"error": "same-origin JSON login required"}, 403)
                return True
            body = self.body_json()
            if not isinstance(body, dict):
                self.send_json({"error": "invalid login request"}, 400)
                return True
            try:
                # Proxy overwrites this header; the direct service is bound to loopback.
                address = (self.headers.get("X-Forwarded-For") or self.client_address[0])[:128]
                login = auth.recovery_login if path == "/auth/recovery" else auth.login
                token, status = login(body.get("username"), body.get("password"), address)
            except Exception:
                self.send_json({"error": "sign-in temporarily unavailable"}, 503)
                return True
            if token:
                self.auth_redirect("/", token)
            else:
                self.send_json({"error": "Too many attempts. Try again in 15 minutes." if status == 429 else
                                "Sign-in failed. Check the account, password and editor administrator access."}, status)
            return True
        try:
            session = auth.session(self.headers.get("Cookie"))
        except Exception:
            self.send_json({"error": "authentication temporarily unavailable"}, 503)
            return True
        if not session:
            if path.startswith("/api/") or method != "GET":
                self.send_json({"error": "Sign in to the editor.", "login": "/login"}, 401)
            else:
                self.auth_redirect("/login?next=" + urllib.parse.quote(self.path, safe=""))
            return True
        self.editor_session = session
        if method == "POST" and path == "/auth/logout":
            if (not valid_origin(self.headers.get("Origin"), self.app.a.public_origin) or
                    not secrets.compare_digest(self.headers.get("X-CSRF-Token") or "", self.app.csrf)):
                self.send_json({"error": "same-origin logout with CSRF token required"}, 403)
            else:
                auth.logout(self.headers.get("Cookie"))
                self.auth_redirect("/login", clear=True)
            return True
        if method == "GET" and path == "/auth/session":
            self.send_json({"username": session["username"], "bootstrap": session["bootstrap"]})
            return True
        return False

    def send_json(self, obj, status=200):
        body = json.dumps(obj, ensure_ascii=False, default=str).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def send_file(self, path, ctype):
        try:
            with open(path, "rb") as f:
                body = f.read()
        except OSError:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        # the icon atlases (1.3 / 3.6 MB) change only when icons.py / build_origin_catalog.py is re-run
        self.send_header("Cache-Control", "max-age=3600" if path.endswith(".png") else "no-cache")
        self.end_headers()
        self.wfile.write(body)

    def check_local(self):
        host = (self.headers.get("Host") or "").split(":")[0]
        if host not in ("127.0.0.1", "localhost", "[::1]", "::1"):
            self.send_json({"error": "Host header %r is not local" % host}, 403)
            return False
        return True

    def body_json(self):
        n = int(self.headers.get("Content-Length") or 0)
        if n < 0 or n > 2_000_000:
            raise Bad("request too large")
        raw = self.rfile.read(n) if n else b""
        try:
            return json.loads(raw.decode("utf-8") or "{}")
        except ValueError:
            raise Bad("body is not JSON")

    # ---- GET
    def do_GET(self):
        if not self.check_local():
            return
        u = urllib.parse.urlparse(self.path)
        qs = urllib.parse.parse_qs(u.query)
        q1 = lambda k, d="": (qs.get(k) or [d])[0]
        app = self.app
        try:
            if self.auth_request("GET", u.path):
                return
            if u.path in ("/", "/index.html", "/shell"):
                return self.index("shell.html")         # the module shell (MODULES.md)
            if u.path in ("/classic", "/classic/"):    # the classic editor was removed; old bookmarks land on the editor
                return self.auth_redirect("/")
            if u.path.startswith("/static/"):
                # sub-folders too (static/ui/, static/shared/, static/modules/); never outside static/
                rel = os.path.normpath(urllib.parse.unquote(u.path[len("/static/"):]))
                if rel.startswith("..") or os.path.isabs(rel) or "\0" in rel:
                    return self.send_json({"error": "not found"}, 404)
                ctype = {"png": "image/png", "css": "text/css; charset=utf-8", "js": "application/javascript; charset=utf-8",
                         "json": "application/json; charset=utf-8", "html": "text/html; charset=utf-8",
                         "svg": "image/svg+xml", "md": "text/plain; charset=utf-8"}.get(rel.rsplit(".", 1)[-1], "application/octet-stream")
                return self.send_file(os.path.join(HERE, "static", rel), ctype)
            if u.path.startswith("/api/m/") or u.path == "/api/m":
                return self.module_dispatch("GET", u.path, {k: v[0] for k, v in qs.items()}, None)
            if u.path == "/api/meta":
                return self.send_json(self.meta())
            if u.path == "/api/accounts":
                return self.send_json(app.accounts(q1("q")))
            if u.path == "/api/characters":
                return self.send_json(app.characters(q1("q"), q1("account")))
            m = re.match(r"^/api/character/(\d+)$", u.path)
            if m:
                return self.send_json(app.sheet(int(m.group(1))))
            if u.path == "/api/catalog/items":
                return self.send_json(app.item_search(q1("q"), int(q1("limit", "40"))))
            if u.path == "/api/catalog/appends":
                return self.send_json(app.ctx.cat.appends)
            if u.path == "/api/catalog/skills":
                t = q1("q").lower()
                res = [{"id": int(k), "kind": "active", "name": v["name"], "meta": v} for k, v in app.ctx.cat.skills.items() if t in v["name"].lower() or t == k]
                res += [{"id": int(k), "kind": "passive", "name": v["name"], "meta": v} for k, v in app.ctx.cat.passive.items() if t in v["name"].lower() or t == k]
                return self.send_json(res[:200])
            if u.path == "/api/catalog/quests":
                t = q1("q").lower()
                res = [{"id": int(k), "title": v["title"]} for k, v in app.ctx.cat.quests.items() if t in v["title"].lower() or t == k]
                return self.send_json(sorted(res, key=lambda r: r["id"])[:300])
            if u.path == "/api/catalog/maps":
                return self.send_json(sorted(app.ctx.cat.maps.values(), key=lambda m: m["id"]))
            if u.path == "/api/changes":
                return self.send_json([public_change(c) for c in app.changes[-200:]][::-1])
            if u.path == "/selftest" or u.path == "/api/selftest":
                with app.lock:
                    rep = app.selftest()
                if q1("format") == "text":
                    txt = ["gwadmin-web selftest on %s: %d characters, %d packed columns, %d identical, %d differences" % (
                        rep["database"], rep["characters"], rep["columns_checked"], rep["identical"], len(rep["differences"]))]
                    for d in rep["differences"]:
                        txt.append("  #%s %s %s.%s differs at byte %s\n    stored:    %s\n    reencoded: %s" % (
                            d["id"], d["name"], d["table"], d["column"], d["first_diff_at"], d["stored"], d["reencoded"]))
                    body = "\n".join(txt).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "text/plain; charset=utf-8")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                    return
                return self.send_json(rep)
            if u.path.startswith("/api/reborn/"):
                return self.reborn_get(u.path, q1)
            if u.path == "/api/rates":
                if not app.rates:
                    return self.send_json({"available": False, "note": "the Server rates page needs the reborn adapter"})
                with app.rates.lock:
                    return self.send_json(dict(app.rates.state(), available=True))
            self.send_json({"error": "not found"}, 404)
        except Bad as e:
            self.send_json({"error": str(e)}, 400)
        except gw.Fail as e:
            self.send_json({"error": str(e)}, 400)
        except RebornError as e:
            self.send_json({"error": str(e)}, 400)
        except Exception as e:      # noqa: BLE001
            traceback.print_exc()
            self.send_json({"error": "%s: %s" % (type(e).__name__, e)}, 500)

    def reborn_get(self, path, q1):
        rb = self.app.reborn
        if not rb:
            return self.send_json({"available": False, "note": "reborn adapter disabled"})
        if path == "/api/reborn/status":
            return self.send_json(rb.status())
        if path == "/api/reborn/characters":
            return self.send_json(rb.characters(q1("q")))
        m = re.match(r"^/api/reborn/character/(\d+)$", path)
        if m:
            return self.send_json(rb.sheet(int(m.group(1))))
        if path == "/api/reborn/catalog/items":
            slot = q1("slot")
            return self.send_json(rb.item_search(q1("q"), int(q1("limit", "40")), q1("kind") or None,
                                                 int(slot) if slot.lstrip("-").isdigit() else None))
        if path == "/api/reborn/catalog/attributes":
            return self.send_json(rb.attribute_catalog())
        if path == "/api/reborn/catalog/pets":
            cat, cli = rb.cat(), rb.client()
            species = [{"species": int(k), "name": (cli["pets"].get(k) or {}).get("name") or v["display_name"],
                        "icon": (cli["pets"].get(k) or {}).get("icon_male", ""), "starter": v["starter_skill_id"],
                        "starter_name": v["starter_skill_name"], "lifetime": v["lifetime_values"]}
                       for k, v in sorted(cat["species"].items(), key=lambda kv: int(kv[0]))]
            apts = [{"aptitude": int(k), "name": v["display_name"], "talents": [n for bit, n in reborn_mod.TALENTS
                     if (31 if int(k) >= 14 else 26 if int(k) >= 10 else 26 if int(k) == 8 else 31 if int(k) == 7 else 0) & bit],
                     "savvy": [v["minimum_initial_savvy"], v["maximum_initial_savvy"]],
                     "growth": [v["minimum_total_growth"], v["maximum_total_growth"]]}
                    for k, v in sorted(cat["aptitudes"].items(), key=lambda kv: int(kv[0]))]
            return self.send_json({"species": species, "aptitudes": apts, "settings": cat.get("pet_settings")})
        if path == "/api/reborn/catalog/petskills":
            res = rb.catalog_search("petskill", rb.client()["petskills"].values(), q1("q"), limit=60)
            res = [dict(v, only=rb.petskill_species(v["id"])) for v in res]
            return self.send_json(res)
        if path == "/api/reborn/catalog/skills":
            return self.send_json(rb.catalog_search("skill", rb.cat()["skills"].values(), q1("q")))
        if path == "/api/reborn/catalog/talents":
            return self.send_json(rb.catalog_search("talent", rb.cat()["talents"].values(), q1("q")))
        if path == "/api/reborn/catalog/quests":
            return self.send_json(rb.catalog_search("quest", rb.client()["quests"].values(), q1("q")))
        if path == "/api/reborn/catalog/maps":
            return self.send_json([{"id": int(k), "name": v.replace("_", " ")} for k, v in sorted(rb.cat()["maps"].items(), key=lambda kv: int(kv[0]))])
        if path == "/api/reborn/selftest":
            with rb.lock:
                rep = rb.selftest()
            if q1("format") == "text":
                txt = ["gwadmin-web reborn selftest on %s: %d characters, %d rows read, %d errors, %d warnings, nothing written" % (
                    rep["database"], rep["characters"], rep["rows"], rep["errors"], rep["warnings"])]
                txt.append("  rows per table: " + ", ".join("%s=%d" % kv for kv in sorted(rep["counts"].items())))
                txt.append("  server start-up validators: " + ", ".join("%s=%s" % (k, v or "ok") for k, v in rep["validators"].items()))
                for f in rep["findings"]:
                    txt.append("  [%s] #%s %s %s: %s" % (f["severity"], f.get("id"), f.get("name"), f["where"], f["msg"]))
                body = "\n".join(txt).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            return self.send_json(rep)
        self.send_json({"error": "not found"}, 404)

    def reborn_post(self, cid, op, p):
        app, rb = self.app, self.app.reborn
        if not rb:
            raise Bad("reborn adapter disabled")
        fn = reborn_mod.OPS.get(op)
        if not fn:
            raise Bad("unknown reborn operation %s" % op)
        build = fn(rb, cid, p)
        c = rb.write(cid, lambda d: reborn_mod.describe(op, p, rb, d), build, app.record_reborn_change)
        return self.send_json({"change": public_change(c), "reborn_sheet": rb.sheet(cid)})

    def meta(self):
        app = self.app
        return {"version": VERSION, "database": app.ctx.db.game, "accounts_db": app.ctx.db.acc, "live": app.ctx.db.live,
                "csrf": app.csrf, "gamelog": app.gamelog_src, "gamelog_error": getattr(app, "gamelog_error", None),
                "backup_dir": app.ctx.backup_dir, "audit_log": app.ctx.db.audit, "catalog_items": len(app.ctx.cat.items),
                "reborn": app.reborn.status() if app.reborn else {"available": False, "note": "reborn adapter disabled (--no-reborn or reborn.py missing)"},
                "reborn_only": bool(app.a.reborn_only),
                "rates": {"available": bool(app.rates), "management_port": app.rates.mgmt_port if app.rates else None},
                "started": app.started, "host": app.a.host, "port": app.a.port, "equip_slots": gw.EQUIP_SLOTS,
                "paperdoll": PAPERDOLL, "slot_icons": SLOT_ICON, "currency": CURRENCY}

    def module_dispatch(self, method, path, query, body):
        if not gwmods:
            return self.send_json({"error": "the module editors failed to load (modules/ import error; see the editor log)"}, 500)
        gwmods.common.ACTOR.name = (getattr(self, "editor_session", None) or {}).get("username")
        try:
            status, obj = gwmods.dispatch(self.app, method, path[len("/api/m"):], query, body)
        finally:
            gwmods.common.ACTOR.name = None
        return self.send_json(obj, status)

    def index(self, page="index.html"):
        path = os.path.join(HERE, "static", page)
        try:
            with open(path, encoding="utf-8") as f:
                html = f.read()
        except OSError:
            self.send_error(404)
            return
        html = html.replace("__CSRF__", self.app.csrf).replace("__DB__", self.app.ctx.db.game).replace("__LIVE__", "1" if self.app.ctx.db.live else "0")
        body = html.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    # ---- POST
    def do_POST(self):
        if not self.check_local():
            return
        app = self.app
        try:
            if self.auth_request("POST", urllib.parse.urlparse(self.path).path):
                return
            tok = self.headers.get("X-CSRF-Token") or ""
            if not secrets.compare_digest(tok, app.csrf):
                raise Bad("missing or wrong CSRF token (reload the page)")
            origin = self.headers.get("Origin")
            if not valid_origin(origin, app.a.public_origin):
                raise Bad("cross-origin POST refused")
            u = urllib.parse.urlparse(self.path)
            p = self.body_json()
            if p.get("force") and not app.a.allow_force:
                raise Bad("'force' options are disabled; start the server with --allow-force to enable them")
            if p.get("force_offline"):
                raise Bad("writes to an online character are refused by this UI; log the player out (or /kick) first, or use gwadmin.py --force-offline from the shell")
            if u.path.startswith("/api/m/"):
                return self.module_dispatch("POST", u.path, {k: v[0] for k, v in urllib.parse.parse_qs(u.query).items()}, p)
            m = re.match(r"^/api/reborn/character/(\d+)/(\w+)$", u.path)
            if m:
                return self.reborn_post(int(m.group(1)), m.group(2), p)
            m = re.match(r"^/api/character/(\d+)/(\w+)$", u.path)
            if m:
                cid, op = int(m.group(1)), m.group(2)
                fn = {"item": op_item, "move": op_move, "storage_money": op_storage_money, "base": op_base,
                      "skill": op_skill, "quest": op_quest, "quest_entries": op_quest_entries}.get(op)
                if not fn:
                    raise Bad("unknown operation %s" % op)
                r = fn(app, cid, p)
                r["sheet"] = app.sheet(cid)
                return self.send_json(r)
            if u.path == "/api/rates/save":
                if not app.rates:
                    raise Bad("the Server rates page needs the reborn adapter")
                try:
                    c = app.rates.save(p.get("values"), p.get("expected"), p.get("stock"), p.get("stock_expected"),
                                       p.get("note"), app.record_reborn_change)
                except rates_mod.RatesConflict as e:
                    return self.send_json({"error": str(e), "conflict": e.conflicts}, 409)
                return self.send_json({"change": public_change(c), "rates": app.rates.state()})
            if u.path == "/api/rates/reload":
                if not app.rates:
                    raise Bad("the Server rates page needs the reborn adapter")
                return self.send_json({"reload": app.rates.reload_server(), "rates": app.rates.state()})
            if u.path == "/api/undo":
                c = next((x for x in app.changes if x["id"] == p.get("change")), None)
                if c and c.get("engine") == "rates":
                    if not app.rates:
                        raise Bad("the Server rates page needs the reborn adapter")
                    try:
                        r = app.rates.undo(c, app.record_reborn_change, overwrite=bool(p.get("overwrite")))
                    except rates_mod.RatesConflict as e:
                        return self.send_json({"error": str(e), "conflict": e.conflicts}, 409)
                    app._save_change(c)
                    return self.send_json({"change": public_change(r), "result": r.get("result"), "undo_of": c["id"],
                                           "rates": app.rates.state()})
                if c and c.get("engine") == "rows" and c.get("no_undo"):
                    raise Bad("change %s cannot be undone here: %s" % (c["id"], c["no_undo"]))
                if c and c.get("engine") == "rows":
                    # module editors' non-character rows (reborn.Reborn.write_rows)
                    if not app.reborn:
                        raise Bad("reborn adapter disabled")
                    try:
                        r = app.reborn.undo_rows(c, bool(p.get("full")), app.record_reborn_change, all_changes=app.changes,
                                                 overwrite=bool(p.get("overwrite")))
                    except reborn_mod.RebornConflict as e:
                        return self.send_json({"error": str(e), "conflict": e.conflicts}, 409)
                    app._save_change(c)
                    return self.send_json({"change": public_change(r), "result": r.get("result"), "undo_of": c["id"]})
                if c and c.get("engine") == "reborn-live" and c.get("inverse") and gwmods:
                    # module editors' live edits (round2 management API): the inverse edit, live or database
                    status, obj = gwmods.dispatch_undo_live(app, c)
                    return self.send_json(obj, status)
                if c and c.get("no_undo"):
                    raise Bad("change %s cannot be undone here: %s" % (c["id"], c["no_undo"]))
                if c and c.get("engine") == "reborn":
                    if not app.reborn:
                        raise Bad("reborn adapter disabled")
                    try:
                        r = app.reborn.undo(c, bool(p.get("full")), app.record_reborn_change, all_changes=app.changes,
                                            overwrite=bool(p.get("overwrite")))
                    except reborn_mod.RebornConflict as e:
                        return self.send_json({"error": str(e), "conflict": e.conflicts}, 409)
                    app._save_change(c)
                    out = {"change": public_change(r), "result": r.get("result"), "undo_of": c["id"]}
                    out["reborn_sheet"] = app.reborn.sheet(r["cid"])
                    return self.send_json(out)
                r = app.undo(p.get("change"), bool(p.get("full")))
                if r.get("change") and r["change"].get("cid"):
                    r["sheet"] = app.sheet(r["change"]["cid"])
                return self.send_json(r)
            if u.path == "/api/account":
                return self.send_json(op_account(app, p))
            self.send_json({"error": "not found"}, 404)
        except Bad as e:
            self.send_json({"error": str(e)}, 400)
        except gw.Fail as e:
            self.send_json({"error": str(e)}, 400)
        except RebornError as e:
            self.send_json({"error": str(e)}, 400)
        except Exception as e:      # noqa: BLE001
            traceback.print_exc()
            self.send_json({"error": "%s: %s" % (type(e).__name__, e)}, 500)


def valid_origin(origin, public_origin=None):
    """Keep browser writes on the configured HTTPS origin; do not trust forwarded headers."""
    if public_origin:
        return origin == public_origin
    return not origin or urllib.parse.urlparse(origin).hostname in ("127.0.0.1", "localhost", "::1")


def build_parser():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--db-suffix", default="", help="'' = live godswar/accounts (needs --live); '_test' = godswar_test/accounts_test")
    p.add_argument("--live", action="store_true", help="allow the live databases (red banner)")
    p.add_argument("--account-auth", action="store_true", help="require GodsWar account credentials and editor_admin flag")
    p.add_argument("--recovery-verifier-file", help="private verifier for the existing owner recovery credential")
    p.add_argument("--bootstrap-token-file", help="private shared token for authenticated owner recovery proxy")
    p.add_argument("--public-origin", default=None, help="exact HTTPS origin of the authenticated reverse proxy; POSTs require matching Origin")
    p.add_argument("--port", type=int, default=8787, help="HTTP port on 127.0.0.1 (default 8787)")
    p.add_argument("--host", default="127.0.0.1", help="MariaDB host (default 127.0.0.1; use an ssh -L tunnel from the Mac)")
    p.add_argument("--mysql-port", dest="port_db", type=int, default=3306)
    p.add_argument("--user", default="root")
    p.add_argument("--password", default="")
    p.add_argument("--mysql", default=None, help="path of the mariadb/mysql client (auto)")
    p.add_argument("--catalog", default=os.path.join(TOOLS, "catalog"))
    p.add_argument("--gamelog", default=None, help="GameServer stdout log for the online check: a path, or ssh://HOST/path "
                   "(default: /opt/godswar/logs/gameserver.log for live, ~/gw-linux/run/gameserver/run.log for _test, if present)")
    p.add_argument("--backup-dir", default=os.path.join(os.getcwd(), "gwadmin-backups"))
    p.add_argument("--audit-log", default=os.path.join(os.path.expanduser("~"), ".gwadmin-audit.log"))
    p.add_argument("--allow-force", action="store_true", help="enable the 'force' tick boxes (unknown items, over-stacking, wrong slot, storage > 32)")
    p.add_argument("--reborn-psql", default="sudo -n -u postgres psql", help="psql command (without database) for the reborn (2.51) "
                   "PostgreSQL (default: sudo -n -u postgres psql = peer auth on the VPS)")
    p.add_argument("--reborn-db", default=None, help="reborn database (default godswar, READ-ONLY; e.g. godswar_admintest = writable copy; "
                   "the live godswar is written only with --live --reborn-db godswar)")
    p.add_argument("--reborn-log", default=None, help="reborn server log for the online check (default /opt/godswar251/logs/server.log "
                   "for the live DB; '' = none)")
    p.add_argument("--reborn-only", action="store_true", help="run without the 1.0 MariaDB (the 1.0 tabs then show an error)")
    p.add_argument("--rates-mgmt", type=int, default=None, help="loopback management port of the reborn server that uses "
                   "--reborn-db, for the Server rates page (GET /rates, POST /rates/reload); default 9090 for the live "
                   "godswar, none otherwise (the server then picks a change up by itself within ~5 s)")
    p.add_argument("--no-reborn", action="store_true")
    p.add_argument("--no-persistent", action="store_true", help="spawn the mysql client per statement like gwadmin.py does (slower through a tunnel)")
    p.add_argument("--gamelog-cache", type=float, default=10.0, help="seconds to reuse a fetched ssh:// GameServer log (default 10)")
    p.add_argument("--gamelog-tail", type=int, default=2_000_000, help="bytes fetched from the end of an ssh:// GameServer log (default 2000000)")
    p.add_argument("--verbose", "-v", action="store_true")
    return p


def main(argv=None):
    a = build_parser().parse_args(argv)
    if a.public_origin:
        u = urllib.parse.urlsplit(a.public_origin)
        if u.scheme != "https" or not u.hostname or u.username or u.password or u.path or u.query or u.fragment:
            raise SystemExit("--public-origin must be an HTTPS origin without path or credentials")
    # gwadmin.DB reads a.port as the MariaDB port; the HTTP port is ours
    http_port = a.port
    a.port = a.port_db
    a.dry_run = False
    try:
        app = App(a)
    except gw.Fail as e:
        print("gwadmin-web: %s" % e, file=sys.stderr)
        return 2
    a.port = http_port
    if not app.ctx.cat.items:
        print("note: no catalog at %s (run build_catalog.py); names will show as ids", file=sys.stderr)
    try:
        app.ctx.db.query("SELECT 1")
    except gw.Fail as e:
        if not a.reborn_only:
            print("gwadmin-web: cannot reach MariaDB at %s:%d: %s (use --reborn-only for the reborn side alone)" % (a.host, a.port_db, e), file=sys.stderr)
            return 2
        print("note: 1.0 MariaDB not reachable (%s); --reborn-only" % str(e)[:200], file=sys.stderr)
    Handler.app = app
    srv = ThreadingHTTPServer(("127.0.0.1", http_port), Handler)
    srv.daemon_threads = True
    banner = "** LIVE databases %s / %s **" % (app.ctx.db.game, app.ctx.db.acc) if app.ctx.db.live else "databases %s / %s" % (app.ctx.db.game, app.ctx.db.acc)
    print("gwadmin-web %s: http://127.0.0.1:%d/  (%s; MariaDB %s:%d; gamelog %s; backups %s)" % (
        VERSION, http_port, banner, a.host, a.port_db, app.gamelog_src or "none - online check uses login_status only", app.ctx.backup_dir), flush=True)
    if app.reborn:
        rb = app.reborn
        idn = getattr(rb, "identity_info", {}) or {}
        print("reborn: database %s via '%s' (psql reaches %s) - %s%s" % (rb.db, " ".join(rb.pg.base),
              ("current_database()=%s, %s" % (idn.get("db"), "port %s" % idn["port"] if idn.get("port") else "unix socket")) if idn.get("db") else "NOTHING: %s" % idn.get("error"),
              ("** LIVE, WRITES ENABLED **" if rb.live and rb.writable else "writes enabled" if rb.writable else "READ-ONLY"),
              ("; online log %s" % rb.log_path) if rb.log_path else ""), flush=True)
    # SIGTERM (pkill, systemd stop) must run the shutdown too, or the ssh ControlMaster outlives us
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    try:
        srv.serve_forever()
    except (KeyboardInterrupt, SystemExit):
        pass
    finally:
        srv.server_close()
        app.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
