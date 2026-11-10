#!/usr/bin/env python3
"""tests/test_skills_probe.py - a headless reborn (2.51) game client for the Skills & talents tests: it logs a
character into the world and DECODES what the server sends about skills, talents, zodiac, titles and quests, to prove
that what the editor wrote is what the game server loaded (and, with hold, what a running server pushes live).

    python3 -I tests/test_skills_probe.py ACCOUNT PASSWORD ROLE HOST:LOGINPORT [--create] [--hold N] [--json OUT]

Login / redirect / enter are the retail-client replay of reborn_probe.py (needs gwcrypt.py: --gwcrypt DIR).
Decoded (offsets from godswar-server src/Godswar.Server/Packets; body offset = packet offset - 4):

  10019 EnterMain          talent points @88 (EnterTalentPointsOffset 92)                     PacketBuilder.cs:13
  10166 status update      level @96, talent points @224 (PlayerStatusTalentPointsOffset 228) PacketBuilder.PlayerStatusUpdate.cs
  10042 TalentRankList     count @4, 16-byte records @8: talent id, rank, display value, next cost
                                                                                            PacketBuilder.SkillsTalents.cs
  10041 ActiveSkillInfo    count @4, 8-byte records @8: skill id, cooldown                  PacketBuilder.SkillsTalents.cs
  10196 DesignationInfo    selected title @0, count @4, 12-byte records @8: title id, type, state
                                                                                            PacketBuilder.Designations.cs
  10297 Zodiac full sync   (asked for with module 0 / SID 1, as the client does when the window opens)
                           state @20: sign i32, level u8 @+8, energy i32 @+12, 16 grids x 16 bytes @+48:
                           ((grid/4) << 8 | level), selected Kind                          docs/zodiac-sync-10297.md
  10090 quest snapshot     count @0, 680-byte blocks @4: quest id, giver npc, responder npc   PacketBuilder.QuestFrames.cs
  10078 / 10079            quest-mark lists: count @0, npc ids @4 (givers with a quest for the character / hand-ins)
"""
import argparse
import hashlib
import json
import os
import socket
import struct
import sys
import time

KEY = b"435kfa3fjaf03fakf3fa3220fasjf3we322f2fjaf92f2fsf92fl1h1535rb7nw4"
HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_GWCRYPT = os.path.normpath(os.path.join(HERE, "..", "..", "..", "..", "GodsWar-2.51", "Polish", "tools"))


def _stream():
    try:
        from gwcrypt import Stream          # noqa: F401
    except ImportError:
        raise SystemExit("gwcrypt.py not found (give --gwcrypt DIR)")
    return Stream


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


class Conn:
    def __init__(self, host, port):
        Stream = _stream()
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


u32 = lambda b, o: struct.unpack_from("<I", b, o)[0]          # noqa: E731
i32 = lambda b, o: struct.unpack_from("<i", b, o)[0]          # noqa: E731
ZODIAC_REQUEST = struct.pack("<IHH", 0, 0, 1) + b"\0" * 12      # 1800 3928 | obj 0 | module 0 | SID 1 | 12 zero bytes


def decode(report, msgs, phase):
    for t, size, body in msgs:
        if t == "CLOSED":
            report["closed"] = phase
            continue
        report["messages"][str(t)] = report["messages"].get(str(t), 0) + 1
        if t == 10019 and len(body) > 92:
            report["enter"] = {"character_id": u32(body, 0), "name": body[4:36].split(b"\0")[0].decode(errors="replace"),
                               "talent_points": i32(body, 88)}
        elif t == 10166 and len(body) >= 228 and u32(body, 0) == 0x1448:
            s = {"phase": phase, "level": i32(body, 96), "talent_points": i32(body, 224)}
            report["status"] = s
            report["status_history"].append(s)
        elif t == 10042 and len(body) >= 8:
            n = i32(body, 4)
            rows = [{"id": i32(body, 8 + 16 * k), "rank": i32(body, 12 + 16 * k), "value": i32(body, 16 + 16 * k),
                     "next_cost": i32(body, 20 + 16 * k)} for k in range(n) if 8 + 16 * k + 16 <= len(body)]
            report["talents"] = rows
            report["talent_history"].append({"phase": phase, "ranks": {str(r["id"]): r["rank"] for r in rows}})
        elif t == 10041 and len(body) >= 8:
            n = i32(body, 4)
            report["skills"] = [i32(body, 8 + 8 * k) for k in range(n) if 8 + 8 * k + 8 <= len(body)]
        elif t == 10196 and len(body) >= 8:
            n = i32(body, 4)
            recs = []
            for k in range(n):
                o = 8 + 12 * k
                if o + 12 > len(body):
                    break
                recs.append({"id": u32(body, o), "type": body[o + 4], "state": body[o + 5]})
            if all(r["type"] == 4 for r in recs):         # DesignationInfo (MedusaDesignationType 4)
                report["titles"] = {"selected": u32(body, 0), "owned": [r["id"] for r in recs], "phase": phase}
            else:
                report["skill_list"] = [r["id"] for r in recs]
        elif t == 10297 and len(body) >= 20 + 304 and struct.unpack_from("<H", body, 6)[0] == 1:
            s = 20
            grids = []
            for g in range(16):
                o = s + 48 + 16 * g
                packed = u32(body, o)
                grids.append({"grid": g, "row": (packed >> 8) & 0xFF, "level": packed & 0xFF, "kind": i32(body, o + 4)})
            z = {"phase": phase, "sign": i32(body, s), "level": body[s + 8], "energy": i32(body, s + 12), "grids": grids}
            report["zodiac"] = z
            report["zodiac_history"].append({k: z[k] for k in ("phase", "sign", "level", "energy")})
        elif t == 10090 and len(body) >= 4:
            n = u32(body, 0)
            if n <= 20 and len(body) >= 4 + 680 * max(n, 0) - 0:
                qs = []
                for k in range(n):
                    o = 4 + 680 * k
                    if o + 84 > len(body):
                        break
                    qs.append({"quest": u32(body, o), "giver": u32(body, o + 4), "responder": u32(body, o + 8),
                               "word80": u32(body, o + 80) >> 16})
                if len(body) >= 2040:                         # the login snapshot (2044+ bytes), not a 1-quest accept frame
                    report["quest_snapshot"] = {"phase": phase, "count": n, "quests": qs}
        elif t in (10078, 10079) and len(body) >= 4:
            n = u32(body, 0)
            ids = [u32(body, 4 + 4 * k) for k in range(min(n, 160)) if 4 + 4 * k + 4 <= len(body)]
            report["marks_available" if t == 10078 else "marks_handin"] = ids


