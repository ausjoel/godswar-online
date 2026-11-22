"""modules/_account_holds.py - W4A: account soft delete (30 days, Restore, then the VPS purge timer), user-name holds
(a renamed or deleted account's name can never be registered again) and the e-mail change that resets the sign-up
site's verification. Pure helpers (SQL text + checks) for modules/character.py; unit test: tests/test_account_holds_unit.py.

Tables (server migration 20261112_240_account_name_holds, PostgresSchemaMigrationCatalog.AccountNameHolds.cs):
  public.account_username_holds (username_key = lower(name) PK, username, account_id, reason renamed|deleted|reserved,
                                 held_at, note)   - first-login registration, the sign-up site and the editor refuse a held name
  public.account_deletions      (account_id PK -> accounts ON DELETE CASCADE, username, previous_status, deleted_at,
                                 purge_after, deleted_by)
accounts.status = 2 means "deleted, restorable": login and world entry refuse any status <> 0 (OperatorControls.
IsAccountBlockedAsync), the GM /ban and /unban commands leave status 2 alone (PostgresOperatorStore.SetAccountStatusAsync).
"""
import re

from .common import Bad, lit

STATUS_DELETED = 2
RESTORE_DAYS = 30
HOLDS = "account_username_holds"
DELETIONS = "account_deletions"
REASONS = ("renamed", "deleted", "reserved")
# accounts.EMAIL_RX's rule, kept here so the helper has no import cycle
EMAIL_RX = re.compile(r"^$|^[^@\s'\"]{1,100}@[^@\s'\"]{1,150}\.[A-Za-z]{2,24}$")
NEEDS_SERVER = ("needs the server release with migration 20261112_240_account_name_holds (tables account_username_holds and "
                "account_deletions are not in this database yet). Nothing was changed.")


def key(name):
    return str(name or "").strip().lower()


def _writable(rel):
    """SQL: rel exists and the editor's role may read and change it (the live editor runs as the gweditor role, which
    has no rights on tables created after its grants: editor/sql/w4a-editor-grants.sql)."""
    return ("coalesce((SELECT bool_and(has_table_privilege(current_user, c, p)) FROM (SELECT to_regclass('%s') AS c) x, "
            "unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'DELETE']) p WHERE x.c IS NOT NULL), false)" % rel)


def ready_sql():
    """json: which W4A tables this database has AND the editor's role may write. Each check stands alone; the sign-up
    schema is looked at only when it exists and the role has USAGE on it (a name lookup in a schema without USAGE fails)."""
    return ("json_build_object('holds', %s, 'deletions', %s, 'signup', CASE WHEN NOT EXISTS (SELECT 1 FROM pg_namespace WHERE "
            "nspname = 'signup') THEN false WHEN NOT has_schema_privilege(current_user, 'signup', 'USAGE') THEN false ELSE %s AND "
            "coalesce(has_table_privilege(current_user, to_regclass('signup.email_tokens'), 'DELETE'), false) END)" % (
                _writable("public." + HOLDS), _writable("public." + DELETIONS), _writable("signup.account_emails")))


def hold_lookup_sql(names):
    keys = sorted({key(n) for n in names if key(n)})
    if not keys:
        return "'[]'::json"
    return ("(SELECT coalesce(json_agg(to_jsonb(h) ORDER BY h.username_key), '[]') FROM public.%s h WHERE h.username_key IN (%s))"
            % (HOLDS, ", ".join(lit(k) for k in keys)))


def hold_refusal(name, holds, account_id=None):
    """The reason a name may not be taken by account_id (None = a new account), or None. A name held for the SAME
    account (it renamed away from it, or its own deletion) may be taken back by that account."""
    k = key(name)
    for h in holds or []:
        if h.get("username_key") != k:
            continue
        if account_id is not None and h.get("account_id") == int(account_id) and h.get("reason") in ("renamed", "deleted"):
            return None
        why = {"renamed": "an account was renamed away from it", "deleted": "it belonged to a deleted account",
               "reserved": "it is reserved"}.get(h.get("reason"), "it is held")
        return "the user name %s cannot be used: %s (held since %s). Release the hold first if you really want it." % (
            name, why, str(h.get("held_at") or "")[:10])
    return None


def hold_insert_sql(name, account_id, reason, note=""):
    """Hold a name (idempotent: an existing hold takes the new owner / reason)."""
    if reason not in REASONS:
        raise Bad("bad hold reason %r" % reason)
    return ("INSERT INTO public.%s (username_key, username, account_id, reason, note) VALUES (%s, %s, %s, %s, %s) "
            "ON CONFLICT (username_key) DO UPDATE SET username = EXCLUDED.username, account_id = EXCLUDED.account_id, "
            "reason = EXCLUDED.reason, held_at = now(), note = EXCLUDED.note"
            % (HOLDS, lit(key(name)), lit(str(name)), "NULL" if account_id is None else int(account_id), lit(reason), lit(str(note)[:200])))


def hold_release_sql(name, account_id=None):
    where = "username_key = %s" % lit(key(name))
    if account_id is not None:
        where += " AND account_id = %d" % int(account_id)
    return "DELETE FROM public.%s WHERE %s" % (HOLDS, where)


