#!/usr/bin/env python3
"""tests/test_world.py - proves the "World & content" module (modules/world.py) on a TEST copy. Stdlib only.

  api    every route, every write type and its Undo, the refusals (400 range / 403 read-only / 409 stale version /
         start-up guard), an online character (EXP events skip it; gifts go live on round2, are refused on stock).
         Leaves the database as it found it (every write undone, newest first).
           python3 -I tests/test_world.py api --editor http://127.0.0.1:58816 --char Worldhero \
               [--probe "ssh vps python3 -I ~/gwed/tools-world/gwadmin-web/tests/test_world.py"] --login 127.0.0.1:60699 \
               --account worldtest1 --password 'World 2026!' --gwcrypt ~/gwed/env-world/gwcrypt
  game   the server LOADS the edited rows: writes a loot table (100 % drop), a GM monster spawn next to the character, a
         GM NPC with a dialogue, a quest payout, an EXP event; then --restart (a shell command that restarts the TEST
         instance); then a headless client in the world must receive them (10020 spawn with the GM object id, 10029 drop
         list after a kill, 10070 GM dialogue page, 10167 EXP buff 511); then everything is undone and the instance
         restarted again.
  gift   (round2 instance) a headless client holds the character online; the editor's Event gift (Silver + an item)
         reaches it live (10166 status / 10033 bag) through the shared save path; stock -> skipped with the reason.
  probe  the headless client used by game / gift (run it where the login port answers, e.g. on the VPS).
Prints each step; ends with "ALL OK" or the first failure. Refuses an editor whose database is the live 'godswar'.
"""
import argparse
import hashlib
import json
import math
import os
import re
import shlex
import socket
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
OK = []


def step(msg, cond, detail=""):
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)) if detail else ""), flush=True)
    if not cond:
        sys.exit(1)
    OK.append(msg)


class Editor:
    def __init__(self, url):
        self.url = url.rstrip("/")
        html = urllib.request.urlopen(self.url + "/", timeout=60).read().decode()
        self.csrf = re.search(r'name="csrf" content="([^"]+)"', html).group(1)

    def __call__(self, path, body=None, timeout=300):
        req = urllib.request.Request(self.url + path, data=json.dumps(body).encode() if body is not None else None,
                                     method="POST" if body is not None else "GET",
                                     headers={"Content-Type": "application/json", "X-CSRF-Token": self.csrf})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read() or b"null")

    def w(self, sub, body=None):
        return self("/api/m/world/" + sub, body)

    def undo(self, change_id, what):
        st, r = self("/api/undo", {"change": change_id})
        step("Undo: " + what, st == 200, (r.get("change") or {}).get("output") or json.dumps(r)[:200])
        return r


def guard_live(ed):
    st, s = ed("/api/m/status")
    step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
    step("not the live database", s["reborn"].get("database") != "godswar" and not s["reborn"].get("live"))
    step("database is writable (a copy)", s["reborn"].get("writable"))
    return s


