# Write rules for the reborn editor (live-edit safety and the write model)

Task "safety", 2026-10-28. This is a read-only analysis. Nothing was written to any database and no server
was started or stopped.

* **Machine-readable rules:** `write_rules.json` in this folder. It has one entry per table, keyed by table
  name, covering all 225 tables of the live database. `character_base` and `accounts` also have one rule per
  column.
* **Sources:**
  * the reborn source at commit `b85a1244`. `S/` means `GodsWar-2.51/emulators/src/godswar-server/src/Godswar.Server/`;
  * the live schema, read in `BEGIN READ ONLY` on 2026-10-28: 225 tables, 37 views, trigger bodies, CHECKs,
    foreign keys and row counts;
  * `reborn.py` and README sections 8 and 9.
* **Round2 tree:** `Polish/reborn-src-round2` was only read. Its `/admin/capabilities` endpoint does not exist
  yet. Section 7 says how the editor should use it once it does.

---

## 1. For the admin: what "editing while the game is live" can and cannot mean

**You can do this today.** The game server keeps running while you use the editor. Other players keep playing.
You can edit any character that is **logged out** (back at the login screen or with the game closed), and
that character sees the change the next time it enters the world.

**One rule still applies.** The character you are editing must not be logged in, and that includes sitting
at the character-select screen. The reason is not that the server would "save over" everything:

* Silver, Gold, items, level, talent points, pets and zodiac are re-read from the database by every action in
  the game. A database change to them is **not lost**.
* But the server only tells your game client about a change when the change came from its own code. If the
  editor changes your Silver behind its back, your client keeps showing the old amount, your stats keep
  using the old gear, and the next action can fail ("item not found") because the client and the database
  no longer agree.
* A few things the server *does* save over from its memory: **quests** (the whole quest list is rewritten
  when it saves), **position** and **HP/MP**. An online edit to those is lost or makes the server
  disconnect you.

**What becomes possible with round2.** The other session ("round2") is adding management endpoints, and the
editor will detect them automatically. With them, some edits apply **instantly to a logged-in character**.
The server applies the change itself and updates your client:

* give an item; change an item's quality, grade, stack or bound flag;
* set level, talent points or zodiac energy;
* move or teleport, mute, ban, kick.

Anything without an endpoint still says "log out first". The editor can also offer **Kick and edit**: it
disconnects the player, makes the change, and the player logs back in.

**What needs a restart, or is not editable at all.** These are separate from characters:

| What you change | When it takes effect |
|---|---|
| Server rates (EXP, drop and gold multipliers, daily caps) | **Instantly.** The server reloads them within 5 s. Values can go as high as the bounds stored in the table; round2 widens them, up to x1,000,000 where that is safe. Some results are still capped by the game itself: a kill gives at most 100,000,000 EXP, currencies stop at 2,147,483,647, and the level cap is 200. |
| Drop tables, quest-reward overrides, Medusa rules, GM monster/NPC placements, the global kill-EXP multiplier | **After `gw251 restart`** (about a minute of downtime). A bad row stops the server from starting, so the editor checks the rows first. |
| Item definitions, skills, maps, monster spawns, NPCs, pet rules, shop and exchange balances | **Not editable.** These are sealed, fingerprinted "content publications" that the server checks at start-up. Changing them means publishing a new revision, which this editor does not do. The editor still reads all of them to build the item / skill / pet libraries. |
| Ledgers, claims, audit logs, archives | **Never edited.** They are the server's record of what it paid out. |

**About "CdKey" and other unclear fields.** CdKey was a 1.0-only field, and the reborn editor does not
show it. Every field in `write_rules.json` has a plain-English `why` or `rule`, which the editor shows on
hover. Fields the server never reads (for example `store_num`, `bag_num`, `prestige`, `earl_rank` and `GM`)
are shown as "not used by this server" instead of being editable.

---

## 2. How the reborn server holds state (findings)

