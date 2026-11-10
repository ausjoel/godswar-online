#!/usr/bin/env python3
"""tests/test_guild.py - proves the Guild editor (modules/guild.py) end to end on a TEST copy:

  * every write route (create, edit, members add / edit / remove, leader hand-over, buildings, altar offerings, the
    guild EXP boost, repair, disband, faction-area control) writes the rows, and Undo puts them back;
  * the game server LOADS what was written: a headless client logs in and decodes what the server sends about its
    guild - 10138 guild base info (name, level, Silver, Gold, gems, member limit, proclamation, buildings), 10143
    member list (names, duties, Silver / Gold contributions), 10166 status (the character panel's guild duty and
    contribution points), 10167 statuses (the [Guild] Double EXP status 1007);
  * refusals: a read-only field (403), a stale value (409), a broken rule (400), a member online (423). With the
    round-2 server the 423 offers Kick and, after the kick, the same edit saves and the client sees it at its next
    login; with the stock server the 423 says live edits arrive with the round-2 update.

Run from the Mac (it reaches both the tunnelled editor and the test instance's login port):

    python3 -I tests/test_guild.py --editor http://127.0.0.1:58815 --login YOUR_SERVER_IP:60599 [--build round2|stock]
        [--gwcrypt GodsWar-2.51/Polish/tools] [--json out.json]

It makes its own accounts and characters (names start with "Gt"), so run it on a test copy only; it refuses the
live database. Prints each step and ends with "ALL OK" or the first failure.
"""
import argparse
import hashlib
import json
import os
import random
import re
import socket
import string
import struct
import sys
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))      # gwadmin-web/
ap = argparse.ArgumentParser()
ap.add_argument("--editor", default="http://127.0.0.1:58815")
ap.add_argument("--login", required=True, help="HOST:PORT of the test instance's login server")
ap.add_argument("--build", choices=["round2", "stock", "auto"], default="auto")
ap.add_argument("--gwcrypt", default=os.path.join(HERE, "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))
ap.add_argument("--json", default=None, help="write every decoded login here")
ap.add_argument("--quick", action="store_true", help="only the online / offline routing part")
ap.add_argument("--reuse", default=None, metavar="TAG", help="reuse the accounts / characters of an earlier run (gt<TAG>l/m/x, Gt<Tag>L/M/X) instead of making new ones; their guilds are disbanded first")
a = ap.parse_args()
sys.path.insert(0, os.path.abspath(a.gwcrypt))
from gwcrypt import Stream  # noqa: E402

REPORT = {"steps": [], "logins": []}

# ------------------------------------------------------------------------------------------- the editor API
html = urllib.request.urlopen(a.editor + "/").read().decode()
CSRF = re.search(r'name="csrf" content="([^"]+)"', html).group(1)


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
    line = ("ok   " if cond else "FAIL ") + msg + ((" - " + detail) if detail else "")
    print(line, flush=True)
    REPORT["steps"].append(line)
    if not cond:
        save()
        sys.exit(1)


def save():
    if a.json:
        with open(a.json, "w") as f:
            json.dump(REPORT, f, indent=1, default=str)


def ok(st, r, what):
    step(what, st == 200, (r.get("change") or {}).get("output", "") if st == 200 else "HTTP %s %s" % (st, json.dumps(r)[:300]))
    return r


def undo(change_id, what):
    st, r = api("/api/undo", {"change": change_id})
    step("Undo: " + what, st == 200 and ((r.get("change") or {}).get("result") or {}).get("all_identical", True),
         (r.get("change") or {}).get("output", "") if st == 200 else "HTTP %s %s" % (st, json.dumps(r)[:400]))
    return r


# ------------------------------------------------------------------------------------------- headless game client
KEY = b"435kfa3fjaf03fakf3fa3220fasjf3we322f2fjaf92f2fsf92fl1h1535rb7nw4"


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


def cstr(b):
    return b.split(b"\0")[0].decode("ascii", "replace")


class Conn:
    def __init__(self, host, port):
        self.s = socket.create_connection((host, port), timeout=10)
        self.enc, self.dec, self.buf = Stream(), Stream(), b""

    def send(self, t, body):
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
                out.append(("CLOSED", 0, b""))
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

    def recv_for(self, want, timeout=10.0, tail=0.6):
        """read until a message of type `want` arrived (the login server hashes the password: PBKDF2, 600,000 rounds,
        which can take seconds on a busy VPS), then `tail` seconds more."""
        out, end = [], time.time() + timeout
        while time.time() < end:
            out += self.recv(0.5)
            if any(m[0] == want for m in out) or any(m[0] == "CLOSED" for m in out):
                out += self.recv(tail)
                break
        return out


def decode(msgs, rep):
    """The guild messages (PacketBuilder.Guild.cs; body offset = packet offset - 4) and the status frames."""
    for t, _, b in msgs:
        if t == "CLOSED":
            rep["closed"] = True
            continue
        rep["types"].append(t)
        if t == 10138 and len(b) >= 0x168:
            n = u32(b, 0x164)
            rep["guild"] = {"name": cstr(b[0:64]), "level": b[0x40], "gold": u32(b, 0x44), "proclamation": cstr(b[0x50:0x90]),
                            "silver": u32(b, 0x154), "bijou": u32(b, 0x158), "member_limit": u32(b, 0x15C),
                            "buildings": {u32(b, 0x168 + 8 * i): u32(b, 0x16C + 8 * i) for i in range(n) if 0x16C + 8 * i + 4 <= len(b)}}
        elif t == 10143 and len(b) >= 8:
            n = u32(b, 4)
            mem = []
            for i in range(n):
                r = b[8 + 52 * i: 8 + 52 * (i + 1)]
                if len(r) < 52:
                    break
                mem.append({"name": cstr(r[0:32]), "online": struct.unpack_from("<h", r, 0x20)[0] == 0, "duty": r[0x22],
                            "level": r[0x23], "profession": r[0x24], "gold": u32(r, 0x2C), "silver": u32(r, 0x30)})
            rep["members"] = mem
        elif t == 10166 and len(b) >= 140 and u32(b, 0) == 0x1448:
            rep["panel"] = {"duty": b[72], "contribution": u32(b, 76)}          # wire 76 / 80 (PlayerStatusUpdate.cs)
        elif t == 10167 and len(b) >= 330:
            n = struct.unpack_from("<H", b, 4)[0]
            rep.setdefault("statuses", set()).update(u32(b, 8 + 4 * k) for k in range(min(n, 40)))
        elif t == 3 and len(b) >= 2 and b[:2] != b"\x01\x00":
            rep["login_refused"] = True


def login(account, password, role, create=False, hold=0.0, until=None, open_window=False):
    """Log in, enter the world, decode, optionally stay `hold` seconds (sending 10179 'open the guild window' when
    open_window), then close. Returns the report. `until(rep)` -> True ends the hold early."""
    rep = {"role": role, "types": [], "guild": None, "members": None, "panel": None}
    host, port = a.login.rsplit(":", 1)
    acct, pw, rl = account.encode(), password.encode(), role.encode()
    c = Conn(host, int(port))
    c.send(1, pad(shift_name(acct), 32) + hashlib.md5(pw).hexdigest().encode() + b"\0" * 68 + struct.pack("<I", 0x3e9774))
    r = c.recv_for(3)
    if not r or r[0][0] != 3 or r[0][2][:2] != b"\x01\x00":
        rep["login_refused"] = True
        c.s.close()
        return rep
    realms = [m for m in r if m[0] == 10008]
    c.send(4, pad(acct, 32) + bytes([realms[0][2][36] if realms else 1]) + bytes.fromhex("adcf0600000000"))
    c.recv(2)
    c.send(6, pad(acct, 32) + bytes.fromhex("402cee11b8de1200408a82112800000007000000000000003032363038303333343931313234303733343732000000000c38a708149fef11c61c40001038a7080f000000149fef110b00000001000000"))
    rd = [m for m in c.recv_for(10001) if m[0] == 10001]
    if not rd:
        rep["error"] = "no redirect"
        return rep
    b = rd[0][2]
    ghost, gport, code = b[1:33].split(b"\0")[0].decode(), u32(b, 36), b[41:66]
    c.s.close()
    g = Conn(ghost, gport)
    g.send(10000, pad(acct, 32) + code + b"\x01")
    r = g.recv_for(10002, 8)
    roles = [m for m in r if m[0] == 10002]
    if create or (roles and roles[0][1] == 500):
        g.send(10003, pad(rl, 32) + bytes.fromhex("0100000035000000" + "00" * 32 + "8085183c"))
        g.recv(3)
    msgs = []
    g.send(10006, pad(rl, 32) + code + b"\0")
    msgs += g.recv_for(10019, 10)
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
    g.send(10357, b"\0" * 4)            # EnterUiReady: the server publishes the guild here (10138 + 10143 member list)
    msgs += g.recv_for(10143, 15, tail=1.5)
    decode(msgs, rep)
    end = time.time() + hold
    while time.time() < end and not rep.get("closed"):
        g.send(10015, b"")
        if open_window:
            g.send(10179, b"")          # ConsortiaInfoRequest: the guild window asks for its guild
        decode(g.recv(2), rep)
        if until and until(rep):
            break
    g.s.close()
    if "statuses" in rep:
        rep["statuses"] = sorted(rep["statuses"])
    REPORT["logins"].append(rep)
    save()
    return rep


def wait_offline(cid, what):
    for _ in range(40):
        st, on = api("/api/m/online/%d" % cid)
        if st == 200 and not on["online"]:
            return
        time.sleep(1)
    step(what + " logged out", False, json.dumps(on)[:300])


# ------------------------------------------------------------------------------------------- 0. environment
st, s = api("/api/m/status")
step("editor answers", st == 200, "database %s" % s["reborn"].get("database"))
step("not the live database", s["reborn"].get("database") != "godswar" and not s["reborn"].get("live"))
round2 = bool(s["capabilities"].get("available"))
if a.build != "auto":
    step("server build is %s" % a.build, round2 == (a.build == "round2"), "capabilities %s" % s["capabilities"])
kick = "character.kick" in (s.get("live_operations") or [])
print("     server build: %s (kick %s)" % ("round2" if round2 else "stock", "offered" if kick else "not offered"))
st, ov = api("/api/m/guild/overview")
step("guild overview", st == 200 and ov["writable"], "%d guilds, %d issues, %d building types" % (len(ov["guilds"]), len(ov["issues"]), len(ov["types"])))

# ------------------------------------------------------------------------------------------- 1. accounts + characters
tag = a.reuse or "".join(random.choice(string.ascii_lowercase) for _ in range(4))
PW = "Gt pass 42"
people = []
if a.reuse:     # a busy test VPS can fault the game server while new characters enter the world: reuse old ones
    st, rows = api("/api/m/guild/characters")
    for role in (("L",) if a.quick else ("L", "M", "X")):
        name = "Gt%s%s" % (tag.capitalize(), role)
        c = next((x for x in rows["characters"] if x["name"] == name), None)
        step("reuse %s" % name, c is not None)
        if c["guild_id"]:
            st, r = api("/api/m/guild/g/%d/disband" % c["guild_id"], {})
            step("clean start: disband %s's old guild #%s" % (name, c["guild_id"]), st in (200, 404), json.dumps(r)[:200])
            st, rows = api("/api/m/guild/characters")
        people.append({"account": "gt%s%s" % (tag, role.lower()), "name": name, "cid": c["id"]})
for k, role in enumerate(() if a.reuse else ("L",) if a.quick else ("L", "M", "X")):
    acc = "gt%s%s" % (tag, role.lower())
    name = "Gt%s%s" % (tag.capitalize(), role)
    st, r = api("/api/m/accounts/create", {"username": acc, "password": PW})
    step("account %s created (Accounts page API)" % acc, st == 200, (r.get("error") or ""))
    rep = login(acc, PW, name, create=True)
    step("headless client made character %s" % name, not rep.get("login_refused") and bool(rep["types"]),
         "messages %s" % sorted(set(rep["types"]))[:12])
    st, rows = api("/api/m/guild/characters")
    cid = next((c["id"] for c in rows["characters"] if c["name"] == name), None)
    step("%s is listed in the all-characters picker (#%s)" % (name, cid), cid is not None, "%d characters listed" % len(rows["characters"]))
    wait_offline(cid, name)
    people.append({"account": acc, "name": name, "cid": cid})
L, Mm, X = (people * 3)[:3] if a.quick else people

# ------------------------------------------------------------------------------------------- 2. online routing first
# a member online -> 423 (round2: Kick offered; stock: "arrives with the round-2 update"); offline -> database
GNAME = "Gt%sGuild" % tag.capitalize()
st, r = api("/api/m/guild/create", {"name": GNAME, "lord": L["cid"], "silver": 1000})
ok(st, r, "create guild %s, leader %s" % (GNAME, L["name"]))
gid = r["guild"]["id"]
create_change = r["change"]["id"]

held = {}


def hold_and_try():
    """L stays in the world; while there, the editor tries a guild edit."""
    import threading
    res = {}

    def run():
        res["rep"] = login(L["account"], PW, L["name"], hold=40, until=lambda rep: rep.get("closed"))
    t = threading.Thread(target=run)
    t.start()
    for _ in range(30):
        time.sleep(1)
        st, on = api("/api/m/online/%d" % L["cid"])
        if on.get("online"):
            break
    return t, res


t, res = hold_and_try()
st, g0 = api("/api/m/guild/g/%d" % gid)
step("the page shows %s online" % L["name"], L["name"] in g0.get("online", []), json.dumps(g0.get("online")))
st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"silver": 777777}, "expected": {"silver": 1000}})
on = (r or {}).get("online") or {}
step("guild edit while a member is online is refused (423)", st == 423, (r or {}).get("error", "")[:220])
if round2:
    step("round2: the refusal offers Kick for the online member", on.get("kick") is True and on.get("members", [{}])[0].get("cid") == L["cid"],
         json.dumps(on.get("members")))
    for _ in range(25):                 # still entering the world -> the server answers "offline" / "busy": retry
        st, k = api("/api/m/kick/%d" % L["cid"], {"reason": "guild editor test"})
        if st == 200 or not re.search(r"offline|busy", json.dumps(k)):
            break
        time.sleep(1)
    step("round2: Kick disconnects %s through the game server" % L["name"], st == 200, json.dumps(k)[:200])
    t.join(timeout=60)
    step("round2: the headless client was disconnected by the kick", res.get("rep", {}).get("closed") is True)
    wait_offline(L["cid"], L["name"])
    st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"silver": 777777}, "expected": {"silver": 1000}})
    ok(st, r, "round2: after the kick the same edit saves (guild Silver 777,777)")
