"""modules/title_giver.py - Title giver (wave 4 D item 7): every title with what it is awarded for (awarder NPC, quests,
instance, battlefield, items, stat bonus), grouped by category (Stats -> MATK, Level titles -> Mage, Instance titles ->
Wonderland ...), with multi-select to grant or revoke administrator copies for one character.
Page: static/modules/title_giver.js. Sources: modules/_title_sources.py (reviewed from the server source, cited per title).

Writes go through title_grants.write_batch (the shared offline grant path: backup, Undo, character must be logged out,
earned titles never touched, ownership limit checked). Medusa's live completion rules are read from
medusa_completion_reward_rules so the page shows the real difficulty/points of each Medusa title.
"""
from . import common, title_grants as T, _title_sources as TS
from .common import Bad

M = common.module(
    "title_giver", "Title giver", icon={"sprite": 811}, order=42, status="ready", needs_character=True,
    description="Every title with what awards it (NPC, quest, instance, battlefield, item, bonus); tick several to grant or revoke.")


def _medusa_rules(rb):
    try:
        if not rb.pg.json("(SELECT to_jsonb(to_regclass('public.medusa_completion_reward_rules') IS NOT NULL AND "
                          "to_regclass('public.medusa_reward_title_definitions') IS NOT NULL))"):
            return {}
        rows = rb.pg.json("(SELECT coalesce(json_agg(json_build_object('id', d.client_title_id, 'difficulty', r.difficulty, "
                          "'kind', r.reward_kind, 'threshold', r.threshold) ORDER BY r.difficulty, r.threshold), '[]') "
                          "FROM medusa_completion_reward_rules r JOIN medusa_reward_title_definitions d ON d.title = r.title)") or []
    except Exception:  # noqa: BLE001 - the page still shows the reviewed text
        return {}
    out = {}
    names = {1: "Normal", 2: "Enhanced", 3: "Mythic"}
    for r in rows:
        # reward_kind 2 = inclusive maximum completion time in seconds (only kind 2 rows carry a title)
        out.setdefault(int(r["id"]), []).append("%s difficulty, cleared within %s" % (
            names.get(r["difficulty"], r["difficulty"]), minutes(r["threshold"])) if r["kind"] == 2 else
            "%s difficulty, %s points" % (names.get(r["difficulty"], r["difficulty"]), common_fmt(r["threshold"])))
    return out


def minutes(sec):
    try:
        s = int(sec)
    except (TypeError, ValueError):
        return str(sec)
    return "%d min" % (s // 60) if s % 60 == 0 else "%d min %d s" % (s // 60, s % 60)


def common_fmt(n):
    try:
        return format(int(n), ",")
    except (TypeError, ValueError):
        return str(n)


def view(ctx, cid):
    rb = ctx.need_rb()
    cid = common.to_int(cid, 1, 2 ** 31 - 1, "character ID")
    ready = T.support(ctx)
    catalog = {int(r["title_id"]): r for r in T.catalogue(ctx)} if ready["available"] else {}
    client = {int(k): v for k, v in (ctx.catalog.file("titles") or {}).items() if str(k).isdigit()}
    owned = set()
    grants = []
    if ready["available"]:
        owned = {int(x) for x in (rb.pg.json("(SELECT coalesce(json_agg(title_id), '[]') FROM character_title_entitlements "
                                             "WHERE character_id = %d)" % cid) or [])}
        grants = sorted(int(r["title_id"]) for r in T.rows(ctx, cid))
    medusa = _medusa_rules(rb)
    ids = sorted(set(client) | set(catalog) | set(TS.S) | owned)
    titles = []
    for tid in ids:
        src = dict(TS.describe(tid))
        name = (catalog.get(tid) or {}).get("display_name") or (client.get(tid) or {}).get("name") or src.get("name") or "Title #%d" % tid
        desc = (catalog.get(tid) or {}).get("description") or (client.get(tid) or {}).get("desc") or ""
        src.pop("name", None)
        if tid in medusa:
            src["rules"] = medusa[tid]
        titles.append(dict(id=tid, name=name, description=desc, owned=tid in owned, admin=tid in grants,
                           earned=tid in owned and tid not in grants, grantable=tid in catalog, **src))
    order = {k: i for i, (k, _, _) in enumerate(TS.CATEGORIES)}
    subs_seen = {}
    for t in sorted(titles, key=lambda t: t["id"]):
        subs_seen.setdefault((t["category"], t["sub"]), len(subs_seen))
    titles.sort(key=lambda t: (order.get(t["category"], 99), subs_seen[(t["category"], t["sub"])], t["id"]))
    cats = []
    for key, title, why in TS.CATEGORIES:
        subs = []
        for t in titles:
            if t["category"] == key and t["sub"] not in subs:
                subs.append(t["sub"])
        cats.append(dict(id=key, title=title, why=why, subs=subs, count=sum(1 for t in titles if t["category"] == key)))
    return dict(character=cid, titles=titles, categories=cats, support=ready, expected=grants,
                writable=bool(rb.writable) and ready["available"], owned_limit=ready.get("owned_limit"),
                note="Ticking grants an administrator copy of the title. Earned titles stay owned when an "
                     "administrator copy is removed. The character must be logged out; it applies at next login.")


@M.get(r"view/(\d+)")
def view_route(ctx, cid):
    return view(ctx, cid)


@M.post(r"apply/(\d+)")
def apply(ctx, cid):
    """{add: [ids], remove: [ids], expected: [current administrator grant ids], note} -> the shared grant writer."""
    b = ctx.body
    if not isinstance(b, dict):
        raise Bad("the request must be an object")
    body = {"add": b.get("add") or [], "remove": b.get("remove") or [], "expected": b.get("expected"),
            "note": b.get("note") or "Title giver (multi-select)"}
    return T.write_batch(ctx, cid, body)
