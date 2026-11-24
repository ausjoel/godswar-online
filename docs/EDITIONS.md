# Editions: classic and reborn

Set `GODSWAR_EDITION` in `config/godswar.env` (setup.sh asks), then restart the server
(`docker compose --env-file config/godswar.env up -d server`).

| | classic | reborn (default) |
|---|---|---|
| Gear quality ("enhancement") limit | 10 | 20 |
| Gear grade limit | 12 | 25 |
| Other limits (talent rank, level, warehouse, stacks) | server defaults | server defaults |

* **classic** is the original game's rule set: forging stops at quality 10 / grade 12.
* **reborn** lets players forge past the original limits. The stock client only draws and
  validates the original limits, so reborn needs the patched client (see [CLIENT.md](CLIENT.md)).
  A stock client connected to a reborn server shows wrong values for items beyond the old limits.
* Loot drops follow the original quality/grade ranges in both editions.
* Rates (EXP, drops, money, forging chances) are separate: change them live from the editor's
  "Server rates" tab. They start at stock values in both editions.

Switching a running world from reborn to classic does not reduce items that are already
above the classic limits; they stay as they are but cannot be forged further.

## Editions and the end-game switch

The edition only caps the forge. Class Suit IV to VII, 4 holy-stone sockets, Holy Suit past Platinum and the
Olympian god gems are opened or closed by the hot end-game master switch (`endgame.enabled`, editor "Server rates").
For the original game use `classic` with the end-game at 0; for everything use `reborn` with the end-game at 1.
See [ENDGAME.md](ENDGAME.md).
