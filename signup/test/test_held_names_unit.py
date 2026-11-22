#!/usr/bin/env python3
"""Unit test (W4A): the sign-up site refuses user names the game holds (server migration 240,
public.account_username_holds: renamed-away and deleted accounts). No database: a fake cursor answers the queries.

    python3 -I signup/test/test_held_names_unit.py
"""
import os
import sys
import types

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "app"))
# the app imports jinja2 / psycopg2 at the top; neither is needed for these checks
for name in ("jinja2", "psycopg2", "psycopg2.pool"):
    sys.modules.setdefault(name, types.ModuleType(name))
sys.modules["psycopg2"].pool = sys.modules["psycopg2.pool"]
import gwsignup  # noqa: E402

FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)))
    if not ok:
        FAIL.append(msg)


class Cur:
    """Answers: accounts lookup, the hold-table state query, the hold lookup, the email cleanup and lookup."""

    def __init__(self, taken=False, state=1, held=()):
        self.taken, self.state, self.held, self.sql, self._next = taken, state, set(held), [], None

    def execute(self, sql, args=()):
        self.sql.append(sql)
        if "FROM public.accounts WHERE lower(username)" in sql:
            self._next = (1,) if self.taken else None
        elif "to_regclass('public.account_username_holds')" in sql:
            self._next = (self.state,)
        elif "FROM public.account_username_holds" in sql:
            self._next = (1,) if args[0].lower() in self.held else None
        elif sql.lstrip().startswith("DELETE"):
            self._next = None
        else:
            self._next = None

    def fetchone(self):
        return self._next


store = gwsignup.Store.__new__(gwsignup.Store)
c = Cur(held={"oldname"})
step("a held name is refused", store.name_or_email_taken(c, "oldname", "a@b.cc") is True)
step("the hold is looked up by lower(name)", any("username_key = lower(%s)" in q for q in c.sql), c.sql)
c = Cur(held={"oldname"})
step("a free name passes", store.name_or_email_taken(c, "newname", "a@b.cc") is False)
c = Cur(state=0)
step("no hold table yet (server before migration 240): only the accounts check", store.name_or_email_taken(c, "x1234", "a@b.cc") is False
     and not any("FROM public.account_username_holds WHERE" in q for q in c.sql), c.sql)
c = Cur(state=-1)
step("table present but not readable: refused (fail closed)", store.name_or_email_taken(c, "x1234", "a@b.cc") is True)
c = Cur(taken=True)
step("a taken name still short-circuits", store.name_or_email_taken(c, "x1234", "a@b.cc") is True and len(c.sql) == 1, c.sql)
sql = open(os.path.join(os.path.dirname(HERE), "sql", "signup-db.sql")).read()
step("signup-db.sql grants the hold column only when the table exists",
     "GRANT SELECT (username_key) ON public.account_username_holds" in sql and "to_regclass('public.account_username_holds') IS NOT NULL" in sql)
step("rankings hide deleted accounts (status 2)", "a.status = 2" in sql)
print("ALL OK" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
