"""modules/login_awards.py - W4-B (the admin 2026-11-12): login-window awards, the Prize Chest and buffs for many players.
Page: static/modules/login_awards.js.

the admin: "can we set awards for logging on between certain dates?", "allow awards to go to warehouse event awarder (not
regular warehouse, I think there is an npc next to them like that)", "Allow buffs to be applied to offline characters
(applies when logs in)", "Apply buffs server wide for players online."

Server side (godswar-dev/server, migration 20261112_241_login_awards):
  * login_window_awards: one award = items (1-99 each, at most 16 rows), Silver / Gold / B-Gold, buffs, a title; once
    per character or once per account; delivered to the bag (a full bag sends the items to the Prize Chest) or to the
    Prize Chest. Paid at world entry between starts_at (inclusive) and ends_at (exclusive), stored in UTC; this page
    shows and takes Brisbane time (AEST, UTC+10, no daylight saving), like the seasons on Events & notices.
  * prize_chest_entries: the Prize Chest (Athens_123 / Sparta_123, next to the warehouse keeper). The player talks to
    it to collect; what does not fit keeps waiting.
  * pending_character_buffs: buffs applied at the character's next world entry (the duration starts then).
  * POST /admin/buff/broadcast on the management port: one buff to every online player (camp / level filter),
    optionally queued for everyone offline too.
Rows are read by the server at each login (no restart). Switches: prize.login_awards, prize.queued_buffs,
prize.prize_chest, prize.prize_chest_window (Rates page).
"""
import datetime
import json
import re

from . import common, world as W
from .common import Bad, NotFound, Refused, lit

M = common.module(
    "login_awards", "Login awards & Prize Chest",
    icon={"sprite": 1063},
    order=28,
    status="ready",
    description="Awards for logging in between two dates, gifts to the Prize Chest by the warehouse, and buffs for "
                "everyone online or queued for the next login.",
    needs_character=False,
)

BRISBANE = datetime.timezone(datetime.timedelta(hours=10))
MAX_ITEMS = 16
MAX_BUFFS = 8
MAX_STACK = 99          # the admin's rule: no stack above 99
MAX_TARGETS = 500
LIMITS = {"silver": 1000000000, "gold": 1000000, "binding_gold": 10000000}
AWARD_PART = [("login_window_awards", "true", "id")]
BROADCAST_PATH = "/admin/buff/broadcast"
NOTIFY_PATH = "/admin/prize-chest/notify"   # W5-F: tell online players their Prize Chest got something
TABLES = ("login_window_awards", "login_window_award_claims", "prize_chest_entries", "pending_character_buffs")


# =========================================================================== helpers
def _q(ctx, sql):
    return W._q(ctx, sql)


def _ready(ctx):
    have = _q(ctx, "(SELECT to_jsonb(array_agg(t)) FROM unnest(ARRAY[%s]) t WHERE to_regclass('public.' || t) IS NOT NULL)"
              % ",".join(lit(t) for t in TABLES)) or []
    return len(have) == len(TABLES)


def _need_ready(ctx):
    if not _ready(ctx):
        raise Refused("this database has no login-award tables yet: the game server release with migration "
                      "20261112_241_login_awards must run against it first")


def _int(v, lo, hi, what, default=None):
    if (v is None or v == "") and default is not None:
        return default
    return W._int(v, lo, hi, what)


def brisbane_to_utc(text, what):
    """'YYYY-MM-DDTHH:MM' (or with seconds, or a space) in Brisbane time -> aware UTC datetime."""
    if not isinstance(text, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(:\d{2})?", text.strip()):
        raise Bad("%s must be a Brisbane date and time (YYYY-MM-DD HH:MM)" % what)
    try:
        local = datetime.datetime.fromisoformat(text.strip().replace(" ", "T"))
    except ValueError:
        raise Bad("%s is not a real date and time" % what)
    return local.replace(tzinfo=BRISBANE).astimezone(datetime.timezone.utc)


def utc_to_brisbane(value):
    if value is None:
        return None
    if isinstance(value, str):
        value = datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))
    return value.astimezone(BRISBANE).strftime("%Y-%m-%d %H:%M")


