#!/usr/bin/env python3
"""tests/test_library.py - the Library module (modules/library.py, static/modules/library.js), end to end.

Run from the Mac against YOUR OWN test environment (tests/README.md): an editor started by make-test-env.sh and
tunnelled, its reborn instance, and the same database read-only through psql for the row checks.

    python3 -I tests/test_library.py --editor http://127.0.0.1:58818 --login YOUR_SERVER_IP:60899 \
        --account libtest1 --password 'Lib pass 1' --role Libhero --mode stock|round2 \
        --psql "ssh -o BatchMode=yes vps sudo -n -u postgres psql" --db godswar_ed_library [--gwcrypt DIR]

What it proves:
  every mode    every read route (tabs, catalogue counts, the detail of the first / middle / last entry of every tab,
                search, zodiac, talents, shops, suits, char), 404 / 400 answers.
  --mode stock  OFFLINE character: give an item (database write) -> the headless client's kit bag at login holds it
                with the quality / grade sent; Undo -> gone at the next login. Move to a map place (shared
                GW.saveFields path) -> the client enters at those coordinates; Undo -> back. A stale `expected`
                answers 409 and writes nothing; a read-only column answers 403; bad input 400.
                ONLINE character (client held in the world): give and move answer 423 with the round-2 message,
                nothing written.
  --mode round2 ONLINE character: give -> mode live, the client in the world receives the item in a 10033 bag page;
                move -> mode live (teleport); after the client logs out the server's own save holds the item and the
                new position (the editor wrote nothing itself); then the database path removes the item and Undo of
                the move restores the position.
Refuses an editor whose database is the live 'godswar'. Prints each step; ends with "ALL OK" or the first failure.
"""
import argparse
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58818")
ap.add_argument("--login", required=True)
ap.add_argument("--account", required=True)
ap.add_argument("--password", required=True)
ap.add_argument("--role", required=True)
ap.add_argument("--mode", choices=("stock", "round2"), required=True)
ap.add_argument("--psql", default="ssh -o BatchMode=yes vps sudo -n -u postgres psql")
ap.add_argument("--db", required=True)
ap.add_argument("--gwcrypt", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))
ap.add_argument("--hold", type=float, default=45)
a = ap.parse_args()
if a.db == "godswar":
    sys.exit("refusing the live database 'godswar'")

html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
FAILS = []


def api(path, body=None):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET",
                                 headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"null")
        except ValueError:
            return e.code, None


def step(msg, cond, detail=""):
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)) if detail else ""), flush=True)
    if not cond:
        sys.exit(1)


def sql(q):
    """read-only query on the test copy -> list of rows (tab separated)."""
    cmd = shlex.split(a.psql) + ["-X", "-A", "-t", "-F", "\t", "-d", a.db, "-c", "SET default_transaction_read_only = on; " + q]
    if cmd[0] == "ssh":       # the remote shell re-splits the command: quote it once more
        cmd = cmd[:cmd.index("sudo")] + [" ".join(shlex.quote(x) for x in cmd[cmd.index("sudo"):])]
    out = subprocess.run(cmd, capture_output=True, text=True, timeout=60)
    if out.returncode:
        raise SystemExit("psql failed: " + out.stderr)
    return [l.split("\t") for l in out.stdout.splitlines() if l.strip() and l != "SET"]


PROBE = None


def probe_copy():
    """reborn_probe.py waits 2-3 s for the login and role answers; a busy test VPS (several instances, PBKDF2 password
    check) can take longer. Run a private copy with longer waits (the protocol steps are unchanged)."""
    global PROBE
    if PROBE is None:
        src = open(os.path.join(HERE, "reborn_probe.py"), encoding="utf-8").read()
        src = re.sub(r"\.recv\(2\)", ".recv(12)", src)          # login, realm, redirect, role list; not the world entry
        fd, PROBE = tempfile.mkstemp(prefix="lib_probe_", suffix=".py")
        with os.fdopen(fd, "w") as f:
            f.write(src)
    return PROBE


def probe(hold=0, create=False, background=False):
    out = tempfile.mktemp(suffix=".json")
    cmd = [sys.executable, "-I", probe_copy(), a.account, a.password, a.role, a.login, "--json", out,
           "--gwcrypt", a.gwcrypt] + (["--create"] if create else []) + (["--hold", str(hold)] if hold else [])
    if background:
        return subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True), out
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=120 + hold)
    if p.returncode:
        raise SystemExit("probe failed: " + (p.stderr or p.stdout)[-600:])
    return json.load(open(out))


def wait_online(cid, want=True, secs=40):
    for _ in range(int(secs * 2)):
        st, o = api("/api/m/online/%d" % cid)
        if st == 200 and bool(o.get("online")) == want:
            return o
        time.sleep(0.5)
    return None


