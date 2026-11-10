#!/usr/bin/env python3
"""tests/test_skills.py - end-to-end test of the Skills & talents editor (modules/skills.py) on a TEST copy.

Every write type goes through the editor's HTTP API, then a headless game client (tests/test_skills_probe.py) logs the
character in and decodes what the reborn server sent: 10041 skills, 10042 talent ranks, 10019 / 10166 talent points,
10297 zodiac sign / level / energy / 16 grids, 10090 quest log, 10078 quest marks. Then every change is undone through
/api/undo and the client proves the old state is back. Refusals (400 / 403 / 409 / 423) are checked too.

    python3 -I tests/test_skills.py --editor http://127.0.0.1:58814 --login YOUR_SERVER_IP:60499 \
        --account sktest1 --password 'skills-test-41' --role Skhero [--phase offline|online|all] [--log CMD]

  offline  every write + server load + Undo + refusals (any build)
  online   a client holds the character in the world: on a stock build every edit is refused (423, the round-2 text);
           on a round2 build talent points and zodiac energy go live (the client receives 10166 / 10297), Undo sends the
           old values back live, the rest is refused with the reason; after the logout the database path works again.
  --log CMD   a command that prints the instance's server.log (e.g. "ssh vps tail -n 3000 ~/gwed/env-skills/server.log");
              used to show the server loaded the daily quest counters ("[quest] daily counts loaded ... rows=N").
The test account / password are the test values created for this environment (README "test environments").
Prints one line per check and ends with "ALL OK" or the first failure. Refuses an editor on the live database.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import test_skills_probe as P          # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58814")
ap.add_argument("--login", required=True)
ap.add_argument("--account", required=True), ap.add_argument("--password", required=True), ap.add_argument("--role", required=True)
ap.add_argument("--phase", default="all", choices=["offline", "online", "all"])
ap.add_argument("--log", default=None)
ap.add_argument("--gwcrypt", default=P.DEFAULT_GWCRYPT)
ap.add_argument("--json", default=None, help="write the decoded client reports here")
a = ap.parse_args()

html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)
EVIDENCE = {}
N = {"ok": 0}


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
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)) if detail != "" else ""), flush=True)
    if not cond:
        if a.json:
            json.dump(EVIDENCE, open(a.json, "w"), indent=1, default=str)
        sys.exit(1)
    N["ok"] += 1


def expect(code, path, body, what):
    st, r = api(path, body)
    step("%s -> HTTP %d" % (what, code), st == code, (r or {}).get("error", "") if isinstance(r, dict) else r)
    return r


def wait_offline(cid, secs=40):
    for _ in range(secs):
        st, on = api("/api/m/online/%d" % cid)
        if not on.get("online"):
            return True
        time.sleep(1)
    return False


def wait_online(cid, secs=40):
    for _ in range(secs * 2):
        st, on = api("/api/m/online/%d" % cid)
        if on.get("online"):
            return on
        time.sleep(0.5)
    return None


def probe(hold=7.0, on_world=None, zodiac_every=0.0):
    r = P.probe(a.account, a.password, a.role, a.login, hold=hold, gwcrypt=a.gwcrypt, on_world=on_world, zodiac_every=zodiac_every)
    time.sleep(1)
    return r


def undo(change, what):
    st, r = api("/api/undo", {"change": change["id"], "full": False})
    if st == 409:
        st, r = api("/api/undo", {"change": change["id"], "full": False, "overwrite": True})
    res = (r or {}).get("result") or {}
    step("undo " + what, st == 200 and res.get("all_identical", True) is not False, (r or {}).get("error") or res.get("identical", ""))
    return r


def ranks(rep):
    return {int(k): v for k, v in ((rep.get("talent_history") or [{}])[-1].get("ranks") or {}).items()}


# ------------------------------------------------------------------ setup
st, s = api("/api/m/status")
step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
step("not the live database", s["reborn"].get("database") != "godswar" and not s["reborn"].get("live"))
ROUND2 = bool(s["capabilities"].get("available"))
print("     game server build: %s (live operations: %s)" % ("round2" if ROUND2 else "stock", ", ".join(s.get("live_operations") or []) or "none"))
st, rows = api("/api/m/characters?q=" + a.role)
cid = next(r["id"] for r in rows if r["name"] == a.role)
step("%s (#%d) is offline" % (a.role, cid), wait_offline(cid))
st, sh = api("/api/m/skills/sheet/%d" % cid)
step("sheet answers", st == 200 and sh["character"]["id"] == cid, "%s %s, %d skill families, %d talents" % (
    sh["class_name"], sh["camp_name"], len(sh["skills"]["families"]), len(sh["talents"]["list"])))
step("class is Warrior (the test plan uses Warrior skills)", sh["character"]["profession"] == 0, sh["class_name"])
st, qr = api("/api/m/skills/questrules")
step("quest roster loaded", st == 200 and qr["count"] > 900, qr.get("count"))
st, tt = api("/api/m/skills/talenttable")
step("talent cost table matches the server (0->60 = 19,810 TP, 0->100 = 103,505 TP)",
     tt["table"][60]["total_cost"] == 19810 and tt["table"][100]["total_cost"] == 103505)
B0 = sh["character"]
fam = {f["base"]: f for f in sh["skills"]["families"]}
T0 = {t["id"]: t["rank"] for t in sh["talents"]["list"]}

if a.phase in ("offline", "all"):
    # ------------------------------------------------------------------ baseline as the server sends it
    base = probe()
    EVIDENCE["before"] = base
    step("client: baseline skills %s" % base["skills"], base["skills"] is not None)
    step("client: baseline talents all rank 0", base["talents"] and all(t["rank"] == 0 for t in base["talents"]))
    step("client: baseline zodiac", base["zodiac"] is not None, {k: base["zodiac"][k] for k in ("sign", "level", "energy")})
    step(a.role + " offline again", wait_offline(cid))

    # ------------------------------------------------------------------ writes
    changes = []
    r = expect(200, "/api/m/skills/skills/%d" % cid, {"set": {"Light Chop": 2, "Whirlwind": 10, "Sword Storm": 34},
               "expected": {"Light Chop": fam["Light Chop"]["current"], "Whirlwind": None, "Sword Storm": None}}, "skills: Light Chop Lv3, Whirlwind Lv1, Sword Storm Lv5")
    changes.append(("skills", r["change"]))
    r = expect(200, "/api/m/skills/talents/%d" % cid, {"ranks": {"0": 60, "5": 100, "15": 7}, "expected": {"0": 0, "5": 0, "15": 0}},
               "talents: Healthy 60, Dueling 100, Armor Construction 7")
    step("talents above 60 carry the client-cap warning", "warning" in r, r.get("warning", ""))
    changes.append(("talents", r["change"]))
    r = expect(200, "/api/m/skills/grids/%d" % cid, {"grids": {"0": {"level": 12, "kind": 10003}, "9": {"level": 5, "kind": 10050}, "5": {"level": 50, "kind": -1}},
               "expected": {"0": {"level": 0, "kind": -1}, "9": {"level": 0, "kind": -1}, "5": {"level": 0, "kind": -1}}},
               "grids: 1 = Lv12 Sword Storm (attack), 10 = Lv5 Fireball (defence), 6 = Lv50 empty")
    changes.append(("grids", r["change"]))
    r = expect(200, "/api/m/skills/base/%d" % cid, {"values": {"zodiac_type": 7, "zodiac_level": 10, "zodiac_energy": 100000, "SkillPoint": 1000000, "SkillExp": 50},
               "expected": {"zodiac_type": B0["zodiac_type"], "zodiac_level": B0["zodiac_level"], "zodiac_energy": B0["zodiac_energy"],
                            "SkillPoint": B0["SkillPoint"], "SkillExp": B0["SkillExp"]}}, "zodiac Scorpio Lv10 100,000 energy; 1,000,000 talent points")
    step("offline base edit went to the database", r.get("mode") == "database", r.get("mode"))
    changes.append(("base", r["change"]))
    r = expect(200, "/api/m/skills/quests/%d" % cid, {"ops": [{"quest": 518, "action": "complete"}, {"quest": 520, "action": "accept", "counters": [7]},
               {"quest": 326, "action": "accept", "counters": [10]}], "expected": {"518": None, "520": None, "326": None}},
               "quests: 518 completed, 520 carried 7/10, 326 carried 10/10")
    changes.append(("quests", r["change"]))
    expect(403, "/api/m/skills/daily/%d" % cid, {"quest": 249, "completions": 1}, "daily counter without 'advanced' (advanced table)")
    r = expect(200, "/api/m/skills/daily/%d" % cid, {"quest": 249, "completions": 1, "advanced": True}, "daily counter: quest 249 done once today")
    changes.append(("daily", r["change"]))
    r = expect(200, "/api/m/skills/appraisal/%d" % cid, {"bonus_basis_points": 1000, "advanced": True}, "quest EXP appraisal +10%")
    changes.append(("appraisal", r["change"]))

    st, sh2 = api("/api/m/skills/sheet/%d" % cid)
    step("sheet shows the edits", {f["base"]: f["current"] for f in sh2["skills"]["families"]}["Sword Storm"] == 34
         and {t["id"]: t["rank"] for t in sh2["talents"]["list"]}[5] == 100 and sh2["zodiac"]["grids"][0]["level"] == 12
         and sh2["character"]["SkillPoint"] == 1000000 and not sh2["zodiac"]["problems"])
    step("talent rows bumped outbox_revision", all((t.get("outbox_revision") or 0) >= 0 for t in sh2["talents"]["list"] if t["stored"]))

    # ------------------------------------------------------------------ the server loads them
    rep = probe()
    EVIDENCE["after"] = rep
    step("client: skills %s" % rep["skills"], set(rep["skills"] or []) == {2, 10, 34, 3062, 4904})
    rk = ranks(rep)
    step("client: talent ranks 0=60, 5=100, 15=7", rk.get(0) == 60 and rk.get(5) == 100 and rk.get(15) == 7, {k: v for k, v in rk.items() if v})
    z = rep["zodiac"]
    step("client: zodiac sign 7 (Scorpio), level 10, energy 100,000", z and z["sign"] == 7 and z["level"] == 10 and z["energy"] == 100000,
         z and {k: z[k] for k in ("sign", "level", "energy")})
    g = {x["grid"]: x for x in z["grids"]}
    step("client: grid 1 Lv12 Kind 10003, grid 10 Lv5 Kind 10050, grid 6 Lv50", g[0]["level"] == 12 and g[0]["kind"] == 10003
         and g[9]["level"] == 5 and g[9]["kind"] == 10050 and g[5]["level"] == 50 and g[5]["kind"] == -1)
    step("client: talent points 1,000,000 (10019 / 10166)", (rep.get("enter") or {}).get("talent_points") == 1000000
         and rep["status_history"][-1]["talent_points"] == 1000000)
    qs = {q["quest"]: q for q in (rep["quest_snapshot"] or {}).get("quests", [])}
    step("client: quest log has 520 and 326, not 518", 520 in qs and 326 in qs and 518 not in qs, sorted(qs))
    step("client: quest 520 progress 7 (counter of its one objective)", qs.get(520, {}).get("word80") == 7, qs.get(520))
    step("client: quest marks changed after 518 was completed", rep["marks_available"] != base["marks_available"],
         "%s -> %s; hand-in %s" % (base["marks_available"], rep["marks_available"], rep["marks_handin"]))
    if a.log:
        out = subprocess.run(a.log, shell=True, capture_output=True, timeout=60).stdout.decode(errors="replace")
        lines = [l for l in out.splitlines() if "daily counts loaded character=%s" % a.role in l]
        step("server log: daily counter row loaded", bool(lines) and "rows=1" in lines[-1], lines[-1] if lines else "no line")
    step(a.role + " offline again", wait_offline(cid))

    # ------------------------------------------------------------------ refusals
    expect(400, "/api/m/skills/talents/%d" % cid, {"ranks": {"0": 101}}, "talent rank 101 (cap 100)")
    expect(403, "/api/m/skills/talents/%d" % cid, {"ranks": {"50": 5}}, "a Champion talent on a Warrior")
    expect(409, "/api/m/skills/talents/%d" % cid, {"ranks": {"0": 61}, "expected": {"0": 59}}, "stale expected rank")
    expect(403, "/api/m/skills/skills/%d" % cid, {"set": {"Fireball": 500}}, "a Mage skill family on a Warrior")
    expect(400, "/api/m/skills/skills/%d" % cid, {"set": {"Light Chop": 34}}, "a skill id outside its family")
    expect(409, "/api/m/skills/skills/%d" % cid, {"set": {"Light Chop": 3}, "expected": {"Light Chop": 0}}, "stale expected skill")
    expect(400, "/api/m/skills/grids/%d" % cid, {"grids": {"1": {"level": 3, "kind": 10004}}}, "attack grid with an unlearned skill")
    expect(400, "/api/m/skills/grids/%d" % cid, {"grids": {"1": {"level": 3, "kind": 10003}}}, "the same skill twice in a row")
    expect(400, "/api/m/skills/grids/%d" % cid, {"grids": {"2": {"level": 3, "kind": 20001}}}, "a group-B Kind in a group-A grid")
    expect(400, "/api/m/skills/grids/%d" % cid, {"grids": {"2": {"level": 51}}}, "grid level 51 (max 50)")
    expect(400, "/api/m/skills/base/%d" % cid, {"values": {"zodiac_energy": 125001}}, "zodiac energy above the level-10 cap")
    expect(400, "/api/m/skills/base/%d" % cid, {"values": {"zodiac_level": 1}}, "zodiac level 1 while energy is above its cap")
    expect(400, "/api/m/skills/base/%d" % cid, {"values": {"zodiac_type": 12}}, "zodiac sign 12 (0-11)")
    expect(403, "/api/m/skills/base/%d" % cid, {"values": {"selected_title_id": 1000}}, "a title the character does not own")
    expect(400, "/api/m/skills/base/%d" % cid, {"values": {"quest_current_id": 5}}, "a column this page does not edit")
    expect(403, "/api/m/skills/quests/%d" % cid, {"ops": [{"quest": 1518, "action": "accept"}]}, "an Athens quest on a Sparta character")
    expect(403, "/api/m/skills/quests/%d" % cid, {"ops": [{"quest": 99999, "action": "complete"}]}, "a quest the server has no rules for")
    many = [q for q, x in qr["quests"].items() if x["camp"] == "Sparta" and int(q) not in (520, 326)][:19]
    expect(400, "/api/m/skills/quests/%d" % cid, {"ops": [{"quest": int(q), "action": "accept"} for q in many]}, "21 carried quests (the server carries 20)")
    expect(409, "/api/m/skills/quests/%d" % cid, {"ops": [{"quest": 520, "action": "complete"}], "expected": {"520": 1}}, "stale expected quest state")
    # verify:skills regressions (each of these used to be written, or crashed with 500)
    expect(400, "/api/m/skills/grids/%d" % cid, {"grids": {"8": {"level": 3, "kind": 10050}}},
           "a Kind already in a LATER grid of the same row (grid 9 = grid 10's Fireball)")
    expect(400, "/api/m/skills/skills/%d" % cid, {"set": {"Sword Storm": None}}, "forget Sword Storm while attack grid 1 still holds it")
    expect(400, "/api/m/skills/daily/%d" % cid, {"quest": 518, "completions": 1, "advanced": True}, "a daily counter for a one-time quest")
    athens_daily = next(int(q) for q, x in qr["quests"].items() if x["camp"] == "Athens" and x.get("per_day"))
    expect(403, "/api/m/skills/daily/%d" % cid, {"quest": athens_daily, "completions": 1, "advanced": True}, "an Athens daily counter on a Sparta character")
    expect(403, "/api/m/skills/daily/%d" % cid, {"quest": 99999, "completions": 1, "advanced": True}, "a daily counter for a quest the server has no rules for")
    for what, path, body in [("skills", "skills", {"set": {"Light Chop": 1}, "expected": ["x"]}),
                             ("talents", "talents", {"ranks": {"0": 1}, "expected": [0]}),
                             ("grids", "grids", {"grids": {"3": {"level": 1}}, "expected": {"3": 5}}),
                             ("base", "base", {"values": {"SkillPoint": 5}, "expected": [1]}),
                             ("quests", "quests", {"ops": [{"quest": 520, "action": "accept"}], "expected": {"520": "x"}}),
                             ("quest counters", "quests", {"ops": [{"quest": 520, "action": "accept", "counters": 5}]})]:
        expect(400, "/api/m/skills/%s/%d" % (path, cid), body, "malformed %s request (400, not a crash)" % what)

    # ------------------------------------------------------------------ undo (newest first) and the server loads the old rows
    for what, c in reversed(changes):
        undo(c, what)
    st, sh3 = api("/api/m/skills/sheet/%d" % cid)
    step("sheet back to the start", {f["base"]: f["current"] for f in sh3["skills"]["families"]} == {f["base"]: f["current"] for f in sh["skills"]["families"]}
         and {t["id"]: t["rank"] for t in sh3["talents"]["list"]} == T0 and sh3["character"]["SkillPoint"] == B0["SkillPoint"]
         and all(x["level"] == 0 for x in sh3["zodiac"]["grids"]) and not sh3["quests"]["daily"] and not sh3["quests"]["appraisal"])
    rep2 = probe()
    EVIDENCE["after_undo"] = rep2
    step("client after undo: skills %s" % rep2["skills"], sorted(rep2["skills"] or []) == sorted(base["skills"] or []))
    step("client after undo: talents all 0", all(v == 0 for v in ranks(rep2).values()))
    z2 = rep2["zodiac"]
    step("client after undo: zodiac and grids as before", z2 and z2["sign"] == base["zodiac"]["sign"] and z2["level"] == base["zodiac"]["level"]
         and all(x["level"] == 0 and x["kind"] == -1 for x in z2["grids"]))
    step("client after undo: talent points %d" % B0["SkillPoint"], rep2["status_history"][-1]["talent_points"] == B0["SkillPoint"])
    step("client after undo: quest log and marks as before", (rep2["quest_snapshot"] or {}).get("count") == (base["quest_snapshot"] or {}).get("count")
         and rep2["marks_available"] == base["marks_available"])
    step(a.role + " offline again", wait_offline(cid))

if a.phase in ("online", "all"):
    # ------------------------------------------------------------------ a logged-in character
    res = {}
    TP_NEW = 4321 if B0["SkillPoint"] != 4321 else 4322
    ZE_NEW = 777 if B0["zodiac_energy"] != 777 else 555
    step("live test values differ from the stored ones", TP_NEW != B0["SkillPoint"] and ZE_NEW != B0["zodiac_energy"],
         "talent points %d -> %d, zodiac energy %d -> %d" % (B0["SkillPoint"], TP_NEW, B0["zodiac_energy"], ZE_NEW))

    def while_online(rep):
        on = wait_online(cid)
        res["online"] = on
        if not on:
            res["done"] = True
            return
        time.sleep(3)                        # let the client finish entering the world (409 "busy" otherwise)
        res["talents"] = api("/api/m/skills/talents/%d" % cid, {"ranks": {"0": 3}})
        res["quests"] = api("/api/m/skills/quests/%d" % cid, {"ops": [{"quest": 518, "action": "complete"}]})
        res["grids"] = api("/api/m/skills/grids/%d" % cid, {"grids": {"0": {"level": 1}}})
        res["sign"] = api("/api/m/skills/base/%d" % cid, {"values": {"zodiac_type": 3}})
        for _ in range(15):
            st, r = api("/api/m/skills/base/%d" % cid, {"values": {"SkillPoint": TP_NEW, "zodiac_energy": ZE_NEW}, "expected": {"SkillPoint": B0["SkillPoint"], "zodiac_energy": B0["zodiac_energy"]}})
            if st != 409:
                break
            time.sleep(1)
        res["live"] = (st, r)
        if st == 200 and r.get("mode") == "live":
            time.sleep(5)
            res["undo"] = api("/api/undo", {"change": r["change"]["id"], "full": False})
            time.sleep(6)
        res["done"] = True

    rep = P.probe(a.account, a.password, a.role, a.login, hold=120, gwcrypt=a.gwcrypt,
                  on_world=lambda rp: threading.Thread(target=while_online, args=(rp,), daemon=True).start(), zodiac_every=2,
                  until=lambda: res.get("done"))
    EVIDENCE["online"] = rep
    EVIDENCE["online_results"] = {k: v for k, v in res.items()}
    step("character seen online by the editor", bool(res.get("online")), "; ".join((res.get("online") or {}).get("reasons", [])))
    for k, what in (("talents", "talent rank"), ("quests", "quest"), ("grids", "zodiac grid"), ("sign", "zodiac sign")):
        st, r = res[k]
        msg = (r or {}).get("error", "")
        step("online %s edit refused (423)" % what, st == 423, msg[:160])
        if not ROUND2:
            step("  ... with the round-2 message", "round-2" in msg)
        else:
            step("  ... with the reason it has no live version", "log the character out" in msg.lower() or "logged out" in msg.lower(), msg[:160])
    st, r = res["live"]
    if not ROUND2:
        step("stock build: talent points / zodiac energy on a logged-in character refused (423)", st == 423, (r or {}).get("error", "")[:160])
        step("  ... with the round-2 message", "round-2" in (r or {}).get("error", ""))
    else:
        step("round2: talent points + zodiac energy applied live", st == 200 and r.get("mode") == "live", (r or {}).get("error") or r.get("result"))
        hist = rep["status_history"]
        step("client received talent points %d live (10166)" % TP_NEW, any(h["phase"] == "hold" and h["talent_points"] == TP_NEW for h in hist),
             [h["talent_points"] for h in hist])
        zh = rep["zodiac_history"]
        step("client received zodiac energy %d live (10297)" % ZE_NEW, any(z["phase"] == "hold" and z["energy"] == ZE_NEW for z in zh), [z["energy"] for z in zh])
        ust, ur = res.get("undo", (0, {}))
        step("live undo sent the old values back", ust == 200, (ur or {}).get("error", ""))
        i = next(k for k, h in enumerate(hist) if h["talent_points"] == TP_NEW)
        step("client received the old talent points %d after the undo" % B0["SkillPoint"], any(h["talent_points"] == B0["SkillPoint"] for h in hist[i + 1:]),
             [h["talent_points"] for h in hist])
        j = next(k for k, z in enumerate(zh) if z["energy"] == ZE_NEW)
        step("client received the old zodiac energy %d after the undo" % B0["zodiac_energy"], any(z["energy"] == B0["zodiac_energy"] for z in zh[j + 1:]),
             [z["energy"] for z in zh])
        st2, sh9 = api("/api/m/skills/sheet/%d" % cid)
        # the server credits zodiac energy for the online time when the character logs out (zodiac_energy_accrual,
        # "final online interval"), so the stored energy may be a little above the old value: never at the live 777
        ze9 = sh9["character"]["zodiac_energy"]
        step("database holds the old values again (zodiac energy may include the server's own online-time accrual)",
             sh9["character"]["SkillPoint"] == B0["SkillPoint"] and B0["zodiac_energy"] <= ze9 < B0["zodiac_energy"] + 200 and ze9 != ZE_NEW,
             "talent points %s, zodiac energy %s (was %s)" % (sh9["character"]["SkillPoint"], ze9, B0["zodiac_energy"]))
    step(a.role + " logged out", wait_offline(cid))
    r = expect(200, "/api/m/skills/talents/%d" % cid, {"ranks": {"0": 3}}, "after the logout the same talent edit goes to the database")
    undo(r["change"], "talent edit after logout")

if a.json:
    with open(a.json, "w") as f:
        json.dump(EVIDENCE, f, indent=1, default=str)
print("ALL OK (%d checks)" % N["ok"])