1. **Valuable state is database-authoritative, one locked transaction per action.**
   * Every shop purchase, sale, kill reward, talent upgrade, forge, bag move and pet command does the same
     thing. It reads the row with `SELECT ... FOR UPDATE`, computes the result from those values, writes it,
     and then reloads the session's copy.
   * Examples:
     * `S/Infrastructure/Inventory/PostgresCapitalShopPurchaseStore.CapitalShopPurchase.cs:56-200` reads
       Money / wallet_revision under the lock, then `GetCharacterByIdAsync`;
     * `S/State/PostgresGameStore.Progression.cs:61-143` (kill EXP and level);
     * `S/Infrastructure/Rewards/PostgresMonsterDeathRewardCommandExecutor.Database.cs:76-86, 337-345`
       (CAS on `progression_reward_revision`);
     * `S/State/PostgresGameStore.SkillsAndTalents.cs:60-128`;
     * `S/Infrastructure/Pets/PostgresPetDurableCommandExecutor.Level.cs:17-104` (pet `revision` CAS).
2. **The session keeps a copy for display and for stats.**
   * `GameClientHandler` holds a `GameCharacter`. It is refreshed after the server's own writes, and fully
     reloaded at login and after every durable pet command (`RefreshCharacterSnapshotAsync`, 13 callers).
   * On a reload, `InstallUpdatedCharacter` keeps the session's position, vitals, checkpoint owner and the
     whole quest state (`S/Game/GameClientHandler.CharacterRefresh.cs:17-48`).
   * Nothing pushes a database change made by someone else to the client.
3. **Three things are flushed from session memory:**
   * **Quests.** `SaveCharacterQuestStateAsync` deletes every `character_quests` row that is not in the
     session's list and upserts the rest (`S/State/PostgresGameStore.QuestState.cs:15-82`).
     `character_quest_daily` is upserted the same way.
   * **Position and HP/MP.** These are checkpoints fenced by `position_revision` / `vitals_revision` and the
     owner generation (`S/Infrastructure/Characters/PostgresCharacterCheckpointStore.Writes.cs`). The legacy
     path `SaveCharacterPositionAsync` is a blind overwrite (`PostgresGameStore.Progression.cs:12-34`).
   * If an admin bumps the revision, the session's next checkpoint is rejected as stale or conflicting, which
     can disconnect the player (`GameClientHandler.CharacterCheckpoints.cs:488-513`). If the admin does not
     bump it, the session's next flush overwrites the edit.
4. **Ownership while online.**
   * World entry runs `AcquireAsync`: it sets `checkpoint_owner_id` and increments
     `checkpoint_owner_generation` (`PostgresCharacterCheckpointStore.Ownership.cs:8-58`).
   * Logout runs `ReleaseAsync` (`:62-150`).
   * At entry, the snapshot's position/vitals revisions must equal what `AcquireAsync` returned, or the login
     is rejected with `checkpoint_revision_mismatch` (`GameClientHandler.CharacterCheckpoints.cs:101-117`).
     An editor write that commits *between* those two reads makes that one login fail; the player simply
     logs in again. The editor's row lock (section 6) makes such a write wait or fail instead.
5. **The economy baseline.**
   * Every economy command first calls `PostgresCharacterEconomyBaseline.EnsureAsync`.
   * If the character has no `character_economy_baseline` row, EnsureAsync can only create one while
     `wallet_revision` and `inventory_revision` are both 0. Otherwise it returns false, and the command
     fails (shop, forge, bag move, warehouse, zodiac grid, pet commands...).
   * **A revision bump on a character with no baseline therefore breaks its economy permanently.**
   * Live today: all 5 characters have a baseline (`character_creation`) [measured]. The rule still has to be
     enforced.
