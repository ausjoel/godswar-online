#!/usr/bin/env python3
"""reborn_probe.py - log a character into a reborn (2.51 protocol) server headlessly and DECODE what the
server sent about it, to prove that what gwadmin-web wrote to PostgreSQL is what the server loaded.

    python3 -I reborn_probe.py ACCOUNT PASSWORD ROLE HOST:LOGINPORT [--create] [--ride] [--json OUT.json]

Login/redirect/enter is the exact retail-client replay of GodsWar-2.51/Server-Reborn/tools/reborn_client.py
(needs gwcrypt.py from that folder: same directory, or --gwcrypt DIR).  Decoded (offsets are the server's
own builders in godswar-server/src/Godswar.Server/Packets, packet offset - 4 = body offset here):

  10002 role list      name, level byte                                (PacketBuilder.WorldEntry.cs CharacterPreview)
  10019 EnterMain      character id @0, name @4, equipment mask @44, 72-byte item records @100 in
                       ascending slot order (EnterEquipmentSlots 0..12,15..20), next-level exp @84
                                                                       (PacketBuilder.WorldEntry.cs, PacketBuilder.cs:10-28)
  10033 KitBag detail  page @12, first record @13, 12 x 72-byte records @20: id, attr1..5, quality,
                       grade, bound, stack                             (PacketBuilder.Inventory.cs KitBagDetailPages)
  10056 slot index     page @8, index @12, item id @16                 (PacketBuilder.Inventory.cs KitBagSlotIndex)
  10166 status update  level @96, Silver @116, Gold @120, B-Gold @132, move-speed multiplier float @52
                                                                       (PacketBuilder.PlayerStatusUpdate.cs, Utilities.cs:75-120)
  10237 owned pets     opened cells @0, count @1, 0xA8-byte records @4: id, name, species, aptitude,
                       sex, level, opened/available skill cells, carried, active skills (u16 x 12),
                       talent mask @0x68, exp @0x60, rank @0x9C        (PacketBuilder.Pets.cs WriteOwnedPetRecord)
  10167 status snapshot  status count @4, status ids @8, ride flag @324, speed @320  (PacketBuilder.PlayerStatusEffects.cs)

--ride casts skill 4904 (Riding, docs/mount-ride-protocol.md) and waits 8 s for the 10167/10166 that
only a server with a valid equipped mount (equipment slot 20) sends.
"""
import argparse
import hashlib
import json
import os
import socket
import struct
import sys
import time

ap = argparse.ArgumentParser()
ap.add_argument("account"), ap.add_argument("password"), ap.add_argument("role"), ap.add_argument("login")
ap.add_argument("--create", action="store_true")
ap.add_argument("--ride", action="store_true")
ap.add_argument("--warehouse", default=None, metavar="NPCOBJ@X,Z", help="walk to a warehouse NPC (e.g. Sparta_023 = 47750@97,-159), open it (10067 + 8-byte 10068) and decode the 10034 snapshot")
ap.add_argument("--json", default=None)
ap.add_argument("--hold", type=float, default=0, help="stay in the world this many seconds before logging out")
ap.add_argument("--gwcrypt", default=None)
a = ap.parse_args()
for d in [a.gwcrypt, os.path.dirname(os.path.abspath(__file__)),
          os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "GodsWar-2.51", "Server-Reborn", "tools")]:
    if d and os.path.exists(os.path.join(d, "gwcrypt.py")):
        sys.path.insert(0, d)
        break
from gwcrypt import Stream  # noqa: E402

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


def u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def i32(b, o):
    return struct.unpack_from("<i", b, o)[0]


def item_record(b, o):
    rid = u32(b, o)
    if rid in (0, 0xFFFFFFFF):
        return None
    return {"id": rid, "attrs": [i32(b, o + 4 + 4 * k) for k in range(5)], "quality": b[o + 24], "grade": b[o + 25],
            "bound": b[o + 26], "stack": b[o + 27]}


