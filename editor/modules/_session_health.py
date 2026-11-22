"""modules/_session_health.py - turns the game server's login records into plain answers. Wave 6 package A.

Pure functions (no database, no clock of their own), used by modules/player_health.py and tested in
tests/test_player_health_unit.py.

The game server writes one row of public.session_health per connection that sent a login packet:
  endpoint 'login' (port 6999): password check, server list, redirect to the game port
  endpoint 'game'  (port 7000): hand-over, character screen, world entry, how the session ended
A player's login is the two rows together, so rows are first paired into ATTEMPTS, then each attempt is explained:
level ok / info / warn / bad, a title in plain words, what was measured, and what to do about it.
"""
import datetime
import re

BRISBANE = datetime.timezone(datetime.timedelta(hours=10))      # the owner's time zone; no daylight saving
PAIR_SECONDS = 180          # a game connection belongs to the password login that redirected it within 3 minutes
EARLY_DROP_SECONDS = 60     # "dropped within a minute of entering the world"
LEVEL_ORDER = {"ok": 0, "info": 1, "warn": 2, "bad": 3}
# A session that "faulted" with one of these only lost its connection (game closed, internet dropped).
NETWORK_ERRORS = {"IOException", "SocketException", "EndOfStreamException", "ObjectDisposedException"}
ENTER_GAME_OPCODE = 10006

# What the client sends as its version (the number it reads from patch.vn). Every release so far ships the same
# patch.vn, so this cannot yet tell our releases apart; the page says so.
BUILD_NAMES = {4102004: "2.51.002"}

# Server error texts the owner should not have to decode. (pattern, plain words). First match wins.
ERROR_WORDS = [
    (r"packed Seal Jade requires a native-width linked pet ID",
     "A packed Seal Jade in the bag has no pet inside it"),
    (r"Pet \d+ has unknown species", "A pet has a species this server does not know"),
    (r"Pet \d+ has a native-incompatible aptitude, sex, or level", "A pet has an aptitude, sex or level outside the game's range"),
    (r"Pet \d+ .*skill", "A pet has skills the game cannot show"),
    (r"owned-pet projection exceeds|at most \d+ owned pets", "The character holds more pets than its pet shed has room for"),
    (r"at most one carried pet|summoned pet must also be the carried pet", "More than one pet is marked as carried or summoned"),
    (r"Owned titles contain invalid or duplicate", "The title list holds a title twice or an unknown title"),
    (r"Character skills contain invalid or duplicate", "The skill list holds a skill twice or an unknown skill"),
    (r"Character talents contain invalid or duplicate", "The talent list holds a talent twice or an unknown talent"),
    (r"wallet contains a negative balance", "Silver or gold is below zero"),
    (r"vitals are outside|progression is outside|calculated stats are outside", "Level, HP, MP or stats are outside the game's range"),
    (r"zodiac", "The zodiac data is outside the game's range"),
    (r"position must be finite|location is missing", "The saved position is not a place on a map"),
    (r"account no longer exists", "The account no longer exists"),
    (r"SingleCharacterV1 found more than one character", "The account holds more than one character"),
]


def plain_error(text):
    """A server error message in plain words; the original when it is not one we know."""
    text = (text or "").strip()
    for pattern, words in ERROR_WORDS:
        if re.search(pattern, text):
            return words
    return text