6. **Content is pinned at start-up.**
   * Item, gameplay, monster, NPC, dialogue, enter-bootstrap and pet content are immutable revisions behind a
     publication pointer. They are hash-checked and pinned for the life of the process.
   * `docs/data-architecture-b20g-content-publication-capture-separation-20260801.md` says: "A process keeps
     its pinned revision until restart".
   * The authoring tables (`item_templates`, `skill_templates`, `map_templates`...) are read only when the
     first publication is created. The item publisher also upserts reviewed values over `item_templates`
     (`PostgresItemTemplateBaselinePublisher.MutableAuthority.cs:10-60`).
   * Other content is mutable but read once at start-up: loot, quest-reward overrides, Medusa rules, the
     monster reward policy, GM monster/NPC overrides, monster combat balance and holy-spirit balance
     (`S/ServerRuntimeBootstrapContext.cs:65-107`, `ServerRuntimeContentComposition.Startup.cs:44-56`).
   * There is no try/catch around these loads. The snapshot constructors validate rows and throw
     (`Application/World/MonsterOverrideSnapshot.cs:78-90`, `GmNpcOverrideSnapshot.cs:40-60`), so **a bad
     row means the next start-up fails**.
7. **The management port today** (stock + RATES) serves `GET /live`, `/ready`, `/metrics` and `/traces`,
   `POST /drain` (with a bearer token from a file), and `GET /rates` / `POST /rates/reload`. It has no
   session list and no character endpoints (`S/Operations/ManagementHttpServer.cs:240-255, 407-411`).

---

## 3. Table groups: when the server reads them, and what happens on an edit

"Applies" is the label the editor shows next to each field.

| Group (tables) | Server reads it | Edit while the character is **online** | Edit while **offline**, server running | Applies |
|---|---|---|---|---|
| **Character base** (`character_base`) | At world entry, plus a locked read per action | Refused. Wallet, level and talent values are not lost, but the client and stats go stale. Position and HP/MP are overwritten or rejected. Name and appearance are already broadcast | Safe | next login (live endpoint: now) |
| **Items** (`character_items`, `character_item_audit`) | World entry + locked rows per bag/forge/shop command | Refused: the client's bag no longer matches, and commands that compare stack/prop fail (`PostgresGameStore.QuestItems.cs:27`) | Safe | next login (live: `give_item` / `item_set`) |
| **Pets** (`character_pets`, `_stat_values`, `_skills`, `_character_bonuses`, previews, `sealed_pet_items`) | World entry + `LockPetAsync` with revision CAS | Refused: the summoned pet is a world entity and the session keeps the pet projection | Safe if the 3 start-up validators pass | next login |
| **Skills, talents, zodiac grids** | World entry + locked upgrades | Refused: the skill bar is sent at entry | Safe | next login |
| **Quests** (`character_quests`, `character_quest_daily`) | World entry | **Lost**: the session rewrites the table | Safe | next login |
| **Titles owned** (3 tables) | World entry | n/a | Read-only (earned evidence, FK to reward rows) | — |
| **Daily / weekly counters** (Lucky Gods, Wishing Pool, Zeus gifts, Medusa and dungeon entries, Lelantine farm, Holy Box daily) | Locked read at each use | Allowed with a warning: the next use reads the new value, but an open NPC window shows the old one | Safe | next use |
| **Boosts and cooldowns** (`character_experience_modifiers`, `character_bag_consumable_cooldowns`) | At each use | Refused: icons and timers are in the client | Safe | next login |
| **Accounts** (`accounts`, `account_entitlements`) | At each login; donator tier in the stat projection | Ban, mute and kick need the live endpoint | Safe | next login / now |
| **Guilds** (`guilds`, `guild_members`, `guild_buildings`, `guild_altar_worship`) | Locked reads + a per-character panel cache | Refused while any member is online | Safe; must stay consistent with `character_base.consortia*` | next login |
| **Rates** (`server_rates`) | Polled every 5 s / `POST /rates/reload` | Allowed | Allowed | now |
| **Start-up content** (loot, quest-reward overrides, Medusa rules, `monster_reward_settings`, `monster_combat_balance`, GM monster/NPC tables, holy-spirit balance) | Once at start-up | Allowed: nothing changes until restart | Allowed | after restart |
| **Per-use settings** (`legacy_instance_settings`, `medusa_instance_settings`, Medusa capture weights, faction-area EXP control, Zeus realm state) | At each use | Allowed | Allowed | now |
| **Published content** (74 tables: `*_content_*`, `gameplay_*`, `*_publication`, `*_revisions`, `monster_spawn_definitions`, `npc_spawn_definitions`, `npc_dialogue_*`, `pet_content_*`, `pet_owner_merge_*`, `pet_skill_*`, Faction Crier / online-award / warehouse policy) | Pinned at start-up, hash-checked | — | Read-only | new revision + restart (not offered) |
| **Authoring tables** (36: `item_templates`, `skill_templates`, `map_*`, `monster_templates`, `npc_*_templates`...) | First publication only | — | Read-only (the library source) | never |
| **Evidence** (46: ledgers, baselines, inbox/outbox, settlements, claims, audits, archives, migrations) | Reconciliation, idempotency | — | Read-only | — |
| **Bookkeeping** (`character_progression_interval_authority`, `pet_durable_stream_versions`, `account_realm`, `server_rates_state`) | Server CAS | — | Read-only | — |
| **Captures** (`*_packets`, `packet_*`, `server_packet_templates`) | Never | — | Hidden | never |

