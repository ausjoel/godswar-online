#!/usr/bin/env python3
"""tests/test_pets.py - proves the Pets & mounts editor (modules/pets.py) against YOUR OWN test copy and instance.

Run from the Mac (it needs the tunnelled editor and the test instance's login port):

    python3 -I tests/test_pets.py --editor http://127.0.0.1:58813 --login YOUR_SERVER_IP:60399 \
        --account pettest1 --password 'pets-test-77' --role Pethero --phase offline   [--json out.json]

Phases (each ends with "ALL OK" or stops at the first failure):
  offline       every write route on a logged-out character (pet add / edit of every editable field / aptitude /
                skills: learn, same-family replace, forget + compaction / shed / mounts: bag, ride, gear,
                quality, unequip, delete), the refusals (wire ranges 400, read-only 403, stale revision 409,
                species-only skill 400), then a headless client logs in and the test DECODES what the server sent
                (10237 owned pets: every field the editor wrote; 10019: the riding slot and its quality), then Undo of
                every change, newest first, and a second login shows the original values.
  stock-online  (stock instance) a headless client stays in the world: a pet edit and a mount-to-bag are refused 423
                with "Live edits arrive with the round-2 server update"; nothing is written.
  hold          only log the headless client in and stay --hold seconds (to look at the online page in a browser).
  live          (round2 instance) a headless client stays in the world: a mount given to the bag and removed again
                go LIVE (the client receives the 10033 bag pages), a pet edit is refused 423 (no live pet operation),
                then Kick logs the player out and the same pet edit goes to the database; the next login shows it.

The headless client is the retail login sequence of reborn_probe.py (gwcrypt.py from --gwcrypt), with a complete
10237 decoder (PacketBuilder.Pets.cs WriteOwnedPetRecord). It never opens a game window. Refuses the live database.
"""
import argparse
import hashlib
import json
import os
import re
import socket
import struct
import sys
import threading
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58813")
ap.add_argument("--login", required=True, help="HOST:PORT of the test instance's login server")
ap.add_argument("--account", default="pettest1"), ap.add_argument("--password", default="pets-test-77")
ap.add_argument("--role", default="Pethero")
ap.add_argument("--phase", choices=["offline", "stock-online", "live", "hold"], default="offline")
ap.add_argument("--hold", type=float, default=120, help="--phase hold: stay in the world this long (for looking at the page)")
ap.add_argument("--gwcrypt", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))
ap.add_argument("--json", default=None)
a = ap.parse_args()
sys.path.insert(0, os.path.abspath(a.gwcrypt))
from gwcrypt import Stream  # noqa: E402

RESULT = {"phase": a.phase, "steps": []}


def step(msg, cond, detail=""):
    print(("ok   " if cond else "FAIL ") + msg + ((" - " + str(detail)) if detail != "" else ""), flush=True)
    RESULT["steps"].append({"ok": bool(cond), "step": msg, "detail": detail if isinstance(detail, (str, int, float, list, dict)) else str(detail)})
    if not cond:
        save()
        sys.exit(1)


def save():
    if a.json:
        with open(a.json, "w") as f:
            json.dump(RESULT, f, indent=1, default=str)


# ----------------------------------------------------------------------------------------------- editor API
html = urllib.request.urlopen(a.editor + "/", timeout=60).read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)


def api(path, body=None, timeout=180):
    req = urllib.request.Request(a.editor + path, data=json.dumps(body).encode() if body is not None else None,
                                 method="POST" if body is not None else "GET",
                                 headers={"Content-Type": "application/json", "X-CSRF-Token": CSRF})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"null")
        except ValueError:
            return e.code, {"error": "HTTP %s" % e.code}


def sheet(cid):
    st, s = api("/api/m/pets/sheet/%d" % cid)
    assert st == 200, s
    return s


# ----------------------------------------------------------------------------------------------- headless client
KEY = b"435kfa3fjaf03fakf3fa3220fasjf3we322f2fjaf92f2fsf92fl1h1535rb7nw4"
ENTER_SLOTS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 18, 19, 20]


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