# =========================================================================== api
def cmd_api(a):
    ed = Editor(a.editor)
    s = guard_live(ed)
    round2 = bool(s["capabilities"].get("available"))
    undo = []                                     # (change id, what), undone newest first at the end

    for sub in ("overview", "loot", "medusa", "instances", "quests", "quest/0", "quest/1", "events", "bosses", "calendar", "balances", "shops",
                "preflight", "gm/maps", "gm/map/0", "npc", "npc/templates/0", "loot/monster/A_normal_deer_001"):
        st, r = ed.w(sub)
        step("GET %s" % sub, st == 200, (r.get("error") if isinstance(r, dict) and st != 200 else ""))
    st, pf = ed.w("preflight")
    step("start-up check passes on the untouched copy", pf["ok"], pf["families"])

    # ---- monster loot: new table, refusals, edit, delete, undo
    st, gm0 = ed.w("gm/map/0")
    near = sorted(gm0["published"], key=lambda p: math.hypot(p["x"] - 165, p["z"] + 97))
    mon = near[0]["template_key"]
    st, info = ed.w("loot/monster/" + mon)
    ver = info["version"]
    st, r = ed.w("loot/save", {"template_key": mon, "version": ver, "table": {"maximum_drops": 9, "enabled": True}, "rules": []})
    step("loot: 9 drops per kill refused (client shows 8)", st == 400, r.get("error"))
    st, r = ed.w("loot/save", {"template_key": mon, "version": ver, "table": {"maximum_drops": 2, "enabled": True},
                               "rules": [{"item_id": 4001, "chance_basis_points": 10000}]})
    step("loot: more drops per kill than rules refused (server would not start)", st == 400, r.get("error"))
    st, r = ed.w("loot/save", {"template_key": mon, "version": ver, "table": {"maximum_drops": 1, "enabled": True},
                               "rules": [{"item_id": 16200, "chance_basis_points": 100}]})
    step("loot: an item missing from the Origin client refused", st == 400, r.get("error"))
    st, r = ed.w("loot/save", {"template_key": mon, "version": ver, "table": {"maximum_drops": 1, "enabled": True},
                               "rules": [{"item_id": 4001, "chance_basis_points": 0}]})
    step("loot: chance 0 refused (0.01 % - 100 %)", st == 400, r.get("error"))
    st, r = ed.w("loot/save", {"template_key": "No_such_monster", "version": "new", "table": {"maximum_drops": 1}, "rules": []})
    step("loot: unknown monster refused", st == 400, r.get("error"))
    rules = [{"item_id": 4001, "chance_basis_points": 10000, "minimum_quantity": 1, "maximum_quantity": 3},
             {"item_id": 1001, "chance_basis_points": 2550, "item_quality": 5, "item_grade": 3, "bound_on_pickup": True,
              "attributes": [{"id": 130, "level": 3}]}]
    st, r = ed.w("loot/save", {"template_key": mon, "version": ver, "table": {"maximum_drops": 2, "enabled": True}, "rules": rules})
    step("loot: table for %s saved (applies after restart)" % mon, st == 200 and r.get("applies") == "after_restart", r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "loot table"))
    st, ld = ed.w("loot")
    rows = [x for x in ld["rules"] if x["template_key"] == mon]
    step("loot: the rows are stored as sent", len(rows) == 2 and rows[1]["item_quality"] == 5 and rows[1]["attribute1"] == 130 and
         rows[1]["attribute_level1"] == 3 and rows[1]["bound_on_pickup"] is True and rows[0]["maximum_quantity"] == 3, rows)
    st, r = ed.w("loot/save", {"template_key": mon, "version": ver, "table": {"maximum_drops": 1, "enabled": True}, "rules": rules[:1]})
    step("loot: a stale version is refused (409)", st == 409, r.get("error"))
    st, info2 = ed.w("loot/monster/" + mon)
    st, r = ed.w("loot/save", {"template_key": mon, "version": info2["version"], "table": {"maximum_drops": 1, "enabled": True},
                               "rules": [dict(rules[0], loot_index=0)]})
    step("loot: edit (one drop removed)", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "loot edit"))
    st, pend = ed("/api/m/pending")
    step("pending-restart list names the loot changes", any(mon in (c.get("action") or "") for c in pend["pending"]))

    # ---- Medusa: monster values, guard refusal, title
    st, md = ed.w("medusa")
    m = next(x for x in md["monsters"] if x["difficulty"] == 1 and x["template_alias"] == "boss-medusa")
    st, r = ed.w("medusa/monster", {"difficulty": 1, "template_alias": "boss-medusa", "version": m["version"],
                                    "values": {"maximum_health": m["maximum_health"] + 1000, "score": 1100}})
    step("Medusa: monster HP saved", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "Medusa monster"))
    st, r = ed.w("medusa/monster", {"difficulty": 1, "template_alias": "boss-medusa", "version": m["version"], "values": {"monster_level": 201}})
    step("Medusa: level 201 refused", st == 400, r.get("error"))
    rw = [dict(x) for x in md["rewards"] if x["difficulty"] == 2]
    bad = [dict(x) for x in rw]
    for x in bad:
        if x["reward_kind"] == 1 and x["threshold"] > 0:
            x["honor_points"] = 1                 # honour goes DOWN as the score rises: the reward policy would throw
            break
    st, r = ed.w("medusa/rewards", {"difficulty": 2, "version": md["reward_versions"]["2"], "rules": bad})
    step("Medusa: honour table the server rejects at start-up is refused by the guard (nothing written)",
         st == 400 and "next server start" in (r.get("error") or ""), (r.get("error") or "")[:160])
    rw2 = [dict(x) for x in rw]
    rw2[-1]["honor_points"] = rw2[-1]["honor_points"]   # unchanged values, new row
    rw2.append({"difficulty": 2, "reward_kind": 2, "threshold": 2000, "honor_points": next(x["honor_points"] for x in sorted(
        [y for y in rw if y["reward_kind"] == 2], key=lambda y: y["threshold"]) if x["threshold"] >= 2000), "title": None})
    st, r = ed.w("medusa/rewards", {"difficulty": 2, "version": md["reward_versions"]["2"], "rules": rw2})
    step("Medusa: honour table row added", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "Medusa honour table"))
    t = md["titles"][0]
    st, r = ed.w("medusa/title", {"title": t["title"], "version": t["version"], "display_name": t["display_name"] + " X",
                                  "physical_attack_basis_points": 350, "magic_attack_basis_points": t["magic_attack_basis_points"],
                                  "physical_defense_basis_points": t["physical_defense_basis_points"],
                                  "magic_defense_basis_points": t["magic_defense_basis_points"]})
    step("Medusa: title renamed and bonus changed", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "Medusa title"))
    lv = next(x["loot_version"] for x in md["monsters"] if x["difficulty"] == 1 and x["template_alias"] == "boss-medusa")
    cur = [x for x in md["loot"] if x["difficulty"] == 1 and x["template_alias"] == "boss-medusa"]
    st, r = ed.w("medusa/loot", {"difficulty": 1, "template_alias": "boss-medusa", "version": lv,
                                 "rules": [{k: x[k] for k in ("loot_index", "item_id", "chance_basis_points", "minimum_quantity", "maximum_quantity", "enabled")}
                                           for x in cur] + [{"item_id": 4002, "chance_basis_points": 5000}]})
    step("Medusa: drop added to Medusa (Normal)", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "Medusa drops"))

    # ---- instances (edit_advanced, applies now)
    st, ins = ed.w("instances")
    atl = ins["legacy"][0]
    st, r = ed.w("instances/save", {"which": "legacy", "instance_kind": 1, "free_entry_limit": 5, "paid_retry_limit": None, "version": atl["version"]})
    step("instances: advanced table without the advanced switch refused (403)", st == 403, r.get("error"))
    st, r = ed.w("instances/save", {"which": "legacy", "instance_kind": 1, "free_entry_limit": 5, "paid_retry_limit": None, "version": atl["version"], "advanced": True})
    step("instances: Atlantis 5 free entries, unlimited retries (applies now)", st == 200 and r["applies"] == "now", r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "Atlantis entries"))
    st, r = ed.w("instances/save", {"which": "medusa", "daily_entry_limit": 0, "version": ins["medusa"]["version"], "advanced": True})
    step("instances: Medusa 0 entries refused (1-99)", st == 400, r.get("error"))
    st, r = ed.w("instances/save", {"which": "medusa", "daily_entry_limit": 7, "version": ins["medusa"]["version"], "advanced": True})
    step("instances: Medusa 7 entries per day", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "Medusa entries"))

    # ---- quest rewards
    st, q0 = ed.w("quest/0")
    step("quest 0 is shown read-only with the reason", q0["editable"] is False, q0.get("why"))
    st, q = ed.w("quest/1")
    st, r = ed.w("quest/save", {"quest_id": 1, "version": q["version"], "values": {"experience": 123456, "talent_points": 7, "silver": 999, "gold": 1},
                                "slots": [{"item_id": 4002, "item_quality": 0}, {"item_id": 1001, "item_quality": 3, "item_grade": 2, "attributes": [{"id": 130, "level": 2}]}]})
    step("quest 1: payout and 2 reward items saved", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "quest rewards"))
    st, q2 = ed.w("quest/1")
    step("quest 1: rows stored", q2["values"]["experience"] == 123456 and len(q2["slots"]) == 2 and q2["slots"][1]["attribute1"] == 130, q2)
    st, r = ed.w("quest/save", {"quest_id": 1, "version": q2["version"], "values": None,
                                "slots": [{"item_id": 4002}] * 9})
    step("quest: 9 reward items refused (the menu holds 8)", st == 400, r.get("error"))

    # ---- GM monsters: per-template stats, per-spawn edit, new spawn, update, delete
    mid = 0
    st, d0 = ed.w("gm/map/%d" % mid)
    tk = near[0]["template_key"]
    st, r = ed.w("gm/attributes", {"map_id": mid, "template_key": tk, "version": d0["version"], "values": {"maximum_health": 5000, "physical_attack": 300}})
    step("GM: stats of every %s on Sparta" % tk, st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "GM template stats"))
    st, d1 = ed.w("gm/map/%d" % mid)
    oid = near[1]["object_id"]
    st, r = ed.w("gm/edit", {"map_id": mid, "object_id": oid, "version": d1["version"], "enabled": False, "values": {}})
    step("GM: published spawn %d switched off" % oid, st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "GM spawn off"))
    st, r = ed.w("gm/edit", {"map_id": mid, "object_id": 123456, "version": d1["version"], "enabled": False})
    step("GM: unknown spawn refused", st == 400, r.get("error"))
    st, d2 = ed.w("gm/map/%d" % mid)
    st, r = ed.w("gm/spawn/create", {"map_id": mid, "template_key": tk, "version": d2["version"], "x": 170, "z": -95, "count": 3, "spread": 4,
                                     "name": "WT-%d" % (time.time() % 100000), "level": 7, "maximum_health": 777, "values": {"physical_defense": 12}})
    step("GM: 3 new spawns of %s (object ids %s)" % (tk, r.get("object_ids")), st == 200 and len(r.get("object_ids") or []) == 3, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "GM new spawns"))
    st, d3 = ed.w("gm/map/%d" % mid)
    new_ids = r["object_ids"]
    g = [x for x in d3["gm_spawns"] if x["object_id"] in new_ids]
    step("GM: packets carry the new level / HP / object id", len(g) == 3 and all(x["level"] == 7 and x["maximum_health"] == 777 for x in g), [(x["name"], x["level"], x["maximum_health"]) for x in g])
    st, r = ed.w("gm/spawn/create", {"map_id": mid, "template_key": "A_boss_boar_001", "version": d3["version"], "x": 1, "z": 1})
    step("GM: a monster with no spawn on the map is refused (nothing to copy)", st == 400, r.get("error"))
    st, r = ed.w("gm/spawn/update", {"id": g[0]["id"], "version": d3["version"], "name": g[0]["name"], "x": 171.5, "z": -96, "level": 9, "maximum_health": 900, "enabled": False})
    step("GM: spawn moved, level 9, off", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "GM spawn update"))
    st, d4 = ed.w("gm/map/%d" % mid)
    st, r = ed.w("gm/spawn/delete", {"id": g[2]["id"], "version": d4["version"]})
    step("GM: spawn deleted", st == 200, r.get("error") or r["change"]["action"])
    del_change = r["change"]["id"]

    # ---- GM NPCs + dialogue
    st, tm = ed.w("npc/templates/0")
    st, n0 = ed.w("npc")
    dlg = {"dialogue_key": "wt_test", "display_name": "Test Envoy", "entry_page": 1, "enabled": True, "version": "new",
           "pages": [{"page_index": 1, "body_text": "Hello!", "buttons": [{"slot": 1, "label": "More", "result_number": 501, "next_page_index": 2},
                                                                           {"slot": 2, "label": "Bye", "result_number": 502}]},
                     {"page_index": 2, "body_text": "Page two", "buttons": [{"slot": 1, "label": "Back", "result_number": 503, "next_page_index": 1}]}]}
    bad = json.loads(json.dumps(dlg))
    bad["pages"][1]["buttons"].append({"slot": 1, "label": "Again", "next_page_index": 1})
    st, r = ed.w("npc/dialogue/save", bad)
    step("NPC: two buttons in one slot refused", st == 400, r.get("error"))
    bad = json.loads(json.dumps(dlg))
    bad["pages"][0]["buttons"][0]["next_page_index"] = 9
    st, r = ed.w("npc/dialogue/save", bad)
    step("NPC: a button leading to a missing page refused", st == 400, r.get("error"))
    st, r = ed.w("npc/dialogue/save", dlg)
    step("NPC: dialogue wt_test (2 pages, 3 buttons)", st == 200, r.get("error") or r["change"]["action"])
    st, nd = ed.w("npc")
    btns = [b_["result_number"] for d_ in nd["dialogues"] if d_["dialogue_key"] == "wt_test" for p_ in d_["pages"] for b_ in p_["buttons"]]
    step("NPC: button numbers are the wire ids page x 1000 + slot", sorted(btns) == [1001, 1002, 2001], btns)
    undo.append((r["change"]["id"], "GM dialogue"))
    st, r = ed.w("npc/spawn/save", {"name": "Wt Envoy", "map_id": 0, "template_key": "Sparta_035_FemMale9", "x": 160, "z": -95, "version": n0["spawns_version"]})
    step("NPC: a model not used on the map is refused (Santa)", st == 400, r.get("error"))
    st, r = ed.w("npc/spawn/save", {"name": "Wt Envoy", "map_id": 0, "template_key": tm[0]["template_key"], "dialogue_key": "wt_test", "x": 168, "z": -97,
                                    "version": n0["spawns_version"]})
    step("NPC: GM NPC placed (model %s)" % tm[0]["template_key"], st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "GM NPC"))
    st, n1 = ed.w("npc")
    step("NPC: stored with the dialogue", any(x["name"] == "Wt Envoy" and x["dialogue_key"] == "wt_test" for x in n1["spawns"]) and n1["dialogues"])
    st, pf = ed.w("preflight")
    step("start-up check passes with all the new rows", pf["ok"], [f for f in pf["families"] if not f["ok"]])

    # ---- EXP events (offline characters only)
    st, chars = ed("/api/m/characters?q=" + a.char)
    cid = next(x["id"] for x in chars if x["name"] == a.char)
    now = time.time()
    iso = lambda t: time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))      # noqa: E731
    ev = {"name": "WT weekend", "status_id": 511, "bonus_pct": 100, "start": iso(now - 60), "end": iso(now + 48 * 3600), "target": {"ids": [cid]}}
    st, r = ed.w("events/exp/start", ev)
    step("EXP event: advanced table without the switch refused (403)", st == 403, r.get("error"))
    st, r = ed.w("events/exp/start", dict(ev, status_id=12345, advanced=True))
    step("EXP event: an icon the client does not know refused", st == 400, r.get("error"))
    st, r = ed.w("events/exp/start", dict(ev, advanced=True))
    step("EXP event: +100 %% for %s" % a.char, st == 200 and r["applied"] and r["applies"] == "next_login", r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "EXP event"))
    st, e1 = ed.w("events")
    step("EXP event: listed as running", any(x["source"] == "gm-event:WT weekend" and x["bonus_basis_points"] == 10000 for x in e1["events"]), e1["events"])
    st, r = ed.w("events/exp/stop", {"name": "WT weekend", "advanced": True})
    step("EXP event: ended", st == 200, r.get("error") or r["change"]["action"])
    undo.append((r["change"]["id"], "EXP event end"))

    # ---- gifts offline (database path). Silver first and undone; the bag item is taken out with the shared bag
    # "remove" (the Undo of a shared-path bag ADD fails: framework bug, see the report), so nothing is left behind.
    st, r = ed.w("events/gift", {"what": "money", "silver": 1234, "target": {"ids": [cid]}})
    step("gift: Silver +1,234 to offline %s by database" % a.char, st == 200 and r["database"] == 1, r.get("results"))
    ed.undo(r["results"][0]["change"], "Silver gift")
    st, r = ed.w("events/gift", {"what": "item", "item_id": 4002, "quantity": 2, "target": {"ids": [cid]}})
    step("gift: 2 x item 4002 to offline %s by database" % a.char, st == 200 and r["database"] == 1, r.get("results"))
    st, sh = ed("/api/reborn/character/%d" % cid)
    for b in [b for b in sh["bag"] if b and b["prop_id"] == 4002 and b["stack"] == 2]:
        st, r = ed("/api/m/edit/%d/bag" % cid, {"op": "remove", "slot": b["slot_index"]})
        step("gift item taken out again (shared bag remove)", st == 200, (r.get("change") or {}).get("action") or r)
    gift_changes = []

    # ---- online character (a headless client holds it in the world)
    if a.probe:
        out = tempfile.mktemp(suffix=".json")
        p = run_probe(a, ["--hold", "90"], out, background=True)
        online = False
        for _ in range(120):
            st, on = ed("/api/m/online/%d" % cid)
            if on.get("online"):
                online = True
                break
            time.sleep(1)
        step("%s is online (headless client in the world)" % a.char, online, on.get("reasons"))
        st, r = ed.w("events/exp/start", dict(ev, advanced=True, name="WT online"))
        step("EXP event: an online character is skipped, nothing written", st == 400 and "online" in (r.get("error") or ""), r.get("error"))
        for _ in range(30):                   # round2 answers "busy / still entering the world" for a few seconds
            st, r = ed.w("events/gift", {"what": "money", "silver": 5, "target": {"ids": [cid]}})
            res0 = (r.get("results") or [{}])[0]
            if not round2 or res0.get("ok") or "entering the world" not in (res0.get("why") or "") and "busy" not in (res0.get("why") or ""):
                break
            time.sleep(2)
        if round2:
            step("gift to the ONLINE character goes live (round2)", st == 200 and r["live"] == 1, r.get("results"))
            gift_changes.append(r["results"][0]["change"])
        else:
            res = (r.get("results") or [{}])[0]
            step("gift to the ONLINE character is skipped on the stock server, with the reason", st == 200 and r["skipped"] == 1 and
                 "round-2" in (res.get("why") or ""), res.get("why"))
        p.wait(timeout=240)
        for _ in range(120):
            st, on = ed("/api/m/online/%d" % cid)
            if not on.get("online"):
                break
            time.sleep(1)
        time.sleep(3)

    # ---- undo everything, newest first
    for cidch in reversed(gift_changes):
        ed.undo(cidch, "gift")
    identity_ok = expect_identity_failure(ed, del_change)
    for ch, what in reversed(undo):
        if what == "GM new spawns" and not identity_ok:
            # its third row was deleted and cannot come back, so the create's Undo is (rightly) blocked: delete the rest
            st, dd = ed.w("gm/map/0")
            for x in [x for x in dd["gm_spawns"] if x["object_id"] in new_ids]:
                st, dd2 = ed.w("gm/map/0")
                st, r = ed.w("gm/spawn/delete", {"id": x["id"], "version": dd2["version"]})
                step("GM spawn %s removed by delete (Undo of the create waits for the framework fix)" % x["name"], st == 200, r.get("error"))
            continue
        ed.undo(ch, what)
    st, pf = ed.w("preflight")
    st, ov = ed.w("overview")
    c = ov["counts"]
    step("copy back as it was (GM / quest / event rows gone)", c["gm_monster_spawns"] == 0 and c["gm_npc_spawns"] == 0 and c["quest_values"] == 0 and
         c["gm_dialogues"] == 0 and c["exp_events"] == 0 and c["loot_tables"] == 2, c)
    step("start-up check passes after the Undos", pf["ok"])
    print("ALL OK (%d checks)" % len(OK))


