# GM powers: inventory and switches (wave 5 B)

Written 2026-11-12 from the server source (`server/src/Godswar.Server`), the live unit's drop-ins (read with
`systemctl cat`, no root) and `GodsWar-2.51/Polish/GUIDE-GM-AND-EVENTS.md`. The machine-readable list is
`Application/Operators/GmPowerPolicy.cs` (the editor's copy: `editor/data/gm_powers_inventory.json`, kept equal by a test).

## 1. Who is a GM

| Setting (live value) | Where | Meaning |
|---|---|---|
| `GODSWAR_DEVELOPER_COMMANDS_ENABLED=true` | drop-ins `character-gm.conf`, `gm.conf` | Off = no GM powers exist at all. Start-up only (restart). |
| `GODSWAR_CHARACTER_GM_FLAGS=true` | drop-in `character-gm.conf` | On = a GM is a CHARACTER with `character_base."GM" = 1`, not deleted, on an unblocked account. Read from the database for every command, so a grant or revoke applies at the next command. |
| `GODSWAR_DEVELOPER_ACCOUNT_IDS=3` | drop-in `gm.conf` | The account allow list. Used ONLY when character flags are off; on live it is ignored. |

Code: `Application/Operators/OperatorAuthority.cs`, `Infrastructure/Operators/PostgresOperatorStore.ReadCharacterGmAsync`.
The editor's GM powers page grants and revokes the flag (one column, audited, Undo restores only that column, the
player may stay online). The three settings above stay root-owned drop-ins; the page shows them.

## 2. What a GM can do, and the switch for each

Every row is one key in `public.gm_power_policy` (no row = allowed). `master` off refuses all of them.
A refused command answers "[word] switched off on this server" to the GM, never reaches chat, and is written to
`operator_log` (`detail` = `refused: <key> is switched off`). A non-GM typing a GM command still gets no answer.

| Group | Commands (key = `cmd.<word>`) | Note |
|---|---|---|
| Moderation | `/announce`, `/kick`, `/mute`, `/unmute`, `/ban`, `/unban` | A GM account cannot be banned with `/ban`. |
| Teleport and instances | `/goto`, `/summon`, `/move`, `/leave`, `/testrun`, `/where`, `/instances` | `/move` also moves a logged-out player (database). |
| The GM's own powers | `/god`, `/walls`, `/speed`, `/power`, `/armor`, `/bufflock`, `/petlock`, `/powers`, `/buff` | Switching one off also ends a power that is already on (`/walls` excepted: it is a client state, ends at relog). |
| Items | `/give`, `/item` (`/gmitem`), `/ruby`, `/mall` (window and purchases), `/craft`, `/bag`, `/bind`, `/itemset`, `/dupe` | `/give` = any item id; `/item` = the GM list, mounts, `clearbag`. |
| Levels and progress | `/level`, `/talentpoints` (`/talent`), `/zodiacenergy` (`/zenergy`), `/medusatitle` | Also on another player with `@Name`. |
| Tools | `/gm` (`/panel`, the GM window), `/commands` | Commands sent from the GM window are checked one by one like typed ones. |
| Rules a GM skips | `power.gate_bypass`: level ranges and opening times of transporters, battlefields, instances, events (Parnassus, battlefield transporter, Cursed Zone, Pan's Labyrinth, Troy and its beach bosses, Hera, Lelantine Farm, Treasure Land, Sicily, city transporters) and the Romance Island level. `power.limit_lift`: no upper limits on `/speed`, `/power`, `/armor` | The gate skip is also subject to each gate's own GM rule (Gates page) and the rate `gm.obey_gates`. |

## 3. GM-related things with no switch here

| Thing | Gate | Where to change |
|---|---|---|
| Per-gate "may a GM skip this" | `level_band_overrides.gm_bypass` (hot) | Gates page |
| All GMs obey gates | rate `gm.obey_gates` (hot) | Server rates page |
| GM Mall price | `GODSWAR_GM_MALL_FREE` (start-up) | drop-in, restart |
| `/admin/*` endpoints on 127.0.0.1:9090 (item add/remove/move, progression, money, teleport, kick, quest chain, buffs, board post, buff broadcast) | loopback only; called by the editor for a signed-in editor admin; every call logged with its actor | not a GM-character power; unaffected by the switches |
| `/sort`, `/sortwh` | none (player commands) | - |
| `/craft` exists only with `GODSWAR_PROFESSIONS=1` | start-up | drop-in |

## 4. How the switches reach the game

`Infrastructure/Operators/GmPowerPolicyRuntime.cs` creates `gm_power_policy` and `gm_power_policy_log` at start-up
(idempotent DDL like `server_rates` and `event_board_entries`: NOT a registered schema migration, so the previous dll
still starts on the same database), grants `gweditor`, and polls the table every 5 seconds
(`GODSWAR_GM_POLICY_POLL_SECONDS`). A read failure keeps the last good policy; before the first good read everything
is allowed. Each extra realm process polls the same table. A trigger stamps `updated_by` and writes the log table.

Rollback: swap the dll back (the tables are ignored by older builds). To clear every switch:
`DELETE FROM public.gm_power_policy;`