**The realm row** (`server`) is read-only in the editor. Its address and ports are what every client
connects to, and its time-zone columns are guarded and audited by triggers.

---

## 4. Exact invariants every write must keep

These apply to every write, whatever the table:

* **One transaction.** It locks `character_base` and `accounts` (`FOR UPDATE OF c, a`) and re-checks the
  three online signals inside the transaction.
* **Never written by the editor:** `checkpoint_owner_id`, `checkpoint_owner_generation`,
  `lifecycle_version`, the zodiac online counters, `login_status` and `login_presence_token`.
* **Revision counters only move forward, by exactly +1, in the same statement as the data:**

  | Counter | Moves with |
  |---|---|
  | `wallet_revision` | Money, Stone, BindingGold, honor, exchange point, exchange medal |
  | `inventory_revision` | any item row, once per edit |
  | `position_revision` | Map, Pos_X, Pos_Z |
  | `vitals_revision` | curHP, curMP |
  | `pet_shed_revision` | pet_shed_capacity |
  | `medusa_reward_revision` | selected_title_id |
  | `fighter_level_seal_revision` | fighter_level_sealed |
  | `character_pets.revision`, stat / skill `revision` | every changed pet row |
  | `character_talents.outbox_revision` | a changed talent row (the outbox stream is `ordered_sparse`, so gaps are allowed) |

  Undo / Restore also move counters forward and never back. That avoids `UNIQUE (character_id,
  wallet_revision, currency_code)` collisions in the ledgers.
* **`warehouse_revision` is special.** It may only advance together with a capacity increase, and that
  increase needs a `warehouse_expansion_settlements` row (triggers `guard_character_warehouse_capacity` and
  `require_warehouse_capacity_evidence`). The editor never writes either column.
* **Economy baseline first.** Before moving `wallet_revision` or `inventory_revision` off 0, a
  `character_economy_baseline` row must exist. If it is missing:
  * and both revisions are 0, insert it exactly as `EnsureAsync` does (`baseline_source 'runtime_cutover'`,
    one `character_inventory_baseline_items` row per item, `to_jsonb(item)`);
  * otherwise refuse.

Item rules:

* Keep the instance `id` on move and swap.
* One row per (user, location, slot).
* Respect the location/slot domains, `prop_id` in the pinned catalogue (FK `item_templates`), stack ≤
  Overlap, quality 0..20 and grade 0..25. Warn above Q10 / G12, which is the client limit.