def cmd_cleanup(a):
    """Undo every change of this module (and the gifts it made through the shared save path) that is not undone yet,
    newest first: puts the TEST copy back after an interrupted run."""
    ed = Editor(a.editor)
    guard_live(ed)
    st, ch = ed("/api/m/changes")
    undone = {c.get("undo_of") for c in ch if c.get("undo_of")}
    todo = [c for c in ch if not c.get("undone") and not c.get("undo_of") and c["id"] not in undone and
            (c.get("module") == "world" or "event gift" in (c.get("action") or "") or "to the bag" in (c.get("action") or ""))]
    for c in todo:                                   # /api/m/changes is newest first
        st, r = ed("/api/undo", {"change": c["id"]})
        print(("undone  " if st == 200 else "FAILED  ") + c["id"] + " " + (c.get("action") or "")[:90] + ("" if st == 200 else " - " + (r.get("error") or "")[:160]))
    # GM spawns a test created and then deleted cannot come back by Undo (GENERATED ALWAYS id), so their create cannot
    # be undone either: remove what is left of them directly
    for mid in (0,):
        st, d = ed.w("gm/map/%d" % mid)
        for g in [g for g in d.get("gm_spawns") or [] if g["name"].startswith("WT-")]:
            st, d2 = ed.w("gm/map/%d" % mid)
            st, r = ed.w("gm/spawn/delete", {"id": g["id"], "version": d2["version"]})
            print("deleted leftover GM spawn %s: HTTP %s" % (g["name"], st))
    print("cleanup done (%d changes)" % len(todo))


