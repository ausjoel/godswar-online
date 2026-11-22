"""modules/account_tools.py - B21 item 24: account search, item search (find illegal items) and account renumbering.

* GET  search      characters with their accounts, filtered by name, class, faction, level, a fighting stat (the
                   server's own character_stat_summary view), Silver / Gold, guild, map, online, banned, GM, deleted.
* GET  items       every item in bags, warehouses and worn slots (optionally only some places), by item name / id,
                   with what is wrong with it: not in the server or the client, GM / dead items, stack above 99 or above
                   the item's own stack size, quality / grade above the client; ?deep=1 also runs the Inventory page's
                   full item check on every row.
* POST renumber/preview  {mode: "compact"} (accounts become 1..N in their current order) or {mode: "set", from, to}
                   -> the dry-run diff: every account that moves, every table and how many rows change, warnings.
* POST renumber/apply    the same + {token} (+ {confirm_config: true} when a moved account is named in the server's
                   configuration): one transaction.

How a renumber is written (accounts.id is the parent of ~30 account columns; 11 foreign keys are NO ACTION on update,
the rest have none, and the ledgers / settlement tables are append-only by trigger):
  1. lock every moved account and its characters, refuse if any of them is logged in (login_status, presence token,
     character checkpoint owner) - checked again inside the transaction;
  2. inside a short session_replication_role = replica window (foreign-key and append-only triggers off, the same
     precedent as reborn.py's Undo of hatch evidence): every account column old -> OFFSET + new, then -> new, so no
     unique key collides on the way; int[] columns with array_replace;
  3. replica off; guard SQL proves every foreign key still has its parent and no column still holds an old number;
  4. the id sequence is moved to the new maximum.
  Old receipts and event payloads (command_inbox / outbox_events / command_audit JSON, Wonderland loot evidence) keep
  the number they were written with: they are history, not links.
  There is no automatic Undo (the append-only triggers forbid restoring the old rows one by one); the result carries
  the inverse map, and "renumber back" with mode "map" puts every account back in one step.
"""
import csv
import glob
import hashlib
import json
import os
import re

from . import common
from .common import Bad, Conflict, Refused, lit

M = common.module(
    "account_tools", "Accounts: search & renumber",
    icon={"sprite": 542},
    order=6,
    status="ready",
    description="Find characters by class, faction, level and stats; find items in every bag and warehouse (and what is "
                "illegal about them); renumber accounts after deletions with a dry run first.",
    needs_character=False,
)

OFFSET = 1_000_000_000
# every account-number column reviewed on 2026-11-21 (live copy). Discovery runs each time; a column that is not in
# this list makes the renumber refuse until it is reviewed (a newer server may add one).
REVIEWED = {
    "public.accounts.id", "public.account_realm.account_id", "public.character_base.account_id",
    "public.account_entitlements.account_id", "public.atlantis_completion_reward_members.account_id",
    "public.character_class_change_receipts.account_id", "public.character_currency_ledger.account_id",
    "public.character_economy_baseline.account_id", "public.character_inventory_baseline_items.account_id",
    "public.character_inventory_ledger.account_id", "public.faction_crier_daily_claims.account_id",
    "public.faction_crier_exchange_settlements.account_id", "public.faction_crier_weekly_reclaims.account_id",
    "public.harbor_attack_completion_reward_members.account_id", "public.holy_suit_daily_exp_storage.account_id",
    "public.legacy_game_login_handoffs.account_id", "public.legacy_instance_opal_payments.account_id",
    "public.monster_death_pet_experience.account_id", "public.monster_death_reward_settlements.account_id",
    "public.monster_loot_pickup_claims.account_id", "public.online_award_claim_settlements.account_id",
    "public.warehouse_expansion_settlements.account_id", "public.wonderland_boss_loot_claims.account_id",
    "public.wonderland_chest_claims.account_id", "public.wonderland_medal_awards.account_id",
    "public.wonderland_title_members.account_id", "public.pet_durable_command_evidence.account_id",
    "public.character_stat_summary.account_id", "public.operator_log.actor_account_id",
    "public.wonderland_title_runs.admitted_account_ids",
    "signup.account_emails.account_id", "signup.account_settings.account_id", "signup.email_tokens.account_id",
    # B22 d56: on live 2026-11-21 (read only). A deletion row (30-day Restore, then the purge timer) and a user-name hold
    # belong to their account and move with it; award claims and session-health rows are per-account history that is
    # also keyed by the account, so they follow it like the ledgers do.
    "public.account_deletions.account_id", "public.account_username_holds.account_id",
    "public.login_window_award_claims.account_id", "public.session_health.account_id",
}
# B22 d56 (the admin: "grant" = renumbering is allowed on the live editor, zero-player release only): a renumber runs only
# while NOBODY is online on the whole server, not just the moved accounts. Checked in the dry run (Apply is refused),
# at Apply, and again inside the transaction.
ONLINE_SQL = ("(a.login_status <> 0 OR a.login_presence_token IS NOT NULL OR EXISTS (SELECT 1 FROM character_base c "
              "WHERE c.account_id = a.id AND c.checkpoint_owner_id IS NOT NULL))")
