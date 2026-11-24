"""modules/_account_purge.py - W23 (the admin 24 Nov: "Still no option to fully delete account"): delete an account COMPLETELY
and put it back from its backup in one click. Helper for modules/character.py (routes account/<id>/full-delete-plan,
account/<id>/full-delete, full-deletes, full-deletes/restore); unit test tests/test_account_purge_unit.py.

What "completely" means (the older modes in character.py stop short of it):
  * every row the database cascades from the account (ON DELETE CASCADE closure, the same rows the server's own purge
    removes: characters, items, pets, skills, quests, guild membership ...);
  * the kill / loot / claim evidence rows that block a plain delete (character.WIPE_EVIDENCE, the same tables as the
    VPS purge timer's gwadmin_wipe_account_evidence());
  * every other table that holds the account's number, one of its characters' numbers or one of their pets' numbers in
    a reviewed column but has no foreign key (event / feature tables made on first use: profession_state, mentor_*,
    character_marriages, autoloot_state ...). Found at run time by column name (ACCOUNT_COLS / CHARACTER_COLS /
    PET_COLS), so a table added by a later server release is covered without a code change;
  * the sign-up site's rows (signup.account_emails / account_settings / email_tokens): they have NO foreign key to the
    game account, so neither the cascade nor the VPS purge timer removes them today.
  Kept on purpose (KEEP): the user-name hold (the name can never be registered again), GM audit history, shared history
  that belongs to other players too (trade log, lottery rounds, group reward lists), and the archive schemas.

How it is written (one transaction; the account must be logged out, checked before and again inside it):
  1. every affected row is read and written to ONE backup file in the editor's backup folder (exact JSON text from
     PostgreSQL, so numbers and nested JSON come back byte for byte);
  2. inside the transaction the row counts are compared with the backup (a row that appeared since => nothing written);
  3. inside a session_replication_role = replica window (foreign-key and append-only triggers off for this one
     transaction, the same precedent as the account renumber and reborn.py's hatch-evidence Undo) the rows are deleted
     children first, then the name is held (reason 'deleted');
  4. guards prove nothing of the account is left in any covered column and no foreign key lost its parent; COMMIT.
  Restore reads that file, refuses when the account number, the user name or a character name is taken again, and
  re-inserts every row parents first in the same kind of window (OVERRIDING SYSTEM VALUE: tables whose ids the database
  numbers itself come back with their own ids), puts the name hold back as it was, and runs the same guards.
  Both are recorded in the change log (audit) with the backup path; the change-log Undo points at Restore.

The live editor connects as gweditor (not a superuser): editor/sql/w23-account-delete-grants.sql gives it exactly the
rights this needs. missing_rights() says what is missing instead of failing half way.
"""
import datetime
import glob
import json
import os
import re

from .common import Bad, Refused, lit, ident

KIND = "account-full-delete"
VERSION = 1
SCHEMAS = ("public", "signup")
ACCOUNT_COLS = {"account_id"}
# columns that hold a character's number (user_id means the character in every reborn table: character_items.user_id,
# character_pets.user_id ...; snapshots in the pet archives are the owner at the time of the archive)
CHARACTER_COLS = {"character_id", "user_id", "owner_character_id", "owner_character_id_snapshot", "owner_user_id",
                  "owner_user_id_snapshot", "user_id_snapshot", "target_character_id", "apprentice_id", "master_id",
                  "partner_id", "groom_id", "bride_id"}
PET_COLS = {"pet_id"}
# "schema.table.column" -> why the rows stay
KEEP = {
    "public.account_username_holds.account_id": "the user name stays held: nobody can register it again",
    "public.operator_log.actor_account_id": "GM audit history",
    "public.eventsnpc_lottery_rounds.winner_character_id": "a past lottery round of the whole server",
    "public.player_trade_log.first_character_id": "trade history, shared with the other player",
    "public.player_trade_log.second_character_id": "trade history, shared with the other player",
    "public.guild_quest_releases.released_by": "guild history",
    "public.troy_guild_claims.claimed_by": "guild history",
    "public.character_marriages.ended_by": "the marriage row itself goes (groom / bride)",
    "public.guilds.lord_character_id": "a guild's leader: the delete is refused while a character of the account leads a guild",
}
# id-looking names that are not a player's number (a camp, an amount)
NOT_IDS = {"public.battlefield_rounds.winner", "public.lelantine_farm_rounds.winner",
           "public.holy_suit_operation_policy_content_definitions.daily_experience_per_player"}