def probe(account, password, role, login, create=False, hold=0.0, gwcrypt=DEFAULT_GWCRYPT, on_world=None, zodiac_every=0.0, until=None):
    """Log in, enter the world, decode; hold N seconds (calling on_world(report) once in the world, in this thread's
    caller-provided callback, and asking for a zodiac sync every zodiac_every seconds; until() -> True ends the hold
    early); log out. Returns the report."""
    if gwcrypt and gwcrypt not in sys.path:
        sys.path.insert(0, gwcrypt)
    report = {"role": role, "messages": {}, "status_history": [], "talent_history": [], "zodiac_history": [], "skills": None,
              "talents": None, "titles": None, "zodiac": None, "quest_snapshot": None, "marks_available": None, "marks_handin": None}
    host, port = login.rsplit(":", 1)
    acct, pw, rl = account.encode(), password.encode(), role.encode()
    c = Conn(host, int(port))
    c.send(1, pad(shift_name(acct), 32) + hashlib.md5(pw).hexdigest().encode() + b"\0" * 68 + struct.pack("<I", 0x3e9774))
    r = []
    for _ in range(10):                  # the PBKDF2 verifier (600,000 rounds) can take a few seconds
        r += c.recv(1.5)
        if any(m[0] == 3 for m in r):
            r += c.recv(0.5)
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
    g = Conn(ghost, gport)
    g.send(10000, pad(acct, 32) + code + b"\x01")
    r = g.recv(2)
    roles = [m for m in r if m[0] == 10002]
    if create or (roles and roles[0][1] == 500):
        g.send(10003, pad(rl, 32) + bytes.fromhex("0100000035000000" + "00" * 32 + "8085183c"))
        g.recv(2)
    allmsgs = []
    g.send(10006, pad(rl, 32) + code + b"\0")
    allmsgs += g.recv(3)
    g.send(10311, b"\0" * 12)
    allmsgs += g.recv(1)
    g.send(10200, bytes.fromhex("cc33b12200000000"))
    g.send(10056, bytes.fromhex("00000000010000000000000000000000a00f00005989cf060000000094fc12009be47f00"))
    g.send(10056, bytes.fromhex("00000000010000000000000001000000be0f00005989cf060000000094fc12009be47f00"))
    allmsgs += g.recv(1)
    g.send(10007, struct.pack("<I", 0x21448) + code + b"\0")
    g.send(10200, bytes.fromhex("e18fcf0600000000"))
    g.send(10202, bytes.fromhex("48140000000000000be77f00"))
    allmsgs += g.recv(1.5)
    g.send(10357, b"\0" * 4)
    allmsgs += g.recv(3)
    decode(report, allmsgs, "login")
    g.send(10297, ZODIAC_REQUEST)
    decode(report, g.recv(2), "login")
    report["entered"] = time.time()
    if hold:
        end = time.time() + hold
        called = False
        last_z = time.time()
        while time.time() < end and not (until and until()):
            if on_world and not called:
                called = True
                on_world(report)
            g.send(10015, b"")
            decode(report, g.recv(min(2, max(0.3, end - time.time()))), "hold")
            if zodiac_every and time.time() - last_z >= zodiac_every:
                g.send(10297, ZODIAC_REQUEST)
                last_z = time.time()
    g.s.close()
    return report


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("account"), ap.add_argument("password"), ap.add_argument("role"), ap.add_argument("login")
    ap.add_argument("--create", action="store_true")
    ap.add_argument("--hold", type=float, default=0)
    ap.add_argument("--json", default=None)
    ap.add_argument("--gwcrypt", default=DEFAULT_GWCRYPT)
    a = ap.parse_args()
    rep = probe(a.account, a.password, a.role, a.login, a.create, a.hold, a.gwcrypt, zodiac_every=5 if a.hold else 0)
    print(json.dumps(rep, indent=1, default=str))
    if a.json:
        with open(a.json, "w") as f:
            json.dump(rep, f, indent=1, default=str)
