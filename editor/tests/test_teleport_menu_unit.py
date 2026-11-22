#!/usr/bin/env python3
"""B22 P1 (d32): the teleporter menu editor backend (modules/_teleport_menu.py) with a fake game server and database.
python3 -m pytest -q tests/test_teleport_menu_unit.py

Set GW_TELEMENU_RIG_DB=<db> (rig Postgres on PGHOST/PGPORT) to also run the generated SQL on that copy."""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ED = os.path.dirname(HERE)
sys.path.insert(0, ED)
from modules import _teleport_menu as T, npcs, world as W, _npc_menu  # noqa: E402
from modules.common import Bad  # noqa: E402

DOC = {
    "teleports": [
        {"npc": "Sparta_042", "sub": 1, "name": "Suburbs of Sparta", "target_map": 4, "level": 1},
        {"npc": "Sparta_042", "sub": 3, "name": "Peloponnesus", "target_map": 13, "level": 30},
        {"npc": "Sparta_042", "sub": 11, "name": "Mycenae", "target_map": 6, "level": 130},
        {"npc": "Mycenae_All_013", "sub": 1093, "name": "Olympus", "target_map": 21, "level": 185},
    ],
    "teleport_menus": {"maximum_buttons": 12, "slot_first": 1094, "slot_last": 1105, "currencies": ["silver", "gold", "bgold"],
                       "npcs": [{"npc": "Sparta_042", "source_map": 0, "compiled": [1, 3, 11], "effective": [1, 3, 11],
                                 "rows": 0, "compiled_fees": [{"sub": 11, "fee": 20000}]},
                                {"npc": "Mycenae_All_013", "source_map": 6, "compiled": [1093], "effective": [1093],
                                 "rows": 0, "compiled_fees": []}]},
}
LABELS = {"types": {"1": {"pages": {"1": {
    "1": {"buttons": [{"text": "*Go to the Suburbs of Sparta"}]}, "3": {"buttons": [{"text": "*Go to Peloponnesus"}]},
    "11": {"buttons": [{"text": "*Go to Mycenae (Lv130)"}]}, "1093": {"buttons": [{"text": "*Go to Olympus (Lv185)"}]}}}}}}


class Ctx:
    def __init__(self):
        self.body, self.writes = {}, []


PATCHED = [(npcs, "_content"), (npcs, "_reload"), (npcs, "_scene"), (npcs, "_plane"), (_npc_menu, "menu_data"),
           (W, "_v"), (W, "_q"), (W, "_version"), (W, "_write")]
ORIGINAL = {(m.__name__, n): getattr(m, n) for m, n in PATCHED}


def restore():
    """Put the real functions back (other test files share these modules in one pytest process)."""
    for m, n in PATCHED:
        setattr(m, n, ORIGINAL[(m.__name__, n)])


def fake(monkey_rows=None):
    captured = {}
    npcs._content = lambda ctx: DOC
    npcs._reload = lambda ctx: None
    npcs._scene = lambda ctx, m: "Scene%d" % m if m in (0, 4, 6, 13, 21) else None
    npcs._plane = lambda scene: None          # no wall data: a warning, not a refusal
    _npc_menu.menu_data = lambda: LABELS
    W._v = lambda ctx, expr: True
    W._q = lambda ctx, sql: list(monkey_rows or [])
    W._version = lambda ctx, parts: "new"

    def write(ctx, scope, action, stmts, fields, **kw):
        captured.update(scope=scope, action=action, stmts=stmts, fields=fields)
        return {"ok": True}
    W._write = write
    return captured


def faked(fn):
    """Run a test with the fakes and always restore the real functions afterwards."""
    def run():
        try:
            return fn()
        finally:
            restore()
    run.__name__ = fn.__name__
    return run


@faked
def test_menu_lists_compiled_and_choices():
    fake()
    m = T.menu(Ctx(), "Sparta_042")
    assert [c["sub"] for c in m["compiled"]] == [1, 3, 11]
    assert m["compiled"][2]["fee"] == 20000 and m["compiled"][2]["label"].startswith("*Go to Mycenae")
    olympus = next(c for c in m["choices"] if c["sub"] == 1093)
    assert olympus["borrowed"] and not olympus["compiled_here"]
    slot = next(c for c in m["choices"] if c["sub"] == 1094)
    assert slot["label"] is None and not slot["borrowed"]
    try:
        T.menu(Ctx(), "Athens_056")
        assert False
    except Bad:
        pass


