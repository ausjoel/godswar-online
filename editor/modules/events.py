"""modules/events.py - battlefield opening times and scheduled notices. Backend for static/modules/events.js.

The game server hands over its compiled windows (GET /event-content on the loopback management port) and reads the
editor's rows from event_schedule_overrides / server_notices / notice_settings (hot reload, no restart).
No row = the compiled behaviour.
"""
import datetime
import re

from . import common, world as W
from .common import Bad

M = common.module("events", "Events & notices", icon={"sprite": 684}, order=26, status="ready",
                  description="When the battlefields open, and messages the server sends by itself on a schedule.")

ACTIVITIES = ("Pindus", "NiMini")
CLOSED_DAY = -1
NOTICE_MAX = 50
WINDOW_MAX = 42
TEXT_MAX = 106                 # PacketBuilder.CenteredRedAnnouncementMaximumTextLength
NOTICE_PARTS = [("server_notices", "true", "id"), ("notice_settings", "true", "name")]
MESSAGE_PART = ("server_messages", "true", "name")  # created by game servers from 2026-11-10 (NpcContentRuntime)
DELIVERIES = ("banner", "log", "board", "chat")  # chat = a World line in the chat box (the admin 2026-11-10)
# Read from the server source. Compiled, so the page only shows them.
FIXED = [
    ("Zeus' Gift exchange", "Saturday 12:00 to Sunday 23:55, realm time"),
    ("Zeus' Gift claims", "Sunday 12:00 to 23:55, realm time"),
    ("Zeus' Gift praying stones", "Monday to Friday, realm time"),
    ("Faction Crier nameplate", "Closed on Sunday, realm time"),
    ("Daily quests", "Reset at 12:00 on the VPS clock (UTC), which is 22:00 in Brisbane"),
]


# W3 seasonal events (the admin F-26): every season starts off; on/off plus first and last day, entered as Brisbane dates
# (AEST, UTC+10, no daylight saving) and stored in UTC. Labels mirror server Application/Seasons/SeasonalEvents.cs.
SEASONS = [
    ("spring_festival", "Spring Festival", "Spring Festival Envoy (gold plate, ham, rice cake, chocolate, rose and couple exchanges), Lunar Beast Slayer (firecrackers) and festival-goods / firecracker drops in both suburbs; suggested window: the 10 days up to Lunar New Year (F-134); the date moves, so set it with yearly off each year (2027: 27 Jan - 6 Feb, to verify)"),
    ("carnival", "Carnival", "Carnival Envoy"),
    ("easter", "Easter", "Easter Envoy, Easter Bunny Envoy"),
    ("may_day", "May Day", "May Day Envoy at (159,76): Fertiliser grows the faction tree, winners claim gift packs"),
    ("igg_anniversary", "IGG Anniversary", "IGG Anniversary Envoy"),
    ("mid_autumn", "Mid-autumn", "Lunar Priest"),
    ("oriental_cuisine", "Oriental Cuisine", "Oriental Cuisine Master"),
    ("halloween", "Halloween", "Halloween Envoy (suit, wand, Pumpkin Candy exchange) and candy drops"),
    ("thanksgiving", "Thanksgiving", "Thanksgiving Envoy (turkey meat exchange) and turkey drops on Marathon / Peloponnese"),
    ("christmas", "Christmas", "Santa Claus (gloves, antler turn-in to each faction's reindeer model, level rewards) and Antler drops from Little Deer / Vicious Fawn; suggested window: the 10 days up to 25 December (F-134)"),
]
SEASON_PARTS = [("seasonal_events", "true", "event_key")]
BRISBANE = datetime.timezone(datetime.timedelta(hours=10))


