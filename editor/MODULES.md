# MODULES.md: the contract every editor module follows

Written 2026-10-28 (task "framework"). The new editor is a **shell** (sidebar of game systems, top bar,
shared components) plus one **module** per game system. Seven module tasks build their editors in parallel,
so each one owns its own files and nothing else.

Read first: `schema/WRITE-RULES.md` (what may be written and how), `schema/SCHEMA-MAP.md` +
`schema/glossary.json` (what every column is), `catalog-origin/CATALOG.md` (every ID type with English names
and icons), `static/ui/UI-STYLE.md` (how the pages should look).

---

## 1. Files: what you own and what you must not touch

A module called `<name>` (lower case, e.g. `inventory`) owns exactly three files:

| File | What goes in it |
|---|---|
| `modules/<name>.py` | the backend: `M = common.module(...)` and its routes |
| `static/modules/<name>.js` | the page: `GW.module("<name>", {mount, unmount})` |
| `static/modules/<name>.css` | its styles; every rule starts with `.gw-mod-<name>` |

Stubs exist for `character`, `inventory`, `pets`, `skills`, `guild`, `world` and `rates` (each page says
"coming"). `accounts` is a finished module (it is the worked example below).

**Do not edit** (they are shared; ask the framework owner instead):
`server.py`, `reborn.py`, `rates.py`, `modules/__init__.py`, `modules/common.py`, `static/shared/*`,
`static/shell.*`, `static/ui/*`, `schema/*`, `catalog-origin/*` and the other modules' files. If you need something shared, put a private
helper in your own file first and say in your report what should move into `common.py` / `static/shared/`.

A new module is picked up automatically: any `modules/*.py` (not starting with `_`, not `common.py`) is
imported at start-up. One that fails to import is shown in the sidebar with its error; the others keep working.

## 2. Backend: `modules/<name>.py`

```python
from . import common
from .common import Bad, Refused, NotFound, Conflict, OnlineRefused, lit

M = common.module(
    "inventory", "Inventory",
    icon={"sprite": 1063},      # a tile of static/catalog-icons.png (catalog "sprite" numbers) or {"ui": "<ui-kit sprite>"}
    order=20,                   # sidebar position
    status="coming",            # "coming" until the editor is done, then "ready"
    description="One line for Home and the page header.",
    needs_character=True,       # the shell passes the selected character (ctx.cid on the page)
)

@M.get(r"bag/(\d+)")            # GET /api/m/inventory/bag/7  -> fn(ctx, "7")
def bag(ctx, cid):
    return {...}                # any JSON-able object

@M.post("give")                 # POST /api/m/inventory/give  (JSON body in ctx.body; CSRF checked by server.py)
def give(ctx):
    ...
```

Routes are regular expressions on the path after `/api/m/<name>/`; groups become arguments. Return plain
JSON-able data. Raise to answer an error; `common.guarded` turns them into HTTP codes the shared front end
understands:

| Raise | HTTP | The page shows |
|---|---|---|
| `Bad("...")` | 400 | the message |
| `Refused("...")` | 403 | "Read-only: ..." (say WHY, e.g. sealed content, evidence) |
| `NotFound("...")` | 404 | the message |
| `Conflict("...", conflicts)` | 409 | "changed since you opened it" + list, then reload |
| `OnlineRefused("...", info)` | 423 | "character online" + what still works live + Kick |
| `reborn.RebornError` | 400 | the PostgreSQL / guard message |

### 2.1 What `ctx` gives you

| | |
|---|---|
| `ctx.rb` | the `reborn.Reborn` adapter (`ctx.need_rb()` raises if it is unavailable). Reads: `rb.pg.json("(SELECT ...)")` (read-only), `rb.dump(cid)` (all character tables), `rb.sheet(cid)`, `rb.characters(q)`, `rb.online_state(cid)`, `rb.item_meta(id)` |
| `ctx.q(key)`, `ctx.qint(key, default, lo, hi)`, `ctx.body` | the request |
| `ctx.catalog` (`common.CATALOG`) | `index(type)` (every entry of an ID type: id, name, group, sprite, classes, level ...), `entry(type, id)` (the full entry), `name(type, id)`, `types()` |
| `ctx.glossary` (`common.GLOSSARY`) | `field(table, col)` -> label, description, type, min/max, values, editable, why, applies; `rules(table)` (write_rules.json); `table(t)` |
| `ctx.caps` | the round-2 capabilities (see 4) |