def expect_identity_failure(ed, change_id):
    """Returns True when the Undo worked. Undo of a deleted GM spawn re-inserts a row whose id is GENERATED ALWAYS: the shared restore (reborn.py) does
    not say OVERRIDING SYSTEM VALUE, so it fails cleanly (nothing written). Framework change requested."""
    st, r = ed("/api/undo", {"change": change_id})
    if st == 200:
        step("Undo: GM spawn delete", True, (r.get("change") or {}).get("output"))
        return True
    step("Undo of a deleted GM spawn fails cleanly until the framework restores identity rows (known, reported)",
         st == 400 and ("GENERATED ALWAYS" in (r.get("error") or "") or "non-DEFAULT" in (r.get("error") or "")), (r.get("error") or "")[:200])
    return False


def run_probe(a, extra, out, background=False):
    args = ["probe", a.account, a.password, a.char, a.login, "--gwcrypt", a.gwcrypt] + extra
    # a remote prefix (ssh) joins its arguments into one shell line: quote them (the password has a space)
    cmd = (shlex.split(a.probe) + [" ".join(shlex.quote(x) for x in args)]) if a.probe else [sys.executable, "-I", os.path.abspath(__file__)] + args
    if background:
        return subprocess.Popen(cmd, stdout=open(out, "w"), stderr=subprocess.PIPE)
    r = subprocess.run(cmd, capture_output=True, timeout=600)
    with open(out, "wb") as f:
        f.write(r.stdout)
    return r


