#!/usr/bin/env python3
"""tools/purge_account.py - W24 (the admin d161): the nightly gw-account-purge removes a due account with the editor's
COMPLETE delete (modules/_account_purge.py via character.account_full_delete), not only the database cascade. So the
sign-up site's rows and the feature tables without a foreign key go too, and the backup lands in the editor's
"Deleted completely" list, where Restore puts the account back in one click.

  purge_account.py --id N --username NAME [--psql "psql -h 127.0.0.1 -U gweditor"] [--db godswar]
                   [--backup-dir /var/lib/gweditor/backups] [--dry-run]

Run as the editor's user (gweditor, its .pgpass) by gw-account-purge. It re-checks that the account is still deleted
in the editor (status 2) and due (account_deletions.purge_after passed), then deletes it. Exit 0 = purged (or would
be, with --dry-run), 3 = refused (not due, online, a guild it leads, missing rights): nothing changed.
The change is recorded in the editor's change log (backup-dir/web-changes) like one made on the page.
"""
import argparse
import json
import os
import sys
from types import SimpleNamespace

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, HERE)
from modules import common, character  # noqa: E402,F401  (character registers the routes)
from reborn import Reborn  # noqa: E402


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--id", type=int, required=True)
    ap.add_argument("--username", required=True)
    ap.add_argument("--psql", default="/usr/bin/psql -h 127.0.0.1 -U gweditor")
    ap.add_argument("--db", default="godswar")
    ap.add_argument("--backup-dir", default="/var/lib/gweditor/backups")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args(argv)
    rb = Reborn(a.psql, a.db, writable=not a.dry_run, live=True, backup_dir=a.backup_dir)
    due = rb.pg.json("(SELECT to_jsonb(count(*)) FROM public.account_deletions d JOIN public.accounts x ON x.id = d.account_id "
                     "WHERE d.account_id = %d AND x.status = 2 AND d.purge_after < now() AND lower(x.username) = lower(%s))"
                     % (a.id, common.lit(a.username)))
    if not due:
        print("refused: account #%d %s is no longer deleted, not due, or renamed (restored meanwhile?)" % (a.id, a.username))
        return 3
    changes_dir = os.path.join(a.backup_dir, "web-changes")

    def record(c):
        c = dict(c, action="nightly purge: " + c.get("action", ""))
        if not a.dry_run and os.path.isdir(changes_dir):
            with open(os.path.join(changes_dir, "change-%s.json" % c["id"]), "w", encoding="utf-8") as f:
                json.dump(c, f, ensure_ascii=False, indent=1, default=str)

    app = SimpleNamespace(reborn=rb, changes=[], record_reborn_change=record, a=SimpleNamespace(rates_mgmt=None), rates=None)
    common.CATALOG.rb = rb
    if a.dry_run:
        st, r = common.dispatch(app, "GET", "character/account/%d/full-delete-plan" % a.id, {}, None, {})
        print(st, json.dumps({k: r.get(k) for k in ("error", "blockers", "total_rows")}, default=str))
        return 0 if st == 200 and not r.get("blockers") else 3
    name = rb.pg.json("(SELECT to_jsonb(username) FROM public.accounts WHERE id = %d)" % a.id)
    st, r = common.dispatch(app, "POST", "character/account/%d/full-delete" % a.id, {}, {"confirm": name}, {})
    if st != 200 or not r.get("backup"):
        print("refused (%s): %s" % (st, json.dumps(r, default=str)[:600]))
        return 3
    print("purged #%d %s: %s rows, backup %s" % (a.id, a.username, r.get("rows"), r.get("backup")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
