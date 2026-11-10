#!/usr/bin/env python3
"""Shop editor test (NPCs module): currency per row, reordering, warnings, stale version, Undo. Runs against an editor
whose reborn database is a PRIVATE COPY and whose --rates-mgmt is a running game server (for the compiled lists):
  python3 -I tests/test_npcs_shop.py --editor http://127.0.0.1:58812 \
      --psql "psql -h 127.0.0.1 -p 55432 -U godswar" --db godswar_shoptest --i-know-this-database-is-a-copy
Leaves the Point Exchanger rows as it found them (every write undone, newest first)."""
import argparse, json, re, subprocess, sys, urllib.request, urllib.error
ap = argparse.ArgumentParser()
ap.add_argument("--editor", required=True); ap.add_argument("--psql", required=True); ap.add_argument("--db", required=True)
ap.add_argument("--i-know-this-database-is-a-copy", action="store_true")
a = ap.parse_args()
if not a.i_know_this_database_is_a_copy or a.db == "godswar":
    sys.exit("refusing: pass --i-know-this-database-is-a-copy and a database other than 'godswar'")
html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
FAIL = []
SHOP = "PointExchanger"


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
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:400]), flush=True)
    if not ok: FAIL.append(msg)


def sql(q):
    return subprocess.check_output(a.psql.split() + ["-X", "-q", "-At", "-d", a.db, "-c", q], text=True).strip()


def rows_db():
    return sql("SELECT coalesce(string_agg(format('%%s:%%s:%%s:%%s', position, item_id, price, category), ' ' ORDER BY position), '') "
               "FROM npc_shop_overrides WHERE service = '%s'" % SHOP)


def shop():
    st, d = api("/api/m/npcs/overview")
    assert st == 200, d
    return d, d["shops"][SHOP]


before = rows_db()
D, s = shop()
cur = s["currency"]
step("overview: Point Exchanger charges by item id", cur["kind"] == "item", cur)
step("overview: currency names sent", D.get("currencies", {}).get("medals") == "Medals", D.get("currencies"))
charges = {r["item"]: r["charges"] for r in s["compiled"]}
for item, want in ((9940, "honour"), (4213, "points"), (14069, "medals"), (9030, "honour"), (11010, "points")):
    step("compiled row %d charges %s" % (item, want), charges.get(item) == want, charges.get(item))
other = D["shops"].get("SpartaPetMerchant", {}).get("currency", {})
step("Sparta pet merchant charges silver", other.get("kind") == "fixed" and other.get("default") == "silver", other)
props = D["shops"].get("PropsVendor", {})
step("Props vendor: tab 1 mixes silver and B-Gold in the original list",
     {r["charges"] for r in props.get("compiled", []) if r["category"] == 0} == {"silver", "bgold"}, props.get("currency"))

base = s["override"] or s["compiled"]
rows = [{"item": r.get("item") or r.get("item_id"), "price": r["price"], "category": r["category"], "quality": r["quality"],
         "grade": r["grade"], "bound": r["bound"], "stack_marker": r["stack_marker"]} for r in base]
undo = []

# 1. add Rank IV Badge 4193 at 2000 in tab 3 right after 14069: saved, but warned that it charges B-Gold
i14069 = next(i for i, r in enumerate(rows) if r["item"] == 14069)
plus = rows[:i14069 + 1] + [dict(rows[i14069], item=4193, price=2000)] + rows[i14069 + 1:]
st, r = api("/api/m/npcs/shop/save", {"service": SHOP, "rows": plus, "version": s["version"]})
step("save with 4193 accepted", st == 200 and r.get("ok"), r)
if st == 200: undo.append(r["change"]["id"])
step("warning says 4193 charges B-Gold", any("#4193" in w and "B-Gold" in w for w in r.get("warnings", [])), r.get("warnings"))
step("warning only for 4193", len(r.get("warnings", [])) == 1, r.get("warnings"))
pos = sql("SELECT position FROM npc_shop_overrides WHERE service = '%s' AND item_id = 4193" % SHOP)
step("4193 stored at position %d" % (i14069 + 1), pos == str(i14069 + 1), pos)
v_after_add = shop()[1]["version"]

# 2. reorder: 14069 to the top of its tab, 4193 to the bottom
D, s = shop()
cur_rows = [{"item": r["item_id"], "price": r["price"], "category": r["category"], "quality": r["quality"], "grade": r["grade"],
             "bound": r["bound"], "stack_marker": r["stack_marker"]} for r in s["override"]]
tab = [r for r in cur_rows if r["category"] == 2]
first_tab_pos = cur_rows.index(tab[0])
medal = next(r for r in tab if r["item"] == 14069); badge = next(r for r in tab if r["item"] == 4193)
new_tab = [medal] + [r for r in tab if r is not medal and r is not badge] + [badge]
reordered = cur_rows[:first_tab_pos] + new_tab + cur_rows[first_tab_pos + len(tab):]
st, r = api("/api/m/npcs/shop/save", {"service": SHOP, "rows": reordered, "version": s["version"]})
step("reorder saved", st == 200 and r.get("ok"), r)
reorder_warnings = r.get("warnings", [])
if st == 200: undo.append(r["change"]["id"])
got = sql("SELECT string_agg(item_id::text, ',' ORDER BY position) FROM npc_shop_overrides WHERE service = '%s' AND category = 2" % SHOP)
step("tab 3 order stored", got == ",".join(str(x["item"]) for x in new_tab), got)
step("row count unchanged by reorder", sql("SELECT count(*) FROM npc_shop_overrides WHERE service = '%s'" % SHOP) == str(len(plus)))

# 3. a stale version is refused and writes nothing
snap = rows_db()
st, r = api("/api/m/npcs/shop/save", {"service": SHOP, "rows": plus, "version": v_after_add})
step("stale version refused", st == 409 or (st >= 400 and "change" in json.dumps(r).lower()), (st, r))
step("refused save wrote nothing", rows_db() == snap)

# 4. reordering never changes what a Point Exchanger row charges (it is by item id): the only warning is still 4193
step("reorder warnings: only the 4193 line", len(reorder_warnings) == 1 and "#4193" in reorder_warnings[0], reorder_warnings)

# 5. Undo, newest first, puts the rows back exactly
for cid in reversed(undo):
    st, r = api("/api/undo", {"change": cid})
    step("undo %s" % cid, st == 200, r)
step("rows identical to before the test", rows_db() == before, (rows_db()[:200], before[:200]))
print("%d failed" % len(FAIL) if FAIL else "all passed")
sys.exit(1 if FAIL else 0)
