#!/usr/bin/env python3
"""tests/test_inventory.py - the Inventory module (modules/inventory.py) against YOUR OWN test copy and instance.

    python3 -I tests/test_inventory.py --editor http://127.0.0.1:58812 --login YOUR_SERVER_IP:60299 \
        --account invtest1 --password 'Inv test 1' --role Invhero --phase offline|live|stock [--gwcrypt DIR]

  offline  (character logged out; any build): every write type through the module's routes - gear into an
           equipment slot with quality / grade / append attributes / holy suit / two socketed holy stones / gear EXP,
           a Holy Box with stored EXP, Storage Box Keys, an over-the-client-limit refusal and its override, a move into
           the warehouse, an equipment <-> bag swap, a delete; refusals: 403 (server temp slot, locked warehouse box),
           409 (stale row), 400 (dedicated attribute in an append row, class attribute on a non-suit item, a stone in a
           closed socket, a wrong slot). Then a headless client logs in and the test reads what the SERVER sends
           (10019 equipment records, 10033 bag pages, 10034 warehouse page). Then every change is undone from the change
           log (newest first), the rows equal the starting rows, and a second login shows the starting items again.
  live     (round2 instance): a headless client stays in the world; a new bag item is given LIVE, moved LIVE, removed
           LIVE (the client receives the 10033 bag pages); an equipment edit and a bag item with sockets are refused
           (423) while online with the round-2 wording; after the logout the same route writes the database.
  stock    (stock instance): with the client in the world, a bag add is refused (423) with "Live edits arrive with the
           round-2 server update"; after the logout it is written to the database (the offline fallback) and undone.
Ends with "ALL OK" or the first failure. Refuses an editor whose database is the live 'godswar'.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58812")
ap.add_argument("--login", required=True)
ap.add_argument("--account", required=True), ap.add_argument("--password", required=True), ap.add_argument("--role", required=True)
ap.add_argument("--phase", choices=["offline", "live", "stock"], default="offline")
ap.add_argument("--gwcrypt", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))
ap.add_argument("--warehouse", default="47750@97,-159", help="warehouse NPC for the probe (Sparta_023)")
ap.add_argument("--slow", default="8")
a = ap.parse_args()

html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
N_OK = [0]


def api(path, body=None):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET",
                                 headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"null")


def step(msg, cond, detail=""):
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)[:400]) if detail else ""), flush=True)
    if not cond:
        sys.exit(1)
    N_OK[0] += 1


def probe(extra=(), hold=0):
    out = tempfile.mktemp(suffix=".json")
    args = [sys.executable, "-I", os.path.join(HERE, "tests", "test_inventory_probe.py"), a.account, a.password, a.role, a.login,
            "--gwcrypt", a.gwcrypt, "--json", out, "--slow", a.slow] + list(extra) + (["--hold", str(hold)] if hold else [])
    return out, args


def run_probe(extra=()):
    out, args = probe(extra)
    p = subprocess.run(args, capture_output=True, text=True, timeout=400)
    step("headless client logged in", p.returncode == 0 and os.path.exists(out), (p.stderr or p.stdout)[-300:])
    return json.load(open(out))


def wait_offline(cid, secs=90):
    for _ in range(secs):
        st, on = api("/api/m/online/%d" % cid)
        if not on.get("online"):
            return on
        time.sleep(1)
    return on


def state(cid):
    st, s = api("/api/m/inventory/state/%d" % cid)
    step("state read", st == 200, s.get("error") if isinstance(s, dict) else s)
    return s


def cell(s, loc, slot):
    for r in s["items"]:
        if r["item_location"] == loc and r["slot_index"] == slot:
            return r
    return None


def blank(pid, **kw):
    it = {"prop_id": pid, "stack": 1, "bound": 1, "item_quality": 1, "item_grade": 1, "item_exp": 0, "holy_suit_code": 0,
          "holy_socket_count": 0}
    it.update(kw)
    return it


def comparable(s):
    keep = ["id", "item_location", "slot_index", "prop_id", "stack", "bound", "item_quality", "item_grade", "item_exp",
            "holy_suit_code", "holy_socket_count"] + ["attribute%d" % i for i in range(1, 6)] + ["attribute_level%d" % i for i in range(1, 6)] + \
           ["holy_socket%d_%s" % (i, k) for i in range(1, 7) for k in ("effect_id", "level")] + ["holy_socket%d_value" % i for i in range(1, 5)]
    return sorted(json.dumps({k: r.get(k) for k in keep}, sort_keys=True) for r in s["items"])


# ------------------------------------------------------------------ common start
st, s = api("/api/m/status")
step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
step("not the live database", s["reborn"].get("database") != "godswar" and not s["reborn"].get("live"))
caps = s["capabilities"].get("available")
print("     server build: %s" % ("round2 (live edits: %s)" % ", ".join(s.get("live_operations", [])) if caps else "stock (no live edits)"))
st, rows = api("/api/m/characters?q=" + a.role)
cid = next(r["id"] for r in rows if r["name"] == a.role)
st, meta = api("/api/m/inventory/meta")
step("meta: slots, %d server attributes, %d holy spirits, %d class-suit items" % (len(meta["attributes"]), len(meta["spirits"]), len(meta["class_suit_items"])),
     st == 200 and meta["client"] == {"quality": 10, "grade": 12, "sockets": 4} and len(meta["slots"]) == 19)
on = wait_offline(cid, 30)
step("%s (#%d) is offline at the start" % (a.role, cid), not on["online"], "; ".join(on.get("reasons", [])))
s0 = state(cid)

if a.phase == "offline":
    changes = []

    def write(route, body, label, code=200):
        st, r = api("/api/m/inventory/%s/%d" % (route, cid), body)
        ok = st == code and (code != 200 or r.get("mode") == "database")
        step("%s -> HTTP %s%s" % (label, st, " (database)" if code == 200 else ""), ok, (r.get("error") if st != 200 else (r.get("warnings") or "")) if isinstance(r, dict) else r)
        if st == 200:
            changes.extend(r.get("changes") or [r["change"]["id"]])       # a swap is three changes
        return r

    # 1. gear editor: Plume Coiffure into Head with everything
    head = blank(2300, item_quality=10, item_grade=12, attribute1=0, attribute_level1=5, attribute2=40, attribute_level2=3,
                 holy_suit_code=403, item_exp=12345, holy_socket_count=2,
                 holy_socket1_effect_id=1, holy_socket1_level=5, holy_socket1_value=300,
                 holy_socket2_effect_id=6, holy_socket2_level=3, holy_socket2_value=None)
    write("set", {"location": 0, "slot": 0, "item": head, "expected": None}, "gear: Plume Coiffure Q10 G12, 2 attributes, suit 403, 2 holy stones -> Head")
    # 2. Holy Box V with stored EXP, Storage Box Keys
    write("set", {"location": 1, "slot": 10, "item": blank(9024, item_quality=0, item_grade=10, item_exp=400000000), "expected": None},
          "Holy Box V with 400,000,000 stored EXP -> bag I-11")
    write("set", {"location": 1, "slot": 11, "item": blank(4102, stack=5, item_quality=0, item_grade=10), "expected": None},
          "5 Storage Box Keys -> bag I-12")
    # 3. client-limit refusal and override
    over = blank(1001, item_quality=12, item_grade=14)
    write("set", {"location": 1, "slot": 12, "item": over, "expected": None}, "Scimitar Q12 G14 without override refused", 400)
    write("set", {"location": 1, "slot": 12, "item": over, "expected": None, "override": True}, "Scimitar Q12 G14 with override -> bag I-13")
    # 4. moves: keys into the warehouse, weapon swap equipment <-> bag
    s1 = state(cid)
    write("move", {"from": {"location": 1, "slot": 11}, "to": {"location": 3, "slot": 3}, "expected_from": cell(s1, 1, 11), "expected_to": None},
          "move the keys bag I-12 -> warehouse SB-1 #4")
    s1 = sw = state(cid)
    write("move", {"from": {"location": 1, "slot": 12}, "to": {"location": 0, "slot": 10}, "expected_from": cell(s1, 1, 12), "expected_to": cell(s1, 0, 10)},
          "swap Scimitar (bag I-13) with the equipped weapon")
    # 5. delete
    s1 = state(cid)
    write("delete", {"location": 1, "slot": 1, "expected": cell(s1, 1, 1)}, "delete the starter potion in bag I-2")
    # 6. refusals
    s1 = state(cid)
    write("set", {"location": 2, "slot": -1, "item": blank(4000), "expected": None}, "server temp slot refused (read-only)", 403)
    write("set", {"location": 3, "slot": 45, "item": blank(4000), "expected": None}, "locked warehouse box SB-2 refused (capacity read-only)", 403)
    stale = dict(cell(s1, 0, 0), item_quality=1)
    write("set", {"location": 0, "slot": 0, "item": dict(head, item_grade=11), "expected": stale}, "stale row (page showed Q1) refused", 409)
    write("set", {"location": 1, "slot": 20, "item": blank(1001, attribute1=480, attribute_level1=1), "expected": None}, "elemental id in an append row refused", 400)
    write("set", {"location": 1, "slot": 20, "item": blank(1001, class_attribute1=200), "expected": None}, "class attribute on a non-suit item refused", 400)
    write("set", {"location": 1, "slot": 20, "item": blank(1001, holy_socket_count=1, holy_socket2_effect_id=1, holy_socket2_level=1), "expected": None},
          "stone in a closed socket refused", 400)
    write("set", {"location": 0, "slot": 0, "item": blank(1001), "expected": cell(s1, 0, 0)}, "a weapon into Head refused", 400)
    s2 = state(cid)
    h0 = cell(s2, 0, 0)
    step("database row: Head = 2300 Q10 G12, attrs 0/Lv5 + 40/Lv3, suit 403, sockets 2 (1 Lv5 = 300, 6 Lv3 = default)",
         h0 and h0["prop_id"] == 2300 and h0["item_quality"] == 10 and h0["item_grade"] == 12 and h0["attribute1"] == 0 and h0["attribute_level1"] == 5
         and h0["attribute2"] == 40 and h0["holy_suit_code"] == 403 and h0["holy_socket_count"] == 2 and h0["holy_socket1_value"] == 300
         and h0["holy_socket2_effect_id"] == 6 and h0["holy_socket2_value"] is None, json.dumps(h0))
    step("the swap kept both item ids", cell(s2, 0, 10)["id"] == cell(sw, 1, 12)["id"] and cell(s2, 1, 12)["id"] == cell(sw, 0, 10)["id"])

    # 7. what the SERVER loads: a headless login
    for attempt in range(3):          # a busy VPS can answer the warehouse open after the probe's window: log in again
        rep = run_probe(["--warehouse", a.warehouse])
        if (rep.get("warehouse") or {}).get("items"):
            break
        print("     no 10034 warehouse page this time (%s); logging in again" % rep.get("warehouse_messages"))
        wait_offline(cid)
    eq, kb, wh = rep.get("equipment") or {}, rep.get("kitbag") or {}, (rep.get("warehouse") or {}).get("items") or {}
    e0 = eq.get("0") or {}
    step("client got Head 2300 Q10 G12 with attributes [0, 40]", e0.get("id") == 2300 and e0.get("quality") == 10 and e0.get("grade") == 12
         and e0.get("attrs", [])[:2] == [0, 40], json.dumps(e0))
    step("client got holy suit 403, item EXP 12,345 and 2 sockets", e0.get("suit") == 403 and e0.get("exp") == 12345 and e0.get("sockets") == 2, json.dumps(e0))
    step("client got stone codes [104, 602] (effect x 100 + level - 1) and values [300, 280 (default for effect 6 Lv3)]",
         e0.get("stones", [])[:2] == [104, 602] and e0.get("stone_values", [])[:2] == [300, 280], json.dumps(e0))
    step("client got the Scimitar Q12 G14 in the weapon slot", (eq.get("10") or {}).get("id") == 1001 and eq["10"]["quality"] == 12 and eq["10"]["grade"] == 14,
         json.dumps(eq.get("10")))
    step("client got the old weapon in bag I-13 and no potion in bag I-2", (kb.get("12") or {}).get("id") == cell(sw, 0, 10)["prop_id"] and "1" not in kb,
         json.dumps({k: (v or {}).get("id") for k, v in kb.items()}))
    step("client got Holy Box V with 400,000,000 EXP (record offset 56 = 4,000,000,000 / 10)", (kb.get("10") or {}).get("id") == 9024
         and kb["10"].get("box_exp_x10") == 4000000000, json.dumps(kb.get("10")))
    step("client got 5 Storage Box Keys in warehouse SB-1 #4", (wh.get("3") or {}).get("id") == 4102 and wh["3"]["stack"] == 5, json.dumps(wh))
    wait_offline(cid)

    # 8. Undo every change (newest first) from the change log
    for cidx in reversed(changes):
        st, r = api("/api/undo", {"change": cidx})
        if st == 409:
            print("     undo %s: the server changed rows since (%s); overwriting" % (cidx, json.dumps(r)[:200]))
            st, r = api("/api/undo", {"change": cidx, "overwrite": True})
        step("undo %s" % cidx, st == 200, (r or {}).get("error") or (r.get("change") or {}).get("output", "")[:160])
    s3 = state(cid)
    step("after Undo every item row equals the start (ids, cells, all columns)", comparable(s3) == comparable(s0),
         set(comparable(s3)) ^ set(comparable(s0)))
    st, r = api("/api/undo", {"change": changes[0]})
    step("a repeated Undo is refused", st != 200, (r or {}).get("error"))
    rep2 = run_probe()
    step("second login: the client sees the starting equipment and bag again",
         {k: v["id"] for k, v in (rep2.get("equipment") or {}).items()} == {str(r["slot_index"]): r["prop_id"] for r in s0["items"] if r["item_location"] == 0}
         and {k: v["id"] for k, v in (rep2.get("kitbag") or {}).items()} == {str(r["slot_index"]): r["prop_id"] for r in s0["items"] if r["item_location"] == 1},
         json.dumps({"equipment": {k: v["id"] for k, v in (rep2.get("equipment") or {}).items()}, "kitbag": {k: v["id"] for k, v in (rep2.get("kitbag") or {}).items()}}))
    wait_offline(cid)

elif a.phase in ("live", "stock"):
    out, args = probe(hold=75)
    pr = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    for _ in range(150):
        st, on = api("/api/m/online/%d" % cid)
        if on.get("online") or pr.poll() is not None:
            break
        time.sleep(1)
    step("%s is online (headless client entering the world)" % a.role, on.get("online"),
         "; ".join(on.get("reasons", [])) if on.get("online") else "probe: %s" % ((pr.stderr.read() or b"").decode()[-300:] if pr.poll() is not None else "still running"))

    def until(route, body, what):
        for _ in range(45):
            st, r = api("/api/m/inventory/%s/%d" % (route, cid), body)
            if st in (200, 423, 400, 403):
                return st, r
            time.sleep(1)          # 409: still entering the world
        return st, r

    if a.phase == "live":
        step("round2 capabilities", caps)
        free = next(sl for sl in range(96) if not cell(s0, 1, sl))
        st, r = until("set", {"location": 1, "slot": free, "item": blank(2300, item_quality=7, item_grade=9, bound=0), "expected": None}, "give")
        step("plain bag add -> LIVE give (Plume Coiffure Q7 G9)", st == 200 and r.get("mode") == "live", r.get("error") or r.get("result"))
        time.sleep(2)
        s1 = state(cid)
        new = [x for x in s1["items"] if x["item_location"] == 1 and x["prop_id"] == 2300 and not cell(s0, 1, x["slot_index"])]
        step("the server wrote the given item itself (bag slot %s, Q7 G9)" % (new[0]["slot_index"] if new else "?"),
             new and new[0]["item_quality"] == 7 and new[0]["item_grade"] == 9, json.dumps(new))
        gslot = new[0]["slot_index"]
        target = next(sl for sl in range(30, 96) if not cell(s1, 1, sl))
        st, r = until("move", {"from": {"location": 1, "slot": gslot}, "to": {"location": 1, "slot": target}, "expected_from": new[0], "expected_to": None}, "move")
        step("bag move %d -> %d LIVE" % (gslot, target), st == 200 and r.get("mode") == "live", r.get("error") or r.get("result"))
        time.sleep(2)
        st, r = api("/api/m/inventory/set/%d" % cid, {"location": 0, "slot": 1, "item": blank(1001), "expected": None})
        step("equipment edit while online refused (423)", st == 423 and "bag" in r.get("error", ""), r.get("error"))
        st, r = api("/api/m/inventory/set/%d" % cid, {"location": 1, "slot": 90, "item": blank(2300, holy_socket_count=1), "expected": None})
        step("bag item with sockets while online refused (423)", st == 423, r.get("error"))
        s2 = state(cid)
        st, r = until("delete", {"location": 1, "slot": target, "expected": cell(s2, 1, target)}, "remove")
        step("bag remove LIVE", st == 200 and r.get("mode") == "live", r.get("error") or r.get("result"))
        pr.wait(timeout=400)
        rep = json.load(open(out))
        ch = rep.get("kitbag_changes") or []
        got = [gslot, None, 2300] in ch or (rep.get("kitbag_login") or {}).get(str(gslot), {}).get("id") == 2300
        step("client received the given item in slot %d (%s)" % (gslot, "10033 while in the world" if [gslot, None, 2300] in ch else
             "in its bag pages while entering the world"), got, json.dumps({"changes": ch, "login": {k: v["id"] for k, v in (rep.get("kitbag_login") or {}).items()}}))
        step("client received the move %d -> %d and the remove (10033 pages): %s" % (gslot, target, ch),
             [target, None, 2300] in ch and [gslot, 2300, None] in ch and [target, 2300, None] in ch, json.dumps(ch))
        wait_offline(cid)
        st, r = api("/api/m/inventory/set/%d" % cid, {"location": 1, "slot": 90, "item": blank(2300), "expected": None})
        step("after the logout the same route writes the database", st == 200 and r.get("mode") == "database", r.get("error") or r["change"]["output"][:120])
        st, r2 = api("/api/undo", {"change": r["change"]["id"]})
        step("and its Undo", st == 200, r2.get("error"))
    else:
        step("stock build: no live edits advertised", not caps)
        st, r = until("set", {"location": 1, "slot": 90, "item": blank(2300), "expected": None}, "give")
        step("bag add while online refused (423) with the round-2 message", st == 423 and "round-2" in r.get("error", ""), r.get("error"))
        st, r = api("/api/m/inventory/move/%d" % cid, {"from": {"location": 1, "slot": 0}, "to": {"location": 1, "slot": 50}})
        step("bag move while online refused (423)", st == 423, r.get("error"))
        s1 = state(cid)
        step("nothing was written while online", comparable(s1) == comparable(s0))
        pr.wait(timeout=400)
        wait_offline(cid)
        st, r = api("/api/m/inventory/set/%d" % cid, {"location": 1, "slot": 90, "item": blank(2300), "expected": None})
        step("offline fallback: after the logout the add is written to the database", st == 200 and r.get("mode") == "database", r.get("error"))
        rep = run_probe()
        step("the client sees it at the next login (bag slot 90 = 2300)", (rep.get("kitbag") or {}).get("90", {}).get("id") == 2300,
             json.dumps({k: v["id"] for k, v in (rep.get("kitbag") or {}).items()}))
        wait_offline(cid)
        st, r2 = api("/api/undo", {"change": r["change"]["id"]})
        if st == 409:
            st, r2 = api("/api/undo", {"change": r["change"]["id"], "overwrite": True})
        step("Undo", st == 200, r2.get("error"))
        step("rows back to the start", comparable(state(cid)) == comparable(s0))

print("ALL OK (%d checks, phase %s)" % (N_OK[0], a.phase))
