"""modules/awards.py - the Online Award (daily online rewarder) editor. Page: static/modules/awards.js.

the admin 2026-11-05: "make the online award items editable; they are too good: add a suggested weaker preset I can apply".

How the reborn server stores the award (src/Godswar.Server, stage2-on-r3 f541eed; docs/online-award.md):
  * online_award_balance_revisions (header: revision, sha256, entry_count, source, created_by, sealed_at) and
    online_award_balance_entries (revision, reward_order, item_id, quantity, item_quality, bound, stack_cap) are
    APPEND-ONLY (triggers refuse UPDATE / DELETE / TRUNCATE). online_award_balance_publication (one row, family
    'online-award') names the active revision; its trigger only accepts revision = old + 1 with a different hash, seals
    the new revision and checks count / quantity / slots / order / hash; online_award_publication_audit is written by
    trigger.
  * A change = publish a complete successor, exactly like PostgresOnlineAwardBalanceSettingsStore.TryPublishSuccessorAsync:
    insert header (unsealed) -> insert entries -> compare-and-swap the publication row (WHERE revision = expected).
  * sha256 = upper-case hex SHA-256 of "online-award-balance-v1\\n" + "reward:<order>,<item>,<qty>,<quality>,<bound>,<cap>\\n"
    per row (OnlineAwardBalanceSnapshot.ComputeSha256; same text as public.online_award_balance_canonical()).
  * The server pins the published award at START-UP and re-validates it (PostgresOnlineAwardBalanceSnapshotReader +
    OnlineAwardPinnedItemPolicy): 1-16 rows, order 0..n-1, quantity 1..127 each and in total, quality 1..16,
    bound 0/1, (item, quality, bound) unique, stack_cap = the item template's Overlap, ceil(qty/cap) summed <= 96, and a
    pet egg needs Overlap 1 plus a native pet profile for (species, quality). A row that breaks any of these makes the
    NEXT START-UP FAIL, so every one is checked here before the write and again in SQL inside the transaction.
  * A publication applies after a gw251 restart (the running server keeps the award it pinned at start-up).
  * Undo: history is immutable, so the framework's row Undo cannot work. "Publish this again" on any older revision
    publishes its rows as a new revision (that is the undo).
"""
import hashlib
import json

from . import common
from .common import Bad, Conflict, lit

M = common.module(
    "awards", "Online award",
    icon={"sprite": 1063},
    order=27,
    status="ready",
    description="The daily Online Award (Athens_132 / Sparta_132 'online reward'): which items it gives. "
                "Each save is a new version; it reaches players after a server restart.",
    needs_character=False,
)

MAX_ROWS = 16
MAX_TOTAL = 127
MAX_SLOTS = 96
ACTOR = "gwadmin-web"

# Presets. "current" is read from the database. The suggested one is GENERATED (not from IGG, the wiki or the client):
# the admin asked for something weaker than the seeded award (Godly + 4 Smart Rock Elf eggs, 5 x Morning Dew 5 = 50,000,000
# pet EXP, 5 x Phoenix's Feather = 5 growth resets to the maximum, every single day).
PRESETS = [
    {"key": "suggested", "generated": True, "title": "Suggested: modest daily pet help",
     # made up by the editor build (2026-11-05); not from IGG, the wiki or the client
     "why": "Our own suggestion, not from the original game. No daily eggs; pet EXP "
            "3 x Morning Dew 2 + 1 x Morning Dew 3 = 1,240,000 (was 50,000,000); one Fairy's Feather (resets savvy) "
            "instead of five Phoenix's Feathers (reset growth to the maximum). Unbound like the seed, so players can trade it.",
     "rows": [{"item_id": 10131, "quantity": 3, "item_quality": 1, "bound": 0},
              {"item_id": 10132, "quantity": 1, "item_quality": 1, "bound": 0},
              {"item_id": 11000, "quantity": 1, "item_quality": 1, "bound": 0}]},
    {"key": "suggested_bound", "generated": True, "title": "Suggested, bound: same items, cannot be traded",
     "why": "Our own suggestion. The same three items, bound, so a player cannot farm them on extra characters and pass them on.",
     "rows": [{"item_id": 10131, "quantity": 3, "item_quality": 1, "bound": 1},
              {"item_id": 10132, "quantity": 1, "item_quality": 1, "bound": 1},
              {"item_id": 11000, "quantity": 1, "item_quality": 1, "bound": 1}]},
    {"key": "seed", "generated": False, "title": "Original award (what the server shipped with)",
     # server seed: revision 1, migration 20260821_105 (docs/online-award.md); the owner finds it too generous
     "why": "The award the server came with. It is very generous.",
     "rows": [{"item_id": 10150, "quantity": 1, "item_quality": 14, "bound": 0},
              {"item_id": 10150, "quantity": 4, "item_quality": 10, "bound": 0},
              {"item_id": 10134, "quantity": 5, "item_quality": 1, "bound": 0},
              {"item_id": 11005, "quantity": 5, "item_quality": 1, "bound": 0}]},
]

