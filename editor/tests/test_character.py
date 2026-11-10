#!/usr/bin/env python3
"""tests/test_character.py - the Character + Account editor (modules/character.py) end to end, against YOUR OWN test copy.

Run from the Mac (it reaches both the tunnelled editor and the test instance's login port):

    python3 -I tests/test_character.py --editor http://127.0.0.1:58811 --login YOUR_SERVER_IP:60199 \
        [--gwcrypt GodsWar-2.51/Polish/tools] [--keep]

It makes its own account + character (chtNNNN / ChtNNNN) through the editor and the headless client, then proves:
  * every character_base write type (wallet, level + EXP, talents, zodiac, faith, honour, map + position, rename):
    written by the editor, LOADED by the game server (the headless client reads level / Silver / Gold / B-Gold from
    10166, name / next-level EXP / X / Z from 10019), and Undo puts the rows back (and the server loads those);
  * refusals: read-only field (403), stale value (409), game rules (400: EXP above the level cost, zodiac energy above
    the level's storage, a title not owned, an unknown map, level 201);
  * an ONLINE character: stock server -> 423 with the round-2 message; round-2 server -> applied live and received by
    the client in the world, Undo live, a field without a live endpoint -> 423;
  * accounts: create (capitals refused), login with it, rename, new password (old one refused), e-mail / donator tier,
    battle pass grant / revoke + Undo, character delete / restore the game's way + Undo, retire + Undo, delete for
    good (refused for an account with evidence) + Undo, and the restored account logs in again.
Prints each step and ends with "ALL OK" (exit 0) or the first failure (exit 1). Refuses an editor on the live database.
"""
import argparse
import json
import os
import random
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58811")
ap.add_argument("--login", required=True, help="HOST:PORT of the test instance's login server")
ap.add_argument("--gwcrypt", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))
ap.add_argument("--tag", default=None, help="4 digits for the test account / character names (default random)")
ap.add_argument("--keep", action="store_true", help="keep the test account at the end (default: delete it for good)")
a = ap.parse_args()

html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
EXP_45 = 76772            # PlayerExperienceCatalog: EXP level 45 needs


def api(path, body=None, timeout=180):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET",
                                 headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw or b"null")
        except ValueError:
            return e.code, {"error": raw[:300].decode(errors="replace")}


N = [0]


def step(msg, cond, detail=""):
    N[0] += 1
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)) if detail else ""), flush=True)
    if not cond:
        sys.exit(1)


_PROBE = []


def probe_path():
    """reborn_probe.py as shipped, except that the first login reply may take up to 30 s: the password check is
    PBKDF2 with 600,000 rounds, and on a busy VPS it does not answer within the probe's fixed 2 s (the copy lives in a
    temp folder; the shared file is not changed)."""
    if not _PROBE:
        src = open(os.path.join(HERE, "reborn_probe.py"), encoding="utf-8").read()
        old = "r = c.recv(2)\nif not r or r[0][0] != 3"
        assert old in src, "reborn_probe.py changed: update test_character.py's login wait"
        src = src.replace(old, "r = c.recv(2)\nfor _w in range(14):\n    if r:\n        break\n    r = c.recv(2)\nif not r or r[0][0] != 3")
        d = tempfile.mkdtemp(prefix="gwchar-probe-")
        with open(os.path.join(d, "reborn_probe.py"), "w", encoding="utf-8") as f:
            f.write(src)
        _PROBE.append(os.path.join(d, "reborn_probe.py"))
    return _PROBE[0]


def probe(account, password, role, create=False, hold=0, wait=True):
    out = tempfile.mktemp(suffix=".json")
    cmd = [sys.executable, "-I", probe_path(), account, password, role, a.login, "--gwcrypt", a.gwcrypt, "--json", out]
    if create:
        cmd.append("--create")
    if hold:
        cmd += ["--hold", str(hold)]
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if not wait:
        return p, out
    so, se = p.communicate(timeout=hold + 120)
    rep = json.load(open(out)) if os.path.exists(out) else None
    return rep, (so + se).decode(errors="replace")