def plain_quarantine(text):
    """'bag slot 17 item 10109: <server message>; ...' -> the same in plain words."""
    parts = []
    for part in str(text or "").split("; "):
        where, _, message = part.partition(": ")
        m = re.match(r"(bag|equipment) slot (\d+) item (\d+)$", where)
        if m and m.group(1) == "bag":
            where = "bag page %d, slot %d" % (int(m.group(2)) // 24 + 1, int(m.group(2)) % 24 + 1)
        elif m:
            where = "equipment slot %d" % (int(m.group(2)) + 1)
        parts.append("%s (%s)" % (plain_error(message), where) if message else where)
    return "; ".join(parts)


def parse_time(value):
    """A timestamptz as PostgreSQL's json prints it -> aware datetime (UTC)."""
    if isinstance(value, datetime.datetime):
        return value if value.tzinfo else value.replace(tzinfo=datetime.timezone.utc)
    text = str(value).strip().replace(" ", "T").replace("Z", "+00:00")
    text = re.sub(r"(\.\d{1,6})\d*", r"\1", text)
    text = re.sub(r"([+-]\d\d)$", r"\1:00", text)
    text = re.sub(r"\.(\d{1,5})(?=[+-])", lambda m: "." + m.group(1).ljust(6, "0"), text)
    return datetime.datetime.fromisoformat(text).astimezone(datetime.timezone.utc)


def local_text(when):
    return when.astimezone(BRISBANE).strftime("%Y-%m-%d %H:%M")


def local_day(when):
    return when.astimezone(BRISBANE).strftime("%Y-%m-%d")


def duration_text(ms):
    seconds = max(0, int(ms or 0)) // 1000
    if seconds < 90:
        return "%d seconds" % seconds
    minutes = seconds // 60
    if minutes < 90:
        return "%d minutes" % minutes
    return "%d h %02d min" % (minutes // 60, minutes % 60)


def build_text(build):
    if build is None:
        return ""
    name = BUILD_NAMES.get(int(build))
    return "%s (%d)" % (name, int(build)) if name else str(int(build))


def _clean(row):
    r = dict(row)
    r["at"] = parse_time(r["started_at"])
    r["stages"] = r.get("stages") if isinstance(r.get("stages"), dict) else {}
    r["duration_ms"] = int(r.get("duration_ms") or 0)
    return r


def pair(rows):
    """Rows of ONE account (any order) -> attempts, oldest first: {"at", "login": row|None, "game": row|None}."""
    rows = sorted((_clean(r) for r in rows), key=lambda r: (r["at"], r.get("id") or 0))
    attempts, open_login = [], None
    for r in rows:
        if r["endpoint"] == "login":
            attempt = {"at": r["at"], "login": r, "game": None}
            attempts.append(attempt)
            open_login = attempt if r.get("outcome") == "accepted" and "redirect" in r["stages"] else None
        else:
            if open_login is not None and 0 <= (r["at"] - open_login["at"]).total_seconds() <= PAIR_SECONDS:
                open_login["game"] = r
            else:
                attempts.append({"at": r["at"], "login": None, "game": r})
            open_login = None
    return attempts


LOGIN_REFUSALS = {
    "bad_credentials": ("warn", "Wrong password",
                        "The user name exists but the password did not match.",
                        "Ask the player to type it again (check Caps Lock), or set a new password on the Accounts page."),
    "unknown_account": ("info", "Unknown user name", "Nobody has an account with the name that was typed.", ""),
    "banned": ("bad", "The account is blocked", "The account is banned or waiting to be deleted, so the server refuses it.",
               "Unblock or restore it on the Accounts page if that was not intended."),
    "old_client": ("bad", "The game client is too old", "The server only accepts newer clients.",
                   "Ask the player to start the game through the launcher so it updates."),
    "malformed": ("bad", "The login was not readable", "The login message was too short or had no user name.",
                  "The player is probably not using our client; ask them to install it again from the website."),
    "server_profile": ("bad", "The server refused this kind of login", "The server is set to refuse password logins of this kind.",
                       "This is a server setting, not the player's fault."),
    "ticket_refused": ("warn", "The server could not start the login", "The server could not create the login ticket.",
                       "Try again; if it repeats, look at the server log."),
}


def explain(attempt, now=None):
    """One attempt -> {"level", "title", "detail", "fix", "code"} in plain words."""
    login, game = attempt.get("login"), attempt.get("game")
    out = _explain(login, game, attempt["at"], now)
    if game and game.get("quarantined"):
        hidden = "The server left this out so the login could go on: %s. It is still in the database." % plain_quarantine(game["quarantined"])
        if LEVEL_ORDER[out["level"]] < LEVEL_ORDER["warn"]:
            out = {"level": "warn", "code": "quarantined", "title": "Logged in, but something was hidden from the player",
                   "detail": hidden, "fix": "Run the Login check below and use its Fix button."}
        else:
            out = dict(out, detail=(out["detail"] + " " + hidden).strip())
    return out


def _explain(login, game, at, now):
    def answer(level, code, title, detail="", fix=""):
        return {"level": level, "code": code, "title": title, "detail": detail, "fix": fix}

    if game is None:
        reason = login.get("reason") or ""
        if login.get("outcome") == "refused":
            level, title, detail, fix = LOGIN_REFUSALS.get(reason, ("warn", "Login refused", "The server refused the login (%s)." % (reason or "no reason recorded"), ""))
            return answer(level, "login_" + (reason or "refused"), title, detail, fix)
        if login.get("error_type"):
            return answer("bad", "login_fault", "Server error during login", plain_error(login.get("error_text")) or login["error_type"],
                          "Look at the server log for this time.")
        if "redirect" not in login["stages"]:
            return answer("info", "stopped_at_server_list", "Stopped at the server list",
                          "The password was accepted; the player did not pick a server (closed the game, or it stopped responding).",
                          "")
        if now is not None and (now - at).total_seconds() < PAIR_SECONDS:
            return answer("info", "in_progress", "Logging in now", "The password was accepted a moment ago.", "")
        return answer("bad", "never_reached_game", "Never reached the game server",
                      "The password was accepted and the server list answered, but no connection to the game port followed.",
                      "The player's network may block port 7000, or the client crashed while it loaded. Ask them to run "
                      "the launcher's check.")

    stages, outcome, reason = game["stages"], game.get("outcome") or "", game.get("reason") or ""
    if outcome == "fault" and (game.get("error_type") or "") in NETWORK_ERRORS:
        outcome = "peer_eof"          # the connection broke under the server: the player's side, not a server error
    error = plain_error(game.get("error_text")) or game.get("error_type") or ""
    if outcome == "refused":
        if reason == "no_password_login":
            return answer("bad", "handover_refused", "The game server refused the hand-over from the login server",
                          "The game port saw no password login from the same address in the last 2 minutes.",
                          "Usually the address changed between the two steps (VPN, mobile data), or loading took over "
                          "2 minutes. Ask the player to try again without a VPN.")
        if reason == "banned":
            return answer("bad", "game_banned", "The account is blocked", "The game server refuses a banned or deleted account.",
                          "Unblock or restore it on the Accounts page if that was not intended.")
        if reason.startswith("character_load"):
            return answer("bad", "character_load", "The character's data could not be loaded",
                          error or "The server refused the saved character (%s)." % reason.split(":")[-1],
                          "Run the Login check below: it names the row to fix.")
        if reason == "unknown_account":
            return answer("warn", "game_unknown_account", "The game server did not know the account", "", "")
        return answer("warn", "game_refused", "The game server refused the connection", reason, "")
    entered = "entered" in stages
    if outcome == "fault" and not entered and ("enter_requested" in stages or reason == "opcode %d" % ENTER_GAME_OPCODE):
        return answer("bad", "enter_fault", "Server error while entering the world", error,
                      "Run the Login check below: it names the item, pet or row to fix.")
    if outcome == "fault" and "character_screen" not in stages:
        return answer("bad", "handover_fault", "Server error before the character screen", error, "Look at the server log for this time.")
    if not entered and game.get("character_id") is None and outcome != "fault":
        return answer("info", "no_character", "Has no character yet", "The account logged in and has not made a character.", "")
    if "enter_requested" not in stages:
        if outcome == "replaced":
            return answer("info", "replaced", "Logged in again while still connected", "The older connection was closed for the new one.", "")
        return answer("info", "left_at_character_screen", "Left at the character screen",
                      "The player reached the character screen and did not enter the world (%s)." % duration_text(game["duration_ms"]), "")
    if not entered:
        return answer("bad", "enter_incomplete", "Did not finish entering the world",
                      error or "The connection closed while the server was sending the character.",
                      "Run the Login check below.")
    if "ui_ready" not in stages:
        return answer("warn", "loading_never_finished", "Entered the world, but the game never finished loading",
                      "The server sent the character; the client closed before it reported its screen ready (%s). "
                      "That is usually the client crashing or being closed while the map loads." % duration_text(game["duration_ms"]),
                      "Ask the player what they saw; a crash leaves an Error.log in the game folder.")
    played_ms = game["duration_ms"] - int(stages.get("ui_ready") or 0)
    if "Timeout" in (game.get("transport_error") or "") or "Timeout" in (game.get("error_type") or ""):
        return answer("warn", "timeout", "The connection went silent",
                      "The server heard nothing from the client and closed the session after %s." % duration_text(played_ms),
                      "The player's internet dropped, or the game froze.")
    if outcome == "fault":
        return answer("warn", "play_fault", "Server error during play",
                      "%s%s after %s in the world." % (error, " (%s)" % reason if reason else "", duration_text(played_ms)),
                      "Look at the server log for this time; the player was disconnected.")
    if outcome == "replaced":
        return answer("info", "replaced", "Logged in again while still connected", "The older connection was closed for the new one.", "")
    if outcome in ("kicked", "banned"):
        return answer("info", outcome, "Disconnected by staff" if outcome == "kicked" else "Banned while online", "", "")
    if outcome in ("server_cancelled", "cancelled", "session_cancelled") and not game.get("transport_error"):
        return answer("info", "server_stopped", "The server stopped or restarted", "Everyone online was disconnected.", "")
    if played_ms < EARLY_DROP_SECONDS * 1000:
        return answer("warn", "early_drop", "Dropped within a minute of entering the world",
                      "The client closed the connection %s after its screen was ready." % duration_text(played_ms),
                      "If it repeats, the client is probably crashing after loading; ask the player for the Error.log.")
    return answer("ok", "ok", "Played and left normally", "In the world for %s." % duration_text(played_ms), "")


def status(explained, findings=()):
    """Traffic light for one account. explained = explain() of its attempts, oldest first; findings = login check."""
    if any(f.get("severity") == "blocks" for f in findings):
        return "red"
    recent = [e for e in explained if e["code"] != "in_progress"][-5:]
    light = "grey" if not recent else "green"
    if recent:
        last = recent[-1]["level"]
        if last == "bad":
            light = "red"
        elif last == "warn" or sum(1 for e in recent if e["level"] in ("warn", "bad")) >= 2:
            light = "amber"
        if light != "red" and len(recent) >= 3 and all(e["code"] == "left_at_character_screen" for e in recent[-3:]):
            light = "amber"
    if light in ("green", "grey") and any(f.get("severity") == "hidden" for f in findings):
        light = "amber"
    return light


def summary(attempts_by_account, now):
    """Server-wide numbers: today's logins and failures (Brisbane day), the top reasons, and a per-day series."""
    today = local_day(now)
    days, reasons, total_today, bad_today = {}, {}, 0, 0
    for attempts in attempts_by_account.values():
        for a in attempts:
            e = a["explained"]
            day = local_day(a["at"])
            d = days.setdefault(day, {"day": day, "logins": 0, "failed": 0})
            d["logins"] += 1
            failed = e["level"] == "bad"
            d["failed"] += 1 if failed else 0
            if day == today:
                total_today += 1
                bad_today += 1 if failed else 0
            if e["level"] in ("bad", "warn"):
                r = reasons.setdefault(e["code"], {"code": e["code"], "title": e["title"], "count": 0, "accounts": set(), "level": e["level"]})
                r["count"] += 1
                r["accounts"].add(a.get("account_id"))
    top = sorted(reasons.values(), key=lambda r: (-r["count"], r["title"]))[:8]
    for r in top:
        r["accounts"] = len(r["accounts"] - {None})
    return {"today": today, "logins_today": total_today, "failed_today": bad_today,
            "failed_percent": round(100.0 * bad_today / total_today) if total_today else 0,
            "top_reasons": top, "days": [days[k] for k in sorted(days)]}


def markers(rows):
    """Moments something changed, from the records themselves: server restarts (every game session ending with
    'server_cancelled' at the same minute) and the first login of a client version not seen before."""
    out, restarts, builds = [], {}, {}
    for r in rows:
        at = parse_time(r["started_at"])
        if r.get("endpoint") == "game" and r.get("outcome") == "server_cancelled":
            end = at + datetime.timedelta(milliseconds=int(r.get("duration_ms") or 0))
            restarts.setdefault(end.replace(second=0, microsecond=0), 0)
        if r.get("client_build") is not None:
            b = int(r["client_build"])
            builds[b] = min(builds.get(b, at), at)
    out += [{"at": t, "what": "the server restart"} for t in restarts]
    if len(builds) > 1:
        first = min(builds.values())
        out += [{"at": t, "what": "the first login with client version %s" % build_text(b)} for b, t in builds.items() if t != first]
    return sorted(out, key=lambda m: m["at"])


def what_changed(attempts, marks, window_hours=24, minimum=3):
    """The newest marker after which failed logins clearly rose: a hint, or None.
    attempts: [{"at", "explained"}] of every account."""
    window = datetime.timedelta(hours=window_hours)
    for m in sorted(marks, key=lambda m: m["at"], reverse=True):
        before = [a for a in attempts if m["at"] - window <= a["at"] < m["at"]]
        after = [a for a in attempts if m["at"] <= a["at"] < m["at"] + window]
        bad_before = sum(1 for a in before if a["explained"]["level"] == "bad")
        bad_after = [a for a in after if a["explained"]["level"] == "bad"]
        if len(bad_after) < minimum:
            continue
        rate_before = 100.0 * bad_before / len(before) if before else 0.0
        rate_after = 100.0 * len(bad_after) / len(after)
        if rate_after >= max(2 * rate_before, rate_before + 10):
            titles = {}
            for a in bad_after:
                titles[a["explained"]["title"]] = titles.get(a["explained"]["title"], 0) + 1
            top = max(titles.items(), key=lambda kv: kv[1])[0]
            return {"at": local_text(m["at"]), "what": m["what"], "before_percent": round(rate_before), "after_percent": round(rate_after),
                    "failed_after": len(bad_after), "top": top,
                    "text": "Failed logins rose from %d%% to %d%% in the day after %s (%s). Most common: %s."
                            % (round(rate_before), round(rate_after), m["what"], local_text(m["at"]), top)}
    return None