SCOPE = [common.scope("online_award_balance_publication", "true", pk=["family"]),
         common.scope("online_award_balance_revisions", "true", pk=["revision"]),
         common.scope("online_award_balance_entries", "true", pk=["revision", "reward_order"]),
         common.scope("online_award_publication_audit", "true", pk=["publication_version"])]
NO_UNDO = ("Online Award revisions are append-only (database triggers refuse to change or delete them). To go back, open "
           "the Online award page and use 'Publish this again' on the older revision.")


def canonical(rows):
    return "online-award-balance-v1\n" + "".join(
        "reward:%d,%d,%d,%d,%d,%d\n" % (r["reward_order"], r["item_id"], r["quantity"], r["item_quality"], r["bound"], r["stack_cap"])
        for r in sorted(rows, key=lambda r: r["reward_order"]))


def sha256(rows):
    return hashlib.sha256(canonical(rows).encode("utf-8")).hexdigest().upper()


def _q(ctx, expr):
    return ctx.need_rb().pg.json(expr)


def _templates(ctx, ids):
    """item id -> {overlap, name, egg_species, profiles: [aptitudes with a native profile]} from the database the server reads."""
    if not ids:
        return {}
    arr = "ARRAY[%s]::int[]" % ",".join(str(int(i)) for i in ids)
    rows = _q(ctx, """(SELECT coalesce(json_agg(json_build_object('id', t.id, 'overlap', t.stats->>'Overlap', 'name', t.display_name,
        'species', s.species_id, 'profiles', (SELECT coalesce(json_agg(n.aptitude ORDER BY n.aptitude), '[]') FROM pet_content_native_profiles n
             WHERE n.revision = p.revision AND n.species_id = s.species_id))), '[]')
        FROM item_templates t
        LEFT JOIN pet_content_publication p ON p.family = 'pets'
        LEFT JOIN pet_content_species_definitions s ON s.revision = p.revision AND s.egg_item_id = t.id
        WHERE t.id = ANY(%s))""" % arr)
    out = {}
    for r in rows:
        ov = r.get("overlap")
        try:
            ov = int(str(ov)) if ov is not None and str(ov).isdigit() else None
        except ValueError:
            ov = None
        try:
            cname = ctx.catalog.name("item", r["id"])
        except Exception:      # noqa: BLE001 - server-only item
            cname = None
        out[int(r["id"])] = {"overlap": ov, "name": cname or r.get("name") or "item %s" % r["id"],
                             "species": r.get("species"), "profiles": r.get("profiles") or []}
    return out