def undo(change, label):
    st, r = api("/api/undo", {"change": change})
    step("Undo: " + label, st == 200, (r.get("change") or {}).get("output", json.dumps(r)[:240]))
    return r


def sheet(cid):
    st, s = api("/api/m/character/sheet/%d" % cid)
    step("sheet #%d reads" % cid, st == 200, s.get("error", ""))
    return s


def wait_offline(cid, secs=40):
    for _ in range(secs):
        st, on = api("/api/m/online/%d" % cid)
        if not on.get("online"):
            return True
        time.sleep(1)
    return False


# ------------------------------------------------------------------ 0. the editor and its database
st, s = api("/api/m/status")
db = s["reborn"].get("database")
step("editor answers", st == 200, "database %s" % db)
step("not the live database", db != "godswar" and not s["reborn"].get("live"))
round2 = bool(s["capabilities"].get("available"))
print("     server build:", "round2 (live edits: %s)" % ", ".join(s.get("live_operations", [])) if round2 else "stock (no live-edit API)")

tag = a.tag or "%04d" % random.randint(0, 9999)
user, pw, role = "cht" + tag, "Test pass " + tag, "Cht" + tag

# ------------------------------------------------------------------ 1. accounts: create + first login
st, r = api("/api/m/character/account/create", {"username": "ChTest" + tag, "password": pw})
step("a user name with capitals is refused (the game client cannot log in with it)", st == 400, r.get("error", "")[:120])
st, r = api("/api/m/character/account/create", {"username": user, "password": pw, "email": user + "@example.test"})
step("account %s created" % user, st == 200 and r.get("account"), (r.get("change") or {}).get("action", r.get("error")))
aid = r["account"]["id"]
acc_create_change = r["change"]["id"]
step("the change log hides the password verifier", "gws$pbkdf2" not in json.dumps(r["change"].get("sql")) or "hidden" in json.dumps(r["change"].get("sql")))
st, r = api("/api/m/character/account/create", {"username": user, "password": pw})
step("the same user name again is refused", st == 400, r.get("error", "")[:100])
rep, log = probe(user, pw, role, create=True)
step("headless client: %s logs in and creates %s" % (user, role), rep and (rep.get("enter") or {}).get("name") == role, log[-300:] if not rep else rep.get("enter"))
st, rows = api("/api/m/characters?q=" + role)
cid = next(x["id"] for x in rows if x["name"] == role)
step("character %s is #%d on account #%d" % (role, cid, aid), wait_offline(cid))

# ------------------------------------------------------------------ 2. character_base writes, loaded by the server, Undo
s0 = sheet(cid)
b0 = s0["base"]
step("sheet: names in English, EXP table, level cap", s0["names"]["class"] and len(s0["exp_table"]) == 200 and s0["level"]["cap"] == 200,
     "%s / %s / map %s" % (s0["names"]["class"], s0["names"]["camp"], s0["names"]["map"]))
home = int(b0["Map"])
vals = {"Money": 123456, "Stone": 2345, "BindingGold": 77, "fighter_job_lv": 45, "fighter_job_exp": 1000, "SkillPoint": 42,
        "SkillExp": 50, "zodiac_type": 5, "zodiac_level": 3, "zodiac_energy": 5000, "belief": 2, "medusa_honor_points": 900,
        "exchange_point": 11, "exchange_medal": 12, "Map": home, "Pos_X": 170.0, "Pos_Z": -39.0, "hair_style": 3,
        "pet_shed_capacity": 4}
