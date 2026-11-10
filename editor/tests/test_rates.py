#!/usr/bin/env python3
"""tests/test_rates.py - the Server rates page v2 (modules/rates.py) against YOUR OWN test copy and instance.

    python3 -I tests/test_rates.py --editor http://127.0.0.1:58817 --login YOUR_SERVER_IP:60799 --build round2 \
        [--account ratest1 --password 'Rates 2026' --role Ratesone] [--no-client]

Part A (API): the state (bounds come from the table: round2 x1,000,000, stock x50), presets resolved for this server,
cap notes, refusals (403 bounds, 400 range / whole number / unknown key / nothing to change, 409 stale value and stale
Undo), a save of rates + a stock setting in ONE transaction that the running server reloads at once (GET /rates shows
the new values), Undo through the page and through the Change log (/api/undo), the Max preset (every key at once)
and its Undo.

Part B (headless game client, the proof that the RUNNING server applies a change to an ONLINE player): a client in
the world kills monsters with Polish/tools/rates_probe.py while the test changes the rates through the editor:
kill 1 at x1, then EXP x5 + Talent x3 -> kill 2 pays 5x / 3x, Undo -> kill 3 pays x1 again, then EXP at this server's
maximum -> kill 4 pays min(100,000,000, base x max). Nobody logs out; no restart.

Refuses an editor whose database is the live 'godswar'. Prints each step; ends with "ALL OK" or the first failure.
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

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58817")
ap.add_argument("--login", default="YOUR_SERVER_IP:60799", help="HOST:PORT of the test instance's login server")
ap.add_argument("--build", choices=["round2", "stock"], required=True, help="which server the instance runs")
ap.add_argument("--account", default="ratest1"), ap.add_argument("--password", default="Rates 2026")
ap.add_argument("--role", default="Ratesone")
ap.add_argument("--probe", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools", "rates_probe.py"))
ap.add_argument("--no-client", action="store_true", help="only part A")
ap.add_argument("--client-only", action="store_true", help="only part B")
ap.add_argument("--json", default=None, help="write the client evidence here")
a = ap.parse_args()

html = urllib.request.urlopen(a.editor + "/", timeout=30).read().decode()
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


N = {"ok": 0}


def step(msg, cond, detail=""):
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)) if detail != "" else ""), flush=True)
    if not cond:
        sys.exit(1)
    N["ok"] += 1


def rates_of(st):
    return {r["rate_key"]: r for r in st["rows"]}


def server_uses(st, key):
    return ((st.get("server") or {}).get("rates") or {}).get(key)


# ===================================================================== part A: API
st, s = api("/api/m/status")
DB = s["reborn"].get("database")
step("not the live database", DB != "godswar" and not s["reborn"].get("live"))


def part_a():
    step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
    DB = s["reborn"].get("database")
    step("not the live database", DB != "godswar" and not s["reborn"].get("live"))
    step("writable copy", s["reborn"].get("writable"))

    code, S = api("/api/m/rates/state")
    step("GET state", code == 200 and S["table"], "%d settings, revision %s, server %s" % (len(S["rows"]), S["revision"], (S["server"] or {}).get("revision")))
    R = rates_of(S)
    want_keys = 41 if a.build == "round2" else 22
    unused = set(S["build"].get("unused") or [])
    step("%s server: %d settings it reads (%d rows in the table)" % (a.build, want_keys, len(R)), len(R) - len(unused) == want_keys
         and S["build"]["keys"] == want_keys and all(R[k]["info"]["unused"] for k in unused), "%d rows, unused %s" % (len(R), sorted(unused)))
    if unused:
        step("rows the running server ignores are flagged and kept out of the presets",
             any("not read by the running game server" in c["msg"] for c in S["checks"])
             and all(not (set(p["resolved"]) & unused) for p in S["presets"]))
        R = {k: v for k, v in R.items() if k not in unused}
    want_max = 1_000_000 if a.build == "round2" else 50
    step("bounds come from the table: exp.monster max x%s" % format(want_max, ","), R["exp.monster"]["max_value"] == want_max, R["exp.monster"]["max_value"])
    step("build detected", S["build"]["round2"] == (a.build == "round2") and S["build"]["live_edits"] == (a.build == "round2"), S["build"]["note"])
    step("game server reachable and on the database revision", S["server"].get("reachable") and S["server"]["revision"] == S["revision"])
    step("every row has its icon, applies text and cap notes", all(r["info"]["sprite"] is not None and r["info"]["applies"] for r in S["rows"]),
         sum(len(r["info"]["caps"]) for r in S["rows"]))
    step("5 presets, each resolved for every key", [p["id"] for p in S["presets"]] == ["stock", "double", "x10", "x100", "max"]
         and all(set(p["resolved"]) == set(R) for p in S["presets"]))
    P = {p["id"]: p["resolved"] for p in S["presets"]}
    step("preset Max: rewards at the max, costs free", P["max"]["exp.monster"]["value"] == want_max
         and all(P["max"][k]["value"] == 0 for k in R if k.startswith("cost.")) and P["max"]["holybox.min_level"]["value"] == 1)
    step("preset x100 is clamped on a stock server only", P["x100"]["exp.monster"]["clamped"] == (a.build == "stock"),
         "exp.monster -> %s (wanted %s)" % (P["x100"]["exp.monster"]["value"], P["x100"]["exp.monster"]["wanted"]))
    step("stock settings readable (9)", len([x for x in S["stock"] if x["available"]]) == 9)
    step("loot rules with English names", all(x.get("item_name") and x.get("monster_name") for x in S["loot"]["rules"]),
         "; ".join("%s from %s %s%%" % (x["item_name"], x["monster_name"], x["chance_bp"] / 100) for x in S["loot"]["rules"][:3]))
    code, H = api("/api/m/rates/history?limit=5")
    step("GET history", code == 200 and isinstance(H["rows"], list), "%d rows" % len(H["rows"]))
    code, H = api("/api/m/rates/history?key=exp.monster&limit=5")
    step("GET history of one key", code == 200 and all(x["rate_key"] == "exp.monster" for x in H["rows"]))
    code, r = api("/api/m/rates/history?key=x;drop")
    step("history: bad key refused (400)", code == 400, r.get("error"))
    code, r = api("/api/m/rates/reload", {})
    step("POST reload", code == 200 and r["reload"].get("reachable"), "revision %s" % r["reload"].get("revision"))

    # ---- refusals
    cur = {k: R[k]["value"] for k in R}
    code, r = api("/api/m/rates/bounds", {"key": "exp.monster"})
    step("raising a bound is refused with the reason (403)", code == 403 and "binary" in r["error"], r["error"][:90])
    code, r = api("/api/m/rates/save", {"values": {"exp.monster": want_max + 1}, "expected": {"exp.monster": cur["exp.monster"]}})
    step("above this server's maximum refused (400)", code == 400 and "on this server" in r["error"], r["error"])
    if a.build == "stock":
        code, r = api("/api/m/rates/save", {"values": {"exp.monster": 1_000_000}, "expected": {"exp.monster": cur["exp.monster"]}})
        step("stock server: x1,000,000 refused until the round-2 update", code == 400, r["error"])
    code, r = api("/api/m/rates/save", {"values": {"holybox.min_level": 70.5}})
    step("absolute limit must be whole (400)", code == 400 and "whole" in r["error"], r["error"])
    code, r = api("/api/m/rates/save", {"values": {"nope.key": 2}})
    step("unknown key refused (400)", code == 400, r["error"])
    code, r = api("/api/m/rates/save", {"values": {"exp.monster": "lots"}})
    step("not a number refused (400)", code == 400, r["error"])
    code, r = api("/api/m/rates/save", {"values": {"exp.monster": cur["exp.monster"]}})
    step("nothing to change refused (400)", code == 400, r["error"])
    code, r = api("/api/m/rates/save", {"values": {"exp.monster": 2}, "expected": {"exp.monster": cur["exp.monster"] + 7}})
    step("stale value refused (409), nothing written", code == 409 and r.get("conflict"), r.get("conflict"))
    code, S2 = api("/api/m/rates/state")
    step("after the refusals nothing changed", S2["revision"] == S["revision"] and rates_of(S2)["exp.monster"]["value"] == cur["exp.monster"])

    # ---- save: two rates + a stock setting, one transaction, the server reloads
    stk = {x["id"]: x for x in S["stock"]}
    med = stk["medusa_daily"]["value"]
    code, r = api("/api/m/rates/save", {"values": {"exp.monster": 2, "holybox.daily_limit": 3_000_000_000},
                                        "expected": {"exp.monster": cur["exp.monster"], "holybox.daily_limit": cur["holybox.daily_limit"]},
                                        "stock": {"medusa_daily": med + 2}, "stock_expected": {"medusa_daily": med}, "note": "test_rates save"})
    step("save 2 rates + 1 stock setting", code == 200, r.get("error") or r["change"]["action"])
    C1 = r["change"]
    step("the running server reloaded and uses the new values", r["mode"] == "hot" and r["server_applied"] == {"exp.monster": True, "holybox.daily_limit": True},
         "revision %s" % r["server_revision"])
    step("change record: engine rates, module rates, applies within 5 s", C1["engine"] == "rates" and C1["module"] == "rates" and C1["applies"] == "hot_reload")
    S3 = r["state"]
    R3 = rates_of(S3)
    step("database holds the new values", R3["exp.monster"]["value"] == 2 and R3["holybox.daily_limit"]["value"] == 3_000_000_000
         and {x["id"]: x for x in S3["stock"]}["medusa_daily"]["value"] == med + 2)
    step("server_rates_log has the rows with the note", any(l["rate_key"] == "exp.monster" and l["note"] == "test_rates save" for l in S3["log"]))
    step("the page's history lists the save", S3["module_changes"][0]["id"] == C1["id"])
    # ---- undo through the page
    code, u = api("/api/m/rates/undo", {"change": C1["id"]})
    step("Undo (page)", code == 200 and u["undo_of"] == C1["id"], u.get("error") or u["change"]["action"])
    R4 = rates_of(u["state"])
    step("Undo put every value back, the server uses them", R4["exp.monster"]["value"] == cur["exp.monster"] and R4["holybox.daily_limit"]["value"] == cur["holybox.daily_limit"]
         and {x["id"]: x for x in u["state"]["stock"]}["medusa_daily"]["value"] == med and server_uses(u["state"], "exp.monster") == cur["exp.monster"])
    code, r = api("/api/m/rates/undo", {"change": C1["id"]})
    step("a second Undo is refused (400)", code == 400, r["error"])
    code, r = api("/api/m/rates/undo", {"change": u["change"]["id"]})
    step("undoing an Undo is refused (400)", code == 400, r["error"])
    # ---- stale undo (409) and overwrite
    code, ra = api("/api/m/rates/save", {"values": {"exp.monster": 3}, "expected": {"exp.monster": cur["exp.monster"]}})
    code, rb = api("/api/m/rates/save", {"values": {"exp.monster": 4}, "expected": {"exp.monster": 3}})
    code, r = api("/api/m/rates/undo", {"change": ra["change"]["id"]})
    step("Undo of an older save after a newer one is refused (409)", code == 409 and r.get("conflict"), r.get("conflict"))
    code, r = api("/api/m/rates/undo", {"change": rb["change"]["id"]})
    step("Undo newest first", code == 200 and rates_of(r["state"])["exp.monster"]["value"] == 3)
    code, r = api("/api/m/rates/undo", {"change": ra["change"]["id"]})
    step("then the older one", code == 200 and rates_of(r["state"])["exp.monster"]["value"] == cur["exp.monster"])
    # ---- the Change log's Undo (/api/undo, server.py) works on a module change too
    code, rc = api("/api/m/rates/save", {"values": {"drop.chance": 2}, "expected": {"drop.chance": cur["drop.chance"]}})
    code, r = api("/api/undo", {"change": rc["change"]["id"]})
    step("Change log Undo (/api/undo) of a page save", code == 200, r.get("error") or r["change"]["action"])
    code, S5 = api("/api/m/rates/state")
    step("drop.chance back", rates_of(S5)["drop.chance"]["value"] == cur["drop.chance"])
    # ---- the Max preset: every key in one save, the server applies all of them, Undo restores all
    R5 = rates_of(S5)
    vals = {k: v["value"] for k, v in P["max"].items() if v["value"] != R5[k]["value"]}
    code, rm = api("/api/m/rates/save", {"values": vals, "expected": {k: R5[k]["value"] for k in vals}, "note": "test_rates preset Max"})
    step("preset Max saved (%d keys, one transaction)" % len(vals), code == 200, rm.get("error") or rm["output"][:120])
    step("the running server applies every Max value", rm["server_applied"] and all(rm["server_applied"].values()) and len(rm["server_applied"]) == len(vals),
         "exp.monster x%s" % format(server_uses(rm["state"], "exp.monster"), ","))
    code, u = api("/api/m/rates/undo", {"change": rm["change"]["id"]})
    R6 = rates_of(u["state"])
    step("Undo of Max restores every key and the server follows", code == 200 and all(R6[k]["value"] == R5[k]["value"] for k in R)
         and all(server_uses(u["state"], k) == R5[k]["value"] for k in R))


if not a.client_only:
    part_a()
if a.no_client:
    print("ALL OK (%d checks, part A only)" % N["ok"])
    sys.exit(0)

# ===================================================================== part B: a headless client in the world
code, acc = api("/api/m/accounts/list?q=" + a.account)
create = False
if not any(x["username"] == a.account for x in acc.get("accounts", [])):
    code, r = api("/api/m/accounts/create", {"username": a.account, "password": a.password})
    step("test account %s created (Accounts page)" % a.account, code == 200, r.get("error", ""))
    create = True
else:
    mine = next(x for x in acc["accounts"] if x["username"] == a.account)
    create = not any(c["name"] == a.role for c in mine["characters"])
code, r = api("/api/m/rates/state")
base = rates_of(r)
for k in ("exp.monster", "tp.monster"):
    step("%s starts at stock" % k, base[k]["value"] == base[k]["default_value"])
top = base["exp.monster"]["max_value"]

cmd = [sys.executable, "-I", os.path.join(HERE, "tests", "test_rates_client.py"), a.probe, a.account, a.password, a.role, a.login, "--walk", "230,-60", "--kill", "1", "--sleep", "12", "--walk", "230,-60", "--kill", "1",
       "--sleep", "12", "--walk", "230,-60", "--kill", "1", "--sleep", "12", "--walk", "225,-40", "--kill", "1", "--sleep", "3"] + (["--create"] if create else [])
print("     client: " + " ".join(cmd[4:]), flush=True)
def run_client(cmd):
    global p, events, actions, err, kills
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)
    events, actions, err = [], {}, []
    threading.Thread(target=lambda: err.extend(p.stderr.readlines()), daemon=True).start()
    kills = 0
    deadline = time.time() + 900
    for line in p.stdout:
        if time.time() > deadline:
            p.kill()
            break
        if not line.startswith("{"):
            continue
        e = json.loads(line)
        events.append(e)
        if e["kind"] == "entered":
            print("     client in the world: %s" % e.get("name"), flush=True)
        if e["kind"] != "killed":
            continue
        kills += 1
        if kills == 1:            # while the client stays online: EXP x5, Talent x3
            code, r = api("/api/m/rates/save", {"values": {"exp.monster": 5, "tp.monster": 3}, "expected": {"exp.monster": 1, "tp.monster": 1},
                                                "note": "test_rates live x5"})
            actions["x5"] = (code, r.get("change", {}).get("id"), r.get("server_applied"), time.time())
        elif kills == 2:          # Undo while online
            code, r = api("/api/m/rates/undo", {"change": actions["x5"][1]})
            actions["undo"] = (code, r.get("change", {}).get("id"), r.get("server_applied"), time.time())
        elif kills == 3:          # the highest EXP this server allows
            code, r = api("/api/m/rates/save", {"values": {"exp.monster": top}, "expected": {"exp.monster": 1}, "note": "test_rates live max"})
            actions["max"] = (code, r.get("change", {}).get("id"), r.get("server_applied"), time.time())
    p.wait(timeout=120)
    return kills

for attempt in range(3):
    run_client(cmd)
    if kills or actions:
        break
    print("     no monster reached (the busy VPS can time out the world entry); trying again", flush=True)
    cmd = [x for x in cmd if x != "--create"]
    time.sleep(5)
step("the client logged in and killed 4 monsters", kills == 4, "kills %d; stderr %s" % (kills, "".join(err)[-300:]))
for k, (code, cid, applied, _) in actions.items():
    step("editor save '%s' while the client was online" % k, code == 200 and applied and all(applied.values()), "%s %s" % (cid, applied))
code, r = api("/api/m/rates/undo", {"change": actions["max"][1]})
step("Undo of the max (after the client left)", code == 200 and rates_of(r["state"])["exp.monster"]["value"] == 1)

# per kill: the EXP gain (0x272F) and Talent EXP (10027) the client received
deaths = [e for e in events if e["kind"] == "monster_death_reward"]
gains = [e for e in events if e["kind"] == "exp_gain"]
per = []
for d in deaths[:4]:
    g = min(gains, key=lambda x: abs(x["t"] - d["t"])) if gains else None
    # 10027 carries the Talent EXP towards the next point (0..99) and the Talent Points: total = points x 100 + exp
    per.append({"monster": d["monster"], "exp": g["gained"] if g else None, "talent_exp_total": d["talent_points"] * 100 + d["talent_exp"], "t": d["t"]})
tal = [None] + [per[i]["talent_exp_total"] - per[i - 1]["talent_exp_total"] for i in range(1, len(per))]
targets = [e.get("key") for e in events if e["kind"] == "target"]
print("     kills: " + "; ".join("#%d %s EXP +%s, Talent EXP +%s" % (i + 1, targets[i] if i < len(targets) else "?", x["exp"], tal[i]) for i, x in enumerate(per)), flush=True)
b = per[0]["exp"]
step("kill 1 at x1: base EXP %s" % b, b and b > 0)
step("kill 2 after EXP x5 + Talent x3 (client online): EXP x5, Talent EXP 30", per[1]["exp"] == 5 * b and tal[1] == 30, "%s / %s" % (per[1]["exp"], tal[1]))
step("kill 3 after Undo: back to x1", per[2]["exp"] == b and tal[2] == 10, "%s / %s" % (per[2]["exp"], tal[2]))
step("kill 4 at x%s: EXP %s (cap 100,000,000)" % (format(top, ","), format(min(100_000_000, b * top), ",")), per[3]["exp"] == min(100_000_000, int(b * top)), per[3]["exp"])
if a.json:
    with open(a.json, "w") as f:
        json.dump({"build": a.build, "database": DB, "actions": actions, "kills": per, "talent_deltas": tal, "targets": targets, "events": events}, f, indent=1)
print("ALL OK (%d checks)" % N["ok"])