def _known_items(ctx, ids):
    if not ids:
        return {}
    rows = _q(ctx, "(SELECT coalesce(json_agg(json_build_object('id', id, 'overlap', stats->>'Overlap', 'name', display_name)), "
                   "'[]') FROM item_templates WHERE id = ANY(ARRAY[%s]::int[]))" % ",".join(str(int(i)) for i in ids)) or []
    out = {}
    for r in rows:
        try:
            name = ctx.catalog.name("item", r["id"])
        except Exception:      # noqa: BLE001 - server-only item
            name = None
        out[int(r["id"])] = {"name": name or r.get("name") or "item %s" % r["id"], "overlap": r.get("overlap")}
    return out


def clean_items(ctx, raw, *, allow_empty=True):
    """[{item_id, quantity 1-99, quality 0-30, grade 0-30, bound 0|1}] checked against item_templates."""
    if raw in (None, ""):
        raw = []
    if not isinstance(raw, list) or len(raw) > MAX_ITEMS:
        raise Bad("items must be a list of at most %d rows" % MAX_ITEMS)
    if not raw and not allow_empty:
        raise Bad("add at least one item")
    out = []
    for i, r in enumerate(raw):
        if not isinstance(r, dict):
            raise Bad("item row %d is malformed" % (i + 1))
        row = {"item_id": _int(r.get("item_id"), 1, 2 ** 31 - 1, "item %d id" % (i + 1)),
               "quantity": _int(r.get("quantity"), 1, MAX_STACK, "item %d quantity (stacks are capped at 99)" % (i + 1), 1),
               "quality": _int(r.get("quality"), 0, 30, "item %d quality" % (i + 1), 1),
               "grade": _int(r.get("grade"), 0, 30, "item %d grade" % (i + 1), 1),
               "bound": _int(r.get("bound"), 0, 1, "item %d bound" % (i + 1), 1)}
        out.append(row)
    known = _known_items(ctx, sorted({r["item_id"] for r in out}))
    missing = sorted({r["item_id"] for r in out} - set(known))
    if missing:
        raise Bad("unknown item id(s): %s (they must exist in item_templates)" % ", ".join(map(str, missing)))
    for r in out:
        common.refuse_ungrantable(r["item_id"])
    return out


def clean_buffs(raw):
    if raw in (None, ""):
        raw = []
    if not isinstance(raw, list) or len(raw) > MAX_BUFFS:
        raise Bad("buffs must be a list of at most %d rows" % MAX_BUFFS)
    out = []
    for i, r in enumerate(raw):
        if not isinstance(r, dict):
            raise Bad("buff row %d is malformed" % (i + 1))
        row = {"status_id": _int(r.get("status_id"), 1, 2 ** 31 - 1, "buff %d status" % (i + 1))}
        if r.get("duration_seconds") not in (None, ""):
            row["duration_seconds"] = _int(r.get("duration_seconds"), 1, 2 ** 32 - 1, "buff %d duration (seconds)" % (i + 1))
        out.append(row)
    return out


def check_buffs_runnable(ctx, buffs):
    """Against the running server's buff catalogue (when it answers): the status must be one it can apply, and not a
    mount ride (needs that player's own mount) or damage over time (needs an attacker) - those cannot wait for a login."""
    if not buffs:
        return
    try:
        from . import buffs as B
        catalogue = B.runtime_catalogue(ctx)
    except Exception:      # noqa: BLE001 - server down or older: the server checks again at the login
        return
    if not catalogue.get("supported"):
        return
    rows = {int(r["statusId"]): r for r in catalogue.get("buffs") or [] if r.get("statusId") is not None}
    for b in buffs:
        row = rows.get(int(b["status_id"]))
        if row is None:
            raise Bad("status %d is not a buff the running server can apply" % b["status_id"])
        if row.get("activation") in ("mount", "periodic-damage"):
            raise Bad("%s cannot be queued or given at login (%s)" % (row.get("name") or b["status_id"],
                      "it needs the player's own mount" if row.get("activation") == "mount" else "damage over time"))