else:
    step("stock: the refusal says live edits arrive with the round-2 update", "round-2" in (r or {}).get("error", ""),
         (r or {}).get("error", "")[:200])
    step("stock: no Kick is offered", not on.get("kick"))
    t.join(timeout=70)
    wait_offline(L["cid"], L["name"])
    st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"silver": 777777}, "expected": {"silver": 1000}})
    ok(st, r, "stock: once logged out the same edit saves to the database (guild Silver 777,777)")
rep = login(L["account"], PW, L["name"])
step("the client receives its guild at login (10138): %s, Silver %s" % ((rep["guild"] or {}).get("name"), (rep["guild"] or {}).get("silver")),
     (rep["guild"] or {}).get("name") == GNAME and rep["guild"]["silver"] == 777777, json.dumps(rep["guild"]))
step("the client's member list (10143) shows %s as Guild Leader (6)" % L["name"],
     any(m["name"] == L["name"] and m["duty"] == 6 for m in rep["members"] or []), json.dumps(rep["members"]))
step("the character panel duty byte (10166) is 6", (rep["panel"] or {}).get("duty") == 6, json.dumps(rep["panel"]))
wait_offline(L["cid"], L["name"])
if a.quick:
    print("ALL OK")
    save()
    sys.exit(0)

# ------------------------------------------------------------------------------------------- 3. every write type
CH = {}
st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"name": GNAME + "X", "level": 7, "member_limit": 250, "silver": 123456789,
                                                         "gold": 98765, "bijou": 4321, "proclamation": "Welcome to the test guild",
                                                         "refuse_applications": True},
                                              "expected": {"silver": 777777}})
