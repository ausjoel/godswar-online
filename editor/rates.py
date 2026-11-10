"""rates.py - the 'Server rates' page of gwadmin-web (GodsWar-2.51/Polish RATES patch, see Polish/RATES.md).

The patched reborn server keeps every server-wide multiplier and limit in ONE table, public.server_rates
(created by the server itself at start-up, src/Godswar.Server/Infrastructure/Rates/PostgresServerRatesStore.cs).
Each row carries its own default, bounds, unit, label and description, so this page renders whatever the
running server defines and never duplicates the catalogue.  The server hot-reloads the table: it polls the
revision every 5 s, and POST /rates/reload on its loopback management port applies a change at once.

This module also exposes the stock server's own database settings that matter for rates
(monster_reward_settings = global EXP multiplier + level gap, read at start-up; medusa_instance_settings /
legacy_instance_settings = daily instance entries, read per request).

Writes go through the same psql adapter and the same live gate as the reborn character sheet (reborn.py):
one psql run per Save, BEGIN ... COMMIT, a database-identity guard, row locks, an optimistic check that the
values are still the ones the page loaded, and a change record with an Undo.
"""
import datetime
import json
import threading
import urllib.error
import urllib.request
from decimal import Decimal as D

from reborn import RebornError, lit

CATEGORY_ORDER = ["Character EXP & Talent", "Pets", "Zodiac", "Instances", "Monster drops", "Money", "Forging",
                  "Holy Box (Master Vestment Forger)",
                  "Upgrade costs", "Daily and weekly limits", "Event times"]  # ROUND2: categories of the round-2 keys

# the stock server's own settings rows (table, key column, key value, column) -> metadata
STOCK_SETTINGS = [
    {"id": "global_exp", "table": "monster_reward_settings", "where": "setting_id = 1",
     "column": "global_experience_multiplier_basis_points", "label": "Global monster EXP multiplier (stock setting)",
     "unit": "basis points (10000 = 1x)", "min": 10000, "max": 50000, "step": 500, "applies": "restart",
     "help": "Stock reborn setting, read once at start-up (MonsterRewardPolicySnapshot): multiplies Fighter, Talent "
             "and pet monster-kill EXP together with the patch's own multipliers. Applies after gw251 restart."},
    {"id": "level_gap", "table": "monster_reward_settings", "where": "setting_id = 1",
     "column": "maximum_lower_level_gap", "label": "Kill EXP level gap (stock setting)", "unit": "levels",
     "min": 0, "max": 199, "step": 1, "applies": "restart",
     "help": "A monster more than this many levels below the player pays no EXP (stock 20). Applies after "
             "gw251 restart."},
    {"id": "medusa_daily", "table": "medusa_instance_settings", "where": "instance_key = 'medusa'",
     "column": "daily_entry_limit", "label": "Medusa Island entries per day", "unit": "entries", "min": 1,
     "max": 99, "step": 1, "applies": "live",
     "help": "Read on every Medusa entry (PostgresMedusaDailyEntryClaimStore). Applies immediately."},
    {"id": "atlantis_free", "table": "legacy_instance_settings", "where": "instance_kind = 1",
     "column": "free_entry_limit", "label": "Atlantis free entries per day", "unit": "entries", "min": 0, "max": 99,
     "step": 1, "applies": "live", "help": "Read on every entry (PostgresLegacyInstanceDailyEntryClaimStore)."},
    {"id": "atlantis_paid", "table": "legacy_instance_settings", "where": "instance_kind = 1",
     "column": "paid_retry_limit", "label": "Atlantis paid (Opal) retries per day", "unit": "retries", "min": 0,
     "max": 99, "step": 1, "applies": "live", "nullable": True,
     "help": "Empty = no paid retries for this instance."},
    {"id": "wonderland_free", "table": "legacy_instance_settings", "where": "instance_kind = 2",
     "column": "free_entry_limit", "label": "Wonderland free entries per day", "unit": "entries", "min": 0,
     "max": 99, "step": 1, "applies": "live", "help": "Read on every entry."},
    {"id": "wonderland_paid", "table": "legacy_instance_settings", "where": "instance_kind = 2",
     "column": "paid_retry_limit", "label": "Wonderland paid retries per day", "unit": "retries", "min": 0,
     "max": 99, "step": 1, "applies": "live", "nullable": True, "help": "Empty = no paid retries."},
    {"id": "harbor_free", "table": "legacy_instance_settings", "where": "instance_kind = 3",
     "column": "free_entry_limit", "label": "Harbor Attack free entries per day", "unit": "entries", "min": 0,
     "max": 99, "step": 1, "applies": "live", "help": "Read on every entry."},
    {"id": "harbor_paid", "table": "legacy_instance_settings", "where": "instance_kind = 3",
     "column": "paid_retry_limit", "label": "Harbor Attack paid retries per day", "unit": "retries", "min": 0,
     "max": 99, "step": 1, "applies": "live", "nullable": True, "help": "Empty = no paid retries."},
]
STOCK_BY_ID = {s["id"]: s for s in STOCK_SETTINGS}


