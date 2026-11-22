#!/usr/bin/env python3
"""Unit test (W4A): account soft delete, user-name holds and the e-mail change (modules/_account_holds.py), plus the
server-side pieces they rely on (the registration insert and the GM status guard). No database, no server.

    python3 -I tests/test_account_holds_unit.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.dirname(HERE))
from modules import _account_holds as AH  # noqa: E402
from modules.common import Bad  # noqa: E402

FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:400]))
    if not ok:
        FAIL.append(msg)


def raises(fn, *a):
    try:
        fn(*a)
    except Bad as e:
        return str(e)
    return None


# ---- holds
H = [{"username_key": "oldname", "account_id": 7, "reason": "renamed", "held_at": "2026-11-12T01:00:00"},
     {"username_key": "gone1", "account_id": 9, "reason": "deleted", "held_at": "2026-11-10"},
     {"username_key": "vip", "account_id": None, "reason": "reserved", "held_at": "2026-11-01"}]
step("a held name is refused for a new account", AH.hold_refusal("OldName", H) is not None)
step("... in any letter case", AH.hold_refusal("OLDNAME", H) is not None)
step("the owner may rename back to its own old name", AH.hold_refusal("oldname", H, account_id=7) is None)
step("another account may not take it", AH.hold_refusal("oldname", H, account_id=8) is not None)
step("a deleted account's name is refused for others", "deleted account" in (AH.hold_refusal("gone1", H, account_id=3) or ""))
step("a reserved name is refused even for an id", AH.hold_refusal("vip", H, account_id=1) is not None)
step("a free name passes", AH.hold_refusal("fresh", H) is None)
sql = AH.hold_insert_sql("OldName", 7, "renamed", "x")
step("hold insert keys on lower(name), keeps the spelling, is idempotent",
     "'oldname', 'OldName', 7, 'renamed'" in sql and "ON CONFLICT (username_key) DO UPDATE" in sql, sql)
step("hold insert refuses an unknown reason", raises(AH.hold_insert_sql, "a", 1, "because") is not None)
step("hold insert quotes text", "'it''s'" in AH.hold_insert_sql("abcd", 1, "reserved", "it's"))
step("release by owner names both", AH.hold_release_sql("OldName", 7) ==
     "DELETE FROM public.account_username_holds WHERE username_key = 'oldname' AND account_id = 7")
step("lookup lists every key once", AH.hold_lookup_sql(["A1", "a1", "b2", ""]).count("'a1'") == 1 and "'b2'" in AH.hold_lookup_sql(["b2"]))
step("lookup of nothing is an empty array", AH.hold_lookup_sql([]) == "'[]'::json")

# ---- soft delete / restore
st = AH.soft_delete_sql(12, "player12", 0)
step("soft delete: deletion row with a 30-day purge date", "interval '30 days'" in st[0] and "(12, 'player12', 0," in st[0], st[0])
step("soft delete: status 2 only from the shown status", st[1] == "UPDATE accounts SET status = 2 WHERE id = 12 AND status = 0", st[1])
step("soft delete: aborts if the status moved", "RAISE EXCEPTION" in st[2])
step("soft delete: holds the name", "'deleted'" in st[3] and "'player12'" in st[3])
step("soft delete of a banned account remembers the ban", "status = 2 WHERE id = 5 AND status = 1" in AH.soft_delete_sql(5, "x1234", 1)[1])
step("soft delete of a deleted account refused", raises(AH.soft_delete_sql, 5, "x1234", 2) is not None)
step("restore window bounded", raises(AH.soft_delete_sql, 5, "x1234", 0, "w", 0) is not None)
rs = AH.restore_sql(12, "player12", 1)
step("restore puts the previous status back (a banned account stays banned)", rs[1] == "UPDATE accounts SET status = 1 WHERE id = 12 AND status = 2", rs)
step("restore removes the deletion row and only its own 'deleted' hold",
     "DELETE FROM public.account_deletions WHERE account_id = 12" in rs and
     any("reason = 'deleted'" in x and "account_id = 12" in x for x in rs), rs)
rows = [{"status": 0, "id": 1}, {"status": 2, "id": 2}, {"status": 1, "id": 3}]
step("the account list hides deleted accounts", [a["id"] for a in AH.hide_deleted([dict(r) for r in rows], False)] == [1, 3])
step("... and shows them on request, flagged", [a["deleted"] for a in AH.hide_deleted([dict(r) for r in rows], True)] == [False, True, False])

# ---- e-mail
step("an empty e-mail is allowed", AH.check_email("  ") == "")
step("a bad e-mail is refused", raises(AH.check_email, "not an email") is not None)
step("a quote is refused", raises(AH.check_email, "a'b@c.de") is not None)
es = AH.email_sql(4, "New@Mail.com", True)
step("e-mail: accounts row", es[0] == "UPDATE accounts SET email = 'New@Mail.com' WHERE id = 4", es)
step("e-mail: sign-up row upserted with verified_at = NULL",
     any("INSERT INTO signup.account_emails" in x and "verified_at = NULL" in x and "'new@mail.com'" in x for x in es), es)
step("e-mail: pending verification links dropped", any("signup.email_tokens" in x for x in es))
step("e-mail: stale rows of gone accounts cleared first", "NOT EXISTS (SELECT 1 FROM accounts a WHERE a.id = s.account_id)" in es[1])
step("e-mail: no sign-up schema -> accounts only", AH.email_sql(4, "a@b.cd", False) == ["UPDATE accounts SET email = 'a@b.cd' WHERE id = 4"])
step("e-mail: empty removes the sign-up row", AH.email_sql(4, "", True)[1] == "DELETE FROM signup.account_emails WHERE account_id = 4")
step("e-mail conflict check ignores the account itself", "a.id <> 4" in AH.email_conflict_sql(4, "x@y.zz"))

# ---- the server side these rely on (source checks)
srv = os.path.join(ROOT, "server", "src", "Godswar.Server")
store = open(os.path.join(srv, "Infrastructure", "Accounts", "PostgresAccountStore.cs")).read()
m = re.search(r"TryCreateAccountWithCredentialAsync\(.*?RETURNING id, username;", store, re.S)
step("server: first-login registration refuses held names",
     m and "FROM public.account_username_holds hold" in m.group(0) and "hold.username_key = lower(@username)" in m.group(0))
ops = open(os.path.join(srv, "Infrastructure", "Operators", "PostgresOperatorStore.cs")).read()
step("server: GM /ban and /unban leave a deleted account alone", "WHERE id = @id AND status <> 2;" in ops)
mig = open(os.path.join(srv, "State", "DatabaseMigrations", "PostgresSchemaMigrationCatalog.AccountNameHolds.cs")).read()
step("server: migration 240 creates both tables", "CREATE TABLE public.account_username_holds" in mig and
     "CREATE TABLE public.account_deletions" in mig and "ON DELETE CASCADE" in mig)
cat = open(os.path.join(srv, "State", "DatabaseMigrations", "PostgresSchemaMigrationCatalog.cs")).read()
step("server: migration 240 is registered", re.search(r"CreateAccountNameHolds\(\),?[^\n]*\n", cat) is not None)

print("ALL OK" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