def normalise(ctx, rows):
    """Validate like the server (see the module docstring) and return the rows with reward_order and stack_cap filled in.
    Raises Bad with every problem listed."""
    if not isinstance(rows, list) or not 1 <= len(rows) <= MAX_ROWS:
        raise Bad("the award must have 1-%d rows" % MAX_ROWS)
    probs, out, seen = [], [], set()
    ids = []
    for r in rows:
        try:
            ids.append(int(r.get("item_id")))
        except (TypeError, ValueError, AttributeError):
            pass
    tpl = _templates(ctx, ids)
    total = slots = 0
    for i, r in enumerate(rows):
        what = "row %d" % (i + 1)
        try:
            item = int(r.get("item_id")); qty = int(r.get("quantity")); q = int(r.get("item_quality", 1)); b = int(r.get("bound", 0))
        except (TypeError, ValueError, AttributeError):
            probs.append("%s: item, quantity, quality and bound must be whole numbers" % what)
            continue
        t = tpl.get(item)
        name = (t or {}).get("name") or "item %d" % item
        if t is None:
            probs.append("%s: item %d is not an item template of this server" % (what, item)); continue
        if common.R.ungrantable_reason(item):
            probs.append("%s: %s" % (what, common.R.ungrantable_reason(item))); continue
        if not 1 <= qty <= MAX_TOTAL:
            probs.append("%s: %s quantity must be 1-%d" % (what, name, MAX_TOTAL))
        if not 1 <= q <= 16:
            probs.append("%s: %s quality must be 1-16 (the server refuses 0 and above 16)" % (what, name))
        if b not in (0, 1):
            probs.append("%s: bound must be 0 or 1" % what)
        cap = t["overlap"]
        if not cap or not 1 <= cap <= 999:
            probs.append("%s: %s has no usable stack size (Overlap) in item_templates, so the server would refuse it" % (what, name)); continue
        if t["species"] is not None:
            if cap != 1:
                probs.append("%s: %s is a pet egg with stack size %d; the server only accepts eggs that stack to 1" % (what, name, cap))
            if q not in t["profiles"]:
                probs.append("%s: %s has no pet profile for quality (aptitude) %d; the server accepts %s" % (
                    what, name, q, ", ".join(map(str, t["profiles"])) or "none"))
        key = (item, q, b)
        if key in seen:
            probs.append("%s: %s with quality %d and bound %d is listed twice (merge the quantities)" % (what, name, q, b))
        seen.add(key)
        total += qty
        slots += (qty + cap - 1) // cap
        out.append({"reward_order": i, "item_id": item, "quantity": qty, "item_quality": q, "bound": b, "stack_cap": cap, "name": name})
    if total > MAX_TOTAL:
        probs.append("the quantities add up to %d; the server allows %d in total" % (total, MAX_TOTAL))
    if slots > MAX_SLOTS:
        probs.append("the award needs %d bag slots; the server allows %d" % (slots, MAX_SLOTS))
    if probs:
        raise Bad("Not published (the server would refuse this award at its next start-up):\n- " + "\n- ".join(probs))
    return out, {"total": total, "slots": slots}


GUARD = """DO $gw$ DECLARE bad text; BEGIN
  -- the start-up reader's item policy (OnlineAwardPinnedItemPolicy) on the PUBLISHED revision, inside this transaction
  SELECT string_agg(format('order %s item %s', e.reward_order, e.item_id), ', ') INTO bad
    FROM online_award_balance_entries e JOIN online_award_balance_publication p ON p.family = 'online-award' AND e.revision = p.revision
    LEFT JOIN item_templates t ON t.id = e.item_id
    LEFT JOIN pet_content_publication pp ON pp.family = 'pets'
    LEFT JOIN pet_content_species_definitions s ON s.revision = pp.revision AND s.egg_item_id = e.item_id
   WHERE t.id IS NULL OR (t.stats->>'Overlap') IS NULL OR (t.stats->>'Overlap') !~ '^[0-9]+$' OR (t.stats->>'Overlap')::int <> e.stack_cap
      OR (s.species_id IS NOT NULL AND (e.stack_cap <> 1 OR NOT EXISTS (SELECT 1 FROM pet_content_native_profiles n
           WHERE n.revision = pp.revision AND n.species_id = s.species_id AND n.aptitude = e.item_quality)));
  IF bad IS NOT NULL THEN RAISE EXCEPTION 'gwadmin guard: online award rows the server would refuse at start-up: %', bad; END IF;
END $gw$;"""