GROUP_RX = re.compile(r"(account|character)_ids$")
# broad name pattern for the "not reviewed" warning: an id-like column that is neither covered nor kept
SUSPECT_RX = re.compile(r"(^|_)(account|character|char|user|owner|player|winner|killer|member|leader|mentor|apprentice|"
                        r"master|groom|bride|spouse|partner|buyer|seller|sender|receiver)(_id|_ids|id)?(_snapshot)?$")
# characters of OTHER accounts that share a row with this account's characters (%(ids)s = its character ids)
PARTNER_SQL = (
    "SELECT CASE WHEN groom_id IN (%(ids)s) THEN bride_id ELSE groom_id END FROM character_marriages "
    "WHERE groom_id IN (%(ids)s) OR bride_id IN (%(ids)s)",
    "SELECT CASE WHEN master_id IN (%(ids)s) THEN apprentice_id ELSE master_id END FROM mentor_relations "
    "WHERE master_id IN (%(ids)s) OR apprentice_id IN (%(ids)s)",
    "SELECT CASE WHEN owner_character_id IN (%(ids)s) THEN target_character_id ELSE owner_character_id END FROM character_relations "
    "WHERE owner_character_id IN (%(ids)s) OR target_character_id IN (%(ids)s)",
)
ARCHIVE_SCHEMAS = ("legacy", "quest_archive")       # read-only history, never touched (listed in the plan)
TIMEOUT = 300


# ----------------------------------------------------------------------------------------------- discovery
def columns_sql():
    """Every integer column of a real table in public / signup with its schema, table, column, array flag."""
    return ("(SELECT coalesce(jsonb_agg(jsonb_build_object('schema', n.nspname, 'table', cl.relname, 'column', a.attname, "
            "'array', t.typcategory = 'A', 'int', format_type(a.atttypid, NULL) IN ('integer', 'bigint', 'smallint', 'integer[]', "
            "'bigint[]')) ORDER BY n.nspname, cl.relname, a.attnum), '[]') "
            "FROM pg_attribute a JOIN pg_class cl ON cl.oid = a.attrelid JOIN pg_namespace n ON n.oid = cl.relnamespace "
            "JOIN pg_type t ON t.oid = a.atttypid "
            "WHERE n.nspname IN (%s) AND cl.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped)"
            % ", ".join(lit(s) for s in SCHEMAS))


def classify(cols):
    """cols: rows of columns_sql(). Returns (covered, kept, unreviewed):
    covered   [{qt, column, by: account|character|pet}]   rows matching these are deleted and backed up
    kept      [{qt, column, why}]                         left alone on purpose
    unreviewed[{qt, column}]                              id-looking columns nobody reviewed (left alone, warned)"""
    covered, kept, unreviewed = [], [], []
    for c in cols:
        if not c.get("int"):
            continue
        qt = "%s.%s" % (c["schema"], c["table"])
        key = "%s.%s" % (qt, c["column"])
        col = c["column"]
        if key in NOT_IDS:
            continue
        if key in KEEP:
            kept.append({"qt": qt, "column": col, "why": KEEP[key]})
            continue
        if c.get("array"):
            if GROUP_RX.search(col):
                kept.append({"qt": qt, "column": col, "why": "a group record (list of members) shared with other players"})
            continue
        by = "account" if col in ACCOUNT_COLS else "character" if col in CHARACTER_COLS else "pet" if col in PET_COLS else None
        if by:
            covered.append({"qt": qt, "column": col, "by": by})
        elif SUSPECT_RX.search(col):
            unreviewed.append({"qt": qt, "column": col})
    return covered, kept, unreviewed


def id_conditions(covered, aid, cids, pids):
    """{qt: SQL condition} from literal ids (order-independent: valid before and after other tables change)."""
    ids = {"account": [int(aid)], "character": sorted({int(x) for x in cids}), "pet": sorted({int(x) for x in pids})}
    out = {}
    for c in covered:
        vals = ids[c["by"]]
        if not vals:
            continue
        cond = "%s IN (%s)" % (ident(c["column"]), ", ".join(str(v) for v in vals))
        out.setdefault(c["qt"], []).append(cond)
    return {t: " OR ".join("(%s)" % x for x in cs) for t, cs in out.items()}


def qualify(table):
    return table if "." in table else "public." + table