def probe_report(out):
    txt = open(out).read()
    m = re.search(r"^REPORT (.*)$", txt, re.M)
    return json.loads(m.group(1)) if m else {}


# =========================================================================== game (the server loads the rows)
def cmd_game(a):
    ed = Editor(a.editor)
    guard_live(ed)
    st, chars = ed("/api/m/characters?q=" + a.char)
    me = next(x for x in chars if x["name"] == a.char)
    cid, mid = me["id"], me["map"]
    out = tempfile.mktemp(suffix=".json")
    run_probe(a, [], out)
    rep = probe_report(out)
    pos = rep.get("enter") or {}
    step("headless client enters the world before the edits", pos.get("x") is not None, pos)
    x0, z0 = pos["x"], pos["z"]
    st, d = ed.w("gm/map/%d" % mid)
    near = sorted(d["published"], key=lambda p: math.hypot(p["x"] - x0, p["z"] - z0))
    tk = near[0]["template_key"]
    changes = []
    # loot: 100 % drop of a Big Healing Potion (4002) x2 from that monster type
    st, info = ed.w("loot/monster/" + tk)
    cur = [] if info["version"] == "new" else [x for x in ed.w("loot")[1]["rules"] if x["template_key"] == tk]
    st, r = ed.w("loot/save", {"template_key": tk, "version": info["version"], "table": {"maximum_drops": 1, "enabled": True},
                               "rules": [{"item_id": 4002, "chance_basis_points": 10000, "minimum_quantity": 2, "maximum_quantity": 2}]})
    step("loot: %s drops 2 x item 4002 at 100 %%" % tk, st == 200, r.get("error") or r["change"]["action"])
    changes.append(r["change"]["id"])
    # GM spawn of the same monster 6 units away, level 3 / 300 HP
    st, d = ed.w("gm/map/%d" % mid)
    st, r = ed.w("gm/spawn/create", {"map_id": mid, "template_key": tk, "version": d["version"], "x": round(x0 + 6, 1), "z": round(z0, 1),
                                     "name": "WT-proof-%d" % (time.time() % 100000), "level": 3, "maximum_health": 120})
    step("GM spawn next to the character", st == 200, r.get("error") or r["change"]["action"])
    gm_oid = r["object_ids"][0]
    changes.append(r["change"]["id"])
    # GM NPC + dialogue
    st, tm = ed.w("npc/templates/%d" % mid)
    st, n0 = ed.w("npc")
    st, r = ed.w("npc/dialogue/save", {"dialogue_key": "wt_proof", "display_name": "Proof", "version": "new",
                                       "pages": [{"page_index": 1, "body_text": "Hi", "buttons": [{"slot": 1, "label": "A", "result_number": 701, "next_page_index": 2},
                                                                                                  {"slot": 2, "label": "B", "result_number": 702}]},
                                                 {"page_index": 2, "body_text": "Two", "buttons": [{"slot": 1, "label": "C", "result_number": 703}]}]})
    step("GM dialogue", st == 200, r.get("error"))
    changes.append(r["change"]["id"])
    st, r = ed.w("npc/spawn/save", {"name": "WT Proof", "map_id": mid, "template_key": tm[0]["template_key"], "dialogue_key": "wt_proof",
                                    "x": round(x0 - 3, 1), "z": round(z0, 1), "version": n0["spawns_version"]})
    step("GM NPC next to the character", st == 200, r.get("error"))
    changes.append(r["change"]["id"])
    st, n1 = ed.w("npc")
    npc_oid = next(x["object_id"] for x in n1["spawns"] if x["name"] == "WT Proof")
    # quest payout of quest 0, EXP event for the character
    st, q = ed.w("quest/1")
    st, r = ed.w("quest/save", {"quest_id": 1, "version": q["version"], "values": {"experience": 4242, "talent_points": 3, "silver": 77, "gold": 0}, "slots": []})
    step("quest 1 payout", st == 200, r.get("error"))
    changes.append(r["change"]["id"])
    now = time.time()
    iso = lambda t: time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))      # noqa: E731
    st, r = ed.w("events/exp/start", {"name": "WT proof", "status_id": 511, "bonus_pct": 100, "start": iso(now - 60), "end": iso(now + 7200),
                                      "target": {"ids": [cid]}, "advanced": True})
    step("EXP event +100 %% for %s" % a.char, st == 200, r.get("error"))
    changes.append(r["change"]["id"])
    st, pf = ed.w("preflight")
    step("start-up check passes", pf["ok"], pf)
    # restart the TEST instance (the editor never restarts a server itself; the test does it on its own instance)
    step("restart the test instance", subprocess.run(a.restart, shell=True).returncode == 0, a.restart)
    if a.logcmd:
        log = subprocess.run(a.logcmd, shell=True, capture_output=True, text=True).stdout
        step("server log: GM monster overrides loaded", re.search(r"\[monster-overrides\] loaded GM spawns=1 ", log) is not None,
             (re.findall(r"\[monster-overrides\][^\n]*", log) or [""])[-1])
        step("server log: GM NPC placements loaded", "[gm-npc] loaded GM placements=1" in log, (re.findall(r"\[gm-npc\][^\n]*", log) or [""])[-1])
    # in the world: the client receives them
    run_probe(a, ["--npc", str(npc_oid), "--func", "%d,85,0" % npc_oid, "--func", "%d,85,1001" % npc_oid, "--kill-oid", str(gm_oid)], out)
    rep = probe_report(out)
    seen = {int(k): v for k, v in (rep.get("monsters") or {}).items()}
    step("client receives the GM spawn %d (%s) in its 10020 monster list" % (gm_oid, tk), gm_oid in seen and seen[gm_oid]["key"] == tk,
         seen.get(gm_oid))
    step("client receives the EXP buff 511 in 10167 (+100 % event)", 511 in (rep.get("statuses") or []), rep.get("statuses"))
    drops = [x for x in rep.get("drops") or [] if x["monster"] == gm_oid]
    step("killing the GM spawn: the 10029 drop list carries 2 x item 4002 (the loot table)", drops and drops[0]["items"] == [[4002, 2]], rep.get("drops"))
    gains = rep.get("exp_gains") or []
    step("the status frame carries the event's EXP bonus (+%s %%)" % (rep.get("exp_bonus", 0) * 100), rep.get("exp_bonus") == 1.0, rep.get("exp_bonus"))
    step("the GM spawn dies and pays EXP while the event buff is on", any(k.get("monster") == gm_oid for k in rep.get("kills") or []) and gains, gains[-3:])
    pages = [x for x in rep.get("npc_pages") or [] if x["npc"] == npc_oid]
    step("GM NPC dialogue: page 1 = text 1101 + buttons 1001/1002; clicking 1001 opens page 2 (text 2101 + button 2001)",
         len(pages) >= 2 and pages[0]["subs"] == [1101, 1001, 1002] and pages[1]["subs"] == [2101, 2001], pages)
    # put everything back (after the client's logout save: the EXP event rows belong to the character)
    for _ in range(120):
        st, on = ed("/api/m/online/%d" % cid)
        if not on.get("online"):
            break
        time.sleep(1)
    step("%s logged out again" % a.char, not on.get("online"), on.get("reasons"))
    for ch in reversed(changes):
        ed.undo(ch, ch)
    step("restart the test instance again (back to the original content)", subprocess.run(a.restart, shell=True).returncode == 0)
    print("ALL OK (%d checks)" % len(OK))