* Respect the kind/slot rules, the mount rules, and the class/elemental/holy-socket CHECKs.
* Call `recompute_character_holy_suit_points` after changing equipped slots 0..11.
* Write one `character_item_audit` row per changed item.
* Never touch temp slots (location 2) or packed-pet items.

Pet rules:

* Talent mask = f(aptitude).
* At most one carried, one summoned and one contributing pet; opened ≤ available ≤ 12 skill cells; active
  skills only in opened cells.
* Hatch evidence must be valid on insert and is immutable afterwards.
* `initial_savvy_source_version = basic-plus-scaled-growth-v3` and exactly 6 stat rows (otherwise the next
  level-up throws: `PostgresPetDurableCommandExecutor.Level.cs:52-64`).
* `added_savvy = (growth + acceleration) × level`.
* Owned pets ≤ shed capacity.
* **The three start-up validators must pass before COMMIT**, or the next restart fails.

Other ranges the server enforces in code but the database does not:

| Field | Range |
|---|---|
| Level | 1..200 |
| Zodiac level | 1..30 |
| Zodiac energy | ≤ the storage cap of its level (`ZodiacEnergyCatalog`) |
| Talent rank | 0..100 (the client shows up to 60) |
| Zodiac grid level | 0..50 |
| Talent EXP | < 100 |
| In-progress quests | at most 20 |
| Quest state | 0 or 1 |

Guild rules: `guild_members` and `character_base.consortia / consortia_job / guild_duty` change together;
`guilds.lord_character_id` is FK RESTRICT.

Start-up content: validate exactly like the snapshot constructor before COMMIT.

* GM monster spawns ≤ 20,000; GM NPC spawns ≤ 2,000 and ≤ 12 slots; button results unique within a
  dialogue tree.
* Loot: maximum drops 1..32, chance in basis points, quantity 1..255, items in the catalogue.
* Holy-spirit balance must be written with the store's revision CAS **and** its socket-clamp statement, which
  rewrites `character_items`, so every affected owner must be offline.

Rates: `value` stays within that row's own `min_value..max_value`. The editor never carries its own bounds.

---

## 5. Restart / republish matrix (loot, spawns, shops, quest rewards, world bosses)

| Content | Tables | Takes effect |
|---|---|---|
| Field/dungeon loot | `monster_loot_tables`, `monster_loot_rules` | restart |
| Loot amounts / chances globally | `server_rates` `drop.*` | now (hot) |
| Medusa loot, monsters, rewards, titles | `medusa_monster_rules`, `medusa_monster_loot_rules`, `medusa_completion_reward_rules`, `medusa_reward_title_definitions` | restart |
| Quest rewards per quest | `quest_reward_slots`, `quest_reward_values` (overrides of compiled quest content) | restart |
| Quest rewards globally | `server_rates` `exp.quest`, `money.quest_*`... | now |
| Monster spawns | `monster_spawn_definitions` (published) | not editable |
| Added / moved / changed monsters | `gm_monster_spawns`, `gm_monster_spawn_edits`, `gm_monster_attributes` | restart |
| NPCs | `npc_spawn_definitions` and `npc_dialogue_*` (published): not editable. `gm_npc_*`: restart | — |
| NPC shops / Mall | compiled into the server (`PacketBuilder.Captured*Shops.cs`) | code change |
| Point Exchanger, Faction Crier, online awards, warehouse expansion | balance publications | new revision + restart (not offered) |
| World bosses | `gameplay_world_boss_definitions`, `gameplay_pending_world_boss_areas` (published); `world_boss_areas` / `pending_world_boss_areas` are authoring only | not editable |
| Boss area EXP control | `faction_area_experience_control` | now (per use) |
| Item definitions (stats, stack, kind, level) | item publication | not editable |
| Item sell price | `item_templates.stats.Money` (read per sale) | now, but a publisher run overwrites it: treat as read-only |
| Global kill EXP / level gap | `monster_reward_settings` | restart |
| Kill EXP via rates | `server_rates` `exp.monster` | now |