HISTORY_JSON = (("public", "command_inbox", "result_payload"), ("public", "outbox_events", "payload"),
                ("public", "command_audit", "detail_payload"), ("public", "wonderland_boss_loot_claims", "evidence"))
# the server's configuration names account numbers (developer commands, GM chat commands for account 3)
CONFIG_FILES = ("/etc/systemd/system/godswar251.service", "/etc/systemd/system/godswar251.service.d/*.conf",
                "/opt/godswar251/appsettings.json", "/opt/godswar251/*.env")
DEFAULT_CONFIG_IDS = {3}            # GODSWAR_DEVELOPER_ACCOUNT_IDS=3 and the gm.conf drop-in (CLAUDE.md)
STATS = ("max_hp", "max_mp", "physical_attack", "physical_defense", "magic_attack", "magic_defense", "hit", "dodge",
         "critical", "critical_resistance", "damage_absorb", "physical_damage_bonus", "magic_damage_bonus", "cure_bonus",
         "hp_recovery", "mp_recovery", "ignore_physical_defense", "ignore_magic_defense", "weapon_score", "armor_score")
LOCS = {"equip": 0, "bag": 1, "warehouse": 3}
LOC_NAMES = {0: "Worn", 1: "Bag", 2: "Server temp", 3: "Warehouse"}
STACK_CAP = 99                      # the admin: no item stack above 99
SCAN_MAX = 500000                   # rows read for "only items with a problem" (the filter runs after the read)


def _rb(ctx):
    return ctx.need_rb()


def _int(ctx, key, lo, hi):
    v = ctx.q(key)
    if v in (None, ""):
        return None
    return common.to_int(v, lo, hi, key)


def _ints(ctx, key, lo, hi):
    v = ctx.q(key)
    if v in (None, ""):
        return []
    return [common.to_int(x, lo, hi, key) for x in str(v).split(",") if x.strip() != ""]


# =========================================================================== search
def search_where(ctx):
    """The WHERE clause of a character search from the query string (every value through lit / to_int)."""
    w = ["true"]
    q = (ctx.q("q") or "").strip()
    if q:
        like = lit("%" + q + "%")
        w.append("(c.name ILIKE %s OR a.username ILIKE %s OR a.email ILIKE %s OR CAST(a.id AS text) = %s OR CAST(c.id AS text) = %s)"
                 % (like, like, like, lit(q), lit(q)))
    cls = _ints(ctx, "class", 0, 3)
    if cls:
        w.append("c.profession IN (%s)" % ", ".join(map(str, cls)))
    camp = _int(ctx, "faction", 0, 1)
    if camp is not None:
        w.append("c.camp = %d" % camp)
    for key, col, lo, hi in (("level_min", "c.fighter_job_lv", 0, 1000), ("silver_min", 'c."Money"', 0, 2 ** 31 - 1),
                             ("gold_min", 'c."Stone"', 0, 2 ** 31 - 1)):
        v = _int(ctx, key, lo, hi)
        if v is not None:
            w.append("%s >= %d" % (col, v))
    v = _int(ctx, "level_max", 0, 1000)
    if v is not None:
        w.append("c.fighter_job_lv <= %d" % v)
    stat = ctx.q("stat")
    if stat:
        if stat not in STATS:
            raise Bad("stat must be one of %s" % ", ".join(STATS))
        smin, smax = _int(ctx, "stat_min", -2 ** 31, 2 ** 31 - 1), _int(ctx, "stat_max", -2 ** 31, 2 ** 31 - 1)
        if smin is not None:
            w.append("s.%s >= %d" % (stat, smin))
        if smax is not None:
            w.append("s.%s <= %d" % (stat, smax))
    guild = _int(ctx, "guild", 0, 2 ** 31 - 1)
    if guild is not None:
        w.append("c.consortia = %d" % guild)
    mp = _int(ctx, "map", 0, 32767)
    if mp is not None:
        w.append('c."Map" = %d' % mp)
    flags = {"online": "(c.checkpoint_owner_id IS NOT NULL OR a.login_status <> 0 OR a.login_presence_token IS NOT NULL)",
             "banned": "a.status = 1", "gm": 'c."GM" > 0', "donator": "a.donator_tier > 0", "editor_admin": "a.editor_admin"}
    for k, sql in flags.items():
        v = ctx.q(k)
        if v in ("1", "true", "yes"):
            w.append(sql)
        elif v in ("0", "false", "no"):
            w.append("NOT " + sql)
    if ctx.q("deleted") not in ("1", "true", "yes", "all"):
        w.append("coalesce(c.lifecycle_state, 'active') = 'active'")
    return " AND ".join(w)


