#!/usr/bin/env python3
"""Unit test: the Gates page's validation (modules/gates.py) and, optionally, a live round trip against a PRIVATE rig.
  python3 -I tests/test_gates_unit.py                      # validation only (no database, no server)
  python3 -I tests/test_gates_unit.py --describe FILE.json  # also validate every gate of a saved GET /gate-content answer
The server owns the defaults (GET /gate-content); the editor only checks what it writes."""
import argparse, json, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))
from modules import gates  # noqa: E402
from modules.common import Bad  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--describe")
a = ap.parse_args()
FAIL = []


def step(msg, ok, detail=""):
    print(("ok   " if ok else "FAIL ") + msg + ("" if ok else "  -> " + str(detail)[:400]))
    if not ok:
        FAIL.append(msg)


def refused(gate, body):
    try:
        gates.validate(gate, body)
    except Bad as e:
        return str(e)
    return None


TRIALS = {"key": "instance.training_ground", "name": "Trials of Hercules", "has_level_band": True, "supports_gm_bypass": False,
          "window_activity": "instance.training_ground", "compiled": {"minimum": 60, "maximum": 200}}
MYC = {"key": "transport.mycenae", "name": "Mycenae", "has_level_band": True, "supports_gm_bypass": True,
       "window_activity": "transport.mycenae", "compiled": {"minimum": 130, "maximum": 200}}
SPIES = {"key": "event.spy_invasion", "name": "Spy invasion", "has_level_band": False, "supports_gm_bypass": True,
         "window_activity": "event.spy_invasion", "compiled": {"minimum": 1, "maximum": 200}}

row, win = gates.validate(TRIALS, {})
step("all defaults = no row (reset)", row is None and win == [], (row, win))
row, win = gates.validate(TRIALS, {"minimum_level": 75})
step("a minimum alone keeps the default maximum (NULL side)", row["minimum_level"] == 75 and row["maximum_level"] is None and row["schedule_mode"] == "compiled", row)
step("min above max refused", refused(TRIALS, {"minimum_level": 150, "maximum_level": 100}) is not None)
step("min above the default max refused", refused(MYC, {"minimum_level": 201}) is not None)
step("level 0 refused", refused(TRIALS, {"minimum_level": 0}) is not None)
step("unknown mode refused", refused(TRIALS, {"schedule_mode": "sometimes"}) is not None)
step("GM skip refused on a party instance", refused(TRIALS, {"gm_bypass": True}) is not None)
row, _ = gates.validate(MYC, {"gm_bypass": True})
step("GM skip allowed on a transporter", row["gm_bypass"] is True, row)
step("custom times need at least one opening", refused(MYC, {"schedule_mode": "windows", "windows": []}) is not None)
row, win = gates.validate(MYC, {"schedule_mode": "windows", "windows": [{"day": 6, "start_minute": 1365, "duration_minutes": 45},
                                                                       {"day": 0, "start_minute": 1410, "duration_minutes": 90}]})
step("custom times: two openings, one past midnight", row["schedule_mode"] == "windows" and len(win) == 2 and win[1]["duration_minutes"] == 90, win)
step("duplicate opening refused", refused(MYC, {"schedule_mode": "windows", "windows": [{"day": 1, "start_minute": 60, "duration_minutes": 5}] * 2}) is not None)
step("length over a day refused", refused(MYC, {"schedule_mode": "windows", "windows": [{"day": 1, "start_minute": 60, "duration_minutes": 1441}]}) is not None)
step("windows without custom mode refused", refused(MYC, {"schedule_mode": "always", "windows": [{"day": 1, "start_minute": 60, "duration_minutes": 5}]}) is not None)
step("a schedule-only gate takes no levels", refused(SPIES, {"minimum_level": 10}) is not None)
row, _ = gates.validate(SPIES, {"schedule_mode": "closed"})
step("a schedule-only gate can be closed", row["schedule_mode"] == "closed", row)

if a.describe:
    doc = json.load(open(a.describe))
    step("describe: has gates", len(doc.get("gates", [])) >= 30, len(doc.get("gates", [])))
    for g in doc["gates"]:
        r, w = gates.validate(g, {})
        ok = r is None and w == [] and {"key", "window_activity", "compiled", "effective", "has_level_band", "supports_gm_bypass"} <= set(g)
        step("describe gate %s validates with defaults" % g["key"], ok, g)

print("FAILED: %d" % len(FAIL) if FAIL else "all passed")
sys.exit(1 if FAIL else 0)
