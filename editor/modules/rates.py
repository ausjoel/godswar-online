"""modules/rates.py - the "Server rates" editor (v2). Contract: ../MODULES.md. Design notes: README.md section 9.

Every row of public.server_rates (the RATES patch; 22 keys on the live RATES build, 41 on the round-2 build) gets a
log-scale slider plus an exact number box on the page. The bounds, stock values, labels and descriptions come from
the TABLE, which the server binary rewrites at every start, so the round-2 build's higher bounds (up to x1,000,000)
appear by themselves. This file adds what the table does not carry:

* KEY_INFO: per key the item icon, which way is "more generous" (for the presets), the hard caps that make a higher
  value pointless (100,000,000 EXP per kill, int32 fields, client caps from Polish/POINTS-AUDIT.md) and the value
  where the first cap is reached (a marker on the slider);
* PRESETS: Stock, Double EXP weekend, x10 casual, x100 fast, Max - resolved against the bounds of THIS server, with
  the values that had to be clamped listed;
* the loot rules a drop multiplier really multiplies (English item and monster names) and who is in the world now.

Writes reuse rates.Rates (top-level rates.py, also the classic page): one guarded psql transaction (database
identity guard, FOR UPDATE, an in-transaction check that the values are still the ones the page showed -> 409),
then POST /rates/reload on the game server's management port so the running server applies it at once (else its
5 s poll does). Every save is a change record (engine "rates", module "rates") with Undo. Rates are server-wide,
never a character's row: nobody has to log out, an online player gets the new value at the next kill / hand-in /
tick. The stock settings (global EXP multiplier and level gap = after a restart; daily instance entries = next
entry) go through the same save.
"""
import datetime
from decimal import Decimal as D, InvalidOperation

from . import common
from .common import Bad, Refused, NotFound, Conflict

M = common.module(
    "rates", "Server rates",
    icon={"sprite": 664},          # catalog-icons.png tile
    order=70,
    status="ready",
    description="Server-wide multipliers and limits (EXP, Talent, pets, zodiac, drops, Silver, forging, Holy Box, "
                "daily caps, event times) with sliders up to the server's own limits; online players get a change "
                "within seconds, nobody has to log out.",
    needs_character=False,
)

KILL_CAP = 100_000_000          # MonsterDeathRewardCommandEnvelope: EXP / Talent EXP per kill (also quests on round 2)
INT32 = 2_147_483_647
UINT32 = 4_294_967_295
EXP_TO_200 = 14_016_850_101     # Fighter EXP from level 1 to 200 (POINTS-AUDIT row 1)