@M.get("search")
def search(ctx):
    """GET /api/m/account_tools/search?q=&class=0,1&faction=&level_min=&level_max=&stat=&stat_min=&stat_max=&silver_min=
    &gold_min=&guild=&map=&online=&banned=&gm=&donator=&deleted=&limit= - characters with their account and stats."""
    rb = _rb(ctx)
    limit = _int(ctx, "limit", 1, 5000) or 500
    stat = ctx.q("stat") if ctx.q("stat") in STATS else None
    rows = rb.pg.json("""(SELECT coalesce(json_agg(r), '[]') FROM (
        SELECT c.id, c.name, c.account_id, a.username, a.email, a.status AS account_status, a.donator_tier, a.editor_admin,
               c.profession, c.camp, c.fighter_job_lv AS level, c."Money" AS silver, c."Stone" AS gold, c."BindingGold" AS bgold,
               c.consortia AS guild, c."Map" AS map, c."GM" AS gm, c.lifecycle_state,
               (c.checkpoint_owner_id IS NOT NULL OR a.login_status <> 0 OR a.login_presence_token IS NOT NULL) AS online,
               to_jsonb(s) - 'user_id' - 'account_id' - 'name' - 'profession' - 'level' AS stats
        FROM character_base c JOIN accounts a ON a.id = c.account_id
        LEFT JOIN character_stat_summary s ON s.user_id = c.id
        WHERE %s ORDER BY %s LIMIT %d) r)""" % (search_where(ctx), ("s.%s DESC NULLS LAST, c.id" % stat) if stat else "c.account_id, c.id", limit))
    for r in rows:
        r["class"] = common.CLASS_NAMES.get(r["profession"], str(r["profession"]))
        r["faction"] = common.CAMP_NAMES.get(r["camp"], str(r["camp"]))
    accounts = len({r["account_id"] for r in rows})
    return {"characters": rows, "accounts": accounts, "limit": limit, "stats": list(STATS)}


# =========================================================================== items
_DEAD = None


def dead_items():
    global _DEAD
    if _DEAD is None:
        p = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data", "dead-items.csv")
        try:
            with open(p, encoding="utf-8") as f:
                _DEAD = {int(r["id"]): r.get("status") or "dead" for r in csv.DictReader(f)}
        except OSError:
            _DEAD = {}
    return _DEAD


def item_flags(rb, r, client_items, dead):
    """What is wrong with one character_items row (cheap checks; no per-character dump)."""
    out = []
    pid = int(r["prop_id"])
    srv = rb.cat()["items"].get(str(pid))
    cli = client_items.get(str(pid))
    name = (srv or {}).get("name") or (cli or {}).get("name") or ("item %d" % pid)
    if not srv:
        out.append("not in the server's item templates")
    if not cli:
        out.append("not in your client")
    if name.startswith("GM "):
        out.append("GM item")
    if pid in dead:
        out.append("dead item (%s)" % dead[pid])
    try:
        overlap = max(1, int((srv or {}).get("overlap") or (cli or {}).get("overlap") or 1))
    except (TypeError, ValueError):
        overlap = 1
    st = int(r.get("stack") or 1)
    if st > STACK_CAP:
        out.append("stack %d is above 99" % st)
    elif st > overlap:
        out.append("stack %d is above its stack size %d" % (st, overlap))
    q, g = int(r.get("item_quality") or 0), int(r.get("item_grade") or 0)
    mq, mg = int((cli or {}).get("max_quality") or 20), int((cli or {}).get("max_grade") or 25)
    if q > mq:
        out.append("quality Q%d above the client's Q%d" % (q, mq))
    if g > mg:
        out.append("grade %d above the client's G%d" % (g, mg))
    if int(r.get("holy_socket_count") or 0) > 4:
        out.append("%s sockets (the client shows 4)" % r["holy_socket_count"])
    if r.get("item_location") == 0 and srv and (srv.get("minlv") or 0) > int(r.get("char_level") or 0) and int(r.get("slot_index") or 0) < 12:
        out.append("worn below its level (needs Lv%s)" % srv.get("minlv"))
    return name, (srv or cli or {}).get("sprite") if cli else None, out


