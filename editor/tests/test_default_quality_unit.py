#!/usr/bin/env python3
"""W4F unit test: data/item-default-quality.json loads into the inventory meta shape (warn-only defaults).
  python3 -I tests/test_default_quality_unit.py"""
import os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import inventory  # noqa: E402

d = inventory.default_qualities()
fail = []
if d.get("14320", {}).get("quality") != 8: fail.append("Argent Armored Dragon 14320 defaults to Q8")
if not all(str(k).isdigit() and isinstance(v.get("quality"), int) and 1 <= v["quality"] <= 20 for k, v in d.items()):
    fail.append("every row has an integer quality 1-20")
if not all(1 <= v.get("grade", 1) <= 25 for v in d.values()): fail.append("grades are 1-25")
src = open(os.path.join(os.path.dirname(HERE), "static", "modules", "inventory.js"), encoding="utf-8").read()
if "P.meta.default_quality" not in src or "saved anyway" not in src: fail.append("the item editor warns (and still saves)")
for f in fail: print("FAIL " + f)
print("ok   %d default-quality rows" % len(d) if not fail else "")
sys.exit(1 if fail else 0)