def _season_day(v, what):
    if v in (None, ""):
        return None
    if not isinstance(v, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", v):
        raise Bad(what + " must be a date (YYYY-MM-DD)")
    try:
        return datetime.date.fromisoformat(v)
    except ValueError:
        raise Bad(what + " is not a real date")


def season_row(r):
    if not isinstance(r, dict) or r.get("event_key") not in {k for k, _, _ in SEASONS}:
        raise Bad("unknown season")
    name = dict((k, n) for k, n, _ in SEASONS)[r["event_key"]]
    first, last = _season_day(r.get("first_day"), name + " first day"), _season_day(r.get("last_day"), name + " last day")
    if (first is None) != (last is None):
        raise Bad(name + ": give both the first and the last day, or neither (switched on with no dates = always open)")
    if first and last < first:
        raise Bad(name + ": the last day is before the first day")
    start = end = None
    if first:
        start = datetime.datetime.combine(first, datetime.time(0, 0), BRISBANE).astimezone(datetime.timezone.utc)
        end = datetime.datetime.combine(last + datetime.timedelta(days=1), datetime.time(0, 0), BRISBANE).astimezone(datetime.timezone.utc)
    return {"event_key": r["event_key"], "enabled": bool(r.get("enabled")), "yearly": bool(r.get("yearly", True)),
            "starts_at": start.isoformat() if start else None, "ends_at": end.isoformat() if end else None}


def _parts(activity):
    return [("event_schedule_overrides", "activity = %s" % common.lit(activity), "day_of_week, start_minute")]


def _scope(activity):
    return common.scope("event_schedule_overrides", "activity = %s" % common.lit(activity),
                        pk=["activity", "day_of_week", "start_minute"])


def _content(ctx):
    port = common.mgmt_port(ctx.app)
    if not port:
        raise Bad("no game server management port is configured for this database")
    st, doc = common._http("GET", port, "/event-content", timeout=8.0)
    if st != 200 or not isinstance(doc, dict):
        raise Bad("the running game server does not offer event editing yet (HTTP %s)" % st)
    return doc


def _reload(ctx):
    """Best effort: the game server also polls every 15 seconds."""
    try:
        common._http("POST", common.mgmt_port(ctx.app), "/npc-content/reload", timeout=4.0)
    except Exception:
        pass


def _activity(v):
    if v not in ACTIVITIES:
        raise Bad("unknown activity")
    return v


def _day(v, what):
    if v in (None, ""):
        return None
    try:
        return datetime.date.fromisoformat(str(v)).isoformat()
    except ValueError:
        raise Bad("%s must be a date" % what)


def check_notice_text(v, what):
    text = W._text(v, what, TEXT_MAX)
    if not re.fullmatch(r"[ -~]+", text) or "|c" in text.lower():
        raise Bad("%s: the game shows plain English letters, digits and punctuation only" % what)
    return text


def notice_row(r, index):
    what = "notice %d" % index
    start = W._int(r.get("start_minute"), 0, 1439, what + " time")
    repeat = W._int(r.get("repeat_minutes", 0), 0, 1439, what + " repeat")
    end = W._int(r.get("end_minute", start), 0, 1439, what + " last time") if repeat else start
    if end < start:
        raise Bad("%s: the last time is before the first (a notice can't run past midnight; add a second one)" % what)
    first, last = _day(r.get("first_day"), what + " first day"), _day(r.get("last_day"), what + " last day")
    if first and last and last < first:
        raise Bad("%s: the last day is before the first day" % what)
    delivery = r.get("delivery")
    if delivery not in DELIVERIES:
        raise Bad("%s: choose how it is shown" % what)
    return {"id": index, "message": check_notice_text(r.get("message"), what), "delivery": delivery,
            "day_mask": W._int(r.get("day_mask"), 1, 127, what + " days"), "start_minute": start, "end_minute": end,
            "repeat_minutes": repeat, "first_day": first, "last_day": last, "enabled": bool(r.get("enabled", True))}


def _has_messages(ctx):
    return bool(W._v(ctx, "(to_regclass('public.server_messages') IS NOT NULL)"))


def _notice_parts(ctx):
    """The notice tables plus server_messages once a game server new enough to create it has run."""
    return NOTICE_PARTS + [MESSAGE_PART] if _has_messages(ctx) else NOTICE_PARTS


def welcome_row(w):
    """The server welcome (server_messages 'welcome'): one board line per login, off by default. None = leave as is."""
    if w is None:
        return None
    if not isinstance(w, dict):
        raise Bad("the welcome message is malformed")
    enabled = bool(w.get("enabled"))
    raw = str(w.get("message") or "").strip()
    if enabled and not raw:
        raise Bad("the welcome message is on but empty")
    message = check_notice_text(raw, "welcome message") if raw else ""
    return {"name": "welcome", "message": message, "enabled": enabled}


def window_rows(windows):
    if not isinstance(windows, list) or not 1 <= len(windows) <= WINDOW_MAX:
        raise Bad("a schedule has 1-%d opening times (or mark it closed)" % WINDOW_MAX)
    rows, seen = [], set()
    for i, w in enumerate(windows):
        what = "opening %d" % (i + 1)
        key = (W._int(w.get("day"), 0, 6, what + " day"), W._int(w.get("start_minute"), 0, 1439, what + " time"))
        if key in seen:
            raise Bad("%s: this day and time is listed twice" % what)
        seen.add(key)
        rows.append({"day_of_week": key[0], "start_minute": key[1],
                     "duration_minutes": W._int(w.get("duration_minutes"), 1, 1440, what + " length")})
    return rows


@M.get("overview")
def overview(ctx):
    doc = _content(ctx)
    over = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(o) ORDER BY day_of_week, start_minute), '[]') FROM event_schedule_overrides o)")
    for a in doc["activities"]:
        a["override"] = [o for o in over if o["activity"] == a["key"]] or None
        a["version"] = W._version(ctx, _parts(a["key"]))
    notices = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(n) ORDER BY id), '[]') FROM server_notices n)")
    stored = W._q(ctx, "(SELECT coalesce(json_object_agg(name, value), '{}') FROM notice_settings)")
    has_messages = _has_messages(ctx)
    messages = W._q(ctx, "(SELECT coalesce(json_object_agg(name, json_build_object('message', message, 'enabled', enabled)), '{}') "
                         "FROM server_messages)") if has_messages else {}
    stored_seasons = {s["event_key"]: s for s in W._q(ctx, "(SELECT coalesce(json_agg(json_build_object('event_key', event_key, "
        "'enabled', enabled, 'yearly', yearly, 'first_day', to_char(starts_at AT TIME ZONE 'Australia/Brisbane', 'YYYY-MM-DD'), "
        "'last_day', to_char((ends_at AT TIME ZONE 'Australia/Brisbane') - interval '1 day', 'YYYY-MM-DD'))), '[]') FROM seasonal_events)")}
    doc["seasons"] = [dict(stored_seasons.get(k, {"enabled": False, "yearly": True, "first_day": None, "last_day": None}),
                           event_key=k, name=n, what=w) for k, n, w in SEASONS]
    doc["seasons_version"] = W._version(ctx, SEASON_PARTS)
    doc.update(notices=notices, notices_version=W._version(ctx, _notice_parts(ctx)), fixed=[{"name": n, "when": w} for n, w in FIXED],
               population_minutes=stored.get("population_minutes", doc["population_default"]),
               population_saved=stored.get("population_minutes_saved") or doc["population_default"] or 5, tick_seconds=15,
               welcome=messages.get("welcome") or {"message": "", "enabled": False}, welcome_available=has_messages)
    return doc