@M.get("items")
def items(ctx):
    """GET /api/m/account_tools/items?q=<name or id>&loc=bag,warehouse,equip&illegal=1&deep=1&min_quality=&min_grade=&limit=
    - item rows in every character's bag / warehouse / worn slots with the character, account and what is wrong."""
    rb = _rb(ctx)
    limit = _int(ctx, "limit", 1, 20000) or 2000
    w = ["coalesce(c.lifecycle_state, 'active') = 'active'" if ctx.q("deleted") not in ("1", "true") else "true"]
    locs = [x for x in (ctx.q("loc") or "").split(",") if x]
    if locs:
        if any(x not in LOCS for x in locs):
            raise Bad("loc must be bag, warehouse and/or equip")
        w.append("i.item_location IN (%s)" % ", ".join(str(LOCS[x]) for x in locs))
    q = (ctx.q("q") or "").strip()
    client_items = common.CATALOG.file("items") or {}
    if q:
        if re.match(r"^\d+(,\d+)*$", q):
            ids = [int(x) for x in q.split(",")]
        else:
            ql = q.lower()
            ids = sorted({int(k) for k, v in list(rb.cat()["items"].items()) + list(client_items.items())
                          if ql in str((v or {}).get("name") or "").lower()})
            if not ids:
                return {"items": [], "count": 0, "flagged": 0, "limit": limit, "note": "no item is called like %r" % q}
        w.append("i.prop_id IN (%s)" % ", ".join(str(int(x)) for x in ids[:5000]))
    for key, col in (("min_quality", "i.item_quality"), ("min_grade", "i.item_grade"), ("min_stack", "i.stack")):
        v = _int(ctx, key, 0, 32767)
        if v is not None:
            w.append("%s >= %d" % (col, v))
    rows = rb.pg.json("""(SELECT coalesce(json_agg(r), '[]') FROM (
        SELECT i.id, i.user_id, c.name AS character, c.account_id, a.username, c.fighter_job_lv AS char_level, c.lifecycle_state,
               i.item_location, i.slot_index, i.prop_id, i.stack, i.bound, i.item_quality, i.item_grade, i.holy_suit_code,
               i.holy_socket_count, i.attribute1, i.attribute2, i.attribute3, i.attribute4, i.attribute5, i.class_attribute1
        FROM character_items i JOIN character_base c ON c.id = i.user_id JOIN accounts a ON a.id = c.account_id
        WHERE %s ORDER BY i.prop_id, c.id, i.item_location, i.slot_index LIMIT %d) r)""" % (
        " AND ".join(w), SCAN_MAX if ctx.q("illegal") in ("1", "true") else limit + 1))
    truncated = len(rows) >= (SCAN_MAX if ctx.q("illegal") in ("1", "true") else limit + 1)
    dead = dead_items()
    deep = ctx.q("deep") in ("1", "true")
    dumps = {}
    out = []
    for r in rows:
        name, sprite, flags = item_flags(rb, r, client_items, dead)
        if deep and r["item_location"] in (0, 1, 3):
            from . import inventory as INV
            d = dumps.get(r["user_id"])
            if d is None:
                d = dumps[r["user_id"]] = rb.dump(r["user_id"])
            full = next((x for x in d.get("character_items") or [] if x["id"] == r["id"]), None)
            if full:
                try:
                    _pid, _meta, warn = INV._check_item(rb, d, r["item_location"], r["slot_index"], dict(full), False, False)
                    flags += [x for x in warn if x not in flags]
                except Bad as e:
                    flags.append(str(e))
        r.update(name=name, sprite=sprite, flags=flags, where=LOC_NAMES.get(r["item_location"], str(r["item_location"])))
        if ctx.q("illegal") in ("1", "true") and not flags:
            continue
        if len(out) >= limit:
            truncated = True
            break
        out.append(r)
    return {"items": out, "count": len(out), "flagged": sum(1 for r in out if r["flags"]), "limit": limit, "deep": deep,
            "scanned": len(rows), "truncated": truncated}