Never build SQL from user text by string concatenation: use `lit()` / `ident()` and the helpers
`update_sql`, `insert_sql`, `delete_sql`, `expected_sql`, `scope`.

### 2.2 Writes: use the write helpers, nothing else

Every write goes through one of these. They keep reborn's invariants (WRITE-RULES.md s.4/s.7): db guard,
`FOR UPDATE OF c, a` lock, online re-check inside the transaction, revision counters +1 in the same
statement, the economy-baseline guard, pet structure guard + the three start-up validators, a backup before,
an after-image, a change-log record and **Undo**.

| Helper | Use it for |
|---|---|
| `ctx.set_character_fields(cid, {col: value}, expected={col: shown}, label=)` | `character_base` columns. **Live or database, decided for you** (section 4). Ranges come from the glossary; read-only columns are refused with the reason. |
| `ctx.bag_item(cid, "add"\|"remove"\|"move", {...})` | kit-bag items (slots 0..95): add an item (first free slots, split by Overlap), remove a slot, move to an empty slot. Live or database. |
| `ctx.char_write(cid, action, build, fields=[...], expected=[...], applies=)` | any other edit of ONE character's rows (equipment, warehouse, attributes, pets, skills, quests ...). `build(d)` gets the character's current rows (`reborn.DUMP_TABLES`) and returns SQL statements. Offline only: an online character answers 423. `fields=["table.col"]` or `["table.*"]` is checked against write_rules (read-only -> 403); cross-table counters (`character_items` -> `inventory_revision`) are added once. |
| `ctx.rows_write(scope_list, action, build, fields=, lock_cids=, guard_sql=, applies=, target=)` | rows that are not one character's: guilds, accounts, start-up content, settings. `scope_list = [common.scope(table, where)]` is every row the write may touch (snapshot, lock, Undo); `lock_cids` = characters whose rows also change (must be offline); `guard_sql` = DO-blocks that RAISE when an invariant breaks (they run before COMMIT and again on Undo). |
| `reborn.OPS` (`op_item`, `op_move`, `op_pet_*`, `op_skill`, `op_quest`, `op_zodiac` ...) | the classic sheet's tested builders; call them inside a `char_write` build instead of rewriting them. |

`expected` makes the write fail with 409 when the row changed since the page read it (the game server or
another tab). Always send the values the page showed.

`applies` (shown as a chip): `next_login` (default for character tables), `now`, `after_restart`
(start-up content: also listed on Home as "pending restart"), `live` (set by the live path).

## 3. Front end: `static/modules/<name>.js`

```js
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  GW.module("inventory", {
    // el: the page body; ctx: {name, info, cid, character, route, params, api(sub, body), navigate(sub)}
    async mount(el, ctx) {
      el.append(GW.onlineBanner(ctx.cid));          // character pages: always show it
      const bag = await ctx.api("bag/" + ctx.cid);   // GET /api/m/inventory/bag/<cid>; ctx.api(sub, body) POSTs
      ...
    },
    unmount() { /* stop timers */ },
  });
})();
```

The shell loads the script and CSS on first visit, routes `#/<name>/<cid>/<sub...>` (`ctx.route` = the parts
after the character), keeps the selected character in the top bar, and gives the page a `.gw-mod-<name>` body.
Insert database and client text as text (`h()` does), never as HTML.

### 3.1 Shared components (`static/shared/`, all on `window.GW`)