---

## 6. What `reborn.py` does right and what it gets wrong

**Right (keep):**

* One `psql` run per edit, with `ON_ERROR_STOP`, `lock_timeout 5s` and `statement_timeout 60s`.
* `db_guard_sql`: the `current_database()` check and the live gate.
* `FOR UPDATE OF c, a`, with the three online signals re-checked inside the transaction.
* The revision bumps for wallet, position, vitals and inventory.
* Row ids kept on move; `character_item_audit` rows written; holy-suit points recomputed.
* Slot / kind / mount checks.
* The unique name and slot indexes used as the final authority.
* The pet structure guard plus the three start-up validators before COMMIT; hatch inserts that mirror
  `PetEggs.cs`.
* Backups before writing; after-images; newest-first Undo; HTTP 409 on server changes since the write.
* Bookkeeping columns frozen; counters moved forward.
* A dry run with `check_character` before Undo.

**Wrong or missing (fix in the new pipeline):**

1. **Quests do nothing in game.** "Mark done / not done" writes `character_base.quest_completed_ids`, but the
   server reads completed quests from `character_quests.state = 1`
   (`PostgresCharacterSnapshotReader.Core.cs:302-319`). The column was copied into `character_quests` once,
   by migration `PostgresSchemaMigrationCatalog.CharacterQuests.cs:52`. The completed list in the UI is also
   read from that dead column.
   * **Fix:** done = upsert `(cid, qid, 1, 0)`; not done = delete the row; read the list from
     `character_quests`.
   * `quest_current_id` is also unused.
2. **No economy-baseline guard** before `wallet_revision` / `inventory_revision` move off 0 (section 4).
3. **Dead fields are editable:** `store_num`, `bag_num`, `prestige`, `earl_rank`, `scholar_job_*`, `GM`,
   `status`, `quest_current_id` and `mutetime` (until round2). The server never reads them, so the edits
   "save" but change nothing.
4. **Guild columns are edited alone.** `consortia`, `consortia_job` and `guild_duty` change without
   `guild_members`, so the two desync.
5. **Missing counters:**
   * `fighter_level_sealed` without `fighter_level_seal_revision`;
   * `pet_shed_capacity` without `pet_shed_revision`;
   * `selected_title_id` without `medusa_reward_revision` and without checking the title is owned;
   * talents without `outbox_revision`.
6. **Ranges looser than the server:**
   * `zodiac_level` accepted up to 32767 (server 1..30);
   * `zodiac_energy` has no storage-cap check;
   * talent rank accepted up to 32767 (server 100);
   * skill level and class are not checked;
   * `SkillExp` accepted above 99.
7. **Restore dump can bump `warehouse_revision` alone.** `SERVER_COUNTERS` includes it. If the server
   expanded the warehouse after an admin write, Restore emits `warehouse_revision = tt.warehouse_revision + 1`
   and the trigger refuses ("Warehouse revisions may advance only with capacity"). If capacity also differs,
   the change runs in the replica window and skips the evidence trigger.
   * **Fix:** drop `warehouse_revision` from the bump set, and refuse to restore a capacity the server
     changed.
8. **The refusal message is inaccurate.** "The server keeps the character in memory and would overwrite or
   ignore the edit" is only true for quests, position and HP/MP. Use the per-table `online.why` from
   `write_rules.json`.
9. **The online check scrapes the server log.** That signal should be replaced by `GET /admin/online` when
   it is advertised (section 7). `login_status` stuck after a crash makes the editor refuse, which is safe
   but needs a "server crashed?" hint.

---

## 7. The editor's write pipeline

Every edit is a **request**: a table, a key, field values, and the values the page showed (`expected`). The
pipeline looks up `write_rules.json` and runs the following steps.