# =========================================================================== renumber
def account_columns(rb):
    """Every column that holds an account number: name account_id / *_account_id / *_account_ids, or a foreign key to
    public.accounts, in base tables of public and signup. [{schema, table, column, array, fk, pk: [..]}]"""
    rows = rb.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.schema, r.tbl, r.col), '[]') FROM (
        SELECT n.nspname AS schema, cl.relname AS tbl, at.attname AS col, at.attndims > 0 OR t.typcategory = 'A' AS is_array,
               EXISTS (SELECT 1 FROM pg_constraint k WHERE k.contype = 'f' AND k.conrelid = cl.oid AND at.attnum = ANY (k.conkey)
                       AND k.confrelid = 'public.accounts'::regclass) AS fk,
               (SELECT json_agg(a2.attname ORDER BY array_position(i.indkey::int2[], a2.attnum)) FROM pg_index i
                  JOIN pg_attribute a2 ON a2.attrelid = i.indrelid AND a2.attnum = ANY (i.indkey)
                 WHERE i.indrelid = cl.oid AND i.indisprimary) AS pk,
               cl.relkind AS kind
        FROM pg_attribute at JOIN pg_class cl ON cl.oid = at.attrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
        JOIN pg_type t ON t.oid = at.atttypid
        WHERE n.nspname IN ('public', 'signup') AND at.attnum > 0 AND NOT at.attisdropped AND cl.relkind IN ('r', 'p', 'v', 'm')
          AND (at.attname = 'account_id' OR at.attname LIKE '%%\\_account\\_id' OR at.attname LIKE '%%\\_account\\_ids'
               OR (n.nspname = 'public' AND cl.relname = 'accounts' AND at.attname = 'id')
               OR EXISTS (SELECT 1 FROM pg_constraint k WHERE k.contype = 'f' AND k.conrelid = cl.oid AND at.attnum = ANY (k.conkey)
                          AND k.confrelid = 'public.accounts'::regclass))) r)""")
    out = []
    for r in rows:
        key = "%s.%s.%s" % (r["schema"], r["tbl"], r["col"])
        out.append({"key": key, "schema": r["schema"], "table": r["tbl"], "column": r["col"], "array": bool(r["is_array"]),
                    "fk": bool(r["fk"]), "pk": r["pk"] or [], "view": r["kind"] in ("v", "m"), "reviewed": key in REVIEWED})
    return out


def _qual(c):
    return "%s.%s" % (common.ident(c["schema"]), common.ident(c["table"]))


def config_ids():
    """Account numbers the server configuration names (developer / GM accounts): read the unit files when this editor
    runs on the server, else the known default (account 3)."""
    ids, seen = set(DEFAULT_CONFIG_IDS), []
    for pat in CONFIG_FILES:
        for p in glob.glob(pat):
            try:
                with open(p, encoding="utf-8", errors="replace") as f:
                    txt = f.read()
            except OSError:
                continue
            for m in re.finditer(r"ACCOUNT_IDS?[\"']?\s*[=:]\s*[\"']?([\d, ]+)", txt):
                found = {int(x) for x in re.findall(r"\d+", m.group(1))}
                if found:
                    ids |= found
                    seen.append(p)
    return ids, seen


def config_hits(m, cfg):
    """Configured numbers this renumber touches, both ways: a configured account moving away, and an account moving
    ONTO a configured number (it would silently get developer / GM rights). -> (sorted numbers, sentences)."""
    named, text = set(), []
    for old, new in sorted(m.items()):
        if old in cfg:
            named.add(old)
            text.append("#%d moves to #%d, so the setting no longer points at it" % (old, new))
        if new in cfg:
            named.add(new)
            text.append("#%d will become #%d, which the configuration names" % (old, new))
    return sorted(named), text


def plan_map(rb, body):
    """{old: new} from {mode: compact} / {mode: set, from, to} / {mode: map, map: {old: new}} (the inverse of an earlier run)."""
    if not isinstance(body, dict):
        raise Bad("renumber options must be an object")
    ids = [int(x) for x in rb.pg.json("(SELECT coalesce(json_agg(id ORDER BY id), '[]') FROM accounts)")]
    have = set(ids)
    mode = body.get("mode", "compact")
    if mode == "compact":
        start = common.to_int(body.get("start", 1), 1, 2 ** 31 - 1, "first number")
        m = {old: start + i for i, old in enumerate(ids) if old != start + i}
    elif mode == "set":
        old = common.to_int(body.get("from"), 1, 2 ** 31 - 1, "account number")
        new = common.to_int(body.get("to"), 1, OFFSET - 1, "new number")
        if old not in have:
            raise Bad("there is no account #%d" % old)
        if new in have and new != old:
            raise Bad("account #%d already exists: pick a free number (or compact first)" % new)
        m = {old: new} if old != new else {}
    elif mode == "map":
        raw = body.get("map")
        if not isinstance(raw, dict) or not raw:
            raise Bad("map must be {old: new}")
        m = {common.to_int(k, 1, 2 ** 31 - 1, "old number"): common.to_int(v, 1, OFFSET - 1, "new number") for k, v in raw.items()}
        m = {k: v for k, v in m.items() if k != v}
        missing = [k for k in m if k not in have]
        if missing:
            raise Bad("no account %s" % ", ".join("#%d" % k for k in missing))
        final = (have - set(m)) | set(m.values())
        if len(final) != len(have) or len(set(m.values())) != len(m):
            raise Bad("the map would give two accounts the same number")
    else:
        raise Bad("mode must be compact, set or map")
    if any(v >= OFFSET for v in m.values()):
        raise Bad("new numbers must be below %d" % OFFSET)
    return m, ids


def renumber_plan(ctx, body):
    rb = _rb(ctx)
    m, ids = plan_map(rb, body)
    cols = account_columns(rb)
    unreviewed = [c["key"] for c in cols if not c["reviewed"] and not c["view"]]
    old = sorted(m)
    tables = []
    seq = rb.pg.json("(SELECT to_jsonb(pg_get_serial_sequence('public.accounts', 'id')))")
    rights = missing_rights(rb, cols, seq)
    if rights:
        # without the grants the row counts below would fail with "permission denied": say what is missing instead
        raise Refused("this editor's database login may not %s, which a renumber needs. The lead runs the grant file "
                      "once (as postgres), then renumbering works here." % "; ".join(rights))
    if old:
        olds = ", ".join(map(str, old))
        for c in cols:
            if c["view"]:
                continue
            if c["array"]:
                cond = "%s && ARRAY[%s]::int[]" % (common.ident(c["column"]), olds)
            else:
                cond = "%s IN (%s)" % (common.ident(c["column"]), olds)
            n = rb.pg.json("(SELECT to_jsonb(count(*)) FROM %s WHERE %s)" % (_qual(c), cond))
            tables.append(dict(c, rows=int(n or 0), where=cond))
    accounts = rb.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.id), '[]') FROM (
        SELECT a.id, a.username, a.login_status, a.login_presence_token IS NOT NULL AS presence,
               (SELECT coalesce(json_agg(json_build_object('id', c.id, 'name', c.name, 'online', c.checkpoint_owner_id IS NOT NULL)
                                         ORDER BY c.id), '[]') FROM character_base c WHERE c.account_id = a.id) AS characters
        FROM accounts a WHERE a.id IN (%s)) r)""" % (", ".join(map(str, old)) if old else "NULL"))
    moves, online, cids = [], [], []
    for a in accounts:
        on = bool(a["login_status"] or a["presence"] or any(c["online"] for c in a["characters"]))
        if on:
            online.append("#%d %s" % (a["id"], a["username"]))
        cids += [c["id"] for c in a["characters"]]
        moves.append({"from": a["id"], "to": m[a["id"]], "username": a["username"], "characters": [c["name"] for c in a["characters"]],
                      "online": on})
    cfg, cfg_files = config_ids()
    named, named_text = config_hits(m, cfg)
    history = []
    for schema, table, col in HISTORY_JSON:
        try:
            n = rb.pg.json("(SELECT to_jsonb(count(*)) FROM %s.%s WHERE %s::text ~ %s)" % (
                schema, table, common.ident(col), lit('"Account_?Id": ?(%s)\\M' % "|".join(map(str, old)) if old else "^$")))
        except Exception:              # noqa: BLE001 - an older database without that table
            n = 0
        if n:
            history.append({"table": "%s.%s" % (schema, table), "rows": int(n)})
    everyone = server_online(rb)
    warnings = []
    if everyone:
        warnings.append("%d player%s online now: %s. A renumber runs only with nobody online on the whole server: wait "
                        "for a quiet moment or stop the game server first." % (len(everyone), "" if len(everyone) == 1 else "s are",
                                                                                online_text(everyone)))
    if online:
        warnings.append("logged in now: %s. Log them out (or stop the game server) first; the renumber refuses while any "
                        "moved account is online." % ", ".join(online))
    if named:
        warnings.append("the server configuration names account %s (developer / GM commands%s): %s. Change the configuration "
                        "too, then restart." % (", ".join("#%d" % x for x in named),
                                                (": " + ", ".join(cfg_files)) if cfg_files else ", known default", "; ".join(named_text)))
    if unreviewed:
        warnings.append("columns not reviewed for renumbering: %s. The renumber refuses until they are checked." % ", ".join(unreviewed))
    if history:
        warnings.append("old receipts and event records keep the old numbers (history): %s" % ", ".join(
            "%s (%d)" % (h["table"], h["rows"]) for h in history))
    # the replica window needs a superuser or GRANT SET ON PARAMETER session_replication_role (the live editor's
    # role gweditor has neither on 2026-11-21)
    can_replica = bool(rb.pg.json("(SELECT to_jsonb((SELECT rolsuper FROM pg_roles WHERE rolname = current_user) OR "
                                  "has_parameter_privilege('session_replication_role', 'SET')))"))
    if not can_replica:
        warnings.append("this editor's database login may not switch the database's safety triggers off for one "
                        "transaction, so the renumber cannot be written from here. Once: sudo -u postgres psql -c "
                        "\"GRANT SET ON PARAMETER session_replication_role TO <editor role>\", or renumber from an editor "
                        "that connects as postgres.")
    final_max = max((set(ids) - set(m)) | set(m.values())) if ids else 0
    view = {"mode": body.get("mode", "compact"), "map": {str(k): v for k, v in sorted(m.items())},
            "inverse": {str(v): k for k, v in sorted(m.items())}, "moves": moves,
            "tables": [{"table": "%s.%s" % (t["schema"], t["table"]), "column": t["column"], "rows": t["rows"], "fk": t["fk"],
                        "array": t["array"]} for t in tables if t["rows"]],
            "rows_total": sum(t["rows"] for t in tables), "warnings": warnings, "online": online, "config_named": named,
            "unreviewed": unreviewed, "history": history, "online_all": [online_text([r]) for r in everyone], "can_replica": can_replica, "sequence": seq, "sequence_to": final_max,
            "accounts": len(ids), "writable": bool(rb.writable), "applies": "now",
            "advice": "Stop the game server (gw251 stop) before renumbering, and restart it afterwards: it keeps account "
                      "numbers of logged-in players in memory."}
    view["token"] = hashlib.sha256(json.dumps({"m": view["map"], "t": view["tables"], "o": online, "u": unreviewed},
                                              sort_keys=True, default=str).encode()).hexdigest()
    return view, tables, sorted(set(cids)), cols