st, r = api("/api/m/character/save/%d" % cid, {"values": vals, "expected": {k: b0[k] for k in vals}})
step("offline save of %d fields -> database" % len(vals), st == 200 and r.get("mode") == "database", (r.get("change") or {}).get("output", r.get("error", ""))[:200])
big = r["change"]["id"]
s1 = sheet(cid)
step("stored: every value", all(float(s1["base"][k]) == float(v) for k, v in vals.items()), {k: s1["base"][k] for k in vals})
st, r = api("/api/m/character/save/%d" % cid, {"values": {"name": role + "x"}, "expected": {"name": role}})
step("rename the character %s -> %sx" % (role, role), st == 200 and r.get("mode") == "database", r.get("error", ""))
ren = r["change"]["id"]
rep, log = probe(user, pw, role + "x")
en, stt = (rep or {}).get("enter") or {}, (rep or {}).get("status") or {}
step("server loaded: name %sx" % role, en.get("name") == role + "x", en)
step("server loaded: level 45 (10166) and next-level EXP %s (10019)" % format(EXP_45, ","), stt.get("level") == 45 and en.get("next_level_exp") == EXP_45, (stt, en.get("next_level_exp")))
step("server loaded: Silver 123,456 / Gold 2,345 / B-Gold 77", stt.get("silver") == 123456 and stt.get("gold") == 2345 and stt.get("binding_gold") == 77, stt)
step("server loaded: position X 170, Z -39", abs(en.get("x", 0) - 170) < 1 and abs(en.get("z", 0) + 39) < 1, (en.get("x"), en.get("z")))
step("logged out again", wait_offline(cid))
# the server saved position / vitals at logout: Undo must still work (it only puts back the columns the edit changed)
undo(ren, "rename")
st, r = api("/api/undo", {"change": big})
if st == 409:
    step("Undo of the big save sees what the server saved at logout (409) and asks", True, (r.get("error") or "")[:160])
    st, r = api("/api/undo", {"change": big, "overwrite": True})
step("Undo of the big save", st == 200, (r.get("change") or {}).get("output", json.dumps(r)[:200]))
s2 = sheet(cid)
step("rows back: level, wallet, zodiac, faith, name", all(str(s2["base"][k]) == str(b0[k]) for k in ("fighter_job_lv", "Money", "Stone", "BindingGold",
                                                                                                     "zodiac_level", "zodiac_energy", "belief", "name", "SkillPoint")),
     {k: (b0[k], s2["base"][k]) for k in ("fighter_job_lv", "Money", "zodiac_energy", "name")})
rep, log = probe(user, pw, role)
stt = (rep or {}).get("status") or {}
step("server loads the undone rows: name %s, level %s, Silver %s" % (role, b0["fighter_job_lv"], b0["Money"]),
     ((rep or {}).get("enter") or {}).get("name") == role and stt.get("level") == int(b0["fighter_job_lv"]) and stt.get("silver") == int(b0["Money"]), stt)
step("logged out again", wait_offline(cid))

# ------------------------------------------------------------------ 3. refusals
b = sheet(cid)["base"]
st, r = api("/api/m/character/save/%d" % cid, {"values": {"prestige": 5}})
step("read-only field (prestige: not read by this server) -> 403", st == 403, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"Money": 5}, "expected": {"Money": int(b["Money"]) + 1}})
step("stale value -> 409", st == 409, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"fighter_job_exp": 10 ** 6}})
step("EXP above what the level needs -> 400", st == 400, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"zodiac_energy": 999999}})
step("zodiac energy above the level's storage -> 400", st == 400, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"selected_title_id": 1000}})
step("a title the character does not own -> 400", st == 400, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"Map": 999, "Pos_X": 0, "Pos_Z": 0}})
step("an unknown map -> 400", st == 400, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"fighter_job_lv": 201}})
step("level 201 -> 400", st == 400, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"curHP": int(b["MaxHP"]) + 1}})
step("HP above the base max HP -> 400 (the server clamps it and every logout's vitals flush would fail)", st == 400, r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"Map": 207, "Pos_X": 0, "Pos_Z": 0}})
step("a dungeon map (Wonderland 207) -> 400 (the server sends the character home at login)", st == 400 and "dungeon" in r.get("error", ""), r.get("error", "")[:140])
st, r = api("/api/m/character/save/%d" % cid, {"values": {"fighter_job_exp": 150}})
step("EXP 150 stored (for the live level + Undo check)", st == 200, r.get("error", ""))

# ------------------------------------------------------------------ 4. the character ONLINE
p, out = probe(user, pw, role, hold=120, wait=False)
for _ in range(150):         # a busy VPS: the PBKDF2 login can take a minute
    st, on = api("/api/m/online/%d" % cid)
    if on.get("online"):
        break
    time.sleep(1)
