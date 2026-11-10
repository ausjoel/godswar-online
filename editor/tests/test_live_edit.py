#!/usr/bin/env python3
"""tests/test_live_edit.py - proves "edit while the game is running and see it": a headless game client stays in the
world, the editor changes the character through the shared save path (/api/m/edit/...), and the client RECEIVES the
change (bag pages 10033, status frame 10166). Then the same edits on the logged-out character go to the database.

Run from the Mac (or anywhere that reaches both the editor and the test instance's login port):

    python3 -I tests/test_live_edit.py --editor http://127.0.0.1:58801 --login YOUR_SERVER_IP:58805 \
        --account edtest1 --password 'Secret 99!' --role Edhero [--gwcrypt DIR]

Needs a round2 test instance (tests/make-test-env.sh <n> <db> <port> round2 up) and an editor started for it
(make-test-env.sh ... editor <port>, tunnelled). Refuses an editor whose database is the live 'godswar'.
Prints each step and ends with "ALL OK" or the first failure.
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
ap.add_argument("--editor", default="http://127.0.0.1:58801")
ap.add_argument("--login", required=True, help="HOST:PORT of the test instance's login server")
ap.add_argument("--account", required=True), ap.add_argument("--password", required=True), ap.add_argument("--role", required=True)
ap.add_argument("--gwcrypt", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))
ap.add_argument("--hold", type=float, default=70)
a = ap.parse_args()

html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)


def api(path, body=None):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET",
                                 headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"null")


def step(msg, cond, detail=""):
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + detail) if detail else ""))
    if not cond:
        sys.exit(1)


st, s = api("/api/m/status")
step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
step("not the live database", s["reborn"].get("database") != "godswar" and not s["reborn"].get("live"))
step("round2 capabilities detected", s["capabilities"].get("available"), ", ".join(s.get("live_operations", [])))
st, rows = api("/api/m/characters?q=" + a.role)
cid = next(r["id"] for r in rows if r["name"] == a.role)
st, on = api("/api/m/online/%d" % cid)
step("%s (#%d) is offline before the test" % (a.role, cid), not on["online"], "; ".join(on.get("reasons", [])))

out = tempfile.mktemp(suffix=".json")
probe = subprocess.Popen([sys.executable, "-I", os.path.join(HERE, "reborn_probe.py"), a.account, a.password, a.role, a.login,
                          "--hold", str(a.hold), "--gwcrypt", a.gwcrypt, "--json", out], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)


def until_live(path, body, what):
    """retry while the client is still entering the world (409) until the edit is applied live."""
    for _ in range(40):
        st, r = api(path, body)
        if st == 200:
            return r
        if st not in (409, 423):
            step(what, False, "HTTP %s %s" % (st, r))
        time.sleep(1)
    step(what, False, "never became live: %s %s" % (st, r))


r = until_live("/api/m/edit/%d/fields" % cid, {"values": {"Money": 7654321, "Stone": 4321, "BindingGold": 99}, "label": "test wallet"},
               "wallet live")
step("wallet set LIVE (Silver 7,654,321, Gold 4,321, B-Gold 99)", r["mode"] == "live", json.dumps(r.get("result"))[:200])
wallet_change = r["change"]["id"]
r = until_live("/api/m/edit/%d/fields" % cid, {"values": {"fighter_job_lv": 45, "SkillPoint": 500, "zodiac_energy": 50}}, "progression live")
step("level 45, talent points 500, zodiac energy 50 LIVE", r["mode"] == "live", json.dumps(r.get("result"))[:200])
r = until_live("/api/m/edit/%d/bag" % cid, {"op": "add", "item": 1001, "quantity": 1, "quality": 10, "grade": 12, "bound": 0}, "give live")
step("Scimitar (1001) Q10 G12 given LIVE", r["mode"] == "live", json.dumps(r.get("result")))
slot = int(re.search(r"slots? (\d+)", json.dumps(r["result"])).group(1))
r = until_live("/api/m/edit/%d/bag" % cid, {"op": "move", "from": slot, "to": 30}, "move live")
step("moved bag slot %d -> 30 LIVE" % slot, r["mode"] == "live", json.dumps(r.get("result")))
st, r = api("/api/m/edit/%d/fields" % cid, {"values": {"pet_shed_capacity": 3}})
step("a field without a live operation is refused while online (423)", st == 423, r.get("error", "")[:160])
st, r = api("/api/m/edit/%d/fields" % cid, {"values": {"Money": 1}, "expected": {"Money": 123}})
step("a stale expected value is refused (409)", st == 409, r.get("error", "")[:160])
time.sleep(1)
st, r = api("/api/undo", {"change": wallet_change})
step("Undo of the live wallet change runs the inverse live", st == 200 and (r.get("change") or {}).get("engine") == "reborn-live",
     (r.get("change") or {}).get("action", json.dumps(r)[:200]))
time.sleep(2)
r = until_live("/api/m/edit/%d/bag" % cid, {"op": "add", "item": 4002, "quantity": 2}, "give 2")
st, r = api("/api/m/edit/%d/bag" % cid, {"op": "remove", "slot": 30})
step("removed the Scimitar from bag slot 30 LIVE", st == 200 and r.get("mode") == "live", json.dumps(r.get("result") or r)[:200])

probe.wait(timeout=a.hold + 120)
rep = json.load(open(out))
hist = rep.get("status_history") or []
held = [x for x in hist if x["phase"] == "hold"]
step("client received 10166 with Silver 7,654,321 / Gold 4,321 / B-Gold 99",
     any(x["silver"] == 7654321 and x["gold"] == 4321 and x["binding_gold"] == 99 for x in held), json.dumps(held[:6]))
step("client received 10166 with level 45", any(x["level"] == 45 for x in held))
step("client received the Undo (Silver back to the old value)", held and held[-1]["silver"] != 7654321, json.dumps(held[-1:]))
kb = rep.get("kitbag") or {}
step("client received bag pages with the Scimitar Q10 G12 in slot 30 (then the remove)",
     [30, None, 1001] in (rep.get("kitbag_changes") or []) and [30, 1001, None] in (rep.get("kitbag_changes") or []),
     "bag changes the client saw [slot, was, now]: %s" % rep.get("kitbag_changes"))
step("the client's bag no longer shows slot 30 after the remove", "30" not in kb, json.dumps(kb.get("30")))
print("     hold messages (s after enter, type, count):", rep.get("hold_messages"))

for _ in range(30):
    st, on = api("/api/m/online/%d" % cid)
    if not on["online"]:
        break
    time.sleep(1)
step("%s logged out" % a.role, not on["online"])
r = api("/api/m/edit/%d/fields" % cid, {"values": {"Money": 4242}})[1]
step("offline: the same save path writes the DATABASE", r.get("mode") == "database", (r.get("change") or {}).get("output", ""))
st, r = api("/api/undo", {"change": r["change"]["id"]})
step("offline Undo", st == 200, (r.get("change") or {}).get("output", json.dumps(r)[:200]))
print("ALL OK")
