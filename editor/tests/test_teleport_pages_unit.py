#!/usr/bin/env python3
"""B22 B26-T (the admin d121-d123): teleporter menu pages (modules/_teleport_pages.py through modules/_teleport_menu.py),
with the fake game server and database of test_teleport_menu_unit.py.
python3 -m pytest -q tests/test_teleport_pages_unit.py"""
import copy
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))
import test_teleport_menu_unit as base  # noqa: E402
from modules import _teleport_menu as T  # noqa: E402
from modules.common import Bad  # noqa: E402

NORTH = [1089, 1088, 1091, 1090, 1092, 1093]


def doc(page_support=True):
    d = copy.deepcopy(base.DOC)
    for sub, name, m in ((1088, "Olympia", 7), (1089, "Delphi Forest", 20), (1090, "Larissa", 10), (1091, "Derveni", 15),
                         (1092, "Elasson", 22)):
        d["teleports"].append({"npc": "Sparta_042", "sub": sub, "name": name, "target_map": m, "level": 100})
    d["teleport_menus"]["page_support"] = page_support
    d["teleport_menus"]["npcs"][0]["pages"] = {"1104": NORTH}
    return d


def fake(page_support=True):
    cap = base.fake()
    d = doc(page_support)
    base.npcs._content = lambda ctx: d
    labels = copy.deepcopy(base.LABELS)
    page = labels["types"]["1"]["pages"]["1"]
    for s in NORTH + [1104, 1105]:
        page[str(s)] = {"buttons": [{"text": "*button %d" % s}]}
    base._npc_menu.menu_data = lambda: labels
    return cap


def save(rows):
    ctx = base.Ctx()
    ctx.body = {"npc": "Sparta_042", "version": "new", "rows": rows}
    return T.save(ctx)


def capital_rows():
    return ([{"sub": 1}, {"sub": 3}, {"sub": 11}, {"sub": 1104, "page_button": True}] +
            [{"sub": s, "page": 1104} for s in NORTH] +
            [{"sub": 1105, "page": 1104, "map": 70, "x": 130, "z": 5, "level": 170, "name": "Aether Sanctum"}])


faked = base.faked


@faked
def test_page_rows_carry_parent_sub_id():
    cap = fake()
    base.npcs._scene = lambda ctx, m: "Scene%d" % m  # map 70 exists in this fake
    out = save(capital_rows())
    ins = [s for s in cap["stmts"] if s.startswith("INSERT")]
    assert len(ins) == 11
    assert "parent_sub_id" in ins[0]
    assert ins[3].endswith("NULL)") and "1104" in ins[3]          # page button: page 1, no map
    assert all(i.endswith(", 1104)") for i in ins[4:])             # page 2 rows point at the page button
    assert "70" in ins[10] and "'Aether Sanctum'" in ins[10]
    assert not any("no words" in w for w in out["warnings"])


@faked
def test_menu_reports_page_support_and_pages():
    fake()
    m = T.menu(base.Ctx(), "Sparta_042")
    assert m["page_support"] is True and m["pages"] == {"1104": NORTH}


@faked
def test_single_page_menu_unchanged():
    """No page in use: rows carry no parent_sub_id key, so an older table / server still takes them."""
    cap = fake(page_support=False)
    save([{"sub": 1}, {"sub": 3}])
    ins = [s for s in cap["stmts"] if s.startswith("INSERT")]
    assert ins and not any("parent_sub_id" in i for i in ins)


@faked
def test_page_refusals():
    fake()

    def refused(rows, why, page_support=True):
        fake(page_support)
        try:
            save(rows)
        except Bad as e:
            assert why in str(e), str(e)
            return
        assert False, "accepted %r" % rows
    north = [{"sub": s, "page": 1104} for s in NORTH]
    refused([{"sub": 1}, {"sub": 1104, "page_button": True}] + north, "no menu pages yet", page_support=False)
    refused([{"sub": 1}] + north, "no button 1104 on page 1")
    refused([{"sub": 1}, {"sub": 1104, "page_button": True}], "has no shown buttons")
    refused([{"sub": 1}, {"sub": 1104, "page_button": True}] + [dict(r, hidden=True) for r in north], "has no shown buttons")
    refused([{"sub": 1}, {"sub": 1104, "page": 1104}] + north, "must be on page 1")
    refused([{"sub": 1}, {"sub": 1104, "page_button": True, "map": 4, "x": 1, "z": 1}] + north, "cannot also go to a map")
    refused([{"sub": 1}, {"sub": 1104, "page_button": True, "price": 5}] + north, "put prices on the buttons")
    many = [{"sub": 1094 + i, "map": 4, "x": 1, "z": 1, "page": 1104} for i in range(10)] + north
    refused([{"sub": 1}, {"sub": 1104, "page_button": True}] + many, "page 1104 shows 16 buttons")
    refused([{"sub": 1, "hidden": True}, {"sub": 1104, "page_button": True, "hidden": True}] + north, "every page-1 button is hidden")


@faked
def test_page1_counted_apart_from_page2():
    """12 on page 1 and 12 on page 2 is fine: the cap is per page."""
    fake()
    p1 = [{"sub": s} for s in (1, 3, 11)] + [{"sub": 1094 + i, "map": 4, "x": 1, "z": 1} for i in range(8)]
    p1.append({"sub": 1104, "page_button": True})
    p2 = [{"sub": s, "page": 1104} for s in NORTH] + [{"sub": 1102, "map": 4, "x": 2, "z": 2, "page": 1104}, {"sub": 1103, "map": 4, "x": 3, "z": 3, "page": 1104}]
    out = save(p1 + p2)
    assert out["ok"]


@faked
def test_hidden_page_button_warns():
    fake()
    out = save([{"sub": 1}, {"sub": 1104, "page_button": True, "hidden": True}] + [{"sub": s, "page": 1104} for s in NORTH])
    assert any("cannot reach its page" in w for w in out["warnings"])


@faked
def test_rig_sql_round_trip():
    """Optional: the generated statements run on a rig copy (GW_TELEMENU_RIG_DB) and are read back."""
    import subprocess
    db = os.environ.get("GW_TELEMENU_RIG_DB")
    if not db:
        return
    cap = fake()
    base.npcs._scene = lambda ctx, m: "Scene%d" % m
    save(capital_rows())
    subprocess.run(["psql", "-d", db, "-qc", "DELETE FROM npc_teleport_menu WHERE npc_key = 'Sparta_042'"], check=True)
    sql = ";\n".join(["BEGIN"] + cap["stmts"] + ["COMMIT"]) + ";"
    subprocess.run(["psql", "-d", db, "-v", "ON_ERROR_STOP=1", "-q"], input=sql, text=True, check=True)
    out = subprocess.run(["psql", "-d", db, "-Atc", "SELECT string_agg(sub_id || ':' || coalesce(parent_sub_id::text, '-'), ',' "
                          "ORDER BY position) FROM npc_teleport_menu WHERE npc_key = 'Sparta_042'"],
                         text=True, capture_output=True, check=True).stdout.strip()
    assert out == "1:-,3:-,11:-,1104:-,1089:1104,1088:1104,1091:1104,1090:1104,1092:1104,1093:1104,1105:1104", out