| Component | What it does |
|---|---|
| `GW.h(tag, attrs, ...kids)`, `GW.clear(el)`, `GW.fmt(n)`, `GW.text("|cFFFF0000red")` | DOM, numbers, the game's colour codes |
| `GW.api(path, body?)` | GET / POST with the CSRF token; errors carry `.status` and `.data` |
| `GW.modal`, `GW.confirm({title, message, ok, danger})`, `GW.alert` | game-style windows |
| hover help | any element with `data-field="table.col"` shows the glossary text (what it is, range, where stored, when it applies, why read-only); `data-help="text"` shows that text; `data-tip-type="item" data-tip-id="1001"` (+ `data-tip-inst='{...row...}'`) shows the game tooltip. Nothing to call. |
| `GW.render.item(entry, inst)`, `.skill`, `.petskill`, `.pet`, `.status`, `.talent`, `.appendattr`, `.holystone`, `.quest`, `GW.render.tooltip(type, id, inst)` | the in-game tooltips as elements |
| `GW.grid({type, onSelect, onPick, filter, group})` | an embedded, virtualised grid of **every** entry of an ID type (icons, English names, kind / class / level filters, search). Thousands of rows stay fast. |
| `GW.pick(type, {title, value, filter, allowNone})` | the same grid in a big window -> `Promise<id | null>` |
| `GW.idField(type, value, {onChange, field})` | icon + name + "Pick" button for one ID |
| `GW.catalog.load(type)` / `row(type, id)` / `get(type, id)` / `name(type, id)` | the catalogue in the browser (cached) |
| `GW.glossary.field("table.col")` | the glossary entry |
| `GW.numberField({field: "table.col", value, onChange})` | number box + slider; min / max / help / read-only from the glossary; log slider for big ranges |
| `GW.appliesChip(applies)` | "applies at the next login" / "after gw251 restart" / "applied now (live)" |
| `GW.onlineBanner(cid)` | online / offline, why, what can be edited live, and a **Kick to edit everything** button when the server offers kick |
| `GW.saveFields(cid, {col: value}, {expected, label})` | the front-end half of `set_character_fields` (live or database) |
| `GW.bagItem(cid, op, p)` | the front-end half of `bag_item` |
| `GW.saveBar({onSave})` | collects pending edits -> review table -> Save; one transaction; Undo in the change log |
| `GW.writeDone(result, label)` / `GW.writeError(e)` | the standard reactions (status line; 423 / 409 / 403 dialogs) |
| `GW.changes.open()` / `GW.changes.undo(change)` | the Change log drawer (top bar) |
| `GW.on("change" | "online" | "character", fn)` | events |

Look: `static/ui/UI-STYLE.md` (window frames `.gw-frame-normalwindow`, cells `.gw-cell`, buttons `.gw-btnx`,
quality colours `.gw-q1..20`, grade colours `.gw-g1..25`, the reference pictures in `static/ui/windows/`).
Demo of every shared part: the sidebar's "Editor parts (demo)" (`#/kit`).

## 4. Live edits (the game running, the player online)

the admin: "I want to be able to make changes while the server is running and see it reflected."

* **Offline characters** (logged out, including not at the character screen): every edit writes the database
  while the server runs; the character sees it at the next login. This works today.
* **Online characters**: the database must not be written behind the session (WRITE-RULES.md s.1). The
  round-2 server adds loopback management endpoints that change an online character **through the server**,
  which then pushes the change to the client at once. The editor feature-detects them:
  `GET 127.0.0.1:<mgmt>/admin/capabilities` (mgmt = 9090 for the live database, `--rates-mgmt` for a copy),
  every 60 s, 2 s timeout; `GET /api/m/capabilities` shows the result; the top bar shows "Live edits: N".
* `ctx.set_character_fields` / `ctx.bag_item` choose the path:

  | Character | Server offers the edit | Path | Result `mode` |
  |---|---|---|---|
  | offline | - | guarded database write | `database` (applies next login) |
  | online | yes | `POST /admin/money`, `/admin/progression`, `/admin/teleport`, `/admin/item/add|remove|move` | `live` (the player sees it now) |
  | online | no (stock server, or a field without an endpoint) | refused, 423: "Live edits arrive with the round-2 server update; this character must be logged out." / "The game server cannot change X live" | - |
  | character select / entering the world | - | refused, 409 "try again in a few seconds" (the server answers `offline` or `busy`; nothing is written) | - |