def pad(b, n):
    return b[:n] + b"\0" * (n - len(b[:n]))


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def u16(b, o):
    return struct.unpack_from("<H", b, o)[0]


class Conn:
    def __init__(self, host, port):
        self.s = socket.create_connection((host, port), timeout=10)
        self.enc, self.dec, self.buf = Stream(), Stream(), b""
        self.lock = threading.Lock()

    def send(self, t, body):
        with self.lock:
            self.s.sendall(self.enc.encrypt(struct.pack("<HH", 4 + len(body), t) + body))

    def recv(self, timeout=1.5):
        out = []
        self.s.settimeout(0.3)
        end = time.time() + timeout
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


def item_record(b, o):
    rid = u32(b, o)
    if rid in (0, 0xFFFFFFFF):
        return None
    return {"id": rid, "quality": b[o + 24], "grade": b[o + 25], "bound": b[o + 26], "stack": b[o + 27]}


def decode_pet(r):
    """PacketBuilder.Pets.cs WriteOwnedPetRecord (offsets inside one 0xA8 record)."""
    nsk = r[0x31]
    return {"id": u32(r, 0), "name": r[4:36].split(b"\0")[0].decode(errors="replace"), "species": r[0x24], "aptitude": r[0x25],
            "food": r[0x26], "sex": r[0x27], "level": r[0x28], "opened_cells": r[0x2B], "available_cells": r[0x2C],
            "satiety": r[0x2D], "amity": r[0x2E], "carried": r[0x30], "skills": [u16(r, 0x32 + 2 * j) for j in range(nsk)],
            "lifetime": u16(r, 0x4A), "max_lifetime": u16(r, 0x4C), "exp": u32(r, 0x60), "next_exp": u32(r, 0x64),
            "talent_mask": u32(r, 0x68), "basic_x100": [u32(r, 0x6C + 4 * j) for j in range(6)],
            "added_x100": [u32(r, 0x84 + 4 * j) for j in range(6)], "rank_x100": u16(r, 0x9C),
            "rebirths_total": r[0x9F], "rebirths_done": r[0xA0], "soul_stage": r[0xA1], "merges": u16(r, 0xA2), "bound": r[0xA4]}