CH["edit"] = ok(st, r, "edit name, level 7, limit 250, Silver / Gold / gems, proclamation, refuse applications")["change"]["id"]
GN = GNAME + "X"
st, r = api("/api/m/guild/g/%d/member/add" % gid, {"cid": Mm["cid"], "duty": 3})
CH["add"] = ok(st, r, "add %s as Elite (3)" % Mm["name"])["change"]["id"]
st, r = api("/api/m/guild/g/%d/member/add" % gid, {"cid": X["cid"], "duty": 1})
CH["addx"] = ok(st, r, "add %s as Trainee (1)" % X["name"])["change"]["id"]
st, r = api("/api/m/guild/g/%d/member/%d/edit" % (gid, Mm["cid"]), {"duty": 4, "contribution_silver": 5555, "contribution_gold": 66,
                                                                     "consortia_contribute": 900000,
                                                                     "expected": {"duty": 3, "contribution_silver": 0, "contribution_gold": 0, "consortia_contribute": 0}})
CH["medit"] = ok(st, r, "%s: Director (4), contributions Silver 5,555 / Gold 66, contribution points 900,000" % Mm["name"])["change"]["id"]
for t_, lv in ((1, 5), (13, 12), (10, 3)):
    st, r = api("/api/m/guild/g/%d/building" % gid, {"type": t_, "level": lv, "expected_level": 0})
    CH["b%d" % t_] = ok(st, r, "building type %d -> level %d" % (t_, lv))["change"]["id"]