step("%s is online (headless client in the world)" % role, on.get("online"), "; ".join(on.get("reasons", [])))
time.sleep(4)
if not round2:
    st, r = api("/api/m/character/save/%d" % cid, {"values": {"Money": 4242}})
    step("stock server: a save on the online character -> 423 'arrive with the round-2 server update'", st == 423 and "round-2" in r.get("error", ""), r.get("error", "")[:160])
else:
    def live(vals, what):
        for _ in range(30):
            st, r = api("/api/m/character/save/%d" % cid, {"values": vals})
            if st == 200:
                return r
            if st not in (409, 423):
                step(what, False, "HTTP %s %s" % (st, r))
            time.sleep(1)
        step(what, False, r)
    r = live({"Money": 7654321, "Stone": 4321, "BindingGold": 99}, "wallet live")
    step("round-2: Silver / Gold / B-Gold applied LIVE", r.get("mode") == "live", json.dumps(r.get("result"))[:160])
    wallet = r["change"]["id"]
    r = live({"fighter_job_lv": 30}, "level live")
    step("round-2: a live level change says EXP 150 -> 0 (the server resets it)", r.get("mode") == "live" and (r.get("now") or {}).get("fighter_job_exp") == 0,
         (r.get("change") or {}).get("action"))
    st, u = api("/api/undo", {"change": r["change"]["id"]})
    s4 = sheet(cid)
    step("round-2: Undo of the live level puts back level %s AND EXP 150" % b["fighter_job_lv"], st == 200 and int(s4["base"]["fighter_job_lv"]) == int(b["fighter_job_lv"])
         and int(s4["base"]["fighter_job_exp"]) == 150, (s4["base"]["fighter_job_lv"], s4["base"]["fighter_job_exp"]))
    r = live({"fighter_job_lv": 45, "SkillPoint": 33, "zodiac_energy": 100}, "progression live")
    step("round-2: level 45, talent points 33, zodiac energy 100 applied LIVE", r.get("mode") == "live", json.dumps(r.get("result"))[:160])
    st, r = api("/api/m/character/save/%d" % cid, {"values": {"belief": 3}})
    step("round-2: a field without a live endpoint (faith) -> 423", st == 423, r.get("error", "")[:160])
    time.sleep(1)
    st, r = api("/api/undo", {"change": wallet})
    step("round-2: Undo of the live wallet change runs live", st == 200 and (r.get("change") or {}).get("engine") == "reborn-live", (r.get("change") or {}).get("action", r))
st, r = api("/api/m/character/account/%d/rename" % aid, {"username": user + "z"})
step("rename the account while online -> 423", st == 423, r.get("error", "")[:120])
st, r = api("/api/m/character/lifecycle/%d/delete" % cid, {"confirm": role})
step("delete the character while online -> 423", st == 423, r.get("error", "")[:120])
st, r = api("/api/m/character/account/%d/delete" % aid, {"confirm": user, "mode": "delete"})
step("delete the account while online -> 423", st == 423, r.get("error", "")[:120])
p.communicate(timeout=120)
rep = json.load(open(out))
held = [x for x in rep.get("status_history") or [] if x["phase"] == "hold"]
if round2:
    step("client received 10166 with Silver 7,654,321 / Gold 4,321 / B-Gold 99", any(x["silver"] == 7654321 and x["gold"] == 4321 and x["binding_gold"] == 99 for x in held), held[:4])
    step("client received 10166 with level 45", any(x["level"] == 45 for x in held))
    step("client received the Undo (Silver back)", held and held[-1]["silver"] != 7654321, held[-1:])
    # a live teleport in a session of its own: the headless client does not answer the map change and is dropped
    # (a real client loads the new map), so it comes after the checks that need the client's report
    step("logged out before the teleport session", wait_offline(cid, 90))
    p, out = probe(user, pw, role, hold=60, wait=False)
    for _ in range(150):
        st, on = api("/api/m/online/%d" % cid)
        if on.get("online") and any("checkpoint_owner" in x for x in on.get("reasons", [])):
            break
        time.sleep(1)
    time.sleep(4)
    r = live({"Map": home, "Pos_X": 150.0, "Pos_Z": -60.0}, "teleport live")
    step("round-2: map + X / Z applied LIVE (teleport)", r.get("mode") == "live", json.dumps(r.get("result"))[:160])
    try:
        p.communicate(timeout=180)
    except subprocess.TimeoutExpired:
        p.kill()