class Client:
    """Log in, enter the world, decode; .hold() pumps in a thread until .stop()."""

    def __init__(self, account, password, role, login, create=False):
        self.rep = {"pets": None, "equipment": {}, "kitbag": {}, "kitbag_changes": [], "messages": {}, "closed": False}
        host, port = login.rsplit(":", 1)
        acct, pw, rl = account.encode(), password.encode(), role.encode()
        c = Conn(host, int(port))
        c.send(1, pad(shift_name(acct), 32) + hashlib.md5(pw).hexdigest().encode() + b"\0" * 68 + struct.pack("<I", 0x3e9774))
        r = []
        for _ in range(12):           # the verifier is PBKDF2 x 600,000: a few seconds on the VPS
            r += c.recv(1)
            if r:
                r += c.recv(1)
                break
        if not r or r[0][0] != 3 or r[0][2][:2] != b"\x01\x00":
            raise RuntimeError("login rejected: %r" % (r[:2],))
        realms = [m for m in r if m[0] == 10008]
        c.send(4, pad(acct, 32) + bytes([realms[0][2][36] if realms else 1]) + bytes.fromhex("adcf0600000000"))
        c.recv(2)
        c.send(6, pad(acct, 32) + bytes.fromhex("402cee11b8de1200408a82112800000007000000000000003032363038303333343931313234303733343732000000000c38a708149fef11c61c40001038a7080f000000149fef110b00000001000000"))
        rd = [m for m in c.recv(2) if m[0] == 10001]
        if not rd:
            raise RuntimeError("no redirect")
        b = rd[0][2]
        ghost, gport, code = b[1:33].split(b"\0")[0].decode(), u32(b, 36), b[41:66]
        c.s.close()
        g = self.g = Conn(ghost, gport)
        g.send(10000, pad(acct, 32) + code + b"\x01")
        r = g.recv(2)
        roles = [m for m in r if m[0] == 10002]
        if create or (roles and roles[0][1] == 500):
            g.send(10003, pad(rl, 32) + bytes.fromhex("0100000035000000" + "00" * 32 + "8085183c"))
            g.recv(2)
        msgs = []
        g.send(10006, pad(rl, 32) + code + b"\0")
        msgs += g.recv(3)
        g.send(10311, b"\0" * 12)
        msgs += g.recv(1)
        g.send(10200, bytes.fromhex("cc33b12200000000"))
        g.send(10056, bytes.fromhex("00000000010000000000000000000000a00f00005989cf060000000094fc12009be47f00"))
        g.send(10056, bytes.fromhex("00000000010000000000000001000000be0f00005989cf060000000094fc12009be47f00"))
        msgs += g.recv(1)
        g.send(10007, struct.pack("<I", 0x21448) + code + b"\0")
        g.send(10200, bytes.fromhex("e18fcf0600000000"))
        g.send(10202, bytes.fromhex("48140000000000000be77f00"))
        msgs += g.recv(1.5)
        g.send(10357, b"\0" * 4)
        msgs += g.recv(3)
        self.decode(msgs, "login")
        self.login_bag = dict(self.rep["kitbag"])
        self._run = False

    def decode(self, msgs, phase):
        for t, size, body in msgs:
            if t == "CLOSED":
                self.rep["closed"] = True
                continue
            self.rep["messages"][str(t)] = self.rep["messages"].get(str(t), 0) + 1
            if t == 10019 and len(body) > 100:
                mask = u32(body, 44)
                slots = [s for s in ENTER_SLOTS if mask & (1 << s)]
                for i, s in enumerate(slots):
                    rec = item_record(body, 100 + 72 * i)
                    if rec:
                        self.rep["equipment"][str(s)] = rec
            elif t == 10033 and len(body) >= 20:
                page, first = body[12], body[13]
                for k in range(12):
                    o = 20 + 72 * k
                    if o + 72 > len(body):
                        break
                    rec = item_record(body, o)
                    key = str(page * 24 + first + k)
                    was = (self.rep["kitbag"].get(key) or {}).get("id")
                    if rec:
                        self.rep["kitbag"][key] = rec
                    else:
                        self.rep["kitbag"].pop(key, None)
                    if phase != "login" and was != (rec or {}).get("id"):
                        self.rep["kitbag_changes"].append([int(key), was, (rec or {}).get("id"), (rec or {}).get("quality")])
            elif t == 10237 and len(body) >= 4:
                cells, cnt = body[0], body[1]
                pets = []
                for k in range(cnt):
                    rr = body[4 + 0xA8 * k: 4 + 0xA8 * (k + 1)]
                    if len(rr) == 0xA8:
                        pets.append(decode_pet(rr))
                self.rep["pets"] = {"shed_cells": cells, "count": cnt, "pets": pets}

    def _loop(self, seconds):
        end = time.time() + seconds
        nxt = 0
        while self._run and time.time() < end:
            if time.time() >= nxt:
                try:
                    self.g.send(10015, b"")
                except OSError:
                    break
                nxt = time.time() + 4
            m = self.g.recv(1)
            self.decode(m, "hold")
            if self.rep["closed"]:
                break

    def hold(self, seconds=600):
        self._run = True
        self.th = threading.Thread(target=self._loop, args=(seconds,), daemon=True)
        self.th.start()

    def stop(self):
        self._run = False
        if getattr(self, "th", None):
            self.th.join(10)
        try:
            self.g.s.close()
        except OSError:
            pass


def login_once(create=False, settle=2.0):
    c = Client(a.account, a.password, a.role, a.login, create=create)
    c._run = True
    c._loop(settle)
    c.stop()
    return c.rep


def wait_offline(cid, secs=40):
    for _ in range(secs):
        st, on = api("/api/m/online/%d" % cid)
        if not on.get("online"):
            return True
        time.sleep(1)
    return False


def until(fn, what, tries=40):
    for _ in range(tries):
        st, r = fn()
        if st == 200:
            return st, r
        if st not in (409, 423):
            return st, r
        time.sleep(1)
    return st, r