def bag_rows(cid, pid):
    return sql("SELECT slot_index, item_quality, item_grade, bound, stack FROM character_items WHERE user_id = %d AND item_location = 1 "
               "AND prop_id = %d ORDER BY slot_index" % (cid, pid))


# ------------------------------------------------------------------------------------------- read routes
st, s = api("/api/m/status")
step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
step("not the live database", s["reborn"].get("database") == a.db and not s["reborn"].get("live"), s["reborn"].get("database"))
caps = s["capabilities"].get("available")
step("server build matches --mode", bool(caps) == (a.mode == "round2"), "capabilities %s" % caps)
st, mods = api("/api/m")
lib = [m for m in mods["modules"] if m["name"] == "library"]
step("library module loaded", st == 200 and lib and lib[0]["js"] and "library" not in mods["load_errors"], lib and lib[0]["routes"])
st, tabs = api("/api/m/library/tabs")
step("tabs", st == 200 and len(tabs["tabs"]) >= 15 and all((t["count"] or 0) > 0 for t in tabs["tabs"]),
     ", ".join("%s %s" % (t["key"], t["count"]) for t in tabs["tabs"]))
items_tab = [t for t in tabs["tabs"] if t["key"] == "items"][0]
st, ix = api("/api/m/catalog/item")
step("the items tab lists every item (client + server-only)", ix["count"] == items_tab["count"] and ix["count"] >= 3473
     and sum(1 for r in ix["rows"] if r[11] == "weapon") >= 145, "%d items, %d weapons" % (ix["count"], sum(1 for r in ix["rows"] if r[11] == "weapon")))