else:
    step("stock: the client saw no change (nothing was written behind the session)", not held or all(x["silver"] == int(b["Money"]) for x in held), held[:3])
step("logged out", wait_offline(cid))
if round2:
    s3 = sheet(cid)
    step("round-2: after logout the database holds the live level 45 (the server saved it)", int(s3["base"]["fighter_job_lv"]) == 45, s3["base"]["fighter_job_lv"])
    step("round-2: after logout the database holds the teleported position (150, -60)", abs(float(s3["base"]["Pos_X"]) - 150) < 2 and abs(float(s3["base"]["Pos_Z"]) + 60) < 2,
         (s3["base"]["Pos_X"], s3["base"]["Pos_Z"]))

# ------------------------------------------------------------------ 5. account edits
st, r = api("/api/m/character/account/%d/password" % aid, {"password": pw + "!"})
step("new password", st == 200, (r.get("change") or {}).get("action", r.get("error")))
rep, log = probe(user, pw, role)
step("the old password is refused at login", rep is None or not rep.get("enter"), log.strip().splitlines()[-1][:120] if log.strip() else "")
pw = pw + "!"
rep, log = probe(user, pw, role)
step("the new password logs in", rep and (rep.get("enter") or {}).get("name") == role, log[-200:] if not rep else "")
step("logged out", wait_offline(cid))
st, r = api("/api/m/character/account/%d/rename" % aid, {"username": user + "z", "expected": user})
step("rename account %s -> %sz" % (user, user), st == 200, (r.get("change") or {}).get("action", r.get("error")))
rename_acc = r["change"]["id"]
st, r = api("/api/m/character/account/%d/rename" % aid, {"username": "testuser"})
step("rename to a taken name -> 400", st == 400, r.get("error", "")[:100])
st, r = api("/api/m/character/account/%d/rename" % aid, {"username": "Bad Name"})
step("rename to a name the client cannot type -> 400", st == 400, r.get("error", "")[:100])
rep, log = probe(user + "z", pw, role)
step("the renamed account logs in (%sz)" % user, rep and (rep.get("enter") or {}).get("name") == role, log[-200:] if not rep else "")
step("logged out", wait_offline(cid))
undo(rename_acc, "account rename")
st, a1 = api("/api/m/character/account/%d" % aid)
step("account name back to %s" % user, a1.get("username") == user, a1.get("username"))
st, r = api("/api/m/character/account/%d/edit" % aid, {"email": "x" + tag + "@example.test", "donator_tier": 3, "donator_expires_at": "2030-01-01",
                                                       "expected": {"email": a1["email"], "donator_tier": a1["donator_tier"]}})
step("e-mail + donator tier 3 until 2030-01-01", st == 200 and r["account"]["donator_tier"] == 3, r.get("error", ""))
undo(r["change"]["id"], "e-mail + donator tier")
st, r = api("/api/m/character/account/%d/entitlement" % aid, {"op": "grant", "key": "battle_pass", "expires_at": "2031-06-30"})
step("grant battle pass until 2031-06-30", st == 200 and any(e["entitlement_key"] == "battle_pass" for e in r["account"]["entitlements"]), r.get("error", ""))
eid = r["account"]["entitlements"][-1]["id"]
grant = r["change"]["id"]
st, r = api("/api/m/character/account/%d/entitlement" % aid, {"op": "revoke", "id": eid})
step("revoke it", st == 200 and r["account"]["entitlements"][-1]["revoked_at"], r.get("error", ""))
undo(r["change"]["id"], "revoke")
st, a2 = api("/api/m/character/account/%d" % aid)
step("battle pass active again after the Undo", not a2["entitlements"][-1]["revoked_at"])
undo(grant, "grant (removes the row)")
st, a2 = api("/api/m/character/account/%d" % aid)
step("no entitlement left", not a2["entitlements"], a2["entitlements"])
st, r = api("/api/m/character/account/%d/ban" % aid, {"banned": True})
step("ban: status 1%s" % ("" if round2 else " (the page says the stock server ignores it)"), st == 200 and r["account"]["banned"] and (round2 or "ignores" in r.get("note", "")), r.get("note", ""))
undo(r["change"]["id"], "ban")