st, r = api("/api/m/guild/g/%d/building" % gid, {"type": 10, "level": 0, "expected_level": 3})
CH["b10x"] = ok(st, r, "remove building type 10 (Apollo)")["change"]["id"]
st, r = api("/api/m/guild/g/%d/worship" % gid, {"cid": Mm["cid"], "type": 13, "points": 600000, "expected": 0})
CH["w"] = ok(st, r, "%s offers 600,000 points on Hephaestus (13)" % Mm["name"])["change"]["id"]
st, r = api("/api/m/guild/g/%d/boost" % gid, {"hours": 16, "percent": 100})
CH["boost"] = ok(st, r, "guild Double EXP +100% for 16 h to every member")["change"]["id"]
st, r = api("/api/m/guild/g/%d/leader" % gid, {"cid": Mm["cid"], "old_duty": 5})
CH["leader"] = ok(st, r, "hand the leadership to %s (%s -> Vice Leader)" % (Mm["name"], L["name"]))["change"]["id"]
st, g1 = api("/api/m/guild/g/%d" % gid)
step("the page reads it all back", st == 200 and g1["guild"]["lord_character_id"] == Mm["cid"] and len(g1["members"]) == 3
     and g1["guild"]["name"] == GN and not g1["issues"], json.dumps(g1["issues"]))