# =========================================================================== gift (live on round2)
def cmd_gift(a):
    ed = Editor(a.editor)
    s = guard_live(ed)
    round2 = bool(s["capabilities"].get("available"))
    st, chars = ed("/api/m/characters?q=" + a.char)
    cid = next(x["id"] for x in chars if x["name"] == a.char)
    st, sh = ed("/api/reborn/character/%d" % cid)
    base = {"silver": int(sh["base"]["Money"]), "gold": int(sh["base"]["Stone"])}
    out = tempfile.mktemp(suffix=".json")
    p = run_probe(a, ["--hold", "80"], out, background=True)
    for _ in range(120):
        st, on = ed("/api/m/online/%d" % cid)
        if on.get("online"):
            break
        time.sleep(1)
    step("%s online" % a.char, on.get("online"), on.get("live_fields"))
    time.sleep(4)
    r = None
    for _ in range(20):
        st, r = ed.w("events/gift", {"what": "money", "silver": 31337, "gold": 7, "target": {"ids": [cid]}})
        if st == 200 and (r["live"] or not round2):
            break
        time.sleep(1)
    res = r["results"][0]
    if not round2:
        step("stock server: the online character is skipped with the round-2 reason", r["skipped"] == 1 and "round-2" in res.get("why", ""), res.get("why"))
        p.wait(timeout=120)
        print("ALL OK (%d checks)" % len(OK))
        return
    step("gift Silver +31,337 / Gold +7 went LIVE", res.get("mode") == "live", res)
    money_change = res["change"]
    st, r = ed.w("events/gift", {"what": "item", "item_id": 4002, "quantity": 3, "target": {"ids": [cid]}})
    step("gift 3 x item 4002 went LIVE", st == 200 and r["live"] == 1, r.get("results"))
    p.wait(timeout=300)
    j = probe_report(out)
    hist = [h_ for h_ in j.get("status_history") or [] if h_["in_world"]]
    step("the client received the new Silver / Gold in 10166 at once (was %s / %s)" % (base["silver"], base["gold"]),
         any(h_["silver"] == base["silver"] + 31337 and h_["gold"] == base["gold"] + 7 for h_ in hist), hist[-3:])
    ch = [c for c in j.get("bag_changes") or [] if c[2] == 4002]
    step("the client received 3 x item 4002 in its bag (10033 page)", bool(ch) and ch[0][3] == 3, j.get("bag_changes"))
    for _ in range(120):
        st, on = ed("/api/m/online/%d" % cid)
        if not on.get("online"):
            break
        time.sleep(1)
    time.sleep(5)
    st, r = ed("/api/undo", {"change": money_change})
    step("Undo of the live gift (inverse values, by database now that it logged out)", st == 200, (r.get("change") or {}).get("action") or r)
    st, sh = ed("/api/reborn/character/%d" % cid)
    step("Silver / Gold back to %s / %s" % (base["silver"], base["gold"]), int(sh["base"]["Money"]) == base["silver"] and int(sh["base"]["Stone"]) == base["gold"])
    for b in [b for b in sh["bag"] if b and b["prop_id"] == 4002 and b["stack"] == 3]:
        st, r = ed("/api/m/edit/%d/bag" % cid, {"op": "remove", "slot": b["slot_index"]})
        step("the gifted item removed again (a live bag gift has no automatic Undo: the opposite edit)", st == 200, (r.get("change") or {}).get("action") or r)
    print("ALL OK (%d checks)" % len(OK))