@M.post("schedule/save")
def schedule_save(ctx):
    b = ctx.body
    activity = _activity(b.get("activity"))
    rows = [{"day_of_week": CLOSED_DAY, "start_minute": 0, "duration_minutes": 1}] if b.get("closed") else window_rows(b.get("windows"))
    stmts = [W._ver_guard(_parts(activity), b.get("version"), "this schedule"),
             "DELETE FROM event_schedule_overrides WHERE activity = %s" % common.lit(activity)]
    stmts += [common.insert_sql("event_schedule_overrides", dict(r, activity=activity)) for r in rows]
    out = W._write(ctx, [_scope(activity)], "%s schedule: %s" % (activity, "closed" if b.get("closed") else "%d openings" % len(rows)),
                   stmts, ["event_schedule_overrides.*"], target="schedule " + activity)
    _reload(ctx)
    return out


@M.post("schedule/reset")
def schedule_reset(ctx):
    activity = _activity(ctx.body.get("activity"))
    out = W._write(ctx, [_scope(activity)], "%s schedule: back to the original times" % activity,
                   [W._ver_guard(_parts(activity), ctx.body.get("version"), "this schedule"),
                    "DELETE FROM event_schedule_overrides WHERE activity = %s" % common.lit(activity)],
                   ["event_schedule_overrides.*"], target="schedule " + activity)
    _reload(ctx)
    return out