w = g1["worship"][0]
step("altar maths: 600,000 points on Hephaestus L12 pay %s%% of %s = %s physical attack" % (w["bonus_percent"], w["base_value"], w["bonus"]),
     w["bonus_percent"] == 90.0 and w["bonus"] == round(w["base_value"] * 0.9))

# ------------------------------------------------------------------------------------------- 4. the server loads it
rep = login(Mm["account"], PW, Mm["name"])
gg = rep["guild"] or {}
step("10138: name %s, level %s, Silver %s, Gold %s, gems %s, limit %s" % (gg.get("name"), gg.get("level"), gg.get("silver"), gg.get("gold"), gg.get("bijou"), gg.get("member_limit")),
     gg.get("name") == GN and gg.get("level") == 7 and gg.get("silver") == 123456789 and gg.get("gold") == 98765 and gg.get("bijou") == 4321
     and gg.get("member_limit") == 250, json.dumps(gg))
step("10138: proclamation %r" % gg.get("proclamation"), gg.get("proclamation") == "Welcome to the test guild")
step("10138: buildings %s (Footstone 5, Hephaestus 12, Apollo removed)" % gg.get("buildings"), gg.get("buildings") == {1: 5, 13: 12},
     json.dumps(gg.get("buildings")))
mm = {m["name"]: m for m in rep["members"] or []}
step("10143: %s leader (6), %s Vice Leader (5), %s Trainee (1)" % (Mm["name"], L["name"], X["name"]),
     mm.get(Mm["name"], {}).get("duty") == 6 and mm.get(L["name"], {}).get("duty") == 5 and mm.get(X["name"], {}).get("duty") == 1,
     json.dumps(rep["members"]))