def qsql(qt):
    s, _, t = qt.partition(".")
    return "%s.%s" % (ident(s), ident(t))


def merge_plan(closure_where, closure_seq, evidence_where, id_where):
    """One condition per table (OR of every reason it is in the set) and the parents-first order:
    the cascade closure in its own order, then the evidence tables, then the tables found only by column."""
    where, order = {}, []
    for t in closure_seq:
        qt = qualify(t)
        where.setdefault(qt, []).append(closure_where[t])
        order.append(qt)
    for t in sorted(evidence_where):
        qt = qualify(t)
        where.setdefault(qt, []).append(evidence_where[t])
        if qt not in order:
            order.append(qt)
    for qt in sorted(id_where):
        where.setdefault(qt, []).append(id_where[qt])
        if qt not in order:
            order.append(qt)
    return {qt: " OR ".join("(%s)" % w for w in ws) if len(ws) > 1 else ws[0] for qt, ws in where.items()}, order


# ----------------------------------------------------------------------------------------------- reading
ROWS_MD5 = "coalesce(md5(string_agg(x::text, '~' ORDER BY x::text)), '-')"     # a table's rows as one hash (order-free)


def raw_rows(rb, where, order, timeout=TIMEOUT):
    """{qt: (count, exact jsonb text of the rows, md5 of the rows)} read in ONE repeatable-read snapshot (one psql script)."""
    lines = ["BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;"]
    for qt in order:
        lines.append("SELECT 'GWT|' || %s || '|' || count(*) || '|' || %s || '|' || coalesce(jsonb_agg(to_jsonb(x))::text, '[]') "
                     "FROM %s x WHERE %s;" % (lit(qt), ROWS_MD5, qsql(qt), where[qt]))
    lines.append("COMMIT;")
    out = rb.pg.run("\n".join(lines) + "\n", timeout=timeout)
    got = {}
    for line in out.splitlines():
        if line.startswith("GWT|"):
            _, qt, n, md5, js = line.split("|", 4)
            got[qt] = (int(n), js, md5)
    missing = [qt for qt in order if qt not in got]
    if missing:
        raise Bad("could not read %s" % ", ".join(missing))
    return got


def insert_columns(rb, tables):
    """{qt: [insertable columns]} (not dropped, not GENERATED ... STORED)."""
    if not tables:
        return {}
    names = ", ".join(lit(t) for t in tables)
    return rb.pg.json("""(SELECT coalesce(jsonb_object_agg(q.qt, q.cols), '{}') FROM (
        SELECT n.nspname || '.' || c.relname AS qt, jsonb_agg(a.attname ORDER BY a.attnum) AS cols
        FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname || '.' || c.relname IN (%s) AND a.attnum > 0 AND NOT a.attisdropped AND a.attgenerated = ''
        GROUP BY 1) q)""" % names) or {}


def fk_pairs(rb, tables):
    """Foreign keys whose PARENT is one of these tables: [{child, parent, cols, pcols}] (schema-qualified)."""
    if not tables:
        return []
    names = ", ".join(lit(t) for t in tables)
    return rb.pg.json("""(SELECT coalesce(jsonb_agg(jsonb_build_object(
        'child', cn.nspname || '.' || cl.relname, 'parent', pn.nspname || '.' || pl.relname,
        'cols', (SELECT jsonb_agg(a.attname ORDER BY k.n) FROM unnest(c.conkey) WITH ORDINALITY k(attnum, n)
                 JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum),
        'pcols', (SELECT jsonb_agg(a.attname ORDER BY k.n) FROM unnest(c.confkey) WITH ORDINALITY k(attnum, n)
                  JOIN pg_attribute a ON a.attrelid = c.confrelid AND a.attnum = k.attnum))), '[]')
      FROM pg_constraint c JOIN pg_class cl ON cl.oid = c.conrelid JOIN pg_namespace cn ON cn.oid = cl.relnamespace
      JOIN pg_class pl ON pl.oid = c.confrelid JOIN pg_namespace pn ON pn.oid = pl.relnamespace
      WHERE c.contype = 'f' AND pn.nspname || '.' || pl.relname IN (%s))""" % names) or []