**Step 1: classify.**

* `editor` = `read_only`, `hidden` or `not_editable` → refuse with the rule's `why`.
* `applies` = `after_restart` or `now` (settings and content) → "content path" (step 6).
* Character / account tables → "character path".

**Step 2: decide the mode for a character write.**

1. **Read online state:** the three DB signals, plus `GET /admin/online` if advertised.
2. **Offline:** the DB path (steps 3-5).
3. **Online, and every changed field has a `live_capability` advertised with `online: true`:** the live
   path (step 7).
4. **Online otherwise:** refuse with the table's `online.why`. If `character.kick` is advertised, offer
   **Kick and edit**:
   1. `POST` kick;
   2. poll up to 15 s until `checkpoint_owner_id IS NULL AND login_presence_token IS NULL AND
      login_status = 0`;
   3. take the DB path.

**Step 3: preflight, outside the transaction.**

* The rule ranges and enums from `fields` and `invariants`.
* Catalogue checks: item in the pinned `official_item_template_content`, and the slot/kind rules; skill,
  talent and map exist.
* Cross-row checks: slot free, stack ≤ Overlap, pets ≤ shed capacity, title owned.
* The economy baseline.

**Step 4: backup.** Dump every character table (the 14 in `DUMP_TABLES`, plus `character_quest_daily`,
`character_experience_modifiers`, `character_bag_consumable_cooldowns`, the daily-counter tables and
`guild_members`) to the backup directory.

**Step 5: the guarded transaction.** It is one psql script, and any error rolls everything back:

```sql
BEGIN;
SET LOCAL lock_timeout = '5s';  SET LOCAL statement_timeout = '60s';
-- db guard: current_database() = the verified name; 'godswar' only with --live --reborn-db godswar
SELECT 1 FROM character_base c JOIN accounts a ON a.id = c.account_id WHERE c.id = :cid FOR UPDATE OF c, a;
-- online re-check: RAISE if c.checkpoint_owner_id IS NOT NULL OR a.login_presence_token IS NOT NULL
--                  OR a.login_status <> 0
-- optimistic check: RAISE 'changed since you opened it' if any edited column <> :expected (HTTP 409)
-- economy baseline: INSERT ... (EnsureAsync SQL) when missing and both revisions are 0; RAISE when missing
--                   and a revision is > 0
-- the statements, each with its revision bump (+1) in the same UPDATE
-- item edits: INSERT INTO character_item_audit ...; SELECT recompute_character_holy_suit_points(:cid)
--             if slots 0..11 changed
-- structure guard (active pet skill outside opened cells; owned pets > shed capacity)
-- the three pet start-up validators for this character
COMMIT;
```

Then:

* re-dump the character;
* run `check_character` on the new rows;
* write the change record: the SQL, the before and after images, the plain-words label, and the
  **applies** label taken from the rule. It says "next login" for an offline character, and "now (live)"
  for step 7.

**Step 6: the content path.**

1. Same backup, but of the changed rows of that table.
2. Transaction: `db_guard`; a `FOR UPDATE` on the rows; the expected-values check (409); the statements;
   then the **snapshot validation** for that family. That means the exact constraints the start-up reader
   enforces (section 4), written as SQL `RAISE`s.
3. After COMMIT:
   * `applies = now` (rates) → call `POST /rates/reload` and show the reloaded revision;
   * `applies = after_restart` → add the change to a persistent **"Pending restart"** list on every page.
4. Optional **restart rehearsal** before the admin restarts the live server:
   1. `pg_dump` the live DB into a scratch copy;
   2. start the test instance against it on spare ports;
   3. wait for `readyz`, then stop it.

   A failure means the live restart would fail too.

**Step 7: the live path (round2 endpoints).**