def award_row(ctx, b):
    """The validated column values of one award (the server re-checks items and buffs at every login)."""
    if not isinstance(b, dict):
        raise Bad("the award is malformed")
    name = (b.get("name") or "").strip()
    if not 1 <= len(name) <= 80:
        raise Bad("the name must be 1-80 characters")
    start, end = brisbane_to_utc(b.get("starts"), "Start"), brisbane_to_utc(b.get("ends"), "End")
    if end <= start:
        raise Bad("the end must be after the start")
    scope = b.get("per_scope") or "character"
    if scope not in ("character", "account"):
        raise Bad("once per character or once per account")
    delivery = b.get("delivery") or "bag"
    if delivery not in ("bag", "prize_chest"):
        raise Bad("delivery is the bag or the Prize Chest")
    items, buffs = clean_items(ctx, b.get("items")), clean_buffs(b.get("buffs"))
    check_buffs_runnable(ctx, buffs)
    money = {k: _int(b.get(k), 0, hi, k.replace("_", " "), 0) for k, hi in LIMITS.items()}
    title = b.get("title_id")
    title = None if title in (None, "", 0, "0") else _int(title, 1, 2 ** 31 - 1, "title")
    if title is not None and not _q(ctx, "(SELECT to_jsonb(EXISTS (SELECT 1 FROM editor_title_catalog WHERE title_id = %d)))" % title):
        raise Bad("title %d is not in the editor title catalogue (titles that can be granted)" % title)
    if not items and not buffs and title is None and not any(money.values()):
        raise Bad("the award gives nothing: add an item, money, a buff or a title")
    note = b.get("note") or ""
    if not isinstance(note, str) or len(note) > 512:
        raise Bad("the note is text up to 512 characters")
    return dict(name=name, starts_at=start.isoformat(), ends_at=end.isoformat(), per_scope=scope, delivery=delivery,
                items=json.dumps(items), buffs=json.dumps(buffs), title_id=title,
                min_level=_int(b.get("min_level"), 1, 200, "minimum level", 1), enabled=bool(b.get("enabled", True)),
                note=note, **money)


def _actor():
    return (getattr(common.ACTOR, "name", None) or "gwadmin-web")[:120]


def _characters(ctx, ids):
    """[{id, name, online}] for active characters; NotFound when one is missing."""
    if not isinstance(ids, list) or not 1 <= len(ids) <= MAX_TARGETS:
        raise Bad("choose 1-%d characters" % MAX_TARGETS)
    clean = sorted({_int(i, 1, 2 ** 31 - 1, "character") for i in ids})
    rows = _q(ctx, "(SELECT coalesce(json_agg(json_build_object('id', id, 'name', name) ORDER BY id), '[]') FROM character_base "
                   "WHERE lifecycle_state = 'active' AND id = ANY(ARRAY[%s]::int[]))" % ",".join(map(str, clean))) or []
    if len(rows) != len(clean):
        missing = sorted(set(clean) - {int(r["id"]) for r in rows})
        raise NotFound("no active character #%s" % ", #".join(map(str, missing)))
    return rows