* Live fields (`common.LIVE_FIELDS`, change names there only): Silver `Money`, Gold `Stone`, B-Gold
  `BindingGold`, level `fighter_job_lv` (EXP resets to 0 like `/level set` unless `fighter_job_exp` is sent),
  `fighter_job_exp`, talent points `SkillPoint`, `zodiac_energy`, position `Map` + `Pos_X` + `Pos_Z`.
  Bag: add / remove / move (kit bag only; no warehouse, no equipment, no swap).
* Every live change is recorded (`engine: reborn-live`) with before / after dumps of the character. **Undo**
  of a field change sends the old values back (live if still online, else the database); a bag change has no
  automatic Undo (do the opposite edit).
* `POST /api/m/kick/<cid>` (the banner's button) disconnects the player through the server and waits for the
  logout save, so any edit can then be made.
* `ctx.live_call(op, name, body)` is the low-level call for anything else the server advertises.

Proof on a round-2 test instance (2026-10-28): `tests/test_live_edit.py` - a headless client in the world
received the new Silver / Gold / B-Gold and level in its 10166 status frame, and the given / moved / removed
item in its 10033 bag pages, while the editor wrote nothing to the database itself.

## 5. Core API (`/api/m/...`, served by `modules/common.py`)

| Route | |
|---|---|
| `GET /api/m` | the modules (sidebar) + load errors |
| `GET /api/m/status` | database, writable / live, game server state, capabilities |
| `GET /api/m/capabilities[?refresh=1]` | round-2 live operations |
| `GET /api/m/catalog`, `/catalog/<type>`, `/catalog/<type>/<id>`, `/catalog/labels` | the ID libraries |
| `GET /api/m/glossary`, `/glossary/<table>`, `/glossary/<table>/<col>`, `?fields=t.c,...`, `/rules/<table>` | field help + write rules |
| `GET /api/m/characters?q=`, `/online/<cid>` | character list, online state + what works live |
| `POST /api/m/edit/<cid>/fields`, `/edit/<cid>/bag`, `/kick/<cid>` | the shared live-or-database edits |
| `GET /api/m/changes[?module=]`, `/pending` | change log, changes waiting for a restart |
| `POST /api/undo {change, full, overwrite}` | Undo (server.py; handles `reborn`, `rows`, `rates`, `reborn-live`) |

## 6. Accounts (the worked example: `modules/accounts.py`, `static/modules/accounts.js`)

* List every account with its characters; search by user name, e-mail or character.
* **New account**: user name (3-32 letters, digits, `_`; case-sensitive) + password (4-32 characters). The row
  is the server's own registration insert; the password is stored as the reborn verifier
  `gws$pbkdf2-sha256$v1$600000$<salt>$<hash>` of the MD5 hex the client sends. Proved: a headless client
  logged in with the new account and created a character; a wrong password was refused.
* New password, e-mail, donator tier + end date, ban / unban (`accounts.status = 1`; the round-2 server refuses
  the login and kicks online characters; the stock server ignores it, and the page says so).

## 7. Tests every module must pass

1. Your own `tests/test_<name>.py` (stdlib only) against **your own** database copy and instance
   (`tests/README.md`): every route, every write and its Undo, a refused read-only field (403), a stale
   `expected` (409), an online character (423 or live).
2. `python3 -I gwadmin-web/tests/test_framework.py --db <your copy>` still ends with `ALL OK`.
3. `/api/reborn/selftest?format=text` on your copy: 0 errors.
4. In the built-in browser (tunnelled editor): the page renders at 100% zoom, hover help on every field,
   every picker lists ALL entries, the online banner, a save and an Undo from the Change log.
5. Never the live database `godswar`, never port 8788, never `godswar251.service`.