n_detail = 0
for t in tabs["tabs"]:
    if not t["type"]:
        continue
    st, tix = api("/api/m/catalog/" + t["type"])
    rows = tix["rows"] if t["key"] != "mounts" else [r for r in tix["rows"] if (r[11] or "").startswith("mount")]
    for r in (rows[0], rows[len(rows) // 2], rows[-1]):
        st, d = api("/api/m/library/detail/%s/%s" % (t["type"], urllib.request.quote(str(r[0]), safe="")))
        if st != 200 or not d.get("attributes") and t["type"] not in ("title",):
            step("detail %s %s" % (t["type"], r[0]), False, (st, d and d.get("error")))
        n_detail += 1
step("detail of the first / middle / last entry of every tab", True, "%d details" % n_detail)
st, d = api("/api/m/library/detail/item/1813")
step("item detail: English attributes, stats by quality, cross references, server template",
     st == 200 and any(x["label"] == "Required level" for x in d["attributes"]) and d["stats"] and d["refs"].get("May roll these append attributes")
     and (d["server"].get("template") or [{}])[0].get("id") == 1813, list(d["refs"]))
st, d = api("/api/m/library/detail/skill/50")
step("skill detail fills the client placeholders", st == 200 and "<s" not in d["desc_filled"] and "{Power" not in d["desc_filled"]
     and "Bleeding" in d["desc_filled"], d["desc_filled"][:90])
st, d = api("/api/m/library/detail/map/2")
step("map detail: places, gates, server spawn points", st == 200 and d["places"] and d["gates"] and d["plot"]["monsters"],
     "%d places, %d monster points" % (len(d["places"]), len(d["plot"]["monsters"])))
st, d = api("/api/m/library/detail/monster/A_Boss_gensbd_002")
step("monster detail: maps + server rows", st == 200 and d["refs"].get("Lives on"), list(d["refs"]))
st, r = api("/api/m/library/search?q=dragon")
step("search across tabs", st == 200 and r["total"] > 10 and len(r["groups"]) >= 3, [(g["tab"], g["count"]) for g in r["groups"]])
for p in ("zodiac", "talents", "shops", "suits"):
    st, r = api("/api/m/library/" + p)
    step("route " + p, st == 200 and r, len(json.dumps(r)))
st, r = api("/api/m/library/detail/item/99999999")
step("unknown id -> 404", st == 404, r and r.get("error"))
st, r = api("/api/m/library/search?q=a")
step("one-letter search -> 400", st == 400, r and r.get("error"))

# ------------------------------------------------------------------------------------------- the test character
st, rows = api("/api/m/characters?q=" + a.role)
mine = [x for x in rows if x["name"] == a.role]
if not mine:
    # the account: made with the Accounts page's own route (the server's registration insert); "exists" is fine
    st, r = api("/api/m/accounts/create", {"username": a.account, "password": a.password})
    step("test account " + a.account, st == 200 or "exists" in str((r or {}).get("error", "")) or "taken" in str((r or {}).get("error", "")),
         (r or {}).get("error") or "created")
    probe(create=True)
    st, rows = api("/api/m/characters?q=" + a.role)
    mine = [x for x in rows if x["name"] == a.role]
step("test character exists", bool(mine), a.role)
cid = mine[0]["id"]
step("character is offline before the test", wait_online(cid, False, 30) is not None)
st, pos0 = api("/api/m/library/char/%d" % cid)
step("char route", st == 200 and "map" in pos0, pos0)
st, m2 = api("/api/m/library/detail/map/%s" % pos0["map"])
place = [p for p in m2["places"] if abs(float(p["x"]) - float(pos0["x"])) + abs(float(p["z"]) - float(pos0["z"])) > 20][0]
ITEM = 1001                    # Scimitar (one-hand sword, equipment: Q1..Q20, grade 0..25)


def give(q=3, g=2):
    return api("/api/m/library/give/%d" % cid, {"item": ITEM, "quantity": 1, "quality": q, "grade": g, "bound": 1})


def move(x, z, expected):
    return api("/api/m/edit/%d/fields" % cid, {"values": {"Map": int(pos0["map"]), "Pos_X": float(x), "Pos_Z": float(z)},
                                               "expected": expected, "label": "library test move"})


exp0 = {"Map": int(pos0["map"]), "Pos_X": float(pos0["x"]), "Pos_Z": float(pos0["z"])}
step("no Scimitar in the bag yet", not bag_rows(cid, ITEM), bag_rows(cid, ITEM))

if a.mode == "stock":
    # ---- offline: give -> database, the client loads it, Undo
    st, r = give()
    step("give (offline) -> database write", st == 200 and r["mode"] == "database" and r["change"]["id"], r.get("error") or r["change"]["action"])
    chg = r["change"]["id"]
    rows_ = bag_rows(cid, ITEM)
    step("row written: Q3 G2 bound", rows_ and rows_[0][1:4] == ["3", "2", "1"], rows_)
    rep = probe()
    got = [v for v in rep["kitbag"].values() if v["id"] == ITEM]
    step("headless client: the kit bag at login holds it (Q3, grade 2, bound)", got and got[0]["quality"] == 3 and got[0]["grade"] == 2 and got[0]["bound"] == 1, got)
    step("character offline again", wait_online(cid, False) is not None)
    st, r = api("/api/undo", {"change": chg})
    step("Undo of the give", st == 200 and not (r or {}).get("error"), (r or {}).get("error") or "undone")
    step("row gone after Undo", not bag_rows(cid, ITEM), bag_rows(cid, ITEM))
    rep = probe()
    step("headless client: gone at the next login", not [v for v in rep["kitbag"].values() if v["id"] == ITEM])
    step("character offline again", wait_online(cid, False) is not None)
    # ---- offline: move to a place (the shared GW.saveFields path the Library's map tab uses)
    st, pos1 = api("/api/m/library/char/%d" % cid)
    exp1 = {"Map": int(pos1["map"]), "Pos_X": float(pos1["x"]), "Pos_Z": float(pos1["z"])}
    st, r = move(place["x"], place["z"], {"Map": exp1["Map"], "Pos_X": exp1["Pos_X"] + 50, "Pos_Z": exp1["Pos_Z"]})
    step("move with a stale expected position -> 409, nothing written", st == 409, (r or {}).get("error", "")[:120])
    st, chk = api("/api/m/library/char/%d" % cid)
    step("position unchanged after the 409", float(chk["x"]) == exp1["Pos_X"] and float(chk["z"]) == exp1["Pos_Z"])
    st, r = move(place["x"], place["z"], exp1)
    step("move to %s (offline) -> database" % place["name"], st == 200 and r["mode"] == "database", (r or {}).get("error") or r["change"]["action"])
    mchg = r["change"]["id"]
    rep = probe()
    e = rep.get("enter") or {}
    step("headless client enters at the new place", abs(e.get("x", 1e9) - float(place["x"])) < 1.5 and abs(e.get("z", 1e9) - float(place["z"])) < 1.5,
         "x %.1f z %.1f (place %s, %s)" % (e.get("x", 0), e.get("z", 0), place["x"], place["z"]))
    step("character offline again", wait_online(cid, False) is not None)
    st, r = api("/api/undo", {"change": mchg, "overwrite": True})
    st2, chk = api("/api/m/library/char/%d" % cid)
    step("Undo of the move restores the position", st == 200 and float(chk["x"]) == exp1["Pos_X"] and float(chk["z"]) == exp1["Pos_Z"],
         (r or {}).get("error") or "x %s z %s" % (chk["x"], chk["z"]))
    # ---- refusals
    st, r = api("/api/m/edit/%d/fields" % cid, {"values": {"warehouse_capacity": 80}})
    step("read-only column -> 403", st == 403, (r or {}).get("error", "")[:120])
    st, r = api("/api/m/library/give/%d" % cid, {"item": 99999999})
    step("give an item the server does not have -> 400", st == 400, (r or {}).get("error"))
    st, r = api("/api/m/library/give/%d" % cid, {"item": ITEM, "quantity": 0})
    step("give quantity 0 -> 400", st == 400, (r or {}).get("error"))
    # ---- online on the stock server: refused with the round-2 message
    proc, out = probe(hold=a.hold, background=True)
    o = wait_online(cid, True, 40)
    step("headless client in the world (online)", o is not None, o and "; ".join(o.get("reasons", []))[:120])
    time.sleep(3)
    st, r = give()
    step("give while online on stock -> 423 'round-2'", st == 423 and "round-2" in (r or {}).get("error", ""), (r or {}).get("error", "")[:140])
    st, pos2 = api("/api/m/library/char/%d" % cid)
    st, r = move(place["x"], place["z"], {"Map": int(pos2["map"]), "Pos_X": float(pos2["x"]), "Pos_Z": float(pos2["z"])})
    step("move while online on stock -> 423 'round-2'", st == 423 and "round-2" in (r or {}).get("error", ""), (r or {}).get("error", "")[:140])
    step("nothing written while online", not bag_rows(cid, ITEM))
    proc.wait(timeout=a.hold + 120)
    step("character offline again", wait_online(cid, False, 60) is not None)
else:
    # ---- round2: the client stays in the world, the running server applies the edits
    proc, out = probe(hold=a.hold, background=True)
    o = wait_online(cid, True, 40)
    step("headless client in the world (online)", o is not None and "character.give_item" in (o.get("live_bag") or []), o and o.get("live_bag"))
    for _ in range(15):            # "at character select / entering the world" answers 409: wait until the world session exists
        st, r = give()
        if not (st == 409 and "again" in str((r or {}).get("error", ""))):
            break
        time.sleep(2)
    step("give while online -> live (the game server adds it)", st == 200 and r["mode"] == "live", (r or {}).get("error") or r["change"]["action"])
    st, pos2 = api("/api/m/library/char/%d" % cid)
    exp2 = {"Map": int(pos2["map"]), "Pos_X": float(pos2["x"]), "Pos_Z": float(pos2["z"])}
    for _ in range(10):
        st, r = move(place["x"], place["z"], exp2)
        if not (st == 409 and "again" in str((r or {}).get("error", ""))):
            break
        time.sleep(2)
    step("move while online -> live teleport", st == 200 and r["mode"] == "live", (r or {}).get("error") or r["change"]["action"])
    mchg = r["change"]["id"]
    proc.wait(timeout=a.hold + 120)
    rep = json.load(open(out))
    ch = [c for c in rep.get("kitbag_changes") or [] if c[2] == ITEM]
    step("headless client RECEIVED the item in a bag page while in the world", bool(ch), rep.get("kitbag_changes"))
    step("character offline again", wait_online(cid, False, 60) is not None)
    rows_ = bag_rows(cid, ITEM)
    step("the server's own logout save holds the item (the editor wrote no item row)", rows_ and rows_[0][1:3] == ["3", "2"], rows_)
    st, chk = api("/api/m/library/char/%d" % cid)
    step("the server's logout save holds the new position", abs(float(chk["x"]) - float(place["x"])) < 1.5 and abs(float(chk["z"]) - float(place["z"])) < 1.5,
         "x %s z %s" % (chk["x"], chk["z"]))
    # ---- offline again: the database path takes the item back, Undo of the live move restores the position
    st, r = api("/api/m/edit/%d/bag" % cid, {"op": "remove", "slot": int(rows_[0][0])})
    step("offline: remove the given item (database path)", st == 200 and r["mode"] == "database", (r or {}).get("error"))
    step("item gone", not bag_rows(cid, ITEM))
    st, r = api("/api/undo", {"change": mchg, "overwrite": True})
    st2, chk = api("/api/m/library/char/%d" % cid)
    step("Undo of the live move (offline now -> database)", st == 200 and abs(float(chk["x"]) - exp2["Pos_X"]) < 1.5,
         (r or {}).get("error") or "x %s z %s" % (chk["x"], chk["z"]))
    rep = probe()
    e = rep.get("enter") or {}
    step("headless client enters at the original position, without the item", abs(e.get("x", 1e9) - exp2["Pos_X"]) < 1.5
         and not [v for v in rep["kitbag"].values() if v["id"] == ITEM], "x %.1f z %.1f" % (e.get("x", 0), e.get("z", 0)))
print("ALL OK")