def server_online(rb):
    """Every account the database shows as online right now (any of the three login signals), server-wide."""
    return rb.pg.json("""(SELECT coalesce(json_agg(r ORDER BY r.id), '[]') FROM (
        SELECT a.id, a.username, a.login_status, a.login_presence_token IS NOT NULL AS presence,
               (SELECT coalesce(json_agg(c.name ORDER BY c.id), '[]') FROM character_base c
                 WHERE c.account_id = a.id AND c.checkpoint_owner_id IS NOT NULL) AS in_world
        FROM accounts a WHERE %s) r)""" % ONLINE_SQL) or []


def online_text(rows):
    return ", ".join("#%d %s%s" % (r["id"], r["username"], (" (" + ", ".join(r["in_world"]) + ")") if r.get("in_world") else "")
                     for r in rows[:20]) + (" and %d more" % (len(rows) - 20) if len(rows) > 20 else "")


def missing_rights(rb, cols, seq):
    """What this editor's database login may not do that a renumber needs: [sentence]. Empty for a superuser."""
    if rb.pg.json("(SELECT to_jsonb(rolsuper) FROM pg_roles WHERE rolname = current_user)"):
        return []
    # SELECT on the table (row counts, the backup) and UPDATE on the account column (the grant file gives exactly that)
    pairs = sorted({("%s.%s" % (c["schema"], c["table"]), c["column"]) for c in cols if not c["view"]})
    q = ", ".join("(%s, %s)" % (lit(t), lit(col)) for t, col in pairs) or "(NULL, NULL)"
    bad = rb.pg.json("""(SELECT coalesce(json_agg(DISTINCT t), '[]') FROM (VALUES %s) v(t, col)
        WHERE t IS NOT NULL AND NOT (has_schema_privilege(split_part(t, '.', 1), 'USAGE') AND has_table_privilege(t, 'SELECT')
                                     AND has_column_privilege(t, col, 'UPDATE')))""" % q) or []
    out = []
    if bad:
        out.append("read and change %s" % ", ".join(sorted(bad)))
    if seq and not rb.pg.json("(SELECT to_jsonb(has_sequence_privilege(%s, 'UPDATE')))" % lit(seq)):
        out.append("move the account number counter")
    return out