def _state(ctx):
    return _q(ctx, """json_build_object(
        'publication', (SELECT to_jsonb(p) FROM online_award_balance_publication p WHERE family = 'online-award'),
        'revisions', (SELECT coalesce(json_agg(json_build_object('revision', r.revision, 'sha256', r.sha256, 'entry_count', r.entry_count,
             'source', r.source, 'created_by', r.created_by, 'created_at', r.created_at, 'sealed_at', r.sealed_at,
             'entries', (SELECT coalesce(json_agg(to_jsonb(e) ORDER BY e.reward_order), '[]') FROM online_award_balance_entries e WHERE e.revision = r.revision))
             ORDER BY r.revision DESC), '[]') FROM online_award_balance_revisions r),
        'audit', (SELECT coalesce(json_agg(to_jsonb(a) ORDER BY a.publication_version DESC), '[]') FROM online_award_publication_audit a),
        'claims_today', (SELECT count(*) FROM online_award_claim_settlements WHERE claimed_at > now() - interval '1 day'))""")


@M.get("overview")
def overview(ctx):
    try:
        st = _state(ctx)
    except Exception as e:  # noqa: BLE001 - an older database without the tables
        raise Bad("this database has no Online Award tables (%s)" % str(e)[:200])
    ids = {int(e["item_id"]) for r in st["revisions"] for e in r["entries"]} | {int(x["item_id"]) for p in PRESETS for x in p["rows"]}
    tpl = _templates(ctx, sorted(ids))
    for r in st["revisions"]:
        for e in r["entries"]:
            e["name"] = (tpl.get(int(e["item_id"])) or {}).get("name")
    presets = []
    for p in PRESETS:
        rows = [dict(x, name=(tpl.get(x["item_id"]) or {}).get("name"), stack_cap=(tpl.get(x["item_id"]) or {}).get("overlap")) for x in p["rows"]]
        presets.append(dict(p, rows=rows))
    eggs = {str(k): v["profiles"] for k, v in tpl.items() if v["species"] is not None}
    return dict(st, presets=presets, eggs=eggs, limits={"rows": MAX_ROWS, "total": MAX_TOTAL, "slots": MAX_SLOTS, "quality": [1, 16]},
                applies="after_restart", writable=bool(ctx.need_rb().writable))


@M.post("check")
def check(ctx):
    """Validate a draft without writing (the page calls it as you edit)."""
    rows, sums = normalise(ctx, (ctx.body or {}).get("rows"))
    return {"ok": True, "rows": rows, "sums": sums, "sha256": sha256(rows)}


@M.post("item")
def item(ctx):
    """stack size / egg profiles of one item, for the page's item picker."""
    try:
        i = int((ctx.body or {}).get("item_id"))
    except (TypeError, ValueError):
        raise Bad("item id must be a number")
    t = _templates(ctx, [i]).get(i)
    if not t:
        raise Bad("item %d is not an item template of this server" % i)
    common.refuse_ungrantable(i)
    return t


