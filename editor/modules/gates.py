"""modules/gates.py - "Gates: level ranges & opening times". Page: static/modules/gates.js.

the admin 2026-11-05: triage A5 ("make the ranges and the event times editable"), F-18 ("build the full GATES-SPEC table before
release") and F-64 ("move the compiled event times into event_schedule_overrides").

Server side (finalise2 P1 build, branch f2-p1-gates, GodsWar-2.51/Polish/reborn-src-f2-p1-gates):
  * Application/Gates/GateCatalog.cs + GateSchedules.cs: one catalogue of every gate (ordinary transporters, battlefields,
    instances, events) and one rule set (band, opening mode, GM rule). GET /gate-content on the loopback management port
    describes every gate: compiled values, the editor's rows, the effective values and their sources, open now, next opening.
    This page reads ONLY that, so it never carries its own copy of the defaults (GATES-SPEC 4).
  * public.level_band_overrides (gate PK, minimum_level, maximum_level, note, schedule_mode, gm_bypass): created / extended by the
    server at start-up (NpcContentRuntime.SchemaSql), read hot (15 s poll or POST /npc-content/reload). No row = compiled
    (the admin's answers). NULL side = keep the compiled value on that side.
  * public.event_schedule_overrides (activity, day_of_week, start_minute, duration_minutes): a gate's custom windows, activity =
    the gate's window_activity (Pindus / NiMini / LelantineFarm keep their old names; rows alone switch those three on, as the
    Events page always did; every other gate needs schedule_mode = 'windows').
  * GM rule: gm_bypass per gate (NULL = compiled), under the global rate gm.obey_gates (Server rates page; the admin F-17 keeps 0).

Times are REALM time: Australia/Brisbane (AEST) on live (the admin F-04).
Before the P1 server build runs, GET /gate-content is missing: the page then says so and saves nothing.
"""
from . import common, world as W
from .common import Bad, lit

M = common.module(
    "gates", "Gates: levels & opening times",
    icon={"sprite": 684},
    order=26,
    status="ready",
    description="Who may enter each transporter destination, battlefield, instance and event, and when it is open "
                "(AEST). Read by the game within 15 seconds.",
    needs_character=False,
)

TABLE = "level_band_overrides"
WINDOWS = "event_schedule_overrides"
MODES = ("compiled", "always", "windows", "closed")
LEVEL_CAP = 200
WINDOW_MAX = 42


def _parts(gate, activity):
    return [(TABLE, "gate = %s" % lit(gate), "gate"),
            (WINDOWS, "activity = %s" % lit(activity), "day_of_week, start_minute")]


def _describe(ctx):
    port = common.mgmt_port(ctx.app)
    if not port:
        return None, "no game server management port is configured for this database"
    try:
        st, doc = common._http("GET", port, "/gate-content", timeout=8.0)
    except Exception as e:  # noqa: BLE001
        return None, "the game server did not answer (%s)" % e
    if st != 200 or not isinstance(doc, dict) or "gates" not in doc:
        return None, ("the running game server has no Gates table yet (GET /gate-content answered HTTP %s). It arrives with the "
                      "finalise2 P1 server build; nothing can be saved until that build runs here." % st)
    return doc, None


def _columns_ready(ctx):
    return bool(W._v(ctx, "(SELECT count(*) = 2 FROM information_schema.columns WHERE table_schema = 'public' "
                          "AND table_name = %s AND column_name IN ('schedule_mode', 'gm_bypass'))" % lit(TABLE)))


def _reload(ctx):
    try:
        common._http("POST", common.mgmt_port(ctx.app), "/npc-content/reload", timeout=4.0)
    except Exception:          # noqa: BLE001 - the server also polls every 15 seconds
        pass


@M.get("overview")
def overview(ctx):
    doc, why = _describe(ctx)
    if doc is None:
        return {"ready": False, "why_missing": why, "gates": []}
    ready = _columns_ready(ctx)
    notes = W._q(ctx, "(SELECT coalesce(json_object_agg(gate, note), '{}') FROM %s)" % TABLE) if ready else {}
    for g in doc["gates"]:
        g["note"] = (notes or {}).get(g["key"], "")
        g["version"] = W._version(ctx, _parts(g["key"], g["window_activity"])) if ready else None
    doc.update(ready=ready, applies="hot_reload",
               why_missing=None if ready else "The table %s has no schedule_mode / gm_bypass columns yet: the P1 server build adds "
                                              "them at start-up." % TABLE)
    return doc


def _level(v, what):
    if v in (None, ""):
        return None
    return W._int(v, 1, LEVEL_CAP, what)