def soft_delete_sql(aid, username, previous_status, actor="gwadmin-web", days=RESTORE_DAYS):
    """accounts.status = 2, the deletion row (purge date) and the name hold, as one block of statements."""
    aid = int(aid)
    if int(previous_status) == STATUS_DELETED:
        raise Bad("account #%d is already deleted" % aid)
    if not 1 <= int(days) <= 365:
        raise Bad("restore window must be 1..365 days")
    return [
        "INSERT INTO public.%s (account_id, username, previous_status, purge_after, deleted_by) VALUES "
        "(%d, %s, %d, transaction_timestamp() + interval '%d days', %s)" % (DELETIONS, aid, lit(username), int(previous_status),
                                                                             int(days), lit(actor[:64])),
        "UPDATE accounts SET status = %d WHERE id = %d AND status = %d" % (STATUS_DELETED, aid, int(previous_status)),
        "DO $gwd$ BEGIN IF NOT EXISTS (SELECT 1 FROM accounts WHERE id = %d AND status = %d) THEN RAISE EXCEPTION "
        "'gwadmin: account #%d changed status while being deleted; nothing written'; END IF; END $gwd$" % (aid, STATUS_DELETED, aid),
        hold_insert_sql(username, aid, "deleted", "soft delete in the editor"),
    ]


def restore_sql(aid, username, previous_status):
    aid = int(aid)
    ps = int(previous_status)
    if ps == STATUS_DELETED:
        ps = 0
    return [
        "DO $gwq$ BEGIN IF NOT EXISTS (SELECT 1 FROM accounts WHERE id = %d AND status = %d) THEN RAISE EXCEPTION "
        "'gwadmin: account #%d is not deleted; nothing written'; END IF; END $gwq$" % (aid, STATUS_DELETED, aid),
        "UPDATE accounts SET status = %d WHERE id = %d AND status = %d" % (ps, aid, STATUS_DELETED),
        "DO $gwr$ BEGIN IF NOT EXISTS (SELECT 1 FROM accounts WHERE id = %d AND status = %d) THEN RAISE EXCEPTION "
        "'gwadmin: account #%d is not deleted; nothing written'; END IF; END $gwr$" % (aid, ps, aid),
        "DELETE FROM public.%s WHERE account_id = %d" % (DELETIONS, aid),
        "DELETE FROM public.%s WHERE username_key = %s AND account_id = %d AND reason = 'deleted'" % (HOLDS, lit(key(username)), aid),
    ]


def check_email(email):
    e = str(email or "").strip()
    if len(e) > 255 or not EMAIL_RX.match(e):
        raise Bad("that e-mail address does not look right (leave it empty for none)")
    return e


def email_conflict_sql(aid, email):
    """json: another account using this address (accounts.email or the sign-up site's table)."""
    e = key(email)
    if not e:
        return "'null'::json"
    return ("(SELECT json_build_object('id', a.id, 'username', a.username) FROM accounts a WHERE a.id <> %d AND a.email <> '' "
            "AND lower(a.email) = %s LIMIT 1)" % (int(aid), lit(e)))


def signup_email_conflict_sql(aid, email):
    e = key(email)
    if not e:
        return "'null'::json"
    return ("(SELECT json_build_object('id', s.account_id) FROM signup.account_emails s WHERE s.account_id <> %d AND "
            "s.email_lower = %s AND EXISTS (SELECT 1 FROM accounts a WHERE a.id = s.account_id) LIMIT 1)" % (int(aid), lit(e)))


def email_sql(aid, email, signup_present):
    """accounts.email, and the sign-up site's row with verified_at reset to NULL (the admin 2026-11-12: an e-mail change
    resets the verification). An empty address removes the sign-up row. Undo restores accounts.email only."""
    aid = int(aid)
    e = str(email or "").strip()
    out = ["UPDATE accounts SET email = %s WHERE id = %d" % (lit(e), aid)]
    if signup_present:
        # a stale row of a GONE account holding this address would hit ux_account_emails_email_lower; the site removes
        # those the same way (gwsignup.py name_or_email_taken)
        if e:
            out.append("DELETE FROM signup.account_emails s WHERE s.email_lower = %s AND s.account_id <> %d AND NOT EXISTS "
                       "(SELECT 1 FROM accounts a WHERE a.id = s.account_id)" % (lit(e.lower()), aid))
            out.append("INSERT INTO signup.account_emails (account_id, email, email_lower, verified_at) VALUES (%d, %s, %s, NULL) "
                       "ON CONFLICT (account_id) DO UPDATE SET email = EXCLUDED.email, email_lower = EXCLUDED.email_lower, "
                       "verified_at = NULL" % (aid, lit(e), lit(e.lower())))
            out.append("DELETE FROM signup.email_tokens WHERE account_id = %d AND purpose = 'verify_email' AND used_at IS NULL" % aid)
        else:
            out.append("DELETE FROM signup.account_emails WHERE account_id = %d" % aid)
    return out


def hide_deleted(rows, show):
    """The account list without status-2 accounts unless show; every row gets 'deleted'."""
    out = []
    for a in rows:
        a["deleted"] = a.get("status") == STATUS_DELETED
        if show or not a["deleted"]:
            out.append(a)
    return out