@M.post("publish")
def publish(ctx):
    """POST {expected_revision, rows: [{item_id, quantity, item_quality, bound}], label?} -> a new sealed revision, published."""
    b = ctx.body or {}
    try:
        expected = int(b.get("expected_revision"))
    except (TypeError, ValueError):
        raise Bad("expected_revision (the revision the page showed) is required")
    rows, sums = normalise(ctx, b.get("rows"))
    digest = sha256(rows)
    cur = _q(ctx, "(SELECT to_jsonb(p) FROM online_award_balance_publication p WHERE family = 'online-award')")
    if not cur:
        raise Bad("this database has no Online Award publication row")
    if int(cur["revision"]) != expected:
        raise Conflict("the award was published again since this page read it (now revision %s)" % cur["revision"])
    if cur["balance_sha256"] == digest:
        raise Bad("these rows are exactly the published award (revision %s): nothing to publish" % cur["revision"])
    new = expected + 1
    # A revision number can exist unpublished only if someone wrote one by hand; refuse rather than guess.
    if _q(ctx, "(SELECT to_jsonb(count(*)) FROM online_award_balance_revisions WHERE revision = %d)" % new):
        raise Bad("revision %d already exists without being published; fix the database by hand first" % new)
    label = str(b.get("label") or "")[:60]
    names = ", ".join("%s x%d%s" % (r["name"], r["quantity"], " Q%d" % r["item_quality"] if r["item_quality"] != 1 else "") for r in rows)

    def build(before):
        st = ["INSERT INTO online_award_balance_revisions (revision, sha256, entry_count, source, created_by) VALUES "
              "(%d, %s, %d, 'gwadmin-web', %s)" % (new, lit(digest), len(rows), lit(ACTOR + (":" + label if label else "")))]
        for r in rows:
            st.append("INSERT INTO online_award_balance_entries (revision, reward_order, item_id, quantity, item_quality, bound, stack_cap) "
                      "VALUES (%d, %d, %d, %d, %d, %d, %d)" % (new, r["reward_order"], r["item_id"], r["quantity"], r["item_quality"], r["bound"], r["stack_cap"]))
        # compare-and-swap like the server's settings store; the trigger seals the revision and checks count / hash
        st.append("DO $gw$ BEGIN UPDATE online_award_balance_publication SET revision = %d, balance_sha256 = %s, "
                  "publication_version = publication_version + 1, updated_by = %s, updated_at = now() "
                  "WHERE family = 'online-award' AND revision = %d; IF NOT FOUND THEN RAISE EXCEPTION "
                  "'gwadmin-conflict: the Online Award was published again since the page read it'; END IF; END $gw$"
                  % (new, lit(digest), lit(ACTOR), expected))
        return st

    r = ctx.rows_write(SCOPE, "online award -> revision %d: %s" % (new, names), build, fields=[], guard_sql=[GUARD],
                       applies="after_restart", target="online award", no_undo=NO_UNDO)
    r.update(revision=new, sha256=digest, sums=sums)
    return r


# =========================================================================== B21 item 29: templates, old revisions, online time
# the admin 2026-11-21: "Online awards: create templates, remove old ones, set the required online time".
#   * Templates are named item lists kept by the editor (sqlite beside its backups); the built-in presets above are
#     templates too and cannot be removed. "Load into draft" then Publish makes one the award.
#   * Published revisions are append-only in the database (triggers refuse DELETE), so "remove" an old revision means
#     hiding it from the History list (the editor remembers which; Show hidden brings them back).
#   * The required online time is the server setting prize.online_award_minutes (B21 server release): minutes in the
#     world this session before the claim is accepted; 0 = at once. Hot: the next claim uses it.
import os as _os
import sqlite3 as _sqlite3
import time as _time

ONLINE_MINUTES_KEY = "prize.online_award_minutes"


class _TemplateStore:
    def __init__(self, path):
        self.path = path
        _os.makedirs(_os.path.dirname(path), exist_ok=True)
        with self._db() as c:
            c.execute("CREATE TABLE IF NOT EXISTS templates (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, rows TEXT NOT NULL, "
                      "note TEXT NOT NULL DEFAULT '', created REAL NOT NULL, created_by TEXT, archived REAL)")
            c.execute("CREATE TABLE IF NOT EXISTS hidden_revisions (revision INTEGER PRIMARY KEY, hidden REAL NOT NULL)")

    def _db(self):
        return _sqlite3.connect(self.path, timeout=10)

    def list(self, archived=False):
        with self._db() as c:
            q = "SELECT id, name, rows, note, created, created_by, archived FROM templates %s ORDER BY id DESC" % (
                "" if archived else "WHERE archived IS NULL")
            return [{"id": r[0], "name": r[1], "rows": json.loads(r[2]), "note": r[3], "created": r[4], "created_by": r[5],
                     "archived": r[6]} for r in c.execute(q)]

    def add(self, name, rows, note, by):
        with self._db() as c:
            return c.execute("INSERT INTO templates (name, rows, note, created, created_by) VALUES (?,?,?,?,?)",
                             (name, json.dumps(rows), note, _time.time(), by)).lastrowid

    def archive(self, tid, on=True):
        with self._db() as c:
            return c.execute("UPDATE templates SET archived = ? WHERE id = ?", (_time.time() if on else None, int(tid))).rowcount

    def hidden(self):
        with self._db() as c:
            return {r[0] for r in c.execute("SELECT revision FROM hidden_revisions")}

    def hide(self, revision, on=True):
        with self._db() as c:
            if on:
                c.execute("INSERT OR REPLACE INTO hidden_revisions (revision, hidden) VALUES (?, ?)", (int(revision), _time.time()))
            else:
                c.execute("DELETE FROM hidden_revisions WHERE revision = ?", (int(revision),))