# ------------------------------------------------------------------ 6. character delete / restore the game's way
st, r = api("/api/m/character/lifecycle/%d/delete" % cid, {"confirm": "wrong"})
step("delete without the name typed -> 400", st == 400)
st, r = api("/api/m/character/lifecycle/%d/delete" % cid, {"confirm": role})
step("delete %s the game's way (restorable 30 days)" % role, st == 200, (r.get("change") or {}).get("output", r.get("error", ""))[:200])
c = next(x for x in r["account"]["characters"] if x["id"] == cid)
step("state deleted, restorable", c["state"] == "deleted" and c["restorable"], c)
deleted = r["change"]["id"]
st, r = api("/api/m/character/lifecycle/%d/restore" % cid, {})
step("restore it", st == 200 and next(x for x in r["account"]["characters"] if x["id"] == cid)["state"] == "active", r.get("error", ""))
undo(r["change"]["id"], "restore (deleted again)")
undo(deleted, "delete (active again)")
rep, log = probe(user, pw, role)
step("the character logs in after delete / restore / Undo", rep and (rep.get("enter") or {}).get("name") == role, log[-200:] if not rep else "")
step("logged out", wait_offline(cid))
st, r = api("/api/m/character/account/%d/delete" % aid, {"confirm": user, "mode": "retire"})
step("retire the account (characters deleted the game's way + ban)", st == 200 and r["account"]["banned"], (r.get("change") or {}).get("action", r.get("error")))
undo(r["change"]["id"], "retire")
st, a3 = api("/api/m/character/account/%d" % aid)
step("after the Undo: not banned, %s active" % role, not a3["banned"] and a3["characters"][0]["state"] == "active", a3["characters"][0]["state"])

# ------------------------------------------------------------------ 7. delete an account for good
st, plan = api("/api/m/character/account/5/delete-plan")
if st == 200 and plan.get("blockers"):
    st, r = api("/api/m/character/account/5/delete", {"confirm": "testuser2", "mode": "delete"})
    step("an account with evidence rows (testuser2: monster rewards) cannot be deleted for good -> 403 (or 423 if online)", st in (403, 423), r.get("error", "")[:160])
st, plan = api("/api/m/character/account/%d/delete-plan" % aid)
step("delete plan for %s: %d rows in %d tables, can delete" % (user, plan.get("total_rows", 0), len(plan.get("tables", []))), plan.get("can_delete"), plan.get("blockers"))
st, r = api("/api/m/character/account/%d/delete" % aid, {"confirm": user, "mode": "delete"})
step("delete %s for good" % user, st == 200, (r.get("change") or {}).get("output", r.get("error", ""))[:200])
gone = r["change"]["id"]
st, r = api("/api/m/character/account/%d" % aid)
step("the account is gone (404)", st == 404)
st, r = api("/api/m/characters?q=" + role)
step("its character is gone", not [x for x in r if x["name"] == role])
undo(gone, "delete for good (every row back)")
st, a4 = api("/api/m/character/account/%d" % aid)
step("account and character back", st == 200 and a4["characters"] and a4["characters"][0]["name"] == role)
rep, log = probe(user, pw, role)
step("the restored account logs in and the server loads %s" % role, rep and (rep.get("enter") or {}).get("name") == role, log[-200:] if not rep else "")
step("logged out", wait_offline(cid))
if not a.keep:
    st, r = api("/api/m/character/account/%d/delete" % aid, {"confirm": user, "mode": "delete"})
    step("cleanup: test account deleted for good", st == 200, r.get("error", ""))
print("ALL OK (%d checks; account %s, character %s, database %s, %s server)" % (N[0], user, role, db, "round2" if round2 else "stock"))