step("10143: %s contributed Silver 5,555 / Gold 66" % Mm["name"], mm.get(Mm["name"], {}).get("silver") == 5555 and mm[Mm["name"]].get("gold") == 66)
step("10166: the panel shows duty 6 and contribution 900,000", (rep["panel"] or {}).get("duty") == 6 and rep["panel"].get("contribution") == 900000,
     json.dumps(rep["panel"]))
step("10167: the [Guild] Double EXP status 1007 is on", 1007 in (rep.get("statuses") or []), json.dumps(rep.get("statuses")))
wait_offline(Mm["cid"], Mm["name"])

# ------------------------------------------------------------------------------------------- 5. refusals
st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"lord_character_id": X["cid"]}})
step("read-only field guilds.lord_character_id refused (403)", st == 403, r.get("error", "")[:160])
st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"silver": 1}, "expected": {"silver": 42}})
step("stale value refused (409)", st == 409, r.get("error", "")[:160])
st, r = api("/api/m/guild/g/%d/building" % gid, {"type": 13, "level": 11, "expected_level": 3})
step("stale building level refused (409)", st == 409, r.get("error", "")[:160])
st, r = api("/api/m/guild/g/%d/member/%d/remove" % (gid, Mm["cid"]), {})
step("removing the leader refused (400)", st == 400, r.get("error", "")[:160])
st, r = api("/api/m/guild/g/%d/building" % gid, {"type": 30, "level": 9})
step("God Altar above its maximum level 8 refused (400)", st == 400, r.get("error", "")[:160])
st, r = api("/api/m/guild/g/%d/member/%d/edit" % (gid, X["cid"]), {"duty": 6})
step("duty 6 outside 'Make leader' refused (400)", st == 400, r.get("error", "")[:160])
st, chars = api("/api/m/guild/characters")
free = next((c for c in chars["characters"] if not c["guild_id"] and not c["online"]), None)
if free:
    st, r = api("/api/m/guild/create", {"name": GN.lower(), "lord": free["id"]})
    step("a second guild with the same name (any case) refused", st == 400 and "exists already" in r.get("error", ""), r.get("error", "")[:160])
st, r = api("/api/m/guild/create", {"name": "Another", "lord": X["cid"]})
step("a leader who is already in a guild refused", st == 400 and "already in a guild" in r.get("error", ""), r.get("error", "")[:160])

# ------------------------------------------------------------------------------------------- 6. Undo, newest first
for k, what in (("leader", "leadership hand-over"), ("boost", "guild EXP boost"), ("w", "altar offering"), ("b10x", "Apollo removal"),
                ("b10", "Apollo build"), ("b13", "Hephaestus 12"), ("b1", "Footstone 5"), ("medit", "member edit"), ("addx", "add Trainee")):
    undo(CH[k], what)
st, g2 = api("/api/m/guild/g/%d" % gid)
step("after the Undos: leader %s again, 2 members, no buildings, no offerings, no boost" % L["name"],
     g2["guild"]["lord_character_id"] == L["cid"] and len(g2["members"]) == 2 and not any(b["level"] for b in g2["buildings"])
     and not g2["worship"] and not g2["boosts"] and not g2["issues"], json.dumps(g2["issues"]))
rep = login(L["account"], PW, L["name"])
mm = {m["name"]: m for m in rep["members"] or []}
step("the server loads the undone state: %s duty 6, %s Elite (3) with no contributions, no buildings" % (L["name"], Mm["name"]),
     mm.get(L["name"], {}).get("duty") == 6 and mm.get(Mm["name"], {}).get("duty") == 3 and mm[Mm["name"]]["silver"] == 0
     and (rep["guild"] or {}).get("buildings") == {} and X["name"] not in mm, json.dumps(rep["members"]) + " " + json.dumps(rep["guild"]))
step("10167: no [Guild] Double EXP after its Undo", 1007 not in (rep.get("statuses") or []), json.dumps(rep.get("statuses")))
wait_offline(L["cid"], L["name"])