1. **Probe** `GET http://127.0.0.1:<mgmt>/admin/capabilities` on page load and every 60 s:
   * 2-second timeout;
   * 404, refused or malformed = no capabilities;
   * mgmt is 9090 for the live `godswar`, and comes from `--rates-mgmt` for copies.
2. **Expected shape.** Feature-detect by operation name and never assume an operation exists. If round2
   ships different names, map them in one table in the editor.

   ```json
   {"version": 1, "server": "<build id>",
    "auth": {"scheme": "bearer", "token_file": "/opt/godswar251/..."},
    "online": "/admin/online",
    "operations": {
      "character.give_item": {"method": "POST", "path": "/admin/character/give", "online": true, "offline": true,
                              "fields": ["character", "item", "qty", "bound"], "cas": false},
      "character.item_set":  {"method": "POST", "path": "/admin/character/item", "online": true, "cas": true},
      "character.level":     {"...": "..."}
    }}
   ```

3. **Auth.** Read the token with `sudo -n cat <token_file>`, the same model as the stock drain token. Never
   send it anywhere but 127.0.0.1.
4. **Request.** Send the request with a request id (idempotency) and, where `cas` is true, the expected old
   value. The server applies it through its own command path, so it writes its ledgers, pushes the change to
   the client and answers with the new state.
5. **Record.** Dump the character's rows before and after (read-only) and keep both in the change record
   (`engine: reborn-live`).
6. **Undo.** Undo of a live change is another live call with the inverse value. When no inverse exists
   (`give_item`), it is an offline Undo of the rows, after the player logs out.
7. **Allowed online.** Only fields whose `live_capability` is advertised are editable for an online
   character. Everything else keeps the "log out first" rule and its reason.
8. **Capability names used in the rules:** `account.ban`, `character.give_item`, `character.item_delete`,
   `character.item_set`, `character.kick`, `character.level`, `character.move`, `character.mute`,
   `character.rename`, `character.talent_points`, `character.wallet`, `character.zodiac_energy` and
   `rates.reload`.

**Undo rules** (unchanged from README 8.4, plus the fixes in section 6):

* Newest first.
* Refuse on server changes since the write unless overwrite is chosen.
* Bookkeeping is frozen; counters move forward, except `warehouse_revision`, which never moves alone.
* Dry run, then the same guards.
* Undo never deletes evidence or ledger rows. The reconciliation report will show admin edits as drift. That
  is expected and harmless: the worker only reports (`S/Infrastructure/Reconciliation/`).

---

## 8. `write_rules.json` format

```text
schema_version, written, source, legend{class, editor, applies}, online_check, capabilities{probe,
expected_shape, names_used, rule}, tables{<table>: {
  group, label, class, editor, key?, rows_live,
  server_reads, server_writes?, applies, online{edit, why, live?}, offline?, while_running?,
  revision{rule, counters}?, invariants[]?, why?, fields{<column>: {applies, editor, min?, max?, enum?,
  revision?, rule?, why?, label?, live_capability?}}?, reborn_py{status, notes}?, evidence[]? }}
```

* `editor`: `edit`, `edit_advanced` (behind a switch, with a warning), `insert_only`, `read_only` (show the
  `why` on hover) or `hidden`.
* `applies`: `now`, `next_login`, `after_restart`, `new_revision_and_restart`, `never` or `not_editable`.
  The UI adds `live` when step 7 applied the change.
* `online.edit`: `refuse`, `allowed`, `allowed_with_warning` or `n/a`.

Counts:

| What | Count |
|---|---|
| Tables by class | 74 published content, 46 evidence, 36 authoring, 26 character state, 11 start-up content, 7 GM overrides, 7 per-use settings, 6 captures, 4 bookkeeping, 4 guild, 2 session-owned, 1 account, 1 hot setting |
| Editor values | 28 `edit`, 26 `edit_advanced`, 1 `insert_only`, 164 `read_only`, 6 `hidden` |
| Columns with a rule | all 80 `character_base` and all 16 `accounts` columns |