def _store(ctx):
    return _TemplateStore(_os.path.join(ctx.app.a.backup_dir, "online-award-templates.sqlite3"))


def _minutes(ctx):
    row = ctx.need_rb().pg.json("(SELECT to_jsonb(r) FROM server_rates r WHERE rate_key = %s)" % lit(ONLINE_MINUTES_KEY)) \
        if ctx.need_rb().pg.json("(SELECT to_jsonb(to_regclass('public.server_rates') IS NOT NULL))") else None
    if not row:
        return {"supported": False, "reason": "The game server needs the B21 release (setting 'Online Award: minutes online first')."}
    return {"supported": True, "value": int(float(row["value"])), "min": int(float(row["min_value"])), "max": int(float(row["max_value"]))}


@M.get("extras")
def extras(ctx):
    """Templates (built-in presets + saved), hidden revisions, the required online time."""
    st = _store(ctx)
    saved = st.list(archived=ctx.q("archived") == "1")
    ids = sorted({int(x["item_id"]) for t in saved for x in t["rows"]})
    tpl = _templates(ctx, ids)
    for t in saved:
        t["rows"] = [dict(x, name=(tpl.get(int(x["item_id"])) or {}).get("name")) for x in t["rows"]]
    return {"templates": saved, "hidden": sorted(st.hidden()), "online_minutes": _minutes(ctx)}


@M.post("templates/save")
def template_save(ctx):
    """POST {name, rows, note?} - a named template (checked like a publish, so a template is always publishable)."""
    b = ctx.body or {}
    name = str(b.get("name") or "").strip()
    if not 1 <= len(name) <= 60:
        raise Bad("the template name must be 1-60 characters")
    rows, _ = normalise(ctx, b.get("rows"))
    clean = [{k: r[k] for k in ("item_id", "quantity", "item_quality", "bound")} for r in rows]
    tid = _store(ctx).add(name, clean, str(b.get("note") or "")[:300], (getattr(common.ACTOR, "name", None) or ACTOR))
    return {"id": tid, "name": name}


@M.post(r"templates/(\d+)/remove")
def template_remove(ctx, tid):
    """Remove a saved template from the list (kept in the file: 'Show removed' + Restore brings it back)."""
    restore = bool((ctx.body or {}).get("restore"))
    if not _store(ctx).archive(int(tid), not restore):
        raise Bad("no template #%s" % tid)
    return {"id": int(tid), "removed": not restore}


@M.post(r"revisions/(\d+)/hide")
def revision_hide(ctx, rev):
    """Hide an old revision from History (the database keeps every revision: they cannot be deleted)."""
    hide = bool((ctx.body or {}).get("hide", True))
    cur = _q(ctx, "(SELECT to_jsonb(revision) FROM online_award_balance_publication WHERE family = 'online-award')")
    if hide and cur is not None and int(rev) == int(cur):
        raise Bad("the published revision cannot be hidden")
    _store(ctx).hide(int(rev), hide)
    return {"revision": int(rev), "hidden": hide}


@M.post("online-minutes")
def online_minutes(ctx):
    """POST {minutes, expected} - the server setting prize.online_award_minutes, through the Rates page's guarded save
    (change log + Undo; the running server reloads it at once)."""
    from . import rates as RT
    m = _minutes(ctx)
    if not m["supported"]:
        raise Bad(m["reason"])
    v = common.to_int((ctx.body or {}).get("minutes"), m["min"], m["max"], "minutes online")
    exp = (ctx.body or {}).get("expected")
    c = RT._save(ctx, {ONLINE_MINUTES_KEY: v}, {ONLINE_MINUTES_KEY: exp} if exp is not None else {}, None, None,
                 "Online award page: minutes online first")
    return {"change": common.public_change(c), "applies": c["applies"], "applies_text": c["applies_text"], "value": v}