# =========================================================================== overview + awards
@M.get("overview")
def overview(ctx):
    if not _ready(ctx):
        return {"ready": False, "reason": "The login-award tables are not in this database yet (server migration "
                                          "20261112_241_login_awards). Release the server first."}
    awards = _q(ctx, """(SELECT coalesce(json_agg(json_build_object('id', a.id, 'name', a.name, 'starts_at', a.starts_at,
        'ends_at', a.ends_at, 'per_scope', a.per_scope, 'delivery', a.delivery, 'items', a.items, 'buffs', a.buffs,
        'silver', a.silver, 'gold', a.gold, 'binding_gold', a.binding_gold, 'title_id', a.title_id, 'min_level', a.min_level,
        'enabled', a.enabled, 'note', a.note, 'created_by', a.created_by, 'open', a.enabled AND now() >= a.starts_at AND now() < a.ends_at,
        'claims', (SELECT count(*) FROM login_window_award_claims c WHERE c.award_id = a.id),
        'claims_chest', (SELECT count(*) FROM login_window_award_claims c WHERE c.award_id = a.id AND c.delivered_to = 'prize_chest'))
        ORDER BY a.starts_at DESC, a.id DESC), '[]') FROM login_window_awards a)""") or []
    ids = sorted({int(i["item_id"]) for a in awards for i in (a.get("items") or [])})
    names = _known_items(ctx, ids)
    for a in awards:
        a["starts"], a["ends"] = utc_to_brisbane(a["starts_at"]), utc_to_brisbane(a["ends_at"])
        for i in a.get("items") or []:
            i["name"] = (names.get(int(i["item_id"])) or {}).get("name") or "item %s" % i["item_id"]
    titles = _q(ctx, "(SELECT coalesce(json_agg(json_build_object('id', title_id, 'name', display_name) ORDER BY title_id), '[]') "
                     "FROM editor_title_catalog)") or []
    chest = _q(ctx, "(SELECT json_build_object('waiting', count(*) FILTER (WHERE claimed_at IS NULL), 'characters', "
                    "count(DISTINCT character_id) FILTER (WHERE claimed_at IS NULL)) FROM prize_chest_entries)") or {}
    queued = _q(ctx, "(SELECT json_build_object('waiting', count(*) FILTER (WHERE applied_at IS NULL)) FROM pending_character_buffs)") or {}
    return {"ready": True, "awards": awards, "version": W._version(ctx, AWARD_PART), "titles": titles, "chest": chest,
            "queued": queued, "time_zone": "Australia/Brisbane (AEST, UTC+10)",
            "now": datetime.datetime.now(BRISBANE).strftime("%Y-%m-%d %H:%M"), "broadcast": broadcast_support(ctx),
            "limits": {"items": MAX_ITEMS, "stack": MAX_STACK, "buffs": MAX_BUFFS, **LIMITS}}


@M.post("award/save")
def award_save(ctx):
    _need_ready(ctx)
    b = ctx.body
    row = award_row(ctx, b.get("award"))
    award_id = b.get("id")
    stmts = [W._ver_guard(AWARD_PART, b.get("version"), "the awards")]
    if award_id in (None, "", 0):
        stmts.append(common.insert_sql("login_window_awards", dict(row, created_by=_actor())))
        what = "login award '%s' added (%s to %s Brisbane)" % (row["name"], utc_to_brisbane(row["starts_at"]), utc_to_brisbane(row["ends_at"]))
    else:
        award_id = _int(award_id, 1, 2 ** 31 - 1, "award")
        sets = ", ".join("%s = %s" % (common.ident(k), lit(v)) for k, v in row.items())
        stmts.append("UPDATE login_window_awards SET %s, updated_at = now() WHERE id = %d" % (sets, award_id))
        what = "login award #%d '%s' changed" % (award_id, row["name"])
    return W._write(ctx, [common.scope("login_window_awards", "true", pk=["id"])], what, stmts, ["login_window_awards.*"],
                    applies="now", target="login awards")


@M.post(r"award/(\d+)/enabled")
def award_enabled(ctx, award_id):
    _need_ready(ctx)
    award_id = _int(award_id, 1, 2 ** 31 - 1, "award")
    on = bool(ctx.body.get("enabled"))
    return W._write(ctx, [common.scope("login_window_awards", "id = %d" % award_id, pk=["id"])],
                    "login award #%d %s" % (award_id, "enabled" if on else "disabled"),
                    [W._ver_guard(AWARD_PART, ctx.body.get("version"), "the awards"),
                     "UPDATE login_window_awards SET enabled = %s, updated_at = now() WHERE id = %d" % (lit(on), award_id)],
                    ["login_window_awards.*"], applies="now", target="login awards")


@M.get(r"award/(\d+)/claims")
def award_claims(ctx, award_id):
    _need_ready(ctx)
    award_id = _int(award_id, 1, 2 ** 31 - 1, "award")
    rows = _q(ctx, """(SELECT coalesce(json_agg(json_build_object('claim_key', c.claim_key, 'account_id', c.account_id,
        'character_id', c.character_id, 'character', b.name, 'delivered_to', c.delivered_to, 'claimed_at', c.claimed_at)
        ORDER BY c.claimed_at DESC), '[]') FROM (SELECT * FROM login_window_award_claims WHERE award_id = %d
        ORDER BY claimed_at DESC LIMIT 500) c LEFT JOIN character_base b ON b.id = c.character_id)""" % award_id) or []
    for r in rows:
        r["claimed"] = utc_to_brisbane(r["claimed_at"])
    return {"award_id": award_id, "claims": rows}


