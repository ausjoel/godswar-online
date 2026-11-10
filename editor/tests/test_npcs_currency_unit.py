#!/usr/bin/env python3
"""Unit test: the editor's shop-currency tables (modules/npcs.py) match the game server source.
  python3 -I tests/test_npcs_currency_unit.py [--src PATH-TO/reborn-src-v2]
The server decides what a shop row charges in code (no database column), so modules/npcs.py mirrors two switches of
CapitalNpcServiceProtocol.cs. This fails when the server source changes them and the mirror was not updated."""
import argparse, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import npcs  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--src", default=os.path.join(HERE, "../../../../GodsWar-2.51/Polish/reborn-src-r3"))
a = ap.parse_args()
FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:400]))
    if not ok:
        FAIL.append(msg)


path = os.path.join(a.src, "src/Godswar.Server/Domain/World/Content/CapitalNpcServiceProtocol.cs")
if not os.path.exists(path):
    sys.exit("server source not found: %s (pass --src)" % path)
cs = open(path, encoding="utf-8").read()
NAME = {"Gold": "gold", "BindingGold": "bgold", "Silver": "silver", "Honor": "honour", "Point": "points", "Medal": "medals"}

# TryGetShopCurrency(byte shopType): "N => CapitalNpcShopCurrency.X"
body = cs[cs.index("byte shopType"):]
body = body[:body.index("_ => default")]
bytes_cs = {int(n): NAME[c] for n, c in re.findall(r"\b(\d+)\s*=>\s*CapitalNpcShopCurrency\.(\w+)", body)}
step("frame byte table matches TryGetShopCurrency(byte)", bytes_cs == npcs.CURRENCY_BYTES, (bytes_cs, npcs.CURRENCY_BYTES))

# TryGetPointExchangerCurrency(uint itemId): "a or b or c => CapitalNpcShopCurrency.X"
body = cs[cs.index("TryGetPointExchangerCurrency("):]
body = body[body.index("itemId switch"):body.index("_ => default")]
body = re.sub(r"//[^\n]*", "", body)
items_cs = {}
for ids, c in re.findall(r"([\d\sor]+?)=>\s*CapitalNpcShopCurrency\.(\w+)", body):
    for i in re.findall(r"\d+", ids):
        items_cs[int(i)] = NAME[c]
step("Point Exchanger item table matches TryGetPointExchangerCurrency (%d items)" % len(items_cs),
     items_cs == npcs.POINT_EXCHANGER_ITEMS, set(items_cs.items()) ^ set(npcs.POINT_EXCHANGER_ITEMS.items()))

# row_currency on the Point Exchanger: item table first, then the tab's first frame byte (4 = B-Gold).
pe = npcs.shop_currency("PointExchanger", [{"item": 9940, "category": c, "currency": 4} for c in range(4)])
for item, want in ((9940, "honour"), (4213, "points"), (14069, "medals"), (4193, "medals")):
    got = npcs.row_currency(pe, item, 2)
    step("Point Exchanger item %d charges %s" % (item, want), got == want, got)

# A tab whose pages carry two bytes (PropsVendor tab 1): the original list keeps each row's own byte, an edited tab
# takes the byte of its first page (BuildOverriddenCapitalShopCatalog categoryCurrency.TryAdd).
props = [{"item": 3100, "category": 0, "currency": 3}, {"item": 4001, "category": 0, "currency": 4}, {"item": 4002, "category": 1, "currency": 4}]
pc = npcs.shop_currency("PropsVendor", props)
step("mixed shop is per tab", pc["kind"] == "tab", pc)
step("original row keeps its own byte", npcs.row_currency(pc, 4001, 0, edited=False, compiled_byte=4) == "bgold")
step("edited row takes its tab's first byte", npcs.row_currency(pc, 4001, 0) == "silver")
step("mall charges gold, admin tab silver",
     npcs.row_currency(npcs.shop_currency("MallAthens", []), 1, 0) == "gold" and
     npcs.row_currency(npcs.shop_currency("MallAdmin", []), 1, 7) == "silver")

print("%d failed" % len(FAIL) if FAIL else "all passed")
sys.exit(1 if FAIL else 0)