@M.post("renumber/preview")
def renumber_preview(ctx):
    """POST {mode: compact | set {from, to} | map {map}} -> the dry-run diff (nothing is written)."""
    return renumber_plan(ctx, ctx.body)[0]


def renumber_statements(m, tables, seq, final_max):
    """The SQL of one renumber (inside a replica window). m: {old: new}; tables: account_columns rows with where."""
    st = ["SET LOCAL session_replication_role = replica"]
    case_off = "CASE %%s %s END" % " ".join("WHEN %d THEN %d" % (o, OFFSET + n) for o, n in sorted(m.items()))
    olds = ", ".join(map(str, sorted(m)))
    news_off = ", ".join(str(OFFSET + n) for n in sorted(m.values()))
    for t in tables:
        col = common.ident(t["column"])
        if t["array"]:
            expr = col
            for o, n in sorted(m.items()):
                expr = "array_replace(%s, %d, %d)" % (expr, o, OFFSET + n)
            st.append("UPDATE %s SET %s = %s WHERE %s" % (_qual(t), col, expr, t["where"]))
        else:
            st.append("UPDATE %s SET %s = %s WHERE %s IN (%s)" % (_qual(t), col, case_off % col, col, olds))
    for t in tables:
        col = common.ident(t["column"])
        if t["array"]:
            expr = col
            for n in sorted(m.values()):
                expr = "array_replace(%s, %d, %d)" % (expr, OFFSET + n, n)
            st.append("UPDATE %s SET %s = %s WHERE %s && ARRAY[%s]::int[]" % (_qual(t), col, expr, col, news_off))
        else:
            st.append("UPDATE %s SET %s = %s - %d WHERE %s >= %d" % (_qual(t), col, col, OFFSET, col, OFFSET))
    st.append("SET LOCAL session_replication_role = origin")
    if seq:
        # the highest number at commit time (registration stays open: an account made during the run counts too)
        st.append("SELECT setval(%s, GREATEST(%d, (SELECT max(id) FROM public.accounts)), true)" % (lit(seq), max(1, final_max)))
    return st