# ----------------------------------------------------------------------------------------------- set-up
api("/api/m/capabilities?refresh=1")         # the editor caches the probe for 60 s; the instance may have just changed build
st, s = api("/api/m/status")
step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
step("not the live database", s["reborn"].get("database") != "godswar" and not s["reborn"].get("live"))
caps = s.get("capabilities") or {}
RESULT["database"], RESULT["capabilities"] = s["reborn"].get("database"), s.get("live_operations")
st, r = api("/api/m/accounts/create", {"username": a.account, "password": a.password})
step("test account %s ready" % a.account, st == 200 or "taken" in json.dumps(r) or "exists" in json.dumps(r), r.get("error", "created"))
st, rows = api("/api/m/characters?q=" + a.role)
if not any(x["name"] == a.role for x in rows):
    rep = login_once(create=True)
    st, rows = api("/api/m/characters?q=" + a.role)
cid = next(x["id"] for x in rows if x["name"] == a.role)
step("character %s is #%d" % (a.role, cid), True)
step("%s is offline before the test" % a.role, a.phase == "hold" or wait_offline(cid))
s0 = sheet(cid)
if s0["character"]["level"] < 45 and a.phase == "offline":
    st, r = api("/api/m/edit/%d/fields" % cid, {"values": {"fighter_job_lv": 45}, "label": "test set-up: level 45 so mounts can be ridden"})
    step("set-up: level 45 (shared save path, database)", st == 200 and r.get("mode") == "database", r.get("error", ""))