@M.post("notices/save")
def notices_save(ctx):
    b = ctx.body
    rows = b.get("rows")
    if not isinstance(rows, list) or len(rows) > NOTICE_MAX:
        raise Bad("the list must have 0-%d notices" % NOTICE_MAX)
    if "population_enabled" in b:
        # An explicit switch: the server reads only population_minutes (0 = off), so "off" stores 0 there and keeps the
        # chosen interval in population_minutes_saved (a name the server ignores) for when it is switched back on.
        enabled = bool(b.get("population_enabled"))
        interval = W._int(b.get("population_minutes"), 1, 1440, "population notice interval")
        population = interval if enabled else 0
        extra = [] if enabled else [common.insert_sql("notice_settings", {"name": "population_minutes_saved", "value": interval})]
    else:
        population = W._int(b.get("population_minutes"), 0, 1440, "population notice interval")
        extra = []
    parts = _notice_parts(ctx)
    welcome = welcome_row(b.get("welcome")) if MESSAGE_PART in parts else None
    stmts = [W._ver_guard(parts, b.get("version"), "the notices"), "DELETE FROM server_notices", "DELETE FROM notice_settings"]
    stmts += [common.insert_sql("server_notices", notice_row(r, i + 1)) for i, r in enumerate(rows)]
    stmts.append(common.insert_sql("notice_settings", {"name": "population_minutes", "value": population}))
    stmts += extra
    scopes = [common.scope("server_notices", "true", pk=["id"]), common.scope("notice_settings", "true", pk=["name"])]
    touched = ["server_notices.*", "notice_settings.*"]
    if welcome is not None:
        stmts += ["DELETE FROM server_messages WHERE name = 'welcome'", common.insert_sql("server_messages", welcome)]
        scopes.append(common.scope("server_messages", "true", pk=["name"]))
        touched.append("server_messages.*")
    out = W._write(ctx, scopes,
                   "notices: %d, population line %s%s" % (len(rows), "every %d min" % population if population else "off",
                                                          "" if welcome is None else ", welcome %s" % ("on" if welcome["enabled"] else "off")),
                   stmts, touched, target="notices")
    _reload(ctx)
    return out


@M.post("seasons/save")
def seasons_save(ctx):
    rows = ctx.body.get("rows")
    if not isinstance(rows, list) or not rows or len(rows) > len(SEASONS):
        raise Bad("the season list is malformed")
    clean = [season_row(r) for r in rows]
    stmts = [W._ver_guard(SEASON_PARTS, ctx.body.get("version"), "the seasons")]
    stmts += ["DELETE FROM seasonal_events WHERE event_key = %s" % common.lit(r["event_key"]) for r in clean]
    stmts += [common.insert_sql("seasonal_events", r) for r in clean]
    on = [r["event_key"] for r in clean if r["enabled"]]
    out = W._write(ctx, [common.scope("seasonal_events", "true", pk=["event_key"])],
                   "seasons: %s" % (", ".join(on) if on else "all off"), stmts, ["seasonal_events.*"], target="seasons")
    _reload(ctx)
    return out


@M.post("board/post")
def board_post(ctx):
    """the admin 2026-11-08: post one line to every online player's board above the chat box, now (client 1.4.3; older
    clients get it as a game log line). Goes through the loopback management port; nothing is stored."""
    text = check_notice_text(ctx.body.get("text"), "the board message")
    port = common.mgmt_port(ctx.app)
    if not port:
        raise Bad("no game server management port is configured for this database")
    actor = (getattr(common.ACTOR, "name", None) or "editor")[:64]
    st, doc = common._http("POST", port, "/admin/board/post", {"actor": "web:" + actor, "text": text}, timeout=8.0)
    if st != 200 or not isinstance(doc, dict) or not doc.get("ok"):
        raise Bad("the running game server did not take the post (HTTP %s: %s); it needs the release of 2026-11-08" % (st, doc))
    return {"mode": "live", "delivered": doc.get("delivered", 0)}
