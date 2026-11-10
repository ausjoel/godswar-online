#!/usr/bin/env python3
"""tests/test_rates_client.py - runs GodsWar-2.51/Polish/tools/rates_probe.py (unchanged on disk) with a longer wait
for the login answer. Accounts made by the editor's Accounts page use the server's PBKDF2 verifier (600,000 rounds),
so the login server answers after 2-6 s; the probe gives up after 2 s ("login rejected: []").

    python3 -I tests/test_rates_client.py PATH/rates_probe.py ACCOUNT PASSWORD ROLE HOST:PORT [probe options ...]
"""
import os
import sys

probe = os.path.abspath(sys.argv[1])
src = open(probe, encoding="utf-8").read()
old = "r = c.recv(2)\nif not r or r[0][0] != 3"
if old not in src:
    sys.exit("rates_probe.py changed: login wait not found")
src = src.replace(old, "r = c.recv(10)\nif not r or r[0][0] != 3", 1)
sys.argv = [probe] + sys.argv[2:]
sys.path.insert(0, os.path.dirname(probe))          # gwcrypt.py beside the probe
exec(compile(src, probe, "exec"), {"__name__": "__main__", "__file__": probe})