# ================================================================================================ OFFLINE
if a.phase == "offline":
    changes = []

    def w(path, body, what, expect=200):
        st, r = api(path, body)
        ok = st == expect
        step(what, ok, (r.get("change") or {}).get("action") if ok and st == 200 else (r.get("error") or json.dumps(r))[:300])
        if st == 200 and (r.get("change") or {}).get("id"):
            changes.append(r["change"]["id"])
        return r

    base_pets = len(s0["pets"])
    if s0["character"]["owned_pets"] >= s0["character"]["pet_shed_capacity"]:
        w("/api/m/pets/shed/%d" % cid, {"capacity": min(8, s0["character"]["pet_shed_capacity"] + 1), "expected": s0["character"]["pet_shed_capacity"]}, "set-up: one more shed cell")
    r = w("/api/m/pets/pet/%d/add" % cid, {"species": 10, "aptitude": 10, "sex": 1, "level": 30, "name": "Tdog"}, "add pet Tdog (Loyal Dog, Smart, Lv 30)")
    s1 = sheet(cid)
    p = max(s1["pets"], key=lambda x: x["id"])
    pid = p["id"]
    step("new pet row: hatch values (Smart -> talent mask 26, 2 cells, starter Guard I #1205 in cell 1, savvy baseline 187)",
         p["talent_mask"] == 26 and p["opened_skill_slots"] == 2 and [x["skill_id"] for x in p["skills"]] == [1205]
         and int(p["initial_savvy_baseline_total"]) == 187, {k: p[k] for k in ("talent_mask", "opened_skill_slots", "initial_savvy_baseline_total")})

    edit = {"name": "Tdog2", "sex": 0, "satiety": 55, "amity": 66, "remaining_lifetime": 777, "rank": "12.34", "completed_pet_merges": 3,
            "completed_rebirths": 2, "rebirths_remaining": 5, "soul_contract_stage": 3, "experience": 12345, "bound": True,
            "available_stat_points": 7, "maximum_energy": 120, "current_energy": 110, "opened_skill_slots": 4, "available_skill_slots": 5,
            "level": 40, "presence": {"carried": True, "summoned": True, "contributes": False}}
    sv = {str(x["stat_code"]): x for x in p["stats"]}
    edit["savvy"] = {"1": str(round(float(sv["1"]["initial_savvy"]) + 5, 2))}
    edit["growth"] = {"1": "0.02", "2": "0.01"}         # total 0.07 stays inside Weak 0.01-0.10
    w("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "revision": p["revision"], "fields": edit}, "edit every pet field in one save (name, sex, bars, rank, rebirths, soul, EXP, bound, cells, level, savvy, growth, carried+summoned)")
    p2 = next(x for x in sheet(cid)["pets"] if x["id"] == pid)
    want = {"name": "Tdog2", "sex": 0, "satiety": 55, "amity": 66, "remaining_lifetime": 777, "completed_pet_merges": 3, "completed_rebirths": 2,
            "rebirths_remaining": 5, "soul_contract_stage": 3, "experience": 12345, "bound": True, "available_stat_points": 7,
            "maximum_energy": 120, "current_energy": 110, "opened_skill_slots": 4, "available_skill_slots": 5, "level": 40,
            "is_carried": True, "is_summoned": True}
    bad = {k: (p2[k], v) for k, v in want.items() if p2[k] != v}
    step("database rows hold every edited value (rank %s)" % p2["rank"], not bad and str(p2["rank"]).startswith("12.34"), bad)
    added = [x for x in p2["stats"] if x["stat_code"] == 1][0]
    step("added savvy recomputed for level 40: (0.02 + 0) x 40 = 0.80", str(added["added_savvy"]).rstrip("0").rstrip(".") in ("0.8",), added["added_savvy"])

    # refusals
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "fields": {"satiety": 150}})
    step("satiety 150 refused (the client shows 0-100)", st == 400, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "fields": {"completed_rebirths": 200, "rebirths_remaining": 100}})
    step("rebirths done + left > 255 refused (one byte)", st == 400, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "fields": {"talent_mask": 31}})
    step("talent mask refused read-only (403, follows the aptitude)", st == 403, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "fields": {"species_id": 3}})
    step("species refused read-only (403)", st == 403, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "revision": p["revision"], "fields": {"amity": 1}})
    step("stale revision refused (409: changed since the page read it)", st == 409, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "fields": {"growth": {"1": "5"}}})
    step("growth total outside the bracket refused (server start-up check)", st == 400, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 405})
    step("Life Totem I (Rock Elves only) refused on a Loyal Dog", st == 400 and "only" in r.get("error", ""), r.get("error"))

    # skills
    def skills_now():
        return [(x["slot_index"], x["skill_id"], x["skill_rank"]) for x in next(y for y in sheet(cid)["pets"] if y["id"] == pid)["skills"]]
    w("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 618}, "learn Sharp Claw III by a rank-step id (#618)")
    step("stored as the tier's canonical id #616 rank 3 in cell 2", skills_now() == [(0, 1205, 1), (1, 616, 3)], skills_now())
    w("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 642}, "learn Sharp Claw V (same family)")
    step("same family replaced in place: cell 2 = #642 rank 5", skills_now() == [(0, 1205, 1), (1, 642, 5)], skills_now())
    w("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 400}, "learn Vital Boost I")
    w("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 500}, "learn Meditate I")
    step("cells: Guard, Sharp Claw V, Vital Boost, Meditate", skills_now() == [(0, 1205, 1), (1, 642, 5), (2, 400, 1), (3, 500, 1)], skills_now())
    w("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "remove", "cell": 2}, "forget Vital Boost (cell 3)")
    step("later skills moved down (no gap): Guard, Sharp Claw, Meditate", skills_now() == [(0, 1205, 1), (1, 642, 5), (2, 500, 1)], skills_now())
    st, r = api("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 700})
    if st == 200:
        changes.append(r["change"]["id"])
    st, r = api("/api/m/pets/pet/%d/skill" % cid, {"pet": pid, "action": "set", "skill": 409})
    step("a fifth skill with 4 opened cells refused", st == 400 and "opened" in r.get("error", ""), r.get("error"))

    # aptitude (talents follow)
    p3 = next(x for x in sheet(cid)["pets"] if x["id"] == pid)
    w("/api/m/pets/pet/%d/edit" % cid, {"pet": pid, "revision": p3["revision"], "fields": {"aptitude": 14}}, "nature -> Godly (14)")
    p4 = next(x for x in sheet(cid)["pets"] if x["id"] == pid)
    step("Godly: talent mask 31, merge talent, basic savvy baseline 3749", p4["talent_mask"] == 31 and p4["has_owner_merge_talent"]
         and int(p4["initial_savvy_baseline_total"]) == 3749, {k: p4[k] for k in ("talent_mask", "initial_savvy_baseline_total")})

    # shed (shared save path)
    cap = sheet(cid)["character"]["pet_shed_capacity"]
    r = w("/api/m/pets/shed/%d" % cid, {"capacity": 6, "expected": cap}, "pet shed cells -> 6 (shared save path)")
    step("shed saved to the database (offline)", r.get("mode") == "database")
    st, r = api("/api/m/pets/shed/%d" % cid, {"capacity": 5, "expected": cap})
    step("stale shed value refused (409)", st == 409, r.get("error"))

    # mounts
    r = w("/api/m/pets/mount/%d/add" % cid, {"item": 14220, "quality": 10, "grade": 3, "bound": 0, "to": "bag"}, "Greek Steed Q10 G3 to the bag")
    step("bag add went to the database (offline)", r.get("mode") == "database")
    ms = sheet(cid)["mounts"]
    steed = next(m for m in ms["items"] if m["prop_id"] == 14220 and m["location"] == 1)
    w("/api/m/pets/mount/%d/ride" % cid, {"row": steed["row"], "expected": steed["expected"]}, "ride the Greek Steed (bag -> riding slot)")
    w("/api/m/pets/mount/%d/add" % cid, {"item": 14500, "quality": 2, "grade": 1, "to": "gear"}, "Boorish Coronet in the Coronet slot")
    st, r = api("/api/m/pets/mount/%d/add" % cid, {"item": 14500, "to": "ride"})
    step("mount gear refused in the riding slot", st == 400, r.get("error"))
    st, r = api("/api/m/pets/mount/%d/add" % cid, {"item": 1001, "to": "bag"})
    step("a weapon refused as a mount", st == 400, r.get("error"))
    ms = sheet(cid)["mounts"]
    w("/api/m/pets/mount/%d/set" % cid, {"row": ms["ride"]["row"], "quality": 15, "grade": 5, "expected": ms["ride"]["expected"]}, "riding mount -> Q15 G5")
    st, r = api("/api/m/pets/mount/%d/unequip" % cid, {"row": ms["ride"]["row"]})
    step("unequip the mount while gear is on refused (gear needs a mount)", st == 400, r.get("error"))
    ms = sheet(cid)["mounts"]
    step("riding slot: Greek Steed Q15 G5; Coronet: Boorish Coronet", ms["ride"]["prop_id"] == 14220 and ms["ride"]["quality"] == 15 and ms["ride"]["grade"] == 5
         and (ms["gear"]["15"] or {}).get("prop_id") == 14500, {"ride": ms["ride"]["where"], "gear": (ms["gear"]["15"] or {}).get("name")})

    # the server loads it: headless login, decode 10237 + 10019
    rep = login_once()
    RESULT["login_after_edits"] = rep
    pets = (rep.get("pets") or {}).get("pets") or []
    lp = next((x for x in pets if x["id"] == pid), None)
    step("client got 10237 with the edited pet", lp is not None, rep.get("pets"))
    exp = {"name": "Tdog2", "sex": 0, "level": 40, "aptitude": 14, "satiety": 55, "amity": 66, "lifetime": 777, "exp": 12345,
           "rank_x100": 1234, "rebirths_done": 2, "rebirths_total": 7, "soul_stage": 3, "merges": 3, "bound": 1, "carried": 1,
           "opened_cells": 4, "available_cells": 5, "talent_mask": 31, "skills": [1205, 642, 500, 700]}
    bad = {k: (lp.get(k), v) for k, v in exp.items() if lp.get(k) != v}
    step("10237 carries every edited value (name, sex, level, nature, satiety, amity, lifetime, EXP, rank, rebirths, soul, merges, bound, carried, cells, talents, skills)",
         not bad, bad or {k: lp[k] for k in exp})
    step("10237 shed cells = 6", rep["pets"]["shed_cells"] == 6, rep["pets"]["shed_cells"])
    eq = rep.get("equipment") or {}
    step("10019 riding slot 20 = Greek Steed 14220 Q15 G5, slot 15 = Boorish Coronet 14500", (eq.get("20") or {}).get("id") == 14220
         and (eq.get("20") or {}).get("quality") == 15 and (eq.get("20") or {}).get("grade") == 5 and (eq.get("15") or {}).get("id") == 14500, eq)
    step("logged out again", wait_offline(cid))

    # Undo everything, newest first
    for chg in reversed(changes):
        st, r = api("/api/undo", {"change": chg})
        if st == 409:
            st, r = api("/api/undo", {"change": chg, "overwrite": True})
        res = (r.get("result") or {})
        step("undo %s" % chg, st == 200 and res.get("all_identical", True) is not False, (r.get("change") or {}).get("action") or r.get("error") or res)
    s9 = sheet(cid)
    step("after Undo: the pets and mounts are as before the test", len(s9["pets"]) == base_pets and not any(m["prop_id"] in (14220, 14500) for m in s9["mounts"]["items"]),
         {"pets": [x["name"] for x in s9["pets"]], "mounts": [m["name"] for m in s9["mounts"]["items"]]})
    rep2 = login_once()
    RESULT["login_after_undo"] = rep2
    step("next login: 10237 no longer has the test pet, slot 20 empty", not any(x["id"] == pid for x in ((rep2.get("pets") or {}).get("pets") or []))
         and "20" not in (rep2.get("equipment") or {}), {"pets": rep2.get("pets"), "equipment": rep2.get("equipment")})
    step("logged out again", wait_offline(cid))
    st, r = api("/api/reborn/selftest?format=json")
    RESULT["selftest"] = r if st == 200 else st

# ================================================================================================ HOLD (for the browser check)
if a.phase == "hold":
    c = Client(a.account, a.password, a.role, a.login)
    c.hold(a.hold)
    step("%s in the world for %d s" % (a.role, a.hold), True)
    c.th.join(a.hold + 10)
    c.stop()
    RESULT["hold"] = {"kitbag_changes": c.rep["kitbag_changes"], "pets": c.rep["pets"]}
    print("kitbag changes seen by the client [slot, was, now, quality]:", c.rep["kitbag_changes"])
    step("logged out", wait_offline(cid))

# ================================================================================================ ONLINE (stock)
if a.phase == "stock-online":
    step("stock server: no live capabilities", not caps.get("available"), caps)
    s1 = sheet(cid)
    if not s1["pets"]:
        st, r = api("/api/m/pets/pet/%d/add" % cid, {"species": 2, "aptitude": 6, "sex": 0, "level": 12, "name": "Stockpixie"})
        step("set-up: a pet to edit (offline, database)", st == 200, r.get("error", ""))
        s1 = sheet(cid)
    pet = s1["pets"][0]
    c = Client(a.account, a.password, a.role, a.login)
    c.hold(120)
    time.sleep(3)
    st, on = api("/api/m/online/%d" % cid)
    step("%s is online (headless client in the world)" % a.role, on.get("online"), on.get("reasons"))
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pet["id"], "fields": {"amity": 42}})
    step("pet edit on the online character refused 423 (round-2 message)", st == 423 and "round-2" in r.get("error", ""), r.get("error"))
    st, r = api("/api/m/pets/pet/%d/skill" % cid, {"pet": pet["id"], "action": "set", "skill": 400})
    step("pet skill on the online character refused 423", st == 423, r.get("error"))
    st, r = api("/api/m/pets/pet/%d/add" % cid, {"species": 1, "aptitude": 1})
    step("add pet on the online character refused 423", st == 423, r.get("error"))
    sh = sheet(cid)
    step("the page's sheet says online and no live pet / bag operation", sh["online"]["online"] and not sh["live"]["bag_add"] and not sh["live"]["pets"], sh["live"])
    st, r = api("/api/m/pets/mount/%d/add" % cid, {"item": 14220, "quality": 1, "to": "bag"})
    step("mount to bag on the online character refused 423 (round-2 message)", st == 423 and "round-2" in r.get("error", ""), r.get("error"))
    st, r = api("/api/m/pets/shed/%d" % cid, {"capacity": 7})
    step("shed cells on the online character refused 423", st == 423, r.get("error"))
    s2 = sheet(cid)
    step("nothing written", json.dumps(s2["pets"], sort_keys=True, default=str) == json.dumps(s1["pets"], sort_keys=True, default=str)
         and len(s2["mounts"]["items"]) == len(s1["mounts"]["items"]))
    c.stop()
    step("logged out", wait_offline(cid))

# ================================================================================================ LIVE (round2)
if a.phase == "live":
    step("round2 capabilities: bag add / remove / kick", caps.get("available") and {"character.give_item", "character.item_delete", "character.kick"}
         <= set(s.get("live_operations") or []), s.get("live_operations"))
    s1 = sheet(cid)
    if not s1["pets"]:
        st, r = api("/api/m/pets/pet/%d/add" % cid, {"species": 12, "aptitude": 8, "sex": 1, "level": 20, "name": "Livedrake"})
        step("set-up: a pet to edit (offline)", st == 200, r.get("error", ""))
        s1 = sheet(cid)
    pet = s1["pets"][0]
    c = Client(a.account, a.password, a.role, a.login)
    c.hold(300)
    time.sleep(3)
    st, on = api("/api/m/online/%d" % cid)
    step("%s is online (headless client in the world)" % a.role, on.get("online"), on.get("reasons"))
    st, r = until(lambda: api("/api/m/pets/mount/%d/add" % cid, {"item": 14220, "quality": 12, "grade": 4, "bound": 0, "to": "bag"}), "give")
    step("Greek Steed Q12 given to the bag LIVE", st == 200 and r.get("mode") == "live", json.dumps(r.get("result") or r)[:300])
    time.sleep(3)
    got = [x for x in c.rep["kitbag_changes"] if x[2] == 14220]
    step("the client received the 10033 bag page with the Greek Steed Q12", bool(got) and got[-1][3] == 12, c.rep["kitbag_changes"])
    slot = got[-1][0]
    ms = sheet(cid)["mounts"]
    row = next((m for m in ms["items"] if m["prop_id"] == 14220 and m["location"] == 1 and m["slot"] == slot), None)
    step("the editor sees it in bag slot %d (saved by the server)" % slot, row is not None, [m["where"] for m in ms["items"]])
    st, r = until(lambda: api("/api/m/pets/mount/%d/delete" % cid, {"row": row["row"], "expected": row["expected"]}), "remove")
    step("deleted from the bag LIVE", st == 200 and r.get("mode") == "live", json.dumps(r.get("result") or r)[:300])
    time.sleep(3)
    step("the client received the bag page without it", [slot, 14220, None, None] in c.rep["kitbag_changes"], c.rep["kitbag_changes"])
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pet["id"], "fields": {"amity": 42}})
    step("pet edit while online refused 423 (no live pet operation; says log out / kick)", st == 423 and "log" in r.get("error", "").lower(), r.get("error"))
    st, r = api("/api/m/pets/mount/%d/add" % cid, {"item": 14220, "to": "ride"})
    step("riding-slot edit while online refused 423 (equipment has no live operation)", st == 423, r.get("error"))
    st, r = api("/api/m/kick/%d" % cid, {"reason": "test_pets kick to edit everything"})
    step("Kick to edit everything", st == 200, r.get("result"))
    c.stop()
    step("offline after the kick", wait_offline(cid))
    p0 = next(x for x in sheet(cid)["pets"] if x["id"] == pet["id"])
    st, r = api("/api/m/pets/pet/%d/edit" % cid, {"pet": pet["id"], "revision": p0["revision"], "fields": {"amity": 42, "satiety": 24}})
    step("the same pet edit now goes to the database", st == 200, (r.get("change") or {}).get("action") or r.get("error"))
    chg = r["change"]["id"]
    rep = login_once()
    lp = next((x for x in (rep.get("pets") or {}).get("pets") or [] if x["id"] == pet["id"]), {})
    step("next login: 10237 amity 42, satiety 24", lp.get("amity") == 42 and lp.get("satiety") == 24, lp)
    step("logged out", wait_offline(cid))
    st, r = api("/api/undo", {"change": chg})
    if st == 409:
        st, r = api("/api/undo", {"change": chg, "overwrite": True})
    step("Undo of that edit", st == 200, (r.get("result") or {}))

print("ALL OK")
RESULT["all_ok"] = True
save()