def missing_rights(rb, tables):
    """What this editor's database login may not do that a full delete / restore needs: [sentence]. Empty for a superuser."""
    if rb.pg.json("(SELECT to_jsonb(rolsuper) FROM pg_roles WHERE rolname = current_user)"):
        return []
    q = ", ".join("(%s)" % lit(t) for t in tables) or "(NULL)"
    bad = rb.pg.json("""(SELECT coalesce(jsonb_agg(DISTINCT t ORDER BY t), '[]') FROM (VALUES %s) v(t)
        WHERE t IS NOT NULL AND NOT (has_schema_privilege(split_part(t, '.', 1), 'USAGE')
          AND has_table_privilege(t, 'SELECT') AND has_table_privilege(t, 'INSERT') AND has_table_privilege(t, 'DELETE')))""" % q) or []
    out = []
    if bad:
        out.append("read, delete and re-insert rows of %s" % ", ".join(bad))
    if not rb.pg.json("(SELECT to_jsonb(has_parameter_privilege('session_replication_role', 'SET')))"):
        out.append("switch the database's safety triggers off for one transaction (SET session_replication_role)")
    return out


# ----------------------------------------------------------------------------------------------- guards
def fk_guard_sql(fks):
    """DO-block: RAISE when any foreign key into the touched tables has a child row without its parent."""
    checks = []
    for f in fks:
        join = " AND ".join("p.%s = c.%s" % (ident(pc), ident(cc)) for cc, pc in zip(f["cols"], f["pcols"]))
        notnull = " AND ".join("c.%s IS NOT NULL" % ident(cc) for cc in f["cols"])
        checks.append("SELECT %s AS what FROM %s c WHERE %s AND NOT EXISTS (SELECT 1 FROM %s p WHERE %s)" % (
            lit("%s(%s) -> %s" % (f["child"], ", ".join(f["cols"]), f["parent"])), qsql(f["child"]), notnull, qsql(f["parent"]), join))
    if not checks:
        return ""
    return ("DO $gwk$ DECLARE bad text; BEGIN SELECT string_agg(DISTINCT what, '; ') INTO bad FROM (%s) x; IF bad IS NOT NULL THEN "
            "RAISE EXCEPTION 'gwadmin: a foreign key would lose its parent (%%); nothing written', bad; END IF; END $gwk$;"
            % " UNION ALL ".join(checks))


def count_guard_sql(where, expect, what):
    """DO-block: RAISE unless every table has exactly the expected number of matching rows."""
    parts = []
    for qt, n in expect.items():
        parts.append("SELECT %s AS t, (SELECT count(*) FROM %s x WHERE %s) AS n, %d AS want" % (lit(qt), qsql(qt), where[qt], int(n)))
    if not parts:
        return ""
    return ("DO $gwn$ DECLARE bad text; BEGIN SELECT string_agg(t || ' ' || n || '/' || want, ', ') INTO bad FROM (%s) x "
            "WHERE n <> want; IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin: %s (%%); nothing written', bad; END IF; END $gwn$;"
            % (" UNION ALL ".join(parts), what.replace("'", "")))


def same_rows_guard_sql(where, expect):
    """DO-block: RAISE unless every table's matching rows are exactly the ones in the backup (count and row hash), so a
    row added or changed between the backup and the delete stops it."""
    parts = ["SELECT %s AS t, (SELECT count(*) || '|' || %s FROM %s x WHERE %s) AS got, %s AS want" % (
        lit(qt), ROWS_MD5, qsql(qt), where[qt], lit("%d|%s" % (n, md5))) for qt, (n, md5) in expect.items()]
    if not parts:
        return ""
    return ("DO $gws$ DECLARE bad text; BEGIN SELECT string_agg(t, ', ') INTO bad FROM (%s) x WHERE got <> want; IF bad IS NOT NULL "
            "THEN RAISE EXCEPTION 'gwadmin: rows changed since the backup was read (%%); nothing written', bad; END IF; END $gws$;"
            % " UNION ALL ".join(parts))


def offline_sql(aid):
    aid = int(aid)
    return ("DO $gwc$ BEGIN IF EXISTS (SELECT 1 FROM public.accounts a WHERE a.id = %d AND (a.login_status <> 0 OR "
            "a.login_presence_token IS NOT NULL)) OR EXISTS (SELECT 1 FROM public.character_base c WHERE c.account_id = %d AND "
            "c.checkpoint_owner_id IS NOT NULL) THEN RAISE EXCEPTION 'gwadmin: account #%d is online (login_status / presence "
            "token / a character owned by a game session); nothing written'; END IF; END $gwc$;" % (aid, aid, aid))