# =========================================================================== probe (headless client)
KEY = b"435kfa3fjaf03fakf3fa3220fasjf3we322f2fjaf92f2fsf92fl1h1535rb7nw4"


def cmd_probe(a):
    sys.path.insert(0, a.gwcrypt)
    from gwcrypt import Stream       # noqa: E402  (GodsWar-2.51/Polish/tools/gwcrypt.py)

    def shift_name(name):
        k = KEY[len(name)]
        k = k - 256 if k >= 128 else k
        out = bytearray()
        for c in name:
            if 65 <= c <= 90:
                out.append((c - 65 + (k % 26)) % 26 + 65)
            elif 97 <= c <= 122:
                out.append((c - 97 + (k % 26)) % 26 + 97)
            elif 48 <= c <= 57:
                out.append((c - 48 + (k % 10)) % 10 + 48)
            else:
                out.append(c)
        return bytes(out)

    pad = lambda b, n: b[:n] + b"\0" * (n - len(b[:n]))      # noqa: E731
    u32 = lambda b, o: struct.unpack_from("<I", b, o)[0]       # noqa: E731
    i32 = lambda b, o: struct.unpack_from("<i", b, o)[0]       # noqa: E731

    class Conn:
        def __init__(self, host, port):
            self.s = socket.create_connection((host, port), timeout=10)
            self.enc, self.dec, self.buf = Stream(), Stream(), b""

        def send(self, t, body):
            self.s.sendall(self.enc.encrypt(struct.pack("<HH", 4 + len(body), t) + body))

        def recv(self, timeout=1.5):
            out, end = [], time.time() + timeout
            self.s.settimeout(0.2)
            while time.time() < end:
                try:
                    d = self.s.recv(65536)
                except socket.timeout:
                    continue
                except OSError:
                    break
                if not d:
                    out.append(("CLOSED", 0, b""))
                    break
                self.buf += self.dec.decrypt(d)
                while len(self.buf) >= 4:
                    size, t = struct.unpack_from("<HH", self.buf)
                    if size < 4 or len(self.buf) < size:
                        break
                    out.append((t, size, self.buf[4:size]))
                    self.buf = self.buf[size:]
            return out

    R = {"monsters": {}, "statuses": [], "drops": [], "kills": [], "exp_gains": [], "npc_pages": [], "types": {},
         "status_history": [], "bag": {}, "bag_changes": []}
    me = {"x": 0.0, "z": 0.0, "obj": 0, "in_world": False}

    def decode(msgs):
        deaths = []
        for t, size, body in msgs:
            if t == "CLOSED":
                R["closed"] = True
                continue
            R["types"][str(t)] = R["types"].get(str(t), 0) + 1
            if t == 10019 and len(body) > 100:
                me["obj"] = u32(body, 48)
                me["x"], me["z"] = struct.unpack_from("<f", body, 52)[0], struct.unpack_from("<f", body, 60)[0]
                R["enter"] = {"character_id": u32(body, 0), "object_id": me["obj"], "x": round(me["x"], 1), "z": round(me["z"], 1)}
            elif t == 10020 and len(body) >= 76:
                oid = u32(body, 4)
                if oid >= 8000:
                    R["monsters"][oid] = {"x": round(struct.unpack_from("<f", body, 24)[0], 1), "z": round(struct.unpack_from("<f", body, 32)[0], 1),
                                          "key": body[40:72].split(b"\0")[0].decode(errors="replace")}
            elif t == 10023 and len(body) >= 4:
                R["monsters"].pop(u32(body, 0), None)
            elif t == 10029 and len(body) >= 8:
                n = u32(body, 4)
                items = [[u32(body, 8 + i * 72), u32(body, 8 + i * 72 + 24) >> 24] for i in range(n) if 8 + i * 72 + 28 <= len(body)]
                R["drops"].append({"monster": u32(body, 0), "items": items})
            elif t == 0x27B7 and len(body) >= 300:
                n = u32(body, 4)
                R["statuses"] = [u32(body, 8 + 4 * i) for i in range(min(n, 20))]
                R["exp_bonus"] = round(struct.unpack_from("<f", body, 296)[0], 4)     # 1.0 = +100 %
            elif t == 0x272F and len(body) >= 8:
                R["exp_gains"].append({"gained": u32(body, 0), "current": u32(body, 4)})
            elif t == 0x272B and len(body) >= 108:
                R["kills"].append({"monster": u32(body, 0)})
                deaths.append(u32(body, 0))
            elif t == 10166 and len(body) >= 140 and u32(body, 0) == 0x1448:      # status update (reborn_probe.py offsets)
                R["status_history"].append({"level": i32(body, 96), "silver": i32(body, 116), "gold": i32(body, 120),
                                            "in_world": me["in_world"]})
            elif t == 10033 and len(body) >= 20:                                    # kit-bag page: 12 x 72-byte records
                page, first = body[12], body[13]
                for k in range(12):
                    o = 20 + 72 * k
                    if o + 72 > len(body):
                        break
                    iid = u32(body, o)
                    iid = None if iid in (0, 0xFFFFFFFF) else iid
                    slot = page * 24 + first + k
                    if me["in_world"] and R["bag"].get(slot) != iid:
                        R["bag_changes"].append([slot, R["bag"].get(slot), iid, body[o + 27] if iid else 0])
                    R["bag"][slot] = iid
            elif t == 10070 and len(body) >= 8:
                R["npc_pages"].append({"npc": u32(body, 0), "dialog": i32(body, 4), "subs": [i32(body, o) for o in range(8, len(body) - 3, 4)]})
        return deaths

    host, port = a.login.rsplit(":", 1)
    acct, pw, role = a.account.encode(), a.password.encode(), a.role.encode()
    c = Conn(host, int(port))
    c.send(1, pad(shift_name(acct), 32) + hashlib.md5(pw).hexdigest().encode() + b"\0" * 68 + struct.pack("<I", 0x3e9774))
    r = []
    for _ in range(20):                  # the PBKDF2 password check is slow on a busy VPS: wait up to a minute
        r = c.recv(3)
        if r:
            break
    if not r or r[0][0] != 3 or r[0][2][:2] != b"\x01\x00":
        sys.exit("login rejected: %r" % (r[:2],))
    realms = [m for m in r if m[0] == 10008]
    c.send(4, pad(acct, 32) + bytes([realms[0][2][36] if realms else 1]) + bytes.fromhex("adcf0600000000"))
    c.recv(2)
    c.send(6, pad(acct, 32) + bytes.fromhex("402cee11b8de1200408a82112800000007000000000000003032363038303333343931313234303733343732000000000c38a708149fef11c61c40001038a7080f000000149fef110b00000001000000"))
    rd = []
    for _ in range(10):
        rd = [m for m in c.recv(3) if m[0] == 10001]
        if rd:
            break
    if not rd:
        sys.exit("no redirect")
    b = rd[0][2]
    ghost, gport, code = b[1:33].split(b"\0")[0].decode(), u32(b, 36), b[41:66]
    c.s.close()
    g = Conn(ghost, gport)
    g.send(10000, pad(acct, 32) + code + b"\x01")
    r = []
    for _ in range(10):
        r = g.recv(3)
        if r:
            break
    roles = [m for m in r if m[0] == 10002]
    if a.create or (roles and roles[0][1] == 500):
        g.send(10003, pad(role, 32) + bytes.fromhex("0100000035000000" + "00" * 32 + "8085183c"))
        g.recv(2)
    g.send(10006, pad(role, 32) + code + b"\0")
    decode(g.recv(8))
    g.send(10311, b"\0" * 12)
    decode(g.recv(1))
    g.send(10200, bytes.fromhex("cc33b12200000000"))
    g.send(10056, bytes.fromhex("00000000010000000000000000000000a00f00005989cf060000000094fc12009be47f00"))
    g.send(10056, bytes.fromhex("00000000010000000000000001000000be0f00005989cf060000000094fc12009be47f00"))
    decode(g.recv(1))
    g.send(10007, struct.pack("<I", 0x21448) + code + b"\0")
    g.send(10200, bytes.fromhex("e18fcf0600000000"))
    g.send(10202, bytes.fromhex("48140000000000000be77f00"))
    decode(g.recv(1.5))
    g.send(10357, b"\0" * 4)
    decode(g.recv(3))
    me["in_world"] = True
    acts = a.actions or []
    for kind, value in acts:
        if kind == "npc":
            g.send(10067, struct.pack("<I", int(value)))
            decode(g.recv(2))
        elif kind == "func":
            npc, dialog, sub = (int(x) for x in value.split(","))
            g.send(10069, struct.pack("<Iiii", npc, dialog, dialog, sub) + struct.pack("<18i", *([-1] * 18)))
            decode(g.recv(2.5))
        elif kind == "kill":
            target = int(value)
            for _ in range(80):
                m = R["monsters"].get(target)
                if m is None:
                    decode(g.recv(1))
                    if target not in R["monsters"]:
                        break
                    continue
                if math.hypot(m["x"] - me["x"], m["z"] - me["z"]) > 2.0:
                    g.send(10194, struct.pack("<Ifff", 0, m["x"], m["z"], 0.0))
                    me["x"], me["z"] = m["x"], m["z"]
                    decode(g.recv(0.6))
                g.send(10026, struct.pack("<IfffI", me["obj"], me["x"], 0.0, me["z"], target) + b"\0" * 8)
                if target in decode(g.recv(1.3)):
                    decode(g.recv(2))
                    break
        elif kind == "hold":
            end = time.time() + float(value)
            while time.time() < end:
                g.send(10015, b"")
                decode(g.recv(min(5, max(0.5, end - time.time()))))
    g.s.close()
    R["monsters"] = {str(k): v for k, v in R["monsters"].items()}
    R["bag"] = {str(k): v for k, v in R["bag"].items() if v}
    print("REPORT " + json.dumps(R), flush=True)


