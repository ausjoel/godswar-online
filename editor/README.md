# GodsWar editor

A web tool for administrators: edit characters, items, pets, skills, guilds and accounts, and
change server rates while the server runs (every save has an undo).

## Run it
It talks to the same PostgreSQL database as the server and to the server's management port
(`127.0.0.1:9090`). Start it on the machine that runs the server:

```bash
cd editor
python3 -I server.py --help        # lists the options (database, port, management URL)
```
Keep it on `127.0.0.1` or behind HTTPS with a strong password. It is for administrators only.
Accounts with the `editor_admin` flag can sign in; create the first one from `server.py --help`.

## Pictures
The game's own pictures are not included. The editor works without them (icons show as blank).
To generate them from your own client see [BUILD-ASSETS.md](BUILD-ASSETS.md).

More: [MODULES.md](MODULES.md) (how modules are written), `schema/` (every table and the edit rules).