# ----------------------------------------------------------------------------------------------- statements
def delete_statements(where, order, aid, username, hold_sql):
    """The delete transaction body (after db guard + lock + offline + count guard): children first in a replica window."""
    st = ["SET LOCAL session_replication_role = replica;"]
    for qt in reversed(order):
        st.append("DELETE FROM %s x WHERE %s;" % (qsql(qt), where[qt]))
    st.append("SET LOCAL session_replication_role = origin;")
    if hold_sql:
        st.append(hold_sql.rstrip(";") + ";")
    return st


def insert_statement(qt, cols, rows_json):
    cl = ", ".join(ident(c) for c in cols)
    return ("INSERT INTO %s (%s) OVERRIDING SYSTEM VALUE SELECT %s FROM jsonb_populate_recordset(NULL::%s, %s::jsonb);"
            % (qsql(qt), cl, cl, qsql(qt), lit(rows_json)))


def restore_statements(backup):
    """Re-insert every table of a backup parents first (replica window), then the name holds as they were."""
    st = ["SET LOCAL session_replication_role = replica;"]
    for t in backup["tables"]:
        if t["rows"]:
            st.append(insert_statement(t["table"], t["columns"], t["json"]))
    st.append("SET LOCAL session_replication_role = origin;")
    hk = backup.get("hold_key")
    if hk:
        st.append("DELETE FROM public.account_username_holds WHERE username_key = %s;" % lit(hk))
        if backup.get("holds_json") and backup["holds_json"] != "[]":
            cols = backup.get("holds_columns") or []
            st.append(insert_statement("public.account_username_holds", cols, backup["holds_json"]))
    return st


# ----------------------------------------------------------------------------------------------- backup files
def backup_name(db, aid, username, stamp):
    return "%s-%s-%d-%s-%s.json" % (KIND, re.sub(r"[^A-Za-z0-9_-]", "_", db), int(aid), re.sub(r"[^A-Za-z0-9_-]", "_", username),
                                    stamp.strftime("%Y%m%d-%H%M%S-%f"))


def write_backup(backup_dir, doc):
    os.makedirs(backup_dir, exist_ok=True)
    path = os.path.join(backup_dir, backup_name(doc["database"], doc["account_id"], doc["username"],
                                                datetime.datetime.fromisoformat(doc["created"])))
    tmp = path + ".part"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False)
    os.replace(tmp, path)
    return path


def list_backups(backup_dir, db):
    """Newest first: [{file, account_id, username, characters, created, rows, tables}] (the JSON bodies are not returned)."""
    out = []
    for p in glob.glob(os.path.join(backup_dir, "%s-%s-*.json" % (KIND, re.sub(r"[^A-Za-z0-9_-]", "_", db)))):
        try:
            with open(p, encoding="utf-8") as f:
                d = json.load(f)
        except (OSError, ValueError):
            continue
        if d.get("kind") != KIND or d.get("database") != db:
            continue
        out.append({"file": os.path.basename(p), "account_id": d.get("account_id"), "username": d.get("username"),
                    "characters": d.get("characters") or [], "created": d.get("created"), "rows": d.get("total_rows"),
                    "tables": len([t for t in d.get("tables") or [] if t.get("rows")]), "change": d.get("change")})
    out.sort(key=lambda x: x.get("created") or "", reverse=True)
    return out


def load_backup(backup_dir, name, db):
    base = os.path.basename(str(name or ""))
    if base != name or not base.startswith(KIND + "-") or not base.endswith(".json"):
        raise Bad("choose a backup from the list")
    p = os.path.join(backup_dir, base)
    try:
        with open(p, encoding="utf-8") as f:
            d = json.load(f)
    except OSError:
        raise Bad("backup %s is not in the backup folder any more" % base)
    except ValueError:
        raise Bad("backup %s is damaged (not JSON)" % base)
    if d.get("kind") != KIND or d.get("version") != VERSION:
        raise Bad("%s is not a full-delete backup this editor can restore" % base)
    if d.get("database") != db:
        raise Refused("backup %s belongs to database %s; this editor uses %s" % (base, d.get("database"), db))
    return d, p


def exists(rb, select_sql):
    """True when the tables of this SELECT exist (an older database lacks the feature tables)."""
    m = re.search(r"FROM (\w+)", select_sql)
    return bool(m and rb.pg.json("(SELECT to_jsonb(to_regclass(%s) IS NOT NULL))" % lit("public." + m.group(1))))