def main():
    ap = argparse.ArgumentParser()
    sp = ap.add_subparsers(dest="cmd", required=True)
    for name in ("api", "game", "gift", "cleanup"):
        p = sp.add_parser(name)
        p.add_argument("--editor", default="http://127.0.0.1:58816")
        p.add_argument("--char", default="Worldhero")
        p.add_argument("--account", default="worldtest1")
        p.add_argument("--password", default="World 2026!")
        p.add_argument("--login", default="127.0.0.1:60699", help="login HOST:PORT as seen from where the probe runs")
        p.add_argument("--gwcrypt", default=os.path.expanduser("~/gwed/env-world/gwcrypt"))
        p.add_argument("--probe", default="", help="command prefix that runs THIS script elsewhere, e.g. 'ssh vps python3 -I .../test_world.py'")
        p.add_argument("--restart", default="", help="shell command that restarts the TEST instance (game)")
        p.add_argument("--logcmd", default="", help="shell command that prints the test instance's server log (game)")
        p.add_argument("--reborn-probe", default="", help="command that runs reborn_probe.py where the login port answers (gift)")
        p.add_argument("--expect-identity-undo", action="store_true", help="the framework restores GENERATED ALWAYS ids")
    p = sp.add_parser("probe")
    p.add_argument("account"), p.add_argument("password"), p.add_argument("role"), p.add_argument("login")
    p.add_argument("--create", action="store_true")
    p.add_argument("--gwcrypt", default=os.path.expanduser("~/gwed/env-world/gwcrypt"))
    p.add_argument("--json", default="-")
    p.add_argument("--npc", action="append", dest="actions", type=lambda v: ("npc", v))
    p.add_argument("--func", action="append", dest="actions", type=lambda v: ("func", v))
    p.add_argument("--kill-oid", action="append", dest="actions", type=lambda v: ("kill", v))
    p.add_argument("--hold", action="append", dest="actions", type=lambda v: ("hold", v))
    a = ap.parse_args()
    if a.cmd == "probe" and not hasattr(a, "char"):
        a.char = a.role
    fn = {"api": cmd_api, "game": cmd_game, "gift": cmd_gift, "probe": cmd_probe, "cleanup": cmd_cleanup}[a.cmd]
    if a.cmd in ("api", "game"):
        try:
            fn(a)
        except SystemExit as e:
            if e.code:
                print("-- a step failed: undoing this module's changes on the test copy", flush=True)
                cmd_cleanup(a)
            raise
        return
    fn(a)


if __name__ == "__main__":
    main()