ENTER_SLOTS = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 18, 19, 20]
report = {"role": a.role, "messages": {}, "kitbag": {}, "slot_index": {}, "equipment": {}, "pets": None, "status": None}
host, port = a.login.rsplit(":", 1)
acct, pw, role = a.account.encode(), a.password.encode(), a.role.encode()
c = Conn(host, int(port))
c.send(1, pad(shift_name(acct), 32) + hashlib.md5(pw).hexdigest().encode() + b"\0" * 68 + struct.pack("<I", 0x3e9774))
r = c.recv(2)
if not r or r[0][0] != 3 or r[0][2][:2] != b"\x01\x00":
    sys.exit("login rejected: %r" % (r[:2],))
realms = [m for m in r if m[0] == 10008]
c.send(4, pad(acct, 32) + bytes([realms[0][2][36] if realms else 1]) + bytes.fromhex("adcf0600000000"))
c.recv(2)
c.send(6, pad(acct, 32) + bytes.fromhex("402cee11b8de1200408a82112800000007000000000000003032363038303333343931313234303733343732000000000c38a708149fef11c61c40001038a7080f000000149fef110b00000001000000"))
rd = [m for m in c.recv(2) if m[0] == 10001]
if not rd:
    sys.exit("no redirect")
b = rd[0][2]
ghost, gport, code = b[1:33].split(b"\0")[0].decode(), u32(b, 36), b[41:66]
c.s.close()
print("redirect to game %s:%d" % (ghost, gport))
g = Conn(ghost, gport)
g.send(10000, pad(acct, 32) + code + b"\x01")
r = g.recv(2)
roles = [m for m in r if m[0] == 10002]
if a.create or (roles and roles[0][1] == 500):
    g.send(10003, pad(role, 32) + bytes.fromhex("0100000035000000" + "00" * 32 + "8085183c"))
    r2 = g.recv(2)
    print("created role %s: %s" % (a.role, [t for t, _, _ in r2]))
    roles = [m for m in r + r2 if m[0] == 10002] or roles
if roles and len(roles[-1][2]) > 70:
    rb = roles[-1][2]
    report["role_list"] = {"name": rb[33:65].split(b"\0")[0].decode(errors="replace")}
allmsgs = []
g.send(10006, pad(role, 32) + code + b"\0")
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
objid = None