@faked
def test_save_builds_rows_in_order_with_prices():
    cap = fake()
    ctx = Ctx()
    ctx.body = {"npc": "Sparta_042", "version": "new", "rows": [
        {"sub": 3}, {"sub": 1, "price": 1500}, {"sub": 11, "hidden": True},
        {"sub": 1093, "level": 150, "price": 5, "currency": "gold"},
        {"sub": 1094, "map": 4, "x": 97.98, "z": -222.85, "price": 0, "name": "Back gate"}]}
    out = T.save(ctx)
    ins = [s for s in cap["stmts"] if s.startswith("INSERT")]
    assert len(ins) == 5 and "DELETE FROM npc_teleport_menu WHERE npc_key = 'Sparta_042'" in cap["stmts"]
    assert "'Sparta_042', 3, 1," in ins[0] and "1500" in ins[1] and "true" in ins[2]
    assert "'gold'" in ins[3] and "150" in ins[3]
    assert "97.98" in ins[4] and "'Back gate'" in ins[4]
    assert any("no words in the current client" in w for w in out["warnings"])  # slot 1094 not labelled yet
    assert cap["fields"] == ["npc_teleport_menu.*"]


@faked
def test_save_refusals():
    fake()

    def refused(rows, why):
        ctx = Ctx()
        ctx.body = {"npc": "Sparta_042", "version": "new", "rows": rows}
        try:
            T.save(ctx)
        except Bad as e:
            assert why in str(e), str(e)
            return
        assert False, "accepted %r" % rows
    refused([{"sub": 1094}], "new destination")                       # added button without a point
    refused([{"sub": 1}, {"sub": 1}], "twice")
    refused([{"sub": 1, "currency": "honour"}], "currency")
    refused([{"sub": 1, "price": -1}], "price")
    refused([{"sub": 1, "hidden": True}], "every button is hidden")
    refused([{"sub": 1094 + i, "map": 4, "x": 1, "z": 1} for i in range(12)] + [{"sub": 1}], "room for 12")
    refused([{"sub": 1094, "map": 99, "x": 1, "z": 1}], "does not exist")
    refused([], "at least one button")


@faked
def test_rig_sql_round_trip():
    """Optional: the generated statements run on a rig copy (GW_TELEMENU_RIG_DB) and are read back."""
    db = os.environ.get("GW_TELEMENU_RIG_DB")
    if not db:
        return
    cap = fake()
    ctx = Ctx()
    ctx.body = {"npc": "Sparta_042", "version": "new", "rows": [
        {"sub": 11, "price": 30000}, {"sub": 1}, {"sub": 3, "hidden": True},
        {"sub": 1094, "map": 4, "x": 97.98, "z": -222.85, "price": 2, "currency": "gold", "level": 1}]}
    T.save(ctx)
    # a scratch copy: start from no rows so the page version "new" is right (the guard refuses otherwise)
    subprocess.run(["psql", "-d", db, "-qc", "DELETE FROM npc_teleport_menu WHERE npc_key = 'Sparta_042'"], check=True)
    sql = ";\n".join(["BEGIN"] + cap["stmts"] + ["COMMIT"]) + ";"
    subprocess.run(["psql", "-d", db, "-v", "ON_ERROR_STOP=1", "-q"], input=sql, text=True, check=True)
    out = subprocess.run(["psql", "-d", db, "-Atc", "SELECT string_agg(sub_id || ':' || hidden::int || ':' || coalesce(price, -1) || "
                          "':' || currency, ',' ORDER BY position) FROM npc_teleport_menu WHERE npc_key = 'Sparta_042'"],
                         text=True, capture_output=True, check=True).stdout.strip()
    assert out == "11:0:30000:silver,1:0:-1:silver,3:1:-1:silver,1094:0:2:gold", out