def _num(v):
    """Decimal -> int when whole, else a plain decimal string-free float for JSON."""
    if v is None:
        return None
    d = D(str(v))
    return int(d) if d == d.to_integral_value() else float(d)


class RatesConflict(RebornError):
    def __init__(self, msg, conflicts):
        super().__init__(msg)
        self.conflicts = conflicts


class Rates:
    def __init__(self, rb, mgmt_port=None):
        self.rb = rb
        self.mgmt_port = mgmt_port
        self.lock = threading.RLock()

    # ------------------------------------------------------------------ reads
    def table_exists(self):
        return bool(self.rb.pg.json("to_jsonb(to_regclass('public.server_rates') IS NOT NULL)", timeout=20))

    def server_status(self):
        """GET /rates of the game server that uses this database (loopback management port)."""
        if not self.mgmt_port:
            return {"reachable": False, "note": "no management port configured (--rates-mgmt PORT); the server "
                                                "still picks changes up by itself within ~5 s"}
        try:
            with urllib.request.urlopen("http://127.0.0.1:%d/rates" % int(self.mgmt_port), timeout=3) as r:
                d = json.loads(r.read().decode("utf-8"))
            d["reachable"] = True
            return d
        except (urllib.error.URLError, OSError, ValueError) as e:
            return {"reachable": False, "note": "management port %s: %s (stock server, stopped, or not this "
                                                "database)" % (self.mgmt_port, e)}

    def reload_server(self):
        if not self.mgmt_port:
            return {"reachable": False, "note": "no management port configured"}
        try:
            req = urllib.request.Request("http://127.0.0.1:%d/rates/reload" % int(self.mgmt_port), data=b"",
                                         method="POST")
            with urllib.request.urlopen(req, timeout=8) as r:
                d = json.loads(r.read().decode("utf-8"))
            d["reachable"] = True
            return d
        except (urllib.error.URLError, OSError, ValueError) as e:
            return {"reachable": False, "note": "reload failed: %s" % e}

    def state(self):
        exists = self.table_exists()
        out = {"database": self.rb.db, "writable": self.rb.writable, "live": self.rb.live, "table": exists,
               "categories": [], "rows": [], "log": [], "revision": None, "stock": [], "server": self.server_status()}
        if exists:
            d = self.rb.pg.json(
                "jsonb_build_object("
                "'rows', (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r.sort_order, r.rate_key), '[]') FROM "
                "public.server_rates r), "
                "'state', (SELECT to_jsonb(s) FROM public.server_rates_state s WHERE setting_id = 1), "
                "'log', (SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.id DESC), '[]') FROM "
                "(SELECT * FROM public.server_rates_log ORDER BY id DESC LIMIT 60) l))", timeout=30)
            rows = d["rows"]
            for r in rows:
                for k in ("value", "default_value", "min_value", "max_value", "step"):
                    r[k] = _num(r[k])
            order = {c: i for i, c in enumerate(CATEGORY_ORDER)}
            rows.sort(key=lambda r: (order.get(r["category"], 99), r["category"], r["sort_order"], r["rate_key"]))
            out["rows"] = rows
            cats = []
            for r in rows:
                if r["category"] not in cats:
                    cats.append(r["category"])
            out["categories"] = cats
            out["revision"] = d["state"]["revision"] if d.get("state") else None
            out["state"] = d.get("state")
            out["log"] = [dict(l, old_value=_num(l.get("old_value")), new_value=_num(l.get("new_value"))) for l in d["log"]]
        out["stock"] = self.stock_settings()
        srv = out["server"]
        checks = []
        if not exists:
            checks.append({"severity": "warn", "msg": "public.server_rates does not exist in %s: this database is used "
                                                      "by the STOCK reborn server (no rates patch) - see Polish/RATES.md "
                                                      "to deploy the patched server" % self.rb.db})
        else:
            for r in out["rows"]:
                if not (D(str(r["min_value"])) <= D(str(r["value"])) <= D(str(r["max_value"]))):
                    checks.append({"severity": "error", "msg": "%s = %s is outside %s..%s" % (
                        r["rate_key"], r["value"], r["min_value"], r["max_value"])})
            if srv.get("reachable"):
                if srv.get("revision") != out["revision"]:
                    checks.append({"severity": "warn", "msg": "server runs revision %s, database is at %s (it reloads "
                                                              "within %ss)" % (srv.get("revision"), out["revision"],
                                                                               srv.get("pollSeconds"))})
                for r in out["rows"]:
                    sv = (srv.get("rates") or {}).get(r["rate_key"])
                    if sv is not None and D(str(sv)) != D(str(r["value"])):
                        checks.append({"severity": "warn", "msg": "%s: server uses %s, database holds %s" % (
                            r["rate_key"], sv, r["value"])})
                if srv.get("lastError"):
                    checks.append({"severity": "error", "msg": "server reload error: %s" % srv["lastError"]})
        out["checks"] = checks
        return out

    def stock_settings(self):
        res = []
        for s in STOCK_SETTINGS:
            try:
                v = self.rb.pg.json("(SELECT to_jsonb(%s) FROM public.%s WHERE %s)" % (
                    s["column"], s["table"], s["where"]), timeout=20)
                res.append(dict(s, value=_num(v), available=True))
            except RebornError as e:
                res.append(dict(s, value=None, available=False, note=str(e)[:200]))
        return res

    # ------------------------------------------------------------------ writes
    def _guard(self):
        if not self.rb.writable:
            raise RebornError("this server is read-only for reborn database %s (live writes need --live "
                              "--reborn-db godswar)" % self.rb.db)

    def save(self, values, expected, stock, stock_expected, note, changes, actor="gwadmin-web"):
        """values: {rate_key: new}; expected: {rate_key: value the page showed}; stock: {id: new};
        stock_expected: {id: shown}.  One transaction; refused with 409 when anything changed meanwhile."""
        self._guard()
        values, expected, stock, stock_expected = values or {}, expected or {}, stock or {}, stock_expected or {}
        note = str(note or "")[:200]
        with self.lock:
            cur = self.state()
            if (values or expected) and not cur["table"]:
                raise RebornError("public.server_rates does not exist in %s (the server is not the RATES build)" % self.rb.db)
            rows = {r["rate_key"]: r for r in cur["rows"]}
            stock_now = {s["id"]: s for s in cur["stock"]}
            plan, before, after, conflicts = [], {}, {}, []
            for k, v in values.items():
                r = rows.get(k)
                if not r:
                    raise RebornError("unknown rate %r" % k)
                try:
                    nv = D(str(v).strip().replace(",", ""))
                except Exception:
                    raise RebornError("%s must be a number (got %r)" % (r["label"], v))
                if not nv.is_finite() or nv < D(str(r["min_value"])) or nv > D(str(r["max_value"])):
                    raise RebornError("%s must be %s..%s (got %s)" % (r["label"], r["min_value"], r["max_value"], v))
                if r["kind"] == "absolute" and nv != nv.to_integral_value():
                    raise RebornError("%s must be a whole number" % r["label"])
                nv = nv.quantize(D("0.0001"))
                if k in expected and D(str(expected[k])) != D(str(r["value"])):
                    conflicts.append({"table": "server_rates", "row": k, "what": "%s -> %s" % (expected[k], r["value"])})
                if D(str(r["value"])) == nv:
                    continue
                before[k], after[k] = _num(r["value"]), _num(nv)
                plan.append((k, nv))
            stock_plan = []
            for sid, v in stock.items():
                s = STOCK_BY_ID.get(sid)
                if not s or not stock_now.get(sid, {}).get("available"):
                    raise RebornError("unknown or unavailable stock setting %r" % sid)
                if (v is None or str(v).strip() == "") and s.get("nullable"):
                    nv = None
                else:
                    try:
                        nv = int(D(str(v).strip()))
                    except Exception:
                        raise RebornError("%s must be a whole number (got %r)" % (s["label"], v))
                    if D(str(v).strip()) != nv or nv < s["min"] or nv > s["max"]:
                        raise RebornError("%s must be a whole number %s..%s (got %s)" % (s["label"], s["min"], s["max"], v))
                old = stock_now[sid]["value"]
                if sid in stock_expected and stock_expected[sid] != old:
                    conflicts.append({"table": s["table"], "row": s["column"], "what": "%s -> %s" % (stock_expected[sid], old)})
                if old == nv:
                    continue
                before["stock:" + sid], after["stock:" + sid] = old, nv
                stock_plan.append((s, nv))
            if conflicts:
                raise RatesConflict("the values changed since the page was loaded (another admin, an Undo, or SQL); "
                                    "reload the page", conflicts)
            if not plan and not stock_plan:
                raise RebornError("nothing to change")
            sql = self._sql(plan, stock_plan, note, actor, guard_values={k: rows[k]["value"] for k, _ in plan},
                            guard_stock={s["id"]: stock_now[s["id"]]["value"] for s, _ in stock_plan})
            self.rb.pg.run(sql, timeout=60)
            reload = self.reload_server() if plan else {"reachable": False, "note": "no server_rates change"}
            stamp = datetime.datetime.now()
            labels = {k: rows[k]["label"] for k, _ in plan}
            labels.update({"stock:" + s["id"]: s["label"] for s, _ in stock_plan})
            action = "server rates: " + "; ".join("%s %s -> %s" % (labels[k], before[k], after[k]) for k in before)
            c = {"engine": "rates", "id": "rt-" + stamp.strftime("%Y%m%d-%H%M%S-%f"), "time": stamp.isoformat(timespec="seconds"),
                 "database": self.rb.db, "character": "(server rates)", "cid": None, "action": action, "note": note,
                 "sql": [{"db": self.rb.db, "sql": sql}], "before": before, "after": after, "labels": labels,
                 "reload": reload, "output": self._reload_text(reload, after), "undone": False}
            changes(c)
            return c

    @staticmethod
    def _reload_text(reload, after):
        if not reload.get("reachable"):
            return "saved; " + (reload.get("note") or "server not reachable") + " (it polls the table every few seconds)"
        rates = reload.get("rates") or {}
        live = ["%s=%s" % (k, rates.get(k)) for k in after if not k.startswith("stock:") and k in rates]
        return "server reloaded revision %s%s" % (reload.get("revision"), (": " + ", ".join(live)) if live else "")

    def _sql(self, plan, stock_plan, note, actor, guard_values, guard_stock):
        lines = ["BEGIN;", "SET LOCAL lock_timeout = '5s';", "SET LOCAL statement_timeout = '30s';", self.rb.db_guard_sql(),
                 "SET LOCAL godswar.rates_actor = %s;" % lit(actor), "SET LOCAL godswar.rates_note = %s;" % lit(note or "gwadmin-web")]
        if plan:
            keys = ", ".join(lit(k) for k, _ in plan)
            lines.append("SELECT 1 FROM public.server_rates WHERE rate_key IN (%s) FOR UPDATE;" % keys)
            # optimistic guard inside the transaction: nobody changed the rows since they were read
            checks = " OR ".join("(rate_key = %s AND value <> %s)" % (lit(k), lit(D(str(v)))) for k, v in guard_values.items())
            lines.append("DO $gwr$ BEGIN IF EXISTS (SELECT 1 FROM public.server_rates WHERE %s) THEN RAISE EXCEPTION "
                         "'gwadmin: server_rates changed since it was read; nothing written'; END IF; END $gwr$;" % checks)
            vals = ", ".join("(%s, %s::numeric)" % (lit(k), lit(v)) for k, v in plan)
            lines.append("UPDATE public.server_rates AS r SET value = v.value, updated_by = %s, updated_at = now() "
                         "FROM (VALUES %s) AS v(rate_key, value) WHERE r.rate_key = v.rate_key;" % (lit(actor), vals))
        for s, nv in stock_plan:
            old = guard_stock[s["id"]]
            cond = "%s IS NULL" % s["column"] if old is None else "%s = %s" % (s["column"], lit(old))
            extra = ", updated_by = %s" % lit(actor) if s["table"] == "monster_reward_settings" else ""
            extra += ", updated_at = now()" if s["table"] in ("medusa_instance_settings",) else ""
            extra += ", updated_at = clock_timestamp()" if s["table"] == "legacy_instance_settings" else ""
            lines.append("DO $gws$ BEGIN UPDATE public.%s SET %s = %s%s WHERE %s AND %s; IF NOT FOUND THEN RAISE EXCEPTION "
                         "'gwadmin: %s.%s changed since it was read; nothing written'; END IF; END $gws$;" % (
                             s["table"], s["column"], lit(nv), extra, s["where"], cond, s["table"], s["column"]))
        lines.append("COMMIT;")
        return "\n".join(lines) + "\n"

    def undo(self, c, changes, overwrite=False):
        """Put the values of change c back.  Refused (409) when the current values are not the ones that change
        wrote, unless overwrite."""
        self._guard()
        if c.get("undone"):
            raise RebornError("change %s is already undone" % c["id"])
        if c.get("database") != self.rb.db:
            raise RebornError("change %s was made on database %s; this server edits %s" % (c["id"], c.get("database"), self.rb.db))
        with self.lock:
            cur = self.state()
            rows = {r["rate_key"]: r for r in cur["rows"]}
            stock_now = {s["id"]: s for s in cur["stock"]}
            conflicts, values, expected, stock, stock_expected = [], {}, {}, {}, {}
            for k, want_now in (c.get("after") or {}).items():
                if k.startswith("stock:"):
                    sid = k[6:]
                    now = stock_now.get(sid, {}).get("value")
                    if now != want_now:
                        conflicts.append({"table": STOCK_BY_ID[sid]["table"], "row": STOCK_BY_ID[sid]["column"],
                                          "what": "%s -> %s" % (want_now, now)})
                    stock[sid], stock_expected[sid] = c["before"][k], now
                else:
                    now = rows.get(k, {}).get("value")
                    if now is None or D(str(now)) != D(str(want_now)):
                        conflicts.append({"table": "server_rates", "row": k, "what": "%s -> %s" % (want_now, now)})
                    values[k], expected[k] = c["before"][k], now
            if conflicts and not overwrite:
                raise RatesConflict("the values changed since this write", conflicts)
            r = self.save(values, expected, stock, stock_expected, "undo of %s" % c["id"], changes)
            r["undo_of"] = c["id"]
            r["action"] = "undo " + c["id"] + ": " + r["action"].split(": ", 1)[-1]
            c["undone"] = True
            r["result"] = {"identical": {"values": True}, "all_identical": True}
            return r


def describe_for_log(c):
    return c.get("action", "")