# Per key: icon item (catalog item id: its sprite is the picture, its game tooltip the hover), "dir" = which way is
# more generous for players (up / down / switch / hours / neutral; presets use it), "applies" = when a change is
# seen in game, "first_cap" = the value from which a cap stops further effect (slider marker) + why, "caps" = notes.
KEY_INFO = {
    "exp.monster": {"item": 4174, "dir": "up", "applies": "next kill",
                    "first_cap": KILL_CAP / 1116, "first_cap_why": "a tier-200 monster (1,116 EXP) reaches the 100,000,000 per-kill cap",
                    "caps": ["One kill pays at most 100,000,000 EXP (server settlement cap). A tier-1 monster (80 EXP) reaches it at x1,250,000, a tier-200 monster (1,116 EXP) at about x89,600.",
                             "Level stops at 200 and stored EXP at 4,294,967,295; the rest of a big kill is lost.",
                             "Monsters more than 20 levels below the player pay nothing (the 'Kill EXP level gap' stock setting)."]},
    "tp.monster": {"item": 4180, "dir": "up", "applies": "next kill",
                   "caps": ["10 Talent EXP per kill at x1; 100 Talent EXP = 1 Talent Point.",
                            "One kill pays at most 100,000,000 Talent EXP (x10,000,000; never reached).",
                            "Talent Points are a 32-bit number: at most 2,147,483,647.",
                            "the admin's client shows talent ranks only up to 60 (the server allows 100)."]},
    "exp.quest": {"item": 4174, "dir": "up", "applies": "next quest hand-in",
                  "caps": ["Round-2 server: one hand-in pays at most 100,000,000 EXP.", "Level stops at 200."]},
    "tp.quest": {"item": 4180, "dir": "up", "applies": "next quest hand-in",
                 "caps": ["Round-2 server: one hand-in pays at most 100,000,000 Talent EXP."]},
    "exp.events": {"item": 3900, "dir": "up", "applies": "next Lucky Gods claim / Zeus' Gift payout",
                   "caps": ["Round-2 server: one payout is capped at 100,000,000 EXP / Talent EXP."]},
    "pet.exp_monster": {"item": 10130, "dir": "up", "applies": "next kill",
                        "first_cap": KILL_CAP / 1116, "first_cap_why": "a tier-200 monster reaches the 100,000,000 per-kill cap",
                        "caps": ["One kill gives the pet at most 100,000,000 EXP.",
                                 "Pets stop at level 120 (252,947,820 EXP in total); the EXP field holds at most 4,294,967,295."]},
    "zodiac.energy": {"item": 3816, "dir": "up", "applies": "next 5-minute online tick",
                      "first_cap": 1_090_000 / 20, "first_cap_why": "one boosted tick (20 energy) fills the largest zodiac store (1,090,000)",
                      "caps": ["Energy is stored only up to the zodiac level's limit: 1,000 at zodiac level 1 up to 1,090,000 at level 30. The client shows that limit too."]},
    "instance.atlantis_hardpoints": {"item": 3944, "dir": "up", "applies": "next Atlantis settlement",
                                     "caps": ["One award stays at most 100,000,000.",
                                              "The Honour balance is a 32-bit number: a settlement that would pass 2,147,483,647 fails (nothing is paid)."]},
    "instance.medusa_honor": {"item": 3902, "dir": "up", "applies": "next Medusa Island settlement",
                              "caps": ["The Honour balance is a 32-bit number: a settlement that would pass 2,147,483,647 fails. At x10,000 the best run pays 33,750,000 (about 63 runs)."]},
    "instance.farm_points": {"item": 4100, "dir": "up", "applies": "next Lelantine Farm kill",
                             "first_cap": 1000, "first_cap_why": "a Cerberus kill (1,000 points) reaches the 1,000,000 per-kill cap",
                             "caps": ["One kill scores at most 1,000,000 points."]},
    "move.speed": {"item": 4100, "dir": "up", "applies": "players online within 15 seconds",
                   "caps": ["Multiplies with mounts, buffs and a GM's /speed."]},
    "drop.chance": {"item": 4150, "dir": "up", "applies": "next kill",
                    "caps": ["Every loot rule is capped at 100%.",
                             "The live database has very few loot rules (see the list below), so this multiplies almost nothing until monster_loot_rules is filled."]},
    "drop.quantity": {"item": 4151, "dir": "up", "applies": "next kill",
                      "first_cap": 255, "first_cap_why": "a quantity-1 drop reaches 255, the most one drop can carry",
                      "caps": ["One drop carries at most 255 (one byte on the wire); picking it up splits it into stacks."]},
    "drop.max_items": {"item": 4152, "dir": "up", "applies": "next kill",
                       "first_cap": 8, "first_cap_why": "the client's loot window has 8 slots",
                       "caps": ["At most 32 items roll per corpse, and never more than the monster's loot rules.",
                                "A corpse may roll more than 8; the server keeps the best 8 (quality, rarer chance, value), capped by drop.corpse_max_items (max 8, client limit).",
                                "drop.corpse_pages 2 keeps up to 16: the first 8 show as today, a second page with the rest after they are all picked up."]},
    # LOOT PAGING: no "dir" on purpose, so no preset (not even Max) turns paging on; it stays at its default 1.
    "drop.corpse_pages": {"item": 4152, "applies": "next kill",
                          "caps": ["1 (default) = one loot window, as before. 2 shows a second page after the first 8 are picked up (at most 16 drops per corpse).",
                                   "Never more than 8 per window: the client's loot window has 8 slots. Pick All takes one page at a time; the 20 s corpse window restarts for page 2."]},
    "money.quest_silver": {"item": 4152, "dir": "up", "applies": "next quest hand-in",
                           "caps": ["Round-2 server: at most 100,000,000 Silver per hand-in; the wallet stops at 2,147,483,647 (it saturates, the hand-in still works)."]},
    "money.quest_gold": {"item": 3961, "dir": "up", "applies": "next quest hand-in",
                         "caps": ["Round-2 server: at most 100,000,000 Gold per hand-in; the wallet stops at 2,147,483,647."]},
    "money.sell_price": {"item": 4150, "dir": "up", "applies": "next sale to an NPC shop",
                         "caps": ["x5 is the safe ceiling: above it an expensive full stack overflows a 32-bit total and the sale fails.",
                                  "The client's own price tooltip still shows the stock price."]},
    "forge.success": {"item": 4200, "dir": "up", "applies": "next forge attempt",
                      "first_cap": 10, "first_cap_why": "a 10% forge step becomes certain (100%)",
                      "caps": ["Chances are capped at 100%.", "The client shows its own odds, from EquipForge.xml."]},
    "forge.holystone_success": {"item": 9030, "dir": "up", "applies": "next holy stone upgrade",
                                "first_cap": 10, "first_cap_why": "even the 10% step (levels 7-9) becomes certain",
                                "caps": ["Chances are capped at 100%."]},
    "holybox.daily_limit": {"item": 9020, "dir": "up", "applies": "next Store at the Master Vestment Forger",
                            "caps": ["The client's counter display stops at 214,748,364; the server enforces the exact value.",
                                     "An active Battle Pass still bypasses the limit."]},
    "holybox.per_store_max": {"item": 9025, "dir": "up", "applies": "next Store",
                              "caps": ["A 32-bit number: at most 2,147,483,647 per Store."]},
    "holybox.capacity": {"item": 9023, "dir": "up", "applies": "next Store",
                         "first_cap": INT32 / 400_000_000, "first_cap_why": "Holy Box V reaches 2,147,483,647, the item EXP field's limit",
                         "caps": ["A box's EXP field is a 32-bit number: every box stops at 2,147,483,647 (Box V at x5.37, Box I at x21,474)."]},
    "holybox.min_level": {"item": 9021, "dir": "down", "applies": "next visit to the forger",
                          "caps": ["The gear itself still needs level 70."]},
    "cost.talent": {"item": 4181, "dir": "down", "applies": "next talent upgrade",
                    "caps": ["0 = free. Costs stop at 2,147,483,647 (a rank above that is unaffordable).",
                             "The client's talent window still prints the stock price; the admin's client shows ranks up to 60."]},
    "cost.zodiac_level": {"item": 3815, "dir": "down", "applies": "next zodiac level-up",
                          "caps": ["0 = free. The zodiac window prints the stock cost."]},
    "cost.zodiac_grid": {"item": 3814, "dir": "down", "applies": "next zodiac grid upgrade",
                         "caps": ["0 = free (energy AND Talent Points). The grid tooltip shows the stock cost."]},
    "exp.faction_crier": {"item": 4174, "dir": "up", "applies": "next nameplate exchange",
                          "caps": ["At most 100,000,000 EXP per exchange."]},
    "tp.faction_crier": {"item": 4180, "dir": "up", "applies": "next nameplate exchange",
                         "caps": ["At most 1,000,000 Talent Points per exchange (they go straight into a 32-bit balance)."]},
    "exp.pill": {"item": 4174, "dir": "up", "applies": "next EXP pill used",
                 "first_cap": 100, "first_cap_why": "one pill (1,000,000 EXP) reaches the 100,000,000 cap",
                 "caps": ["One pill gives at most 100,000,000 EXP: anything above x100 changes nothing.",
                          "The old EXP scrolls 4620-4623 do nothing on this server (not implemented upstream)."]},
    "pet.exp_items": {"item": 10130, "dir": "up", "applies": "next Morning Dew used",
                      "caps": ["Capped at the pet's EXP field (4,294,967,295); the game then says 'maximum reached'."]},
    "cap.lucky_gods_daily": {"item": 4164, "dir": "up", "applies": "next wish (today's count is kept)", "caps": []},
    "cap.wishing_pool_daily": {"item": 4164, "dir": "up", "applies": "next use (today's count is kept)",
                               "caps": ["The 1-hour wait between uses stays."]},
    "cap.zeus_gifts_weekday": {"item": 3900, "dir": "up", "applies": "next delivery",
                               "caps": ["12 is the most the gift round table has."]},
    "cap.zeus_gifts_weekend": {"item": 3900, "dir": "up", "applies": "next delivery",
                               "caps": ["12 is the most the gift round table has."]},
    "cap.zeus_exchanges_week": {"item": 3900, "dir": "up", "applies": "next exchange", "caps": []},
    "cap.quest_daily": {"item": 4600, "dir": "up", "applies": "next quest accept / hand-in",
                        "caps": ["One-time quests stay one-time."]},
    "event.pindus_always_open": {"item": 2506, "dir": "switch", "applies": "next entry attempt",
                                 "caps": ["The client's event list still prints the stock times."]},
    "event.nimini_always_open": {"item": 2506, "dir": "switch", "applies": "next entry attempt",
                                 "caps": ["The client's event list still prints the stock times."]},
    "event.battlefield_minutes": {"item": 2506, "dir": "up", "applies": "next window", "caps": []},
    "event.battlefield_shift_hours": {"item": 2506, "dir": "neutral", "applies": "next window",
                                      "caps": ["Event times follow the realm calendar (Asia/Singapore, UTC+8): Brisbane (AEST) is 2 hours later. Example from the round-2 notes: -2 moves the 21:00 Pindus window to 19:00 realm time = 21:00 AEST.", "The client's event list still prints the stock times."]},
    "event.wonderland_close_hour": {"item": 2506, "dir": "up", "applies": "next entry attempt", "caps": ["24 = open all day."]},
    "event.zeus_stone_any_day": {"item": 3900, "dir": "switch", "applies": "next hand-in", "caps": []},
}

