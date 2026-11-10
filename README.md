# GodsWar

An open, community-run server for **GodsWar Online** (2.51 client protocol), with the tools
around it: account sign-up site, web editor for characters/items/rates, and a launcher that
downloads and updates a client.

> The game client and its art belong to their owners and are **not** included. You supply
> your own copy of the client. See [docs/CLIENT.md](docs/CLIENT.md).

## Quick start (one-click setup)

```bash
git clone <this repository> godswar && cd godswar
sudo ./setup.sh --vps       # a VPS others join: Ubuntu 22.04+ / Debian 12+
sudo ./setup.sh --local     # this computer only (Debian/Ubuntu); on macOS: ./setup.sh --local (Homebrew)
./setup.sh --docker         # anywhere Docker runs
./setup.sh                  # asks
```

`setup.sh` asks a few questions, each with a sensible default (press Enter): the address players use
(the VPS's public IP is detected), an optional domain for the HTTPS sign-up site (without one it uses
an `<ip>.sslip.io` name), server name, administrator name, ports, optional Resend mail, and whether to
open the firewall. Then it:

- installs what is missing: PostgreSQL 17, the .NET 10 SDK (to build the server), Python with jinja2 and
  psycopg2 (sign-up site), Caddy (HTTPS, VPS only), ufw if wanted;
- generates every secret itself (database password, administrator password, the key that signs client
  updates, the sign-up site's keys) into root-only files. None ship in this repository;
- builds the server, loads the game database (items, monsters, maps, quests; **no players**), writes the
  realm address, creates your administrator with GM commands on, installs systemd services and starts them;
- runs a login self-test and prints a summary: addresses, ports, where the passwords are, start/stop.

Running it again is safe: it keeps the database, the secrets and your previous answers.
For automation: `sudo ./setup.sh --vps --yes --config config/godswar.env` (all answers from the file, see
`config/godswar.env.example`), and `--dry-run` prints every step without changing anything.
`./setup.sh --help` lists all options. Details: [docs/INSTALL-VPS.md](docs/INSTALL-VPS.md),
[docs/INSTALL-LOCAL.md](docs/INSTALL-LOCAL.md).

## What is in this folder

| Folder | What it is |
|---|---|
| `setup.sh` | One-click install: VPS, local (Linux/macOS) or Docker |
| `config/` | `godswar.env.example`: answers for `setup.sh --config` (your copy `godswar.env` is ignored by git) |
| `server/` | The game server (C# / .NET 10, PostgreSQL), GPL-3. Start at `server/README.md` |
| `editor/` | Web editor: characters, items, rates, accounts |
| `signup/` | Public sign-up / password-reset website |
| `launcher/` | Player launcher with self-updating client (Go) |
| `tools/` | Helper scripts (admin account, update-signing key) |
| `docs/` | Guides |
| `local/` | **Your private files.** Ignored by git |

## Working on it without leaking your own details

Settings, addresses, keys and notes never go in tracked files. Keep them in
`config/godswar.env` and `local/`; both are git-ignored. Before sending a change, run
`python3 -I tools/leak-scan.py` — it fails if your IP, home paths or keys are in the tree.
See [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

## Editions

`GODSWAR_EDITION=classic` runs the original game's limits; `reborn` (default) lifts them
(gear quality 20 / grade 25). See [docs/EDITIONS.md](docs/EDITIONS.md).

## Credits

The server descends from open-source work: [godswar-server-reborn](https://github.com/hejing900/godswar-server-reborn)
(C#/.NET, hejing900), itself a port of [sush1lemon/godswar-server](https://github.com/sush1lemon/godswar-server)
(Go) and based on [Godswar-Reborn](https://github.com/p5y-Ph3R/Godswar-Reborn). This repository contains
modified versions (changes from 2026-10 onward). GodsWar Online is the property of its owners; this is an
unofficial preservation project and is not affiliated with them.

## Licence

GPL-3.0, see `server/LICENSE`. Credits and upstream projects: `server/NOTICE.md`.
