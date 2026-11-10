#!/usr/bin/env python3
"""Headless 2.51 client login: does an account log in to reborn and reach character select?

    python3 -I game_login.py ACCOUNT PASSWORD HOST:LOGINPORT [--gwcrypt DIR]

Replays the retail client's login exactly like GodsWar-VPS/tools/gwadmin-web/reborn_probe.py
(Caesar-shifted name, md5 hex password, then realm pick, redirect, game hello) and stops at the
role list (message 10002) = the character select screen. Creates nothing and enters no world.
Exit code 0 = reached character select, 1 = refused or no answer.
Needs gwcrypt.py (GodsWar-2.51/Polish/gm-test-kit or Server-Reborn/tools).
"""
import argparse
import hashlib
import os
import socket
import struct
import sys
import time

ap = argparse.ArgumentParser()
ap.add_argument("account"), ap.add_argument("password"), ap.add_argument("login")
ap.add_argument("--gwcrypt", default=None)
a = ap.parse_args()
here = os.path.dirname(os.path.abspath(__file__))
for d in [a.gwcrypt, here, os.path.join(here, "..", "..", "..", "GodsWar-2.51", "Polish", "gm-test-kit")]:
    if d and os.path.exists(os.path.join(d, "gwcrypt.py")):
        sys.path.insert(0, os.path.abspath(d))
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

    def recv(self, timeout=2.0):
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


host, port = a.login.rsplit(":", 1)
acct, pw = a.account.encode(), a.password.encode()
c = Conn(host, int(port))
c.send(1, pad(shift_name(acct), 32) + hashlib.md5(pw).hexdigest().encode() + b"\0" * 68 + struct.pack("<I", 0x3e9774))
r = c.recv(2)
if not r or r[0][0] != 3:
    print("LOGIN: no answer %r" % ([m[0] for m in r],))
    sys.exit(1)
code = r[0][2][:2]
print("LOGIN: reply 3, result bytes %s (%s)" % (code.hex(), "accepted" if code == b"\x01\x00" else "REFUSED"))
if code != b"\x01\x00":
    sys.exit(1)
realms = [m for m in r if m[0] == 10008]
names = [m[2][:32].split(b"\0")[0].decode(errors="replace") for m in realms]
print("REALMS: %s" % names)
c.send(4, pad(acct, 32) + bytes([realms[0][2][36] if realms else 1]) + bytes.fromhex("adcf0600000000"))
c.recv(2)
c.send(6, pad(acct, 32) + bytes.fromhex("402cee11b8de1200408a82112800000007000000000000003032363038303333343931313234303733343732000000000c38a708149fef11c61c40001038a7080f000000149fef110b00000001000000"))
rd = [m for m in c.recv(2) if m[0] == 10001]
c.s.close()
if not rd:
    print("REDIRECT: none")
    sys.exit(1)
b = rd[0][2]
ghost, gport, ticket = b[1:33].split(b"\0")[0].decode(), struct.unpack_from("<I", b, 36)[0], b[41:66]
print("REDIRECT: game server %s:%d" % (ghost, gport))
g = Conn(ghost, gport)
g.send(10000, pad(acct, 32) + ticket + b"\x01")
r = g.recv(3)
roles = [m for m in r if m[0] == 10002]
g.s.close()
if not roles:
    print("CHARACTER SELECT: not reached, got %s" % [m[0] for m in r])
    sys.exit(1)
body = roles[-1][2]
first = body[33:65].split(b"\0")[0].decode(errors="replace") if len(body) > 70 else ""
print("CHARACTER SELECT: reached (message 10002, %d bytes; first character: %s)" % (roles[-1][1], first or "none yet"))
sys.exit(0)
