#!/usr/bin/env python3
"""Unit test (W4A): the Inventory page's warehouse opener (modules/inventory.py keys_needed) against the live policy
(revision 3: SB-2..SB-4 for 1 / 2 / 3 keys) and the optional nine-box successor (revision 4, server migration file
PostgresSchemaMigrationCatalog.WarehouseNineBoxKeys.cs, not registered). No database, no server.

    python3 -I tests/test_warehouse_opener_unit.py
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.dirname(HERE))
from modules import inventory as INV  # noqa: E402
from modules.common import Bad  # noqa: E402

FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:400]))
    if not ok:
        FAIL.append(msg)


def refused(*a):
    try:
        INV.keys_needed(*a)
    except Bad as e:
        return str(e)
    return None


V3 = [{"capacity": c, "keys": k, "item": 4102} for c, k in ((40, 0), (80, 1), (120, 2), (160, 3))]
V4 = [{"capacity": 40 * (i + 1), "keys": i, "item": 4102} for i in range(9)]
p = INV.keys_needed(V3, 40, 4)
step("1 -> 4 boxes: 1 + 2 + 3 = 6 keys", p["total"] == 6 and [s["box"] for s in p["steps"]] == [2, 3, 4], p)
step("keys already in the bag are not given twice", INV.keys_needed(V3, 40, 4, held=4)["give"] == 2)
step("more keys held than needed: nothing to give", INV.keys_needed(V3, 40, 2, held=5)["give"] == 0)
step("2 -> 3 boxes: 2 keys", INV.keys_needed(V3, 80, 3)["total"] == 2)
step("already there: no steps", INV.keys_needed(V3, 160, 3)["steps"] == [])
step("live policy refuses SB-5", "SB-5..SB-9" in (refused(V3, 40, 5) or ""))
step("0 or 10 boxes refused", refused(V3, 40, 0) is not None and refused(V3, 40, 10) is not None)
p = INV.keys_needed(V4, 40, 9)
step("nine-box policy: 36 keys from one box to nine", p["total"] == 36 and len(p["steps"]) == 8, p)
step("a policy hole is refused", refused([{"capacity": 40, "keys": 0}, {"capacity": 120, "keys": 2}], 40, 3) is not None)

mig = open(os.path.join(ROOT, "server", "src", "Godswar.Server", "State", "DatabaseMigrations",
                        "PostgresSchemaMigrationCatalog.WarehouseNineBoxKeys.cs")).read()
levels = [(int(c), int(k)) for c, k in re.findall(r"\(4,\s*(\d+),\s*(\d+),\s*4102\)", mig)]
step("successor levels = 40..360 for 0..8 keys", levels == [(40 * (i + 1), i) for i in range(9)], levels)
step("successor publishes over exactly the live v3 publication", "AND revision = 3" in mig and "publication_version = 3" in mig
     and "05E00F650DCAA45FA72926AAE57A55656E0CEFB9F3410D8DEC47DDEB33BC9EBC" in mig)
step("successor hash = the v2 nine-box hash (same canonical levels)", "417B28788C5BFA91341E2E7818C999002FD79B4B46988BC5DB2793C5F7BD37C8" in mig)
cat = open(os.path.join(ROOT, "server", "src", "Godswar.Server", "State", "DatabaseMigrations", "PostgresSchemaMigrationCatalog.cs")).read()
step("successor is NOT registered (a decision for the admin / the lead)", "CreateWarehouseNineBoxKeys()" not in cat)
nine = open(os.path.join(ROOT, "server", "src", "Godswar.Server", "State", "DatabaseMigrations", "PostgresSchemaMigrationCatalog.WarehouseNineBox.cs")).read()
step("the v2 levels it copies are the audited nine-box ones", all("(2, %3d, %d, 4102)" % (40 * (i + 1), i) in nine for i in range(9)))
print("ALL OK" if not FAIL else "%d FAILED" % len(FAIL))
sys.exit(1 if FAIL else 0)