# =========================================================================== Prize Chest
@M.post("chest/send")
def chest_send(ctx):
    """Put items and money into the Prize Chest of each chosen character (online or offline: they collect it there).
    Online targets are told in chat by the game server straight away (notify_chest)."""
    _need_ready(ctx)
    b = ctx.body
    targets = _characters(ctx, b.get("targets"))
    items = clean_items(ctx, b.get("items"))
    money = {k: _int(b.get(k), 0, hi, k.replace("_", " "), 0) for k, hi in LIMITS.items()}
    if not items and not any(money.values()):
        raise Bad("send at least one item or some money")
    note = b.get("note") or ""
    if not isinstance(note, str) or len(note) > 512:
        raise Bad("the note is text up to 512 characters")
    source = ("editor:%s" % _actor())[:128]
    stmts = []
    for t in targets:
        for i in items:
            stmts.append(common.insert_sql("prize_chest_entries", {
                "character_id": int(t["id"]), "kind": "item", "item_id": i["item_id"], "quantity": i["quantity"],
                "item_quality": i["quality"], "item_grade": i["grade"], "bound": i["bound"], "source": source, "note": note}))
        for kind, amount in money.items():
            if amount:
                stmts.append(common.insert_sql("prize_chest_entries", {
                    "character_id": int(t["id"]), "kind": kind, "quantity": amount, "source": source, "note": note}))
    where = "character_id IN (%s) AND claimed_at IS NULL" % ",".join(str(int(t["id"])) for t in targets)
    out = W._write(ctx, [common.scope("prize_chest_entries", where, pk=["id"])],
                   "Prize Chest: %d row(s) to %s" % (len(stmts) // max(1, len(targets)),
                                                      ", ".join(t["name"] for t in targets[:5]) + (" and %d more" % (len(targets) - 5) if len(targets) > 5 else "")),
                   stmts, ["prize_chest_entries.*"], applies="now", target="prize chest")
    out.update(sent_to=len(targets), rows=len(stmts), online=None if getattr(ctx, "chest_quiet", False)
               else notify_chest(ctx, [int(t["id"]) for t in targets]))
    return out


def notify_chest(ctx, character_ids):
    """W5-F: after the rows are written, ask the running game server to tell each target that is in the world (one
    chat line, and a repaint if the chest's award tab is open). Best effort and read-only on the server: the rows are
    already saved, so a server that is down, older or busy only means the player finds the prize at the chest or reads
    the reminder at the next login. Returns {notified, online, note} for the page."""
    port = common.mgmt_port(ctx.app)
    if not port:
        return {"notified": 0, "online": 0, "note": "no game server is attached; players see it at the chest or at login"}
    try:
        st, doc = common._http("POST", port, NOTIFY_PATH,
                               {"actor": _actor()[:64], "characterIds": sorted(set(character_ids))[:500]}, timeout=8.0)
    except Exception as e:      # noqa: BLE001 - the send itself already succeeded
        return {"notified": 0, "online": 0, "note": "the game server did not answer (%s); players see it at the chest or at login" % e}
    if st != 200 or not isinstance(doc, dict) or not doc.get("ok"):
        why = "the running game server needs the W5-F release to tell online players" if st == 404 else \
              "the game server answered HTTP %s" % st
        return {"notified": 0, "online": 0, "note": why + "; players see it at the chest or at login"}
    n, online = int(doc.get("notified") or 0), int(doc.get("online") or 0)
    note = ("%d online player(s) were told in chat" % n) if n else \
           ("the target is online but was not told (busy); it is in the chest" if online else "nobody on the list is online; they are told at login")
    return {"notified": n, "online": online, "note": note,
            "characters": [{"id": c.get("id"), "name": c.get("name"), "result": c.get("result")} for c in (doc.get("characters") or [])[:50]]}