# ------------------------------------------------------------------------------------------- 7. desync repair, remove, disband + Undo
st, r = api("/api/m/guild/g/%d/member/%d/remove" % (gid, Mm["cid"]), {})
CH["remove"] = ok(st, r, "remove %s (membership + guild columns, as leaving does)" % Mm["name"])["change"]["id"]
st, r = api("/api/m/guild/repair/%d" % Mm["cid"], {})
step("repair of an in-step character answers 'already in step' (400)", st == 400, r.get("error", "")[:120])
undo(CH["remove"], "remove member")
st, r = api("/api/m/guild/g/%d/disband" % gid, {})
CH["disband"] = ok(st, r, "disband %s" % GN)["change"]["id"]
st, r = api("/api/m/guild/g/%d" % gid)
step("the guild is gone", st == 404)
rep = login(L["account"], PW, L["name"])
step("after the disband the client gets no guild and the panel duty is 0", rep["guild"] is None and (rep["panel"] or {}).get("duty") == 0,
     json.dumps([rep["guild"], rep["panel"]]))
wait_offline(L["cid"], L["name"])
undo(CH["disband"], "disband (the guild row and its identity id come back)")
rep = login(Mm["account"], PW, Mm["name"])
step("after the Undo the server loads the guild again: %s with %s and %s" % ((rep["guild"] or {}).get("name"), L["name"], Mm["name"]),
     (rep["guild"] or {}).get("name") == GN and {m["name"] for m in rep["members"] or []} == {L["name"], Mm["name"]}, json.dumps(rep["members"]))
wait_offline(Mm["cid"], Mm["name"])
undo(CH["add"], "add member")
undo(CH["edit"], "guild edit")

# ------------------------------------------------------------------------------------------- 8. faction areas
st, fa = api("/api/m/guild/factions")
step("faction areas: %d world-boss maps, %d realms" % (len(fa["areas"]), len(fa["realms"])), st == 200 and len(fa["areas"]) >= 1)
mp = fa["areas"][0]["map_id"]
st, r = api("/api/m/guild/faction/control", {"realm": 1, "map": mp, "camp": 1, "hours": 6, "percent": 50})
fc = ok(st, r, "Athens controls map %d: +50%% EXP for 6 hours" % mp)["change"]["id"]
st, fa2 = api("/api/m/guild/factions")
c = [x for x in fa2["control"] if x["map_id"] == mp and x["realm_id"] == 1]
step("the control row is active (camp 1, 5000 basis points)", c and c[0]["active"] and c[0]["controlling_camp"] == 1 and c[0]["bonus_basis_points"] == 5000)
undo(fc, "faction control")
step("faction-area changes apply at the next reward (read per use): chip 'now'", r.get("applies") == "now")
# verify:guild 2026-10-28: Undo of an UPDATE must put the earlier camp back (the glossary marks every column of this
# table 'derived', which made Undo restore nothing and still say "identical")
st, r = api("/api/m/guild/faction/control", {"realm": 1, "map": mp, "camp": 0, "hours": 5, "percent": 30})
fc1 = ok(st, r, "Sparta controls map %d: +30%%" % mp)["change"]["id"]
st, r = api("/api/m/guild/faction/control", {"realm": 1, "map": mp, "camp": 1, "hours": 9, "percent": 70})
fc2 = ok(st, r, "then Athens: +70% (an update of the same row)")["change"]["id"]
undo(fc2, "faction update")
st, fa3 = api("/api/m/guild/factions")
c = [x for x in fa3["control"] if x["map_id"] == mp and x["realm_id"] == 1]
step("after that Undo Sparta holds it again at +30%", c and c[0]["controlling_camp"] == 0 and c[0]["bonus_basis_points"] == 3000, json.dumps(c)[:200])
undo(fc1, "faction Sparta")
# text is not a yes/no: bool("false") is True
st, r = api("/api/m/guild/g/%d/edit" % gid, {"values": {"refuse_applications": "false"}})
step("'false' as text refused (400), not read as yes", st in (400, 404), json.dumps(r)[:200])
print("ALL OK")
REPORT["people"] = people
REPORT["guild"] = {"id": gid, "name": GN}
save()