def window_rows(windows):
    if not isinstance(windows, list) or not 1 <= len(windows) <= WINDOW_MAX:
        raise Bad("custom times need 1-%d openings" % WINDOW_MAX)
    rows, seen = [], set()
    for i, w in enumerate(windows):
        what = "opening %d" % (i + 1)
        key = (W._int(w.get("day"), 0, 6, what + " day"), W._int(w.get("start_minute"), 0, 1439, what + " time"))
        if key in seen:
            raise Bad("%s: this day and time is listed twice" % what)
        seen.add(key)
        rows.append({"day_of_week": key[0], "start_minute": key[1],
                     "duration_minutes": W._int(w.get("duration_minutes"), 1, 1440, what + " length (1-1440 minutes)")})
    return rows


def validate(gate, b):
    """The row and window rows to write for one gate, from the request body and the server's description of that gate."""
    mn = _level(b.get("minimum_level"), "minimum level")
    mx = _level(b.get("maximum_level"), "maximum level")
    if not gate.get("has_level_band") and (mn is not None or mx is not None):
        raise Bad("%s has no level band (it is a schedule only)" % gate["name"])
    lo = mn if mn is not None else gate["compiled"]["minimum"]
    hi = mx if mx is not None else gate["compiled"]["maximum"]
    if lo > hi:
        raise Bad("the minimum level %d is above the maximum %d (counting the default for an empty side): nobody could enter" % (lo, hi))
    mode = b.get("schedule_mode") or "compiled"
    if mode not in MODES:
        raise Bad("opening must be one of: default, always open, closed, custom times")
    gm = b.get("gm_bypass")
    if gm not in (None, True, False):
        raise Bad("GM skips must be default, yes or no")
    if gm is not None and not gate.get("supports_gm_bypass"):
        raise Bad("%s checks every party member on the server; a GM skip cannot be set here" % gate["name"])
    windows = []
    if mode == "windows":
        windows = window_rows(b.get("windows"))
    elif b.get("windows"):
        raise Bad("opening times are kept only with 'Custom times'")
    note = W._text(b.get("note", ""), "note", 200, empty=True)
    row = None
    if mn is not None or mx is not None or mode != "compiled" or gm is not None:
        row = {"gate": gate["key"], "minimum_level": mn, "maximum_level": mx, "schedule_mode": mode, "gm_bypass": gm, "note": note}
    return row, windows


@M.post("save")
def save(ctx):
    """POST {gate, minimum_level|null, maximum_level|null, schedule_mode, gm_bypass|null, windows:[...], note, version}.
    Writes the gate's level_band_overrides row and its event_schedule_overrides rows in one transaction; an all-default
    request removes both (= Reset)."""
    b = ctx.body or {}
    doc, why = _describe(ctx)
    if doc is None:
        raise Bad(why)
    if not _columns_ready(ctx):
        raise Bad("the Gates columns are not in this database yet (P1 server build)")
    gates = {g["key"]: g for g in doc["gates"]}
    key = str(b.get("gate") or "")
    if key not in gates:
        raise Bad("unknown gate %r" % key)
    gate = gates[key]
    row, windows = ({}, []) if b.get("reset") else validate(gate, b)
    activity = gate["window_activity"]
    stmts = [W._ver_guard(_parts(key, activity), b.get("version"), "this gate"),
             "DELETE FROM %s WHERE activity = %s" % (WINDOWS, lit(activity))]
    if row:
        stmts.append(W._upsert(TABLE, ["gate"], row, touch=False))
    else:
        stmts.append("DELETE FROM %s WHERE gate = %s" % (TABLE, lit(key)))
    stmts += [common.insert_sql(WINDOWS, dict(w, activity=activity)) for w in windows]
    if not row:
        act = "%s: back to the defaults" % key
    else:
        act = "%s: level %s-%s, opening %s%s, GM skips %s" % (
            key, row["minimum_level"] or "default", row["maximum_level"] or "default", row["schedule_mode"],
            " (%d times)" % len(windows) if windows else "", {None: "default", True: "yes", False: "no"}[row["gm_bypass"]])
    out = W._write(ctx,
                   [common.scope(TABLE, "gate = %s" % lit(key), pk=["gate"]),
                    common.scope(WINDOWS, "activity = %s" % lit(activity), pk=["activity", "day_of_week", "start_minute"])],
                   act, stmts, ["%s.*" % TABLE, "%s.*" % WINDOWS], applies="hot_reload", target="gate " + key)
    _reload(ctx)
    return out