@M.get(r"chest/(\d+)")
def chest_view(ctx, cid):
    _need_ready(ctx)
    cid = _int(cid, 1, 2 ** 31 - 1, "character")
    rows = _q(ctx, """(SELECT coalesce(json_agg(to_jsonb(e) ORDER BY e.claimed_at IS NOT NULL, e.id DESC), '[]')
        FROM (SELECT * FROM prize_chest_entries WHERE character_id = %d ORDER BY id DESC LIMIT 300) e)""" % cid) or []
    names = _known_items(ctx, sorted({int(r["item_id"]) for r in rows if r.get("item_id")}))
    for r in rows:
        r["name"] = (names.get(int(r["item_id"])) or {}).get("name") if r.get("item_id") else r["kind"].replace("_", " ")
        r["created"], r["claimed"] = utc_to_brisbane(r["created_at"]), utc_to_brisbane(r.get("claimed_at"))
    return {"character_id": cid, "entries": rows}


@M.post(r"chest/(\d+)/remove")
def chest_remove(ctx, entry_id):
    """Take back one prize that is still waiting (a claimed one is in the player's bag already)."""
    _need_ready(ctx)
    entry_id = _int(entry_id, 1, 2 ** 63 - 1, "chest row")
    return W._write(ctx, [common.scope("prize_chest_entries", "id = %d" % entry_id, pk=["id"])],
                    "Prize Chest: row #%d taken back" % entry_id,
                    ["DO $gwpc$ BEGIN IF NOT EXISTS (SELECT 1 FROM prize_chest_entries WHERE id = %d AND claimed_at IS NULL) "
                     "THEN RAISE EXCEPTION 'gwadmin-conflict: this prize was already collected or removed'; END IF; END $gwpc$" % entry_id,
                     "DELETE FROM prize_chest_entries WHERE id = %d AND claimed_at IS NULL" % entry_id],
                    ["prize_chest_entries.*"], applies="now", target="prize chest")


# =========================================================================== buffs for many
def broadcast_support(ctx):
    port = common.mgmt_port(ctx.app)
    if not port:
        return {"available": False, "reason": "no game server management port is configured for this database"}
    try:
        st, doc = common._http("GET", port, "/admin/capabilities", timeout=2.0)
    except Exception as e:      # noqa: BLE001 - shown on the page
        return {"available": False, "reason": "the game server did not answer (%s)" % e}
    paths = {e.get("path") for e in (doc.get("endpoints") or []) if isinstance(e, dict)} if st == 200 and isinstance(doc, dict) else set()
    if BROADCAST_PATH not in paths:
        return {"available": False, "reason": "the running game server needs the W4-B release (server-wide buffs)"}
    return {"available": True}


def broadcast_body(b):
    body = {}
    if b.get("status_id") in (None, ""):
        raise Bad("choose a buff")
    body["statusId"] = _int(b.get("status_id"), 1, 2 ** 31 - 1, "buff status")
    if b.get("duration_seconds") not in (None, ""):
        body["durationSeconds"] = _int(b.get("duration_seconds"), 1, 2 ** 32 - 1, "duration (seconds)")
    camp = b.get("camp")
    if camp not in (None, "", "all"):
        body["camp"] = _int(camp, 0, 1, "faction (0 Sparta, 1 Athens)")
    body["minLevel"] = _int(b.get("min_level"), 1, 200, "minimum level", 1)
    body["maxLevel"] = _int(b.get("max_level"), 1, 200, "maximum level", 200)
    if body["minLevel"] > body["maxLevel"]:
        raise Bad("the minimum level is above the maximum level")
    body["queueOffline"] = bool(b.get("queue_offline"))
    body["allowHostile"] = bool(b.get("allow_hostile"))
    return body


@M.post("buffs/broadcast")
def buffs_broadcast(ctx):
    """Apply one buff to every online player now (faction / level filter); optionally queue it for everyone offline."""
    if not ctx.need_rb().writable:
        raise Bad("this editor connection is read-only")
    support = broadcast_support(ctx)
    if not support["available"]:
        raise Bad(support["reason"])
    body = broadcast_body(ctx.body)
    body["actor"] = _actor()[:64]
    st, doc = common._http("POST", common.mgmt_port(ctx.app), BROADCAST_PATH, body, timeout=12.0)
    if st != 200 or not isinstance(doc, dict) or not doc.get("ok"):
        raise Bad("the game server refused the server-wide buff (HTTP %s: %s)" % (st, (doc or {}).get("error") if isinstance(doc, dict) else doc))
    doc.update(mode="live", applies="live", applies_text=common.APPLIES_TEXT.get("live", "live"))
    return doc