def renumber_guards(m, cols):
    """DO blocks that RAISE when a foreign key lost its parent or a column still holds an old / offset number."""
    g = []
    fks = [c for c in cols if c["fk"] and not c["view"] and not c["array"]]
    for c in fks:
        g.append("DO $gwr$ BEGIN IF EXISTS (SELECT 1 FROM %s t WHERE t.%s IS NOT NULL AND NOT EXISTS (SELECT 1 FROM public.accounts a "
                 "WHERE a.id = t.%s)) THEN RAISE EXCEPTION 'gwadmin: renumber left %s.%s without its account'; END IF; END $gwr$;" % (
                     _qual(c), common.ident(c["column"]), common.ident(c["column"]), c["table"], c["column"]))
    # an old number may come back as a NEW number (compact: 8 -> 6, 9 -> 8): only old numbers nobody gets are stale
    stale = sorted(set(m) - set(m.values()))
    for c in cols:
        if c["view"]:
            continue
        col = common.ident(c["column"])
        if c["array"]:
            if not stale:
                continue
            cond = "%s && ARRAY[%s]::int[]" % (col, ", ".join(map(str, stale)))
        else:
            cond = "%s >= %d" % (col, OFFSET) + (" OR %s IN (%s)" % (col, ", ".join(map(str, stale))) if stale else "")
        g.append("DO $gwr$ BEGIN IF EXISTS (SELECT 1 FROM %s WHERE %s) THEN RAISE EXCEPTION 'gwadmin: renumber left an old "
                 "account number in %s.%s'; END IF; END $gwr$;" % (_qual(c), cond, c["table"], c["column"]))
    return g


@M.post("renumber/apply")
def renumber_apply(ctx):
    """POST {mode..., token, confirm_config?} -> renumber in one transaction (refused while a moved account is online)."""
    rb = _rb(ctx)
    if not rb.writable:
        raise Refused("this editor is read-only for the selected database")
    view, tables, cids, cols = renumber_plan(ctx, ctx.body)
    if ctx.body.get("token") != view["token"]:
        raise Conflict("accounts or their rows changed since the dry run; preview again")
    m = {int(k): v for k, v in view["map"].items()}
    if not m:
        raise Bad("nothing to renumber: the accounts already have these numbers")
    if view["online"]:
        raise common.OnlineRefused("renumber: %s logged in. Log them out first." % ", ".join(view["online"]),
                                   {"name": ", ".join(view["online"]), "online": True, "reasons": view["online"], "live": [], "kick": False})
    if view["online_all"]:
        raise common.OnlineRefused("renumber: nobody may be online on the whole server while account numbers change; online "
                                   "now: %s. Wait until they log out or stop the game server, then preview again."
                                   % ", ".join(view["online_all"]),
                                   {"name": ", ".join(view["online_all"]), "online": True, "reasons": view["online_all"],
                                    "live": [], "kick": False})
    if not view["can_replica"]:
        raise Refused("the editor's database login cannot turn the safety triggers off for one transaction (needs GRANT SET ON "
                      "PARAMETER session_replication_role); nothing written")
    if view["unreviewed"]:
        raise Refused("account columns not reviewed for renumbering: %s" % ", ".join(view["unreviewed"]))
    if view["config_named"] and ctx.body.get("confirm_config") is not True:
        raise Bad("account %s is named in the server configuration; tick the box that you will update it, then apply again"
                  % ", ".join("#%d" % x for x in view["config_named"]))
    old_ids = sorted(m)
    every = sorted(set(old_ids) | set(m.values()))
    ids = ", ".join(map(str, every))
    # snapshot + lock: the accounts rows (old and new numbers) and every table with a primary key; the backup file then
    # holds every row the renumber touches
    scope_list = [common.scope("accounts", "id IN (%s)" % ids, pk=["id"], counters=[], ignore=[])]
    for t in tables:
        if t["table"] == "accounts" or not t["pk"] or not t["rows"] or t["schema"] != "public":
            continue
        cond = ("%s && ARRAY[%s]::int[]" % (common.ident(t["column"]), ids)) if t["array"] else "%s IN (%s)" % (common.ident(t["column"]), ids)
        if t["table"] in {s["table"] for s in scope_list}:
            continue
        scope_list.append(common.scope(t["table"], cond, pk=t["pk"], counters=[], ignore=[]))
    # B22 d56: server-wide, not only the moved accounts
    online_guard = ("DO $gwo$ DECLARE bad text; BEGIN SELECT string_agg(a.username, ', ') INTO bad FROM accounts a WHERE %s; "
                    "IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %% online; a renumber needs nobody online; nothing "
                    "renumbered', bad; END IF; END $gwo$" % ONLINE_SQL)

    def build(_before):
        return [online_guard] + renumber_statements(m, tables, view["sequence"], view["sequence_to"])
    label = "renumber accounts: " + ", ".join("#%s -> #%s" % (k, v) for k, v in sorted(m.items()))
    r = ctx.rows_write(scope_list, label, build, fields=[], lock_cids=cids, guard_sql=renumber_guards(m, cols), applies="now",
                       target="accounts", no_undo="a renumber is put back with a second renumber: use \"Renumber back\" "
                                                  "(the inverse map) on the Accounts: search & renumber page")
    r.update(map=view["map"], inverse=view["inverse"], rows=view["rows_total"], warnings=view["warnings"])
    return r