CATEGORY_ITEM = {"Character EXP & Talent": 4174, "Pets": 10130, "Zodiac": 3816, "Instances": 3902, "Monster drops": 4151,
                 "Money": 4152, "Forging": 4200, "Holy Box (Master Vestment Forger)": 9020, "Upgrade costs": 4181,
                 "Daily and weekly limits": 4164, "Event times": 2506}

EXP_KEYS = ["exp.monster", "tp.monster", "exp.quest", "tp.quest", "exp.events", "pet.exp_monster",
            "exp.faction_crier", "tp.faction_crier"]

# presets: {key: value}; keys not listed go back to stock (Max is computed from the bounds and "dir")
PRESETS = [
    {"id": "stock", "label": "Stock", "item": 4102,
     "help": "Every setting back to the stock reborn value (what the server does without the rates patch)."},
    {"id": "double", "label": "Double EXP weekend", "item": 4174,
     "help": "EXP, Talent and pet EXP from kills, quests and event NPCs x2. Everything else stock.",
     "values": dict({k: 2 for k in EXP_KEYS})},
    {"id": "x10", "label": "x10 casual", "item": 4180,
     "help": "EXP, Talent, pet EXP and zodiac energy x10; honour, farm points and drop chance x3; drop quantity x2; "
             "quest Silver x10; forging x1.5. Everything else stock.",
     "values": dict({k: 10 for k in EXP_KEYS}, **{"zodiac.energy": 10, "exp.pill": 10, "pet.exp_items": 10,
                                                   "instance.atlantis_hardpoints": 3, "instance.medusa_honor": 3,
                                                   "instance.farm_points": 3, "drop.chance": 3, "drop.quantity": 2,
                                                   "money.quest_silver": 10, "money.quest_gold": 2,
                                                   "forge.success": 1.5, "forge.holystone_success": 1.5})},
    {"id": "x100", "label": "x100 fast", "item": 4152,
     "help": "EXP, Talent, pet EXP and zodiac energy x100; honour and farm points x10; drops x10 / x5 / x2; quest "
             "Silver x100, Gold x10; forging x3; Holy Box x5; upgrade costs halved; 3x the daily limits.",
     "values": dict({k: 100 for k in EXP_KEYS}, **{"zodiac.energy": 100, "exp.pill": 100, "pet.exp_items": 100,
                                                    "instance.atlantis_hardpoints": 10, "instance.medusa_honor": 10,
                                                    "instance.farm_points": 10, "drop.chance": 10, "drop.quantity": 5,
                                                    "drop.max_items": 2, "money.quest_silver": 100, "money.quest_gold": 10,
                                                    "money.sell_price": 2, "forge.success": 3, "forge.holystone_success": 3,
                                                    "holybox.capacity": 5, "cost.talent": 0.5, "cost.zodiac_level": 0.5,
                                                    "cost.zodiac_grid": 0.5, "cap.lucky_gods_daily": 30,
                                                    "cap.wishing_pool_daily": 9, "cap.quest_daily": 3,
                                                    "cap.zeus_exchanges_week": 15})},
    {"id": "max", "label": "Max", "item": 9025,
     "help": "Everything as generous as THIS server allows: every reward at its maximum, costs free, daily limits at "
             "their maximum, battlefields always open. The caps still apply (100,000,000 EXP per kill, 100% chances, "
             "32-bit wallets). Use with care on the live server."},
]
DIR_MAX = {"up": "max", "down": "min", "switch": "max"}