def decode(msgs, phase):
    global objid
    for t, size, body in msgs:
        report["messages"].setdefault(str(t), 0)
        report["messages"][str(t)] += 1
        if t == 10019 and len(body) > 100:
            objid = u32(body, 48)
            mask = u32(body, 44)
            slots = [s for s in ENTER_SLOTS if mask & (1 << s)]
            eq = {}
            for i, s in enumerate(slots):
                rec = item_record(body, 100 + 72 * i)
                if rec:
                    eq[str(s)] = rec
            report["enter"] = {"character_id": u32(body, 0), "name": body[4:36].split(b"\0")[0].decode(errors="replace"),
                               "object_id": objid, "equipment_mask": mask, "next_level_exp": u32(body, 84),
                               "hp": [i32(body, 72), i32(body, 64)], "x": struct.unpack_from("<f", body, 52)[0],
                               "z": struct.unpack_from("<f", body, 60)[0]}
            report["equipment"] = eq
        elif t == 10033 and len(body) >= 20:
            page, first = body[12], body[13]
            for k in range(12):
                o = 20 + 72 * k
                if o + 72 > len(body):
                    break
                rec = item_record(body, o)
                key = str(page * 24 + first + k)
                was = (report["kitbag"].get(key) or {}).get("id")
                if rec:
                    report["kitbag"][key] = rec
                else:
                    report["kitbag"].pop(key, None)
                if phase != "login" and was != (rec or {}).get("id"):
                    # what changed in the bag after entering the world: [slot, old item id, new item id (None = empty)]
                    report.setdefault("kitbag_changes", []).append([int(key), was, (rec or {}).get("id")])
        elif t == 10056 and len(body) >= 20 and i32(body, 0) == -1:
            iid = i32(body, 16)
            if iid != -1:
                report["slot_index"][str(u32(body, 8) * 24 + u32(body, 12))] = iid
        elif t == 10166 and len(body) >= 140 and u32(body, 0) == 0x1448:
            report["status"] = {"level": i32(body, 96), "silver": i32(body, 116), "gold": i32(body, 120),
                                "binding_gold": i32(body, 132), "speed": round(struct.unpack_from("<f", body, 52)[0], 4), "phase": phase}
            report.setdefault("status_history", []).append(report["status"])
        elif t == 10167 and len(body) >= 330:
            n = struct.unpack_from("<H", body, 4)[0]
            report.setdefault("status_snapshots", []).append({
                "phase": phase, "statuses": [u32(body, 8 + 4 * k) for k in range(min(n, 20))],
                "speed": round(struct.unpack_from("<f", body, 320)[0], 4), "riding_flag": u32(body, 324)})
        elif t == 10034 and len(body) >= 16:
            cap, chunk, mask = struct.unpack_from("<H", body, 8)[0], body[10] // 2, struct.unpack_from("<H", body, 12)[0]
            report.setdefault("warehouse", {"capacity": cap, "items": {}})
            k = 0
            for bit in range(12):
                if mask & (1 << bit):
                    rec = item_record(body, 20 + 72 * k)
                    k += 1
                    if rec:
                        report["warehouse"]["items"][str(chunk * 12 + bit)] = rec
        elif t == 10237 and len(body) >= 4:
            cells, cnt = body[0], body[1]
            pets = []
            for k in range(cnt):
                o = 4 + 0xA8 * k
                rb = body[o:o + 0xA8]
                if len(rb) < 0xA8:
                    break
                nsk = rb[0x31]
                pets.append({"id": u32(rb, 0), "name": rb[4:36].split(b"\0")[0].decode(errors="replace"), "species": rb[0x24],
                             "aptitude": rb[0x25], "sex": rb[0x27], "level": rb[0x28], "opened_cells": rb[0x2B],
                             "available_cells": rb[0x2C], "carried": rb[0x30], "skills": [struct.unpack_from("<H", rb, 0x32 + 2 * j)[0] for j in range(nsk)],
                             "lifetime": struct.unpack_from("<H", rb, 0x4A)[0], "exp": u32(rb, 0x60), "talent_mask": u32(rb, 0x68),
                             "basic_savvy_x100": [u32(rb, 0x6C + 4 * j) for j in range(6)],
                             "added_savvy_x100": [u32(rb, 0x84 + 4 * j) for j in range(6)],
                             "rank_wire": struct.unpack_from("<H", rb, 0x9C)[0], "bound": rb[0xA4]})
            report["pets"] = {"opened_cells": cells, "count": cnt, "pets": pets}


decode(allmsgs, "login")
report["kitbag_login"] = dict(report["kitbag"])
t_enter = time.time()
if a.warehouse:
    npc, pos = a.warehouse.split("@")
    wx, wz = (float(v) for v in pos.split(","))
    g.send(10194, struct.pack("<Ifff", 0, wx + 2, wz + 2, 0.0))
    decode(g.recv(2), "walk")
    g.send(10067, struct.pack("<I", int(npc)))
    decode(g.recv(1.5), "warehouse")
    g.send(10068, struct.pack("<I", int(npc)))
    rw = g.recv(2.5)
    decode(rw, "warehouse")
    report["warehouse_messages"] = [t for t, _, _ in rw]
if a.ride:
    ex, ez = (report.get("enter") or {}).get("x", 0.0), (report.get("enter") or {}).get("z", 0.0)
    g.send(10040, struct.pack("<IIIIIffff", objid or 0x1448, 4904, 0, 0, 0, ex, ez, ex, ez))
    rr = g.recv(9)
    decode(rr, "ride")
    report["ride_messages"] = [t for t, _, _ in rr]
if a.hold:
    end = time.time() + a.hold
    report["hold_messages"] = []
    while time.time() < end:
        g.send(10015, b"")
        hm = g.recv(min(5, max(0.5, end - time.time())))
        # decode what the server pushes while we wait (live edits: bag pages 10033, wallet 10166, player detail ...)
        decode(hm, "hold")
        for t, _, _ in hm:          # [seconds since enter, message type, how many in a row]
            if t == 10015:
                continue
            hl = report["hold_messages"]
            if hl and hl[-1][1] == t:
                hl[-1][2] += 1
            else:
                hl.append([round(time.time() - t_enter, 1), t, 1])
g.s.close()
print(json.dumps(report, indent=1, default=str))
if a.json:
    with open(a.json, "w") as f:
        json.dump(report, f, indent=1, default=str)
