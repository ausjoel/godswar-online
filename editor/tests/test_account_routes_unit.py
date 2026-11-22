#!/usr/bin/env python3
"""Unit test (W4A): the Character page's account routes for soft delete, Restore, rename holds and e-mail, with a fake
editor context (no database, no server). Checks what each route asks the write helper to do: statements, scope
tables, refusals.

    python3 -I tests/test_account_routes_unit.py
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import character as CH  # noqa: E402
from modules.common import Bad, Refused  # noqa: E402

FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:500]))
    if not ok:
        FAIL.append(msg)


class PG:
    def __init__(self, world):
        self.w = world

    def json(self, sql, timeout=None):
        w = self.w
        if "to_regclass('public.account_username_holds')" in sql and "'holds'" in sql:
            return dict(w["ready"])
        if "FROM accounts a WHERE a.id = " in sql and "'characters'" in sql:
            a = dict(w["account"])
            a["characters"] = [dict(c) for c in w["characters"]]
            return a
        if "FROM public.account_deletions d WHERE d.account_id" in sql:
            return w.get("deletion")
        if "FROM public.account_username_holds h WHERE h.account_id" in sql:
            return [h for h in w["holds"] if h.get("account_id") == w["account"]["id"]]
        if "FROM public.account_username_holds h WHERE h.username_key IN" in sql:
            return [h for h in w["holds"] if ("'%s'" % h["username_key"]) in sql]
        if "'state', lifecycle_state) FROM character_base WHERE id =" in sql:
            cid = int(sql.rsplit("=", 1)[1].strip(" )"))
            c = next(x for x in w["characters"] if x["id"] == cid)
            return {"id": cid, "account_id": w["account"]["id"], "name": c["name"], "state": c["state"]}
        if "FROM signup.account_emails WHERE account_id" in sql:
            return None
        if "FROM accounts WHERE lower(username) = lower(" in sql:
            return sum(1 for n in w["names"] if ("'%s'" % n.lower()) in sql.lower())
        if "lower(a.email) =" in sql:
            return w.get("email_taken")
        if "FROM signup.account_emails s WHERE s.account_id <>" in sql:
            return None
        raise AssertionError("unexpected SQL: " + sql[:200])


class RB:
    def __init__(self, world):
        self.pg = PG(world)
        self.writable = True


class Ctx:
    def __init__(self, world, body=None, q=None):
        self.w = world
        self.rb = RB(world)
        self.body = dict(body or {})
        self._q = q or {}
        self.caps = {"available": True}
        self.writes = []

    def need_rb(self):
        return self.rb

    def q(self, k):
        return self._q.get(k)

    def rows_write(self, scope, action, build, **kw):
        stmts = build({})
        self.writes.append({"scope": scope, "action": action, "stmts": stmts, "kw": kw})
        return {"change": {"id": "x"}, "applies": kw.get("applies")}


def world(**kw):
    w = {"ready": {"holds": True, "deletions": True, "signup": True},
         "account": {"id": 7, "username": "alpha1", "email": "a@b.cd", "status": 0, "login_status": 0, "presence": False,
                     "donator_tier": 0, "hashed": True, "realms": [], "entitlements": []},
         "characters": [{"id": 70, "name": "Al", "profession": 0, "camp": 0, "online": False, "state": "active"}],
         "holds": [], "names": ["alpha1", "beta22"], "deletion": None}
    w.update(kw)
    return w


def call(fn, ctx, *a):
    try:
        return fn(ctx, *a), None
    except (Bad, Refused) as e:
        return None, e


# ---- soft delete
c = Ctx(world(), {"confirm": "alpha1", "mode": "soft"})
r, e = call(CH.account_delete, c, "7")
w = c.writes[0] if c.writes else {}
step("soft delete writes once", e is None and len(c.writes) == 1, e)
step("soft delete: offline guard first, then status 2, deletion row, hold",
     w and "is online" in w["stmts"][0] and any("SET status = 2" in s for s in w["stmts"])
     and any("INSERT INTO public.account_deletions" in s for s in w["stmts"])
     and any("INSERT INTO public.account_username_holds" in s and "'deleted'" in s for s in w["stmts"]), w.get("stmts"))
step("soft delete: Undo covers accounts, account_deletions and the hold",
     w and {s["table"] for s in w["scope"]} == {"accounts", "account_deletions", "account_username_holds"}, w.get("scope"))
c = Ctx(world(), {"confirm": "wrong", "mode": "soft"})
step("soft delete needs the user name typed", isinstance(call(CH.account_delete, c, "7")[1], Bad))
c = Ctx(world(ready={"holds": False, "deletions": False, "signup": False}), {"confirm": "alpha1", "mode": "soft"})
r, e = call(CH.account_delete, c, "7")
step("soft delete before the server release is refused (migration 240)", isinstance(e, Refused) and "240" in str(e), e)
dw = world(account=dict(world()["account"], status=2), deletion={"previous_status": 1, "purge_after": "2026-12-12T00:00"})
c = Ctx(dw, {"confirm": "alpha1", "mode": "soft"})
step("deleting a deleted account is refused", isinstance(call(CH.account_delete, c, "7")[1], Refused))
c = Ctx(dw, {"confirm": "alpha1", "mode": "retire"})
step("retire of a deleted account is refused (it would turn status 2 into 1)", isinstance(call(CH.account_delete, c, "7")[1], Refused))
c = Ctx(dw, {"banned": False})
step("unban of a deleted account is refused (it would undelete)", isinstance(call(CH.account_ban, c, "7")[1], Refused))

# ---- restore
c = Ctx(dw, {})
r, e = call(CH.account_restore, c, "7")
w = c.writes[0] if c.writes else {}
step("restore writes status = previous (1 = still banned)", e is None and w and w["stmts"][1] == "UPDATE accounts SET status = 1 WHERE id = 7 AND status = 2", (e, w.get("stmts")))
step("restore removes the deletion row and the deleted-name hold",
     w and any("DELETE FROM public.account_deletions" in s for s in w["stmts"]) and any("reason = 'deleted'" in s for s in w["stmts"]))
c = Ctx(world(), {})
r, e = call(CH.account_restore, c, "7")
step("restore of a live account changes nothing", e is None and not c.writes and "not deleted" in r["note"])

# ---- rename
c = Ctx(world(), {"username": "gamma33", "expected": "alpha1"})
r, e = call(CH.account_rename, c, "7")
w = c.writes[0] if c.writes else {}
step("rename holds the old name", e is None and any("'alpha1', 'alpha1', 7, 'renamed'" in s for s in w.get("stmts", [])), (e, w.get("stmts")))
step("rename scope includes both names' holds", w and any(s["table"] == "account_username_holds" and "'alpha1'" in s["where"] and "'gamma33'" in s["where"] for s in w["scope"]), w.get("scope"))
c = Ctx(world(holds=[{"username_key": "gamma33", "account_id": 9, "reason": "deleted", "held_at": "2026-11-01"}]), {"username": "gamma33"})
step("rename to another account's held name is refused", isinstance(call(CH.account_rename, c, "7")[1], Bad))
c = Ctx(world(holds=[{"username_key": "gamma33", "account_id": 7, "reason": "renamed", "held_at": "2026-11-01"}]), {"username": "gamma33"})
r, e = call(CH.account_rename, c, "7")
step("rename back to its own old name releases that hold", e is None and any("DELETE FROM public.account_username_holds WHERE username_key = 'gamma33' AND account_id = 7" in s
                                                                     for s in c.writes[0]["stmts"]), e)
c = Ctx(world(ready={"holds": False, "deletions": False, "signup": False}), {"username": "gamma33"})
r, e = call(CH.account_rename, c, "7")
step("rename on an old database still works, without holds", e is None and not any("account_username_holds" in s for s in c.writes[0]["stmts"])
     and "free again" in r["note"], (e, r))
c = Ctx(world(holds=[{"username_key": "newbie1", "account_id": 3, "reason": "deleted", "held_at": "x"}]), {"username": "newbie1", "password": "pass1"})
step("new account with a held name is refused", isinstance(call(CH.account_create, c)[1], Bad))

# ---- e-mail
c = Ctx(world(), {"email": "New@Mail.com"})
r, e = call(CH.account_email, c, "7")
w = c.writes[0] if c.writes else {}
step("e-mail change writes accounts and resets the sign-up verification", e is None and w and w["stmts"][0].startswith("UPDATE accounts SET email")
     and any("verified_at = NULL" in s for s in w["stmts"]), (e, w.get("stmts")))
step("e-mail change is guarded by the shown value", w and w["kw"]["expected"][0]["values"] == {"email": "a@b.cd"}, w.get("kw"))
c = Ctx(world(email_taken={"id": 9, "username": "zed"}), {"email": "x@y.zz"})
step("an address another account uses is refused", isinstance(call(CH.account_email, c, "7")[1], Bad))
c = Ctx(world(), {"email": "a@b.cd"})
r, e = call(CH.account_email, c, "7")
step("unchanged e-mail writes nothing", e is None and not c.writes)

# ---- list
class ACCStub:
    @staticmethod
    def list_accounts(ctx):
        return {"accounts": [{"id": 1, "username": "a1234", "status": 0, "donator_tier": 0},
                             {"id": 2, "username": "b1234", "status": 2, "donator_tier": 0}]}


CH.ACC = ACCStub
c = Ctx(world(), {})
r = CH.accounts_list(c)
step("the account list hides deleted accounts and says how many", [a["id"] for a in r["accounts"]] == [1] and r["hidden_deleted"] == 1, r)
c = Ctx(world(), {}, q={"deleted": "1"})
r = CH.accounts_list(c)
step("Show deleted lists them, flagged", [a["deleted"] for a in r["accounts"]] == [False, True], r)

# ---- swap a deleted character in (one active character per account and realm)
sw = world(characters=[{"id": 70, "name": "Al", "profession": 0, "camp": 0, "online": False, "state": "active", "realm": 1},
                       {"id": 71, "name": "Bo", "profession": 1, "camp": 0, "online": False, "state": "deleted", "realm": 1, "restorable": True}])
c = Ctx(sw, {"confirm": "Al"})
r, e = call(CH.lifecycle_swap, c, "71")
w = c.writes[0] if c.writes else {}
step("swap: delete the active one, then restore, in one write", e is None and w and "lifecycle_state = 'deleted'" in w["stmts"][1]
     and "lifecycle_state = 'active'" in w["stmts"][2] and "id = 70" in w["stmts"][1] and "id = 71" in w["stmts"][2], (e, w.get("stmts")))
c = Ctx(sw, {"confirm": "Bo"})
step("swap needs the ACTIVE character's name typed", isinstance(call(CH.lifecycle_swap, c, "71")[1], Bad))
c = Ctx(sw, {"confirm": "Al"})
step("swap of an active character refused", isinstance(call(CH.lifecycle_swap, c, "70")[1], Bad))

print("ALL OK" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
