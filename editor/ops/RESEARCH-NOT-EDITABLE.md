# What the editor cannot change today (wave 4, package C item 7)

Written 2026-11-12 from `schema/write_rules.json` (237 tables), `server/appsettings.json`, the server's `GODSWAR_*`
environment switches, the live unit's drop-ins (read with `systemctl show`, no root) and a grep of `const` values in
`server/src/Godswar.Server`. Nothing on the VPS was changed.

## 1. The picture

| Kind | Count | Editable now? | Why not |
|---|---|---|---|
| Character, account, guild, loot, quest-reward, spawn, NPC, shop, event, rate tables | 68 tables | yes (`edit` / `edit_advanced`) | - |
| Published content (items, monster spawns, skills, maps, pets, holy suits, NPC texts ...) | 74 tables, ~14,200 rows | no (NPC greetings only, by successor release) | immutable revision rows; the server pins one publication and checks it at start |
| Authoring templates (`item_templates`, `monster_templates`, `map_templates` ...) | 36 tables, ~11,000 rows | no | the server reads them only when it builds the FIRST publication; editing them changes nothing in game |
| Evidence, bookkeeping, capture (`schema_migrations`, outbox, audits, `account_realm`, packet captures) | 55 tables | no, by design | history and invariants the server writes itself |
| Realm list (`server`) | 1 table | name only before this branch | addresses, ports, enable, order: read-only in write_rules (this branch adds them; see the lead's lines) |
| Gameplay feature switches (`GODSWAR_*` in 8 systemd drop-ins: Parnassus, Pan's Labyrinth, Hera, professions, north quests, box dial, mentor, duel PvP, AFK revival ...) | 29 set on live, ~200 known names | no (now visible read-only on Operations) | root-owned drop-ins; a change needs a restart |
| `appsettings.json` (ports, network limits, zodiac energy ticks, world-instance capacity, `allowRegistration`, auth KDF) | ~150 keys | no | root/godswar251-owned file holding the DB password; read at start |
| Hard-coded constants in C# | hundreds | no | compiled in; needs a server build |

Hard-coded values that players notice (file, value):
- `State/PlayerExperienceCatalog.cs` and `State/PlayerRecoveryCatalog.cs`: `MaximumLevel = 200`
- `Game/GameClientHandler.Quests.cs`: `LegacyLevelCap = 120`, `QuestExperienceCap = 500,000,000`
- `Domain/World/Content/InstanceCallerProtocol.cs`: `HarborAttackMaximumLevel = 90`
- `Infrastructure/Operators/PostgresOperatorStore.cs`, `Game/GameClientHandler.RookieGrowth.cs`: kit bag `96` slots (4 pages of 24)
- `Game/SummonItemPolicy.cs`, `Application/FactionCrier/FactionCrierRewardPolicy.cs`, `Infrastructure/Items/LegacyInstanceOpalItemContentBaseline.cs`: stacks `99`. These stay at 99 (the admin's rule).

## 2. Ranked: what could be made editable

Value is for the admin running the game. Cost is the work, and whether the server's C# has to change.

| # | What | Value | Cost | Status |
|---|---|---|---|---|
| 1 | Realm list: add a realm (starts disabled), edit address, port, limit, order, enable, recommended; remove an unreferenced realm | high (the admin asked) | cheap, editor only | **built** (Operations -> Servers). Needs the write_rules lines below and `GRANT INSERT, DELETE ON public.server TO gweditor` |
| 2 | Feature switches: view | high | cheap | **built** (Operations -> Game server -> Feature switches, values of secret-named keys hidden) |
| 3 | Feature switches: edit an allowlist (event toggles such as `GODSWAR_PARNASSUS`, `GODSWAR_PAN_LABYRINTH`, `GODSWAR_GOLD_BOX_DIAL`), written to one editor-owned drop-in `90-editor.conf`, applied by a restart at zero players | high | medium: a helper v3 action that writes only allowlisted `KEY=value` pairs matching a per-key regex, then `daemon-reload` | proposed |
| 4 | Gameplay constants moved into `server_rates` (hot table, polled): max level, quest EXP cap, harbour attack level, legacy level cap | medium-high | medium: server C# per constant. The Rates page shows them automatically | proposed (server package) |
| 5 | Published content by successor release (the NPC-greetings pattern: copy the pinned revision, change it, register the successor, flip the pointer, restart): monster spawns, item templates, skill numbers | high | high per family: each needs a server-side successor registration table and validator | proposed, one family at a time (spawns first; `gm_monster_spawns` already covers most needs) |
| 6 | Selected `appsettings` keys (zodiac energy per tick, `allowRegistration`, open-world player capacity), patched by the helper with an allowlist, then a restart | medium | medium | proposed |
| 7 | Balance-revision tables (`warehouse_expansion_policy_levels`, `faction_crier_balance_*`): publish a new revision the way the server's settings store does (CAS on the publication pointer) | medium | medium | proposed |
| 8 | `account_realm.character_slot_limit` (1..16, now 1): more characters per account | medium (the admin asked; package A) | cheap in the DB; whether the 2.51 client shows more than its slots is package F's question | left to A/F |
| 9 | Authoring templates | low | - | not worth it: the server never reads them again |
| 10 | Evidence, bookkeeping, capture | none | - | never |

## 3. Built in this branch
- Rows 1 and 2 above.
- `status` and `check` (gw251) without root, and the resource monitor (cgroup v2 + /proc).
- Helper v2: `backups` and `restore` (with its own zero-player check and automatic roll-back).
