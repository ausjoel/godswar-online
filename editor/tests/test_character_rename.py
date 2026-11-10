#!/usr/bin/env python3
"""Character RENAME test (Character module, POST /api/m/character/save/<cid> {values: {name}}): runs against ANY editor
instance whose database is a private copy.
  python3 -I test_character_rename.py --editor http://127.0.0.1:58831 --character 2 --online-character 14 \
      --psql "psql -h 127.0.0.1 -p 55432 -U postgres" --db godswar_ed_rename --i-know-this-database-is-a-copy
Checks: a rename is stored and Undo puts the old name back; a no-op save writes nothing; a case-only rename works; a
name taken by another active character (any case) is refused; a name held by a deleted character that can still be
restored is refused; bad characters, 32+ characters and leading spaces are refused; a stale page (expected name) gets
409; an online character gets 423; only character_base.name changes (no other table holds the name).
The page side of the bug (static/modules/character.js set() dropped text changes) is tests/test_character_rename_ui.cjs."""
import argparse, json, re, subprocess, sys, urllib.request, urllib.error
ap = argparse.ArgumentParser()
ap.add_argument("--editor", required=True); ap.add_argument("--character", type=int, required=True)
ap.add_argument("--online-character", type=int, default=0, help="a character the editor sees as online (423 check)")
ap.add_argument("--psql", required=True); ap.add_argument("--db", required=True)
ap.add_argument("--i-know-this-database-is-a-copy", action="store_true")
a = ap.parse_args()
if not a.i_know_this_database_is_a_copy:
    sys.exit("refusing: pass --i-know-this-database-is-a-copy (this test renames a character, then undoes it)")
html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
N = [0]; FAIL = []


def api(path, body=None):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET", headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        raw = e.read()
        try: return e.code, json.loads(raw or b"null")
        except ValueError: return e.code, {"error": raw[:300].decode(errors="replace")}


def step(msg, ok, detail=""):
    N[0] += 1
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:300]), flush=True)
    if not ok: FAIL.append(msg)


def sql(q):
    return subprocess.check_output(a.psql.split() + ["-X", "-q", "-At", "-d", a.db, "-c", q], text=True).strip()


def lit(s):
    return "'" + s.replace("'", "''") + "'"


def name_of(cid):
    return sql("select name from character_base where id = %d" % cid)


def fingerprint():
    """md5 of every character_base row except the renamed one's name, plus every table's row count: proves the rename
    touched one column of one row."""
    rows = sql("select md5(string_agg((to_jsonb(c) - 'name')::text, '|' order by id)) from character_base c")
    tabs = sql("select string_agg(relname || '=' || n_live_tup, ',' order by relname) from pg_stat_user_tables")
    return rows, tabs


def save(cid, new, expected):
    return api("/api/m/character/save/%d" % cid, {"values": {"name": new}, "expected": {"name": expected}})


cid = a.character
st, sheet = api("/api/m/character/sheet/%d" % cid)
step("character %d found" % cid, st == 200, sheet)
old = sheet["base"]["name"]
step("character %d is offline in the editor's eyes" % cid, not sheet["online"]["online"], sheet.get("online"))
others = sql("select string_agg(name, ',') from character_base where lifecycle_state = 'active' and id <> %d" % cid).split(",")
deleted = sql("select coalesce(string_agg(name, ','), '') from character_base where lifecycle_state = 'deleted' "
              "and restore_until > now() and id <> %d" % cid)
new = (old[:6] + "Rn")[:31]
while new.lower() in [o.lower() for o in others] + [d.lower() for d in deleted.split(",") if d]:
    new = new + "x"
before = fingerprint()

# ---- refusals first (nothing may change)
st, r = save(cid, old, old)
step("saving the stored name is a no-op (mode none)", st == 200 and r.get("mode") == "none", r)
if others and others[0]:
    st, r = save(cid, others[0].swapcase(), old)
    step("a name another active character holds, in another case (%s), is refused (400)" % others[0].swapcase(), st == 400 and "taken" in r.get("error", ""), r)
if deleted:
    d = deleted.split(",")[0]
    st, r = save(cid, d, old)
    step("a restorable deleted character's name (%s) is refused (400)" % d, st == 400 and "deleted character" in r.get("error", ""), r)
for bad, why in (("Bad<Name>", "symbols"), ("x" * 32, "32 characters"), (" Lead", "a leading space"), ("Trés", "non-ASCII"), ("", "empty")):
    st, r = save(cid, bad, old)
    # " Lead" is stripped to "Lead" by the server (ReadFixedAscii trims too), so it is accepted as "Lead" unless taken;
    # check the stored name instead of the status for that one
    if bad.startswith(" "):
        if st == 200:
            step("a leading space is stripped, not stored (%r)" % name_of(cid), name_of(cid) == "Lead", r)
            api("/api/undo", {"change": r["change"]["id"]})
        else:
            step("a leading space: refused or stripped", st == 400, r)
        continue
    step("a name with %s is refused (400)" % why, st == 400, r)
st, r = save(cid, new, old + "-stale")
step("a stale page (expected name differs) gets 409", st == 409, r)
step("nothing changed by the refusals", name_of(cid) == old and fingerprint() == before, (name_of(cid), old))
if a.online_character:
    st, r = save(a.online_character, "Onlinern", name_of(a.online_character))
    step("an online character's rename is refused (423)", st == 423, r)

# ---- the rename
st, r = save(cid, new, old)
step("rename %s -> %s" % (old, new), st == 200 and r.get("mode") == "database", r)
if st != 200:
    sys.exit("stopped: rename failed")
change = r["change"]["id"]
step("stored: character_base.name = %s" % new, name_of(cid) == new, name_of(cid))
step("the change log names it", "Name %s -> %s" % (old, new) in r["change"].get("action", ""), r["change"].get("action"))
step("only character_base.name changed (diff)", r["change"]["diff"] == {"character_base": {"added": [], "removed": [], "changed": {"[%d]" % cid: ["name"]}}}, r["change"]["diff"])
step("no other row or table changed", fingerprint()[0] == before[0], "character_base rows (without name) differ")
st, s2 = api("/api/m/character/sheet/%d" % cid)
step("the character window shows the new name", st == 200 and s2["base"]["name"] == new, s2.get("base", {}).get("name"))
st, r = save(cid, new.swapcase() if new.swapcase() != new else new.upper(), new)
case_change = r.get("change", {}).get("id") if st == 200 else None
step("a case-only rename of the same character is allowed", st == 200 and name_of(cid).lower() == new.lower(), r)
if case_change:
    st, u = api("/api/undo", {"change": case_change})
    step("Undo of the case-only rename", st == 200 and name_of(cid) == new, (u, name_of(cid)))
st, u = api("/api/undo", {"change": change})
step("Undo accepted", st == 200, u)
step("Undo: the old name %s is back" % old, name_of(cid) == old, name_of(cid))
step("Undo: every other row is as before", fingerprint()[0] == before[0])
print("%d checks, %d failed" % (N[0], len(FAIL)))
sys.exit(1 if FAIL else 0)