def _n(v):
    if v is None:
        return None
    d = D(str(v))
    return int(d) if d == d.to_integral_value() else float(d)


def _same_num(a, b):
    """True when the value a page sent (number, numeric string or null) is the stored value b. Garbage is simply
    'not the same' (-> 409), never a 500."""
    if a is None or b is None:
        return a is None and b is None
    try:
        x, y = D(str(a).strip().replace(",", "")), D(str(b))
        return x.is_finite() and x == y
    except (InvalidOperation, ValueError, TypeError):
        return False


_ITEMS = {}


def _item_row(item_id):
    """{name, sprite} of an item from the catalogue index (no database read)."""
    if not _ITEMS:
        ix = common.CATALOG.index("item")
        f = ix["fields"]
        for r in ix["rows"]:
            _ITEMS[r[0]] = {"name": r[f.index("name")], "sprite": r[f.index("sprite")]}
    return _ITEMS.get(item_id) or {}


def _rates(ctx):
    r = getattr(ctx.app, "rates", None)
    if not r:
        raise Bad("the Server rates page needs the reborn adapter (see the editor's start-up log)")
    return r


def _preset_values(p, rows, loot_tables=None):
    """{key: {"value", "wanted", "clamped", "note"}} for every row (stock for keys the preset does not name).
    loot_tables: monster_loot_tables rows; Max keeps drop.max_items x the largest maximum_drops at 8 or less. That is
    no longer a client limit: with drop.max_items above 1 a corpse may roll more than 8, and the server (F-166) keeps the
    best 8 (quality, then rarer chance, then value), capped by drop.corpse_max_items (max 8, the client limit)."""
    out = {}
    biggest = max([int(t.get("maximum_drops") or 1) for t in (loot_tables or [])] or [1])
    for r in rows:
        k, lo, hi, de = r["rate_key"], D(str(r["min_value"])), D(str(r["max_value"])), D(str(r["default_value"]))
        info = KEY_INFO.get(k, {})
        note = None
        if p["id"] == "max" and k == "drop.max_items":
            want = max(lo, min(hi, D(8 // max(1, biggest))))
            note = ("kept at x%s: the largest loot table drops %d item(s). With drop.max_items above 1 a corpse may "
                    "roll more than 8; the server keeps the best 8 (quality, rarer chance, value), capped by "
                    "drop.corpse_max_items (max 8, client limit)" % (_n(want), biggest))
            out[k] = {"value": _n(want), "wanted": _n(want), "clamped": False, "note": note}
            continue
        if p["id"] == "max":
            if k == "event.wonderland_close_hour":
                want = hi
            elif k == "event.battlefield_shift_hours":
                want = de
            else:
                side = DIR_MAX.get(info.get("dir"))
                want = hi if side == "max" else lo if side == "min" else de
        else:
            want = D(str((p.get("values") or {}).get(k, de)))
        val = min(hi, max(lo, want))
        if r["kind"] == "absolute":
            val = val.to_integral_value()
        out[k] = {"value": _n(val), "wanted": _n(want), "clamped": val != want, "note": note}
    return out


CATEGORY_ORDER = ["Character EXP & Talent", "Pets", "Zodiac", "Instances", "Monster drops", "Money", "Forging",
                  "Holy Box (Master Vestment Forger)", "Upgrade costs", "Daily and weekly limits", "Event times"]


def _stock_list(ctx):
    import sys
    mod = sys.modules.get(type(_rates(ctx)).__module__)
    return list(getattr(mod, "STOCK_SETTINGS", []))


def _read(ctx, extras=True):
    """Everything the page shows in ONE psql run (rates.Rates.state() needs about a dozen; this page must stay
    fast): rates rows, revision, stock settings, and with extras the database log, the loot rules, who is in the
    world. Same shape as rates.Rates.state() plus the extras."""
    rt = _rates(ctx)
    rb = ctx.need_rb()
    stock = _stock_list(ctx)
    stock_sql = ", ".join("%s, (SELECT to_jsonb(%s) FROM public.%s WHERE %s)" % (common.lit(s["id"]), s["column"], s["table"], s["where"])
                          for s in stock) or "'_', null"
    parts = ["'stock', jsonb_build_object(%s)" % stock_sql]
    if extras:
        parts += [
            "'loot', jsonb_build_object("
            "'rules', (SELECT coalesce(jsonb_agg(jsonb_build_object('monster', r.template_key, 'item', r.item_id, "
            "'chance_bp', r.chance_basis_points, 'min', r.minimum_quantity, 'max', r.maximum_quantity, 'enabled', r.enabled) "
            "ORDER BY r.template_key, r.loot_index), '[]') FROM (SELECT * FROM public.monster_loot_rules ORDER BY template_key, loot_index LIMIT 200) r), "
            "'tables', (SELECT coalesce(jsonb_agg(jsonb_build_object('monster', t.template_key, 'maximum_drops', t.maximum_drops) "
            "ORDER BY t.template_key), '[]') FROM (SELECT * FROM public.monster_loot_tables ORDER BY template_key LIMIT 200) t), "
            "'rule_count', (SELECT count(*) FROM public.monster_loot_rules), "
            "'max_drops', (SELECT max(maximum_drops) FROM public.monster_loot_tables))",
            "'world', (SELECT jsonb_build_object('count', count(*), 'names', coalesce(jsonb_agg(c.name ORDER BY c.name), '[]')) "
            "FROM public.character_base c WHERE c.checkpoint_owner_id IS NOT NULL)"]
    rates_parts = ["'rows', (SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r.sort_order, r.rate_key), '[]') FROM public.server_rates r)",
                   "'state', (SELECT to_jsonb(s) FROM public.server_rates_state s WHERE setting_id = 1)"]
    if extras:
        rates_parts.append("'log', (SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.id DESC), '[]') FROM "
                           "(SELECT * FROM public.server_rates_log ORDER BY id DESC LIMIT 60) l)")
    try:
        d = rb.pg.json("jsonb_build_object(%s)" % ", ".join(rates_parts + parts), timeout=30)
        d["table"] = True
    except common.RebornError as e:
        if "server_rates" not in str(e):
            raise
        d = rb.pg.json("jsonb_build_object(%s)" % ", ".join(parts), timeout=30)       # stock server: no rates table
        d.update(table=False, rows=[], state=None, log=[])
    rows = d.get("rows") or []
    for r in rows:
        for k in ("value", "default_value", "min_value", "max_value", "step"):
            r[k] = _n(r[k])
    order = {c: i for i, c in enumerate(CATEGORY_ORDER)}
    rows.sort(key=lambda r: (order.get(r["category"], 99), r["category"], r["sort_order"], r["rate_key"]))
    cats = []
    for r in rows:
        if r["category"] not in cats:
            cats.append(r["category"])
    sv = d.get("stock") or {}
    stock_out = []
    for s in stock:
        stock_out.append(dict(s, value=_n(sv.get(s["id"])), available=True))
    out = {"database": rb.db, "writable": rb.writable, "live": rb.live, "table": d["table"], "categories": cats,
           "rows": rows, "revision": (d.get("state") or {}).get("revision"), "state": d.get("state"), "stock": stock_out,
           "log": [dict(l, old_value=_n(l.get("old_value")), new_value=_n(l.get("new_value"))) for l in d.get("log") or []]}
    if extras:
        out["server"] = rt.server_status()
        loot = d.get("loot") or {"rules": [], "tables": []}
        for r in loot.get("rules") or []:
            row = _item_row(r["item"])
            r["item_name"], r["sprite"] = row.get("name") or "item #%s" % r["item"], row.get("sprite")
            r["monster_name"] = common.CATALOG.name("monster", r["monster"])
        for t in loot.get("tables") or []:
            t["monster_name"] = common.CATALOG.name("monster", t["monster"])
        out["loot"], out["world"] = loot, d.get("world") or {"count": None, "names": []}
        checks = []
        if not out["table"]:
            checks.append({"severity": "warn", "msg": "public.server_rates does not exist in %s: the game server is stock reborn "
                                                      "without the RATES patch (Polish/RATES.md deploys it)." % rb.db})
        for r in rows:
            if not (D(str(r["min_value"])) <= D(str(r["value"])) <= D(str(r["max_value"]))):
                checks.append({"severity": "error", "msg": "%s = %s is outside %s..%s" % (r["rate_key"], r["value"], r["min_value"], r["max_value"])})
        srv = out["server"]
        if out["table"] and srv.get("reachable"):
            if srv.get("revision") != out["revision"]:
                checks.append({"severity": "warn", "msg": "the game server runs revision %s, the database is at %s (it re-reads "
                                                          "the table within %s s)" % (srv.get("revision"), out["revision"], srv.get("pollSeconds"))})
            for r in rows:
                v = (srv.get("rates") or {}).get(r["rate_key"])
                if v is not None and D(str(v)) != D(str(r["value"])):
                    checks.append({"severity": "warn", "msg": "%s: the server uses %s, the database holds %s" % (r["label"], v, r["value"])})
            if srv.get("lastError"):
                checks.append({"severity": "error", "msg": "server reload error: %s" % srv["lastError"]})
        out["checks"] = checks
    return out


def _state(ctx):
    st = _read(ctx, extras=True)
    rows = st.get("rows") or []
    caps = common.capabilities(ctx.app)
    # keys the RUNNING server reads (GET /rates). A database that once ran the round-2 build keeps its 19 extra rows
    # after a rollback to the stock RATES build, which ignores them: say so, and keep presets off them.
    srv = st.get("server") or {}
    srv_keys = set(srv["rates"]) if srv.get("reachable") and isinstance(srv.get("rates"), dict) and srv["rates"] else None
    unused = [r["rate_key"] for r in rows if srv_keys is not None and r["rate_key"] not in srv_keys]
    for r in rows:
        info = KEY_INFO.get(r["rate_key"], {})
        item = info.get("item") or CATEGORY_ITEM.get(r["category"])
        r["info"] = {"item": item, "sprite": _item_row(item).get("sprite"), "item_name": _item_row(item).get("name"),
                     "dir": info.get("dir", "neutral"), "applies": info.get("applies", "next use"), "caps": info.get("caps", []),
                     "first_cap": round(info["first_cap"], 2) if info.get("first_cap") else None,
                     "first_cap_why": info.get("first_cap_why"), "unused": r["rate_key"] in unused}
    if unused:
        st.setdefault("checks", []).append({"severity": "warn", "msg": (
            "%d settings in the table are not read by the running game server (%s): they were left by a newer server "
            "build. Changing them does nothing until that build runs again; the presets leave them alone."
            % (len(unused), ", ".join(unused)))})
    keys = srv_keys if srv_keys is not None else {r["rate_key"] for r in rows}
    exp = next((r for r in rows if r["rate_key"] == "exp.monster"), None)
    st["presets"] = [dict({k: v for k, v in p.items() if k != "values"}, resolved=_preset_values(p, [r for r in rows if r["rate_key"] not in unused],
                                                                                 [{"maximum_drops": (st.get("loot") or {}).get("max_drops")}]),
                          sprite=_item_row(p.get("item")).get("sprite")) for p in PRESETS]
    st["stock_sprite"] = _item_row(4102).get("sprite")
    st["category_item"] = {c: {"item": i, "sprite": _item_row(i).get("sprite")} for c, i in CATEGORY_ITEM.items()}
    maxtxt = "{:,}".format(exp["max_value"]) if exp else "?"
    round2 = "cost.talent" in keys
    st["build"] = {
        "round2": round2, "keys": len(rows) - len(unused), "unused": unused, "exp_max": exp["max_value"] if exp else None,
        "live_edits": bool(caps.get("available")), "live_operations": sorted((caps.get("operations") or {}).keys()),
        "capabilities_error": caps.get("error"),
        "note": ("Round-2 server: %d settings, multipliers up to x%s." % (len(rows) - len(unused), maxtxt)) if round2 else
                ("Stock RATES server: %d settings, EXP up to x%s. The round-2 server update adds 19 settings and raises the "
                 "limits up to x1,000,000; this page picks them up by itself." % (len(rows) - len(unused), maxtxt)) if rows else
                "This database has no rates table (stock reborn server without the RATES patch)."}
    st["constants"] = {"kill_cap": KILL_CAP, "int32": INT32, "uint32": UINT32, "exp_to_200": EXP_TO_200}
    st["module_changes"] = [common.public_change(c) for c in ctx.app.changes[-200:]
                            if c.get("engine") == "rates" and c.get("database") == st.get("database")][::-1][:40]
    for s in st.get("stock") or []:
        s["field"] = "%s.%s" % (s["table"], s["column"])
        if s.get("nullable"):        # the classic page's help says "no paid retries"; the database comment and reborn
            s["help"] = ("Paid retries (1 Opal each) after the free entries. Empty = unlimited (the database's NULL; "   # migration 215 say NULL = unlimited
                         "reborn migration 215 made Wonderland unlimited this way). 0 = no paid retries.")
    return st


@M.get("state")
def state(ctx):
    """GET /api/m/rates/state - every rate with its bounds (from the table), the server's running values, presets
    resolved for this server, cap notes, the stock settings, the database history and this page's changes."""
    ctx.need_rb()
    return _state(ctx)


@M.get("history")
def history(ctx):
    """GET /api/m/rates/history?key=&limit= - server_rates_log (written by the table's trigger, also for plain SQL)."""
    ctx.need_rb()
    limit = ctx.qint("limit", 200, 1, 2000)
    key = ctx.q("key")
    if key and not all(ch.isalnum() or ch in "._" for ch in key):
        raise Bad("bad rate key")
    where = "WHERE rate_key = %s" % common.lit(key) if key else ""
    rows = ctx.rb.pg.json("(SELECT coalesce(jsonb_agg(to_jsonb(l) ORDER BY l.id DESC), '[]') FROM (SELECT * FROM "
                          "public.server_rates_log %s ORDER BY id DESC LIMIT %d) l)" % (where, limit), timeout=20)
    for r in rows:
        r["old_value"], r["new_value"] = _n(r.get("old_value")), _n(r.get("new_value"))
    return {"rows": rows}


def _check_writable(ctx):
    rb = ctx.need_rb()
    if not rb.writable:
        raise Refused("this editor is read-only for database %s (the live database needs OPEN-EDITOR.command 'Edit'; "
                      "a copy is writable), so rates cannot be saved here" % rb.db)


def _save(ctx, values, expected, stock, stock_expected, note, undo_of=None):
    """The guarded write. Same rules and the same change record as rates.Rates.save (so the Change log's Undo works
    too) with one read instead of a dozen: validate against the table's bounds, 409 when a value is not the one the
    page showed, then rates.Rates._sql (db guard, FOR UPDATE, in-transaction re-check, guarded stock updates) and
    POST /rates/reload."""
    _check_writable(ctx)
    rt = _rates(ctx)
    values, expected, stock, stock_expected = values or {}, expected or {}, stock or {}, stock_expected or {}
    for part, v in (("values", values), ("expected", expected), ("stock", stock), ("stock_expected", stock_expected)):
        if not isinstance(v, dict):
            raise Bad("%s must be an object" % part)
    note = str(note or "")[:200]
    with rt.lock:
        cur = _read(ctx, extras=False)
        if (values or expected) and not cur["table"]:
            raise Bad("public.server_rates does not exist in %s (the server is not the RATES build)" % ctx.rb.db)
        rows = {r["rate_key"]: r for r in cur["rows"]}
        stock_now = {s["id"]: s for s in cur["stock"]}
        stock_by = {s["id"]: s for s in _stock_list(ctx)}
        plan, before, after, conflicts = [], {}, {}, []
        for k, v in values.items():
            r = rows.get(k)
            if not r:
                raise Bad("unknown rate %r (this server has %d settings)" % (k, len(rows)))
            try:
                nv = D(str(v).strip().replace(",", ""))
            except (InvalidOperation, ValueError):
                raise Bad("%s must be a number (got %r)" % (r["label"], v))
            if not nv.is_finite() or nv < D(str(r["min_value"])) or nv > D(str(r["max_value"])):
                raise Bad("%s must be %s..%s on this server (got %s)" % (r["label"], r["min_value"], r["max_value"], v))
            if r["kind"] == "absolute" and nv != nv.to_integral_value():
                raise Bad("%s must be a whole number" % r["label"])
            if nv != nv.quantize(D("0.0001")):
                raise Bad("%s: at most 4 decimals" % r["label"])
            nv = nv.quantize(D("0.0001"))
            if k in expected and not _same_num(expected[k], r["value"]):
                conflicts.append({"table": "server_rates", "row": k, "what": "%s -> %s" % (expected[k], r["value"])})
            if D(str(r["value"])) == nv:
                continue
            before[k], after[k] = _n(r["value"]), _n(nv)
            plan.append((k, nv))
        stock_plan = []
        for sid, v in stock.items():
            s = stock_by.get(sid)
            if not s:
                raise Bad("unknown stock setting %r" % sid)
            if (v is None or str(v).strip() == "") and s.get("nullable"):
                nv = None
            else:
                try:
                    if isinstance(v, bool) or not isinstance(v, (int, float, str)):
                        raise ValueError("not a number")
                    dv = D(str(v).strip())
                    nv = int(dv)          # NaN -> ValueError, Infinity -> OverflowError
                except (InvalidOperation, ValueError, TypeError, OverflowError):
                    raise Bad("%s must be a whole number (got %r)" % (s["label"], v))
                if dv != nv or nv < s["min"] or nv > s["max"]:
                    raise Bad("%s must be a whole number %s..%s (got %s)" % (s["label"], s["min"], s["max"], v))
            old = stock_now[sid]["value"]
            if sid in stock_expected and not _same_num(stock_expected[sid], old):
                conflicts.append({"table": s["table"], "row": s["column"], "what": "%s -> %s" % (stock_expected[sid], old)})
            if old == nv:
                continue
            before["stock:" + sid], after["stock:" + sid] = old, nv
            stock_plan.append((s, nv))
        if conflicts:
            raise Conflict("the values changed since the page read them (another admin, an Undo, or SQL); nothing was "
                           "written", conflicts)
        if not plan and not stock_plan:
            raise Bad("nothing to change: every value is already what you asked for")
        restart = any(s.get("applies") == "restart" for s, _ in stock_plan)
        applies = "after_restart" if restart else "hot_reload"
        sql = rt._sql(plan, stock_plan, note or ("undo of %s" % undo_of if undo_of else ""), "gwadmin-web:rates",
                      guard_values={k: rows[k]["value"] for k, _ in plan},
                      guard_stock={s["id"]: stock_now[s["id"]]["value"] for s, _ in stock_plan})
        ctx.rb.pg.run(sql, timeout=60)
        reload = rt.reload_server() if plan else {"reachable": False, "note": "no server_rates change"}
        stamp = datetime.datetime.now()
        labels = {k: rows[k]["label"] for k, _ in plan}
        labels.update({"stock:" + s["id"]: s["label"] for s, _ in stock_plan})
        def fmt(k, v):
            if v is None:
                return "(empty)"
            t = "{:,}".format(v) if isinstance(v, int) or (isinstance(v, float) and v >= 1000) else str(v)
            return "x" + t if rows.get(k, {}).get("kind") == "multiplier" else t
        action = ("undo %s: " % undo_of if undo_of else "server rates: ") + "; ".join(
            "%s %s -> %s" % (labels[k], fmt(k, before[k]), fmt(k, after[k])) for k in before)
        c = {"engine": "rates", "module": M.name, "id": "rt-" + stamp.strftime("%Y%m%d-%H%M%S-%f"),
             "time": stamp.isoformat(timespec="seconds"), "database": ctx.rb.db, "character": "(server rates)", "cid": None,
             "action": action, "note": note, "sql": [{"db": ctx.rb.db, "sql": sql}], "before": before, "after": after,
             "labels": labels, "reload": reload, "output": rt._reload_text(reload, after), "undone": False,
             "applies": applies, "applies_text": common.APPLIES_TEXT.get(applies, applies)}
        if undo_of:
            c["undo_of"] = undo_of
            c["result"] = {"identical": {"values": True}, "all_identical": True}
        ctx.app.record_reborn_change(c)
        return c


def _answer(ctx, c):
    server = c.get("reload") or {}
    applied = {}
    for k, v in (c.get("after") or {}).items():
        if not k.startswith("stock:") and isinstance(server.get("rates"), dict) and k in server["rates"]:
            applied[k] = D(str(server["rates"][k])) == D(str(v))
    return {"change": common.public_change(c), "mode": "hot" if server.get("reachable") else "poll",
            "applies": c["applies"], "applies_text": c["applies_text"], "server_applied": applied,
            "server_revision": server.get("revision"), "output": c.get("output"), "result": c.get("result"),
            "state": _state(ctx)}


@M.post("save")
def save(ctx):
    """POST /api/m/rates/save {values: {key: new}, expected: {key: shown}, stock: {id: new}, stock_expected: {id: shown},
    note} - one transaction, then the server reloads. 409 when anything changed since the page read it."""
    b = ctx.body
    c = _save(ctx, b.get("values"), b.get("expected"), b.get("stock"), b.get("stock_expected"), b.get("note"))
    return _answer(ctx, c)


@M.post("reload")
def reload(ctx):
    """POST /api/m/rates/reload - ask the running server to re-read the table now (it also polls every 5 s)."""
    return {"reload": _rates(ctx).reload_server(), "state": _state(ctx)}


@M.post("undo")
def undo(ctx):
    """POST /api/m/rates/undo {change, overwrite} - put back what a rates save changed (the same rule as the Change
    log's Undo). 409 when the values changed since that save, unless overwrite."""
    _check_writable(ctx)
    cid = str(ctx.body.get("change") or "")
    c = next((x for x in ctx.app.changes if x.get("id") == cid), None)
    if not c:
        raise NotFound("no change %r in this editor's change log" % cid)
    if c.get("engine") != "rates":
        raise Bad("change %s is not a Server rates change; undo it from the Change log" % cid)
    if c.get("undone"):
        raise Bad("change %s is already undone" % cid)
    if c.get("undo_of"):
        raise Bad("change %s is itself an undo; save the values again instead" % cid)
    if c.get("database") != ctx.rb.db:
        raise Bad("change %s was made on database %s; this editor edits %s" % (cid, c.get("database"), ctx.rb.db))
    cur = _read(ctx, extras=False)
    rows = {r["rate_key"]: r for r in cur["rows"]}
    stock_now = {s["id"]: s for s in cur["stock"]}
    conflicts, values, expected, stock, stock_expected = [], {}, {}, {}, {}
    for k, want_now in (c.get("after") or {}).items():
        if k.startswith("stock:"):
            sid = k[6:]
            now = stock_now.get(sid, {}).get("value")
            if now != want_now:
                conflicts.append({"table": "stock setting", "row": sid, "what": "%s -> %s" % (want_now, now)})
            stock[sid], stock_expected[sid] = c["before"][k], now
        else:
            now = rows.get(k, {}).get("value")
            if now is None or D(str(now)) != D(str(want_now)):
                conflicts.append({"table": "server_rates", "row": k, "what": "%s -> %s" % (want_now, now)})
            values[k], expected[k] = c["before"][k], now
    if conflicts and not ctx.body.get("overwrite"):
        raise Conflict("these values changed since that save; nothing was written", conflicts)
    r = _save(ctx, values, expected, stock, stock_expected, "undo of %s" % c["id"], undo_of=c["id"])
    c["undone"] = True
    ctx.app._save_change(c)
    out = _answer(ctx, r)
    out["undo_of"] = c["id"]
    return out


@M.post("bounds")
def bounds(ctx):
    """POST /api/m/rates/bounds - always refused: the page's "raise the limit" button explains why."""
    raise Refused("the lowest and highest value of every rate come from the game server binary "
                  "(Application/Rates/ServerRates.cs). It writes them into server_rates at every start and would undo "
                  "any change here; the CHECK constraints then keep every value inside them. The round-2 server update "
                  "raises the multipliers up to x1,000,000; once it runs, this page offers the new limits by itself.")


@M.get("info")
def info(ctx):
    """GET /api/m/rates/info - short summary (Home page / tests)."""
    st = _state(ctx)
    return {"module": M.name, "status": M.status, "database": st.get("database"), "keys": len(st.get("rows") or []),
            "revision": st.get("revision"), "server": (st.get("server") or {}).get("revision"), "build": st["build"],
            "time": datetime.datetime.now().isoformat(timespec="seconds")}