def queue_buff(ctx, target_ids, status_id, duration_seconds=None, discard_after=None):
    """Queue one buff for characters: applied at each one's next world entry (the duration starts then). Also used by
    the per-character Buffs page for an offline character."""
    _need_ready(ctx)
    targets = _characters(ctx, target_ids)
    buff = clean_buffs([{"status_id": status_id, "duration_seconds": duration_seconds}])[0]
    check_buffs_runnable(ctx, [buff])
    discard = None
    if discard_after not in (None, ""):
        discard = brisbane_to_utc(discard_after, "Drop it if not back by").isoformat()
    source = ("editor:%s" % _actor())[:128]
    stmts = [common.insert_sql("pending_character_buffs", {
        "character_id": int(t["id"]), "status_id": buff["status_id"], "duration_seconds": buff.get("duration_seconds"),
        "source": source, "created_by": source, "discard_after": discard}) for t in targets]
    where = "character_id IN (%s) AND applied_at IS NULL" % ",".join(str(int(t["id"])) for t in targets)
    out = W._write(ctx, [common.scope("pending_character_buffs", where, pk=["id"])],
                   "queued buff %d for the next login of %s" % (buff["status_id"], ", ".join(t["name"] for t in targets[:5]) +
                                                                (" and %d more" % (len(targets) - 5) if len(targets) > 5 else "")),
                   stmts, ["pending_character_buffs.*"], applies="now", target="queued buffs")
    out.update(queued=len(stmts))
    return out


@M.post("buffs/queue")
def buffs_queue(ctx):
    b = ctx.body
    return queue_buff(ctx, b.get("targets"), b.get("status_id"), b.get("duration_seconds"), b.get("discard_after"))


@M.get("buffs/pending")
def buffs_pending(ctx):
    _need_ready(ctx)
    cid = ctx.q("character")
    where = "applied_at IS NULL" if not cid else "character_id = %d" % _int(cid, 1, 2 ** 31 - 1, "character")
    rows = _q(ctx, """(SELECT coalesce(json_agg(json_build_object('id', p.id, 'character_id', p.character_id, 'character', b.name,
        'status_id', p.status_id, 'duration_seconds', p.duration_seconds, 'source', p.source, 'created_at', p.created_at,
        'discard_after', p.discard_after, 'applied_at', p.applied_at, 'result', p.result) ORDER BY p.id DESC), '[]')
        FROM (SELECT * FROM pending_character_buffs WHERE %s ORDER BY id DESC LIMIT 300) p
        LEFT JOIN character_base b ON b.id = p.character_id)""" % where) or []
    for r in rows:
        r["created"], r["applied"] = utc_to_brisbane(r["created_at"]), utc_to_brisbane(r.get("applied_at"))
        r["discard"] = utc_to_brisbane(r.get("discard_after"))
        try:
            r["name"] = (ctx.catalog.entry("status", r["status_id"]) or {}).get("name")
        except Exception:      # noqa: BLE001
            r["name"] = None
        r["name"] = r["name"] or "Status #%s" % r["status_id"]
    return {"rows": rows}


@M.post(r"buffs/pending/(\d+)/cancel")
def buffs_cancel(ctx, row_id):
    _need_ready(ctx)
    row_id = _int(row_id, 1, 2 ** 63 - 1, "queued buff")
    return W._write(ctx, [common.scope("pending_character_buffs", "id = %d" % row_id, pk=["id"])],
                    "queued buff #%d cancelled" % row_id,
                    ["DO $gwqb$ BEGIN IF NOT EXISTS (SELECT 1 FROM pending_character_buffs WHERE id = %d AND applied_at IS NULL) "
                     "THEN RAISE EXCEPTION 'gwadmin-conflict: this buff was already applied or cancelled'; END IF; END $gwqb$" % row_id,
                     "UPDATE pending_character_buffs SET applied_at = now(), result = 'cancelled in the editor' "
                     "WHERE id = %d AND applied_at IS NULL" % row_id],
                    ["pending_character_buffs.*"], applies="now", target="queued buffs")
