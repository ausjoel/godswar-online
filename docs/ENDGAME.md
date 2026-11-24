# The end-game master switch

One server setting, `endgame.enabled`, opens or closes the whole end-game path. Change it live from the editor's
"Server rates" tab (Game version limits, then "End-game master switch"). It applies within seconds; no restart is
needed and nobody has to log out.

## What the end-game path is
- **Class Suit IV to VII** at the Class Suit NPC: IV, then Titan (V, level 170), Primordial (VI, level 185) and
  Aether (VII, level 200).
- **Gear past the original limits**: quality up to 20, grade up to +25, 4 holy-stone sockets, Holy Suit up to
  Adamantium 10.
- **Rare gems for +21 to +25**:
  - Radiant and Primal gems from the Aether Sanctum;
  - the Olympian god gems, which only the open-map world bosses drop and which +25 needs.
- **Weapon ranks 11 to 14.** Rank 14 needs an Aether weapon at +25.

## The three values
| Value | Gear limits | Olympian god gems | |
|---|---|---|---|
| **0 = off** | original game: quality 10, +12, 2 sockets, Platinum 10, Class Suit up to III | never drop | the original game's ceiling |
| **1 = on** | quality 20, +25, 4 sockets, Adamantium 10, Class Suit IV to VII | drop at "God gems: drop chance per boss kill" (default 2%) | the whole end-game |
| **2 = separate switches** (default) | "Original GodsWar limits" decides | "God gems: drops from the 19 world bosses" decides | for servers that already set those two |

- With 0 or 1 the two separate switches are ignored. They keep their values, and 2 hands control back to them.
- On a fresh database the default (2) gives the raised limits but **no** god gems, so nobody can reach +25.
  - **Set the master to 1** for the full end-game, or to 0 for the original ceiling.
- Medals (Valour Laurels, Aether Crests) and the Sanctum's Radiant and Primal gems drop in every mode.
- Items already above a limit keep working and are never changed. Only further progress is refused, with the game's
  own messages.
- The server log shows the state at start-up and after each change, for example
  `[endgame] master=on limits=origin class_suit_max=VII god_gems=on path=OPEN`.
  - `OPEN-WITHOUT-G25` warns that the limits are open but no god gem can drop.

## With `GODSWAR_EDITION`
- `GODSWAR_EDITION` ([EDITIONS.md](EDITIONS.md)) is read once at start-up and caps only the forge.
  - `classic` stops forging at quality 10 and +12, whatever this switch says.
- For the original game, use `classic` with the end-game at 0.
- For everything, use `reborn` with the end-game at 1.

## By SQL (as the database owner)
```sql
UPDATE public.server_rates SET value = 1, updated_by = 'admin' WHERE rate_key = 'endgame.enabled';
```
The row exists after the server's first start.
