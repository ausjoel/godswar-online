# Test environments for the editor modules

Every module verifier gets its own database copy, its own second reborn server and its own editor, all on the
VPS, on its own ports. Nothing here touches the live server (`godswar251.service`, database `godswar`,
ports 6999 / 7000 / 9090) or the admin's editor (port 8788).

## Base copy

`godswar_editor_base` is a copy of the live database made on 2026-10-28 04:36 UTC (read-only dump, then
restore; the live database was not written):

```sh
mkdir -p ~/gwed && cd ~/gwed
sudo -u postgres pg_dump -Fc godswar > godswar-20261028T043558Z.dump
sudo -u postgres createdb -O godswar godswar_editor_base
sudo -u postgres pg_restore -d godswar_editor_base --no-owner --role=godswar godswar-20261028T043558Z.dump
```

Do not use or change the base copy itself; `make-test-env.sh` clones it (a template copy takes a second).

## Your environment: `make-test-env.sh`

Run it on the VPS from your own copy of `GodsWar-VPS/tools` (rsync it there first):

```sh
# on the Mac
rsync -a --delete --exclude __pycache__ GodsWar-VPS/tools/ vps:gwed/tools-<you>/
# only for round2 instances, once per VPS (the build is shared, read-only):
rsync -a GodsWar-2.51/Polish/reborn-round2-linux-x64/ vps:gwed/round2-build/

# on the VPS
cd ~/gwed/tools-<you>/gwadmin-web/tests
./make-test-env.sh <n> <dbname> <login-port> [stock|round2] [command]
```

| Argument | |
|---|---|
| `<n>` | your environment name; its files are in `~/gwed/env-<n>` (server copy, appsettings, logs, backups, `pids.log`) |
| `<dbname>` | your database, e.g. `godswar_ed_inventory`; cloned from `godswar_editor_base`, realm row set to `YOUR_SERVER_IP:<login-port+1>` |
| `<login-port>` | the ports you were given: login = this, game = +1, management = 127.0.0.1:+2 (10000-65000; the VPS firewall only passes 10000-65535 from the admin's Mac) |
| `stock` / `round2` | the server build. `stock` = a copy of the live `/opt/godswar251/server` (live today: no live-edit API, online characters must be logged out). `round2` = the round-2 build (`GET /admin/capabilities`, live edits of online characters). Remembered per environment; default `stock`. |

| Command | |
|---|---|
| `up` (default) | clone the database if missing, start the instance, wait until `/readyz` answers |
| `clone` | only clone the database |
| `start` / `stop` | start / stop the instance (`stop` records the PID in `pids.log`) |
| `status` | instance and editor state |
| `reset` | stop, drop the database, clone a fresh one |
| `destroy` | stop, drop the database, remove `~/gwed/env-<n>` |
| `editor <port>` | start the editor from this tools copy on 127.0.0.1:`<port>` against your database and instance (`--rates-mgmt` = your management port) |
| `editor-stop <port>` | stop it |

Then tunnel the editor to the Mac and open it in the built-in browser:

```sh
ssh -o BatchMode=yes -o ExitOnForwardFailure=yes -f -N -L <port>:127.0.0.1:<port> vps
# http://127.0.0.1:<port>/
```

The instance runs as your VPS user with the same environment as `godswar251.service`
(`GODSWAR_RUNTIME_PROFILE=LocalDevelopment`, legacy raw authentication on, developer commands for account 3).
Registration is open on copies: the first login with a new name creates the account. The **Accounts** page
of the editor can also create accounts with a chosen password.

## Headless game clients (never open a visible game window)

```sh
# log in (and with --create make a character), decode what the server sends; --hold N stays in the world
python3 -I gwadmin-web/reborn_probe.py <account> <password> <Character> YOUR_SERVER_IP:<login-port> \
    [--create] [--hold 60] [--json out.json] --gwcrypt GodsWar-2.51/Polish/tools
```

`--hold` decodes what arrives while in the world: `status_history` (10166: level, Silver, Gold, B-Gold),
`kitbag` and `kitbag_changes` (10033 pages: `[slot, old item, new item]`), `hold_messages`.
Other clients: `GodsWar-2.51/Polish/gm-test-kit/gmclient.py` (GM commands), `GodsWar-2.51/Polish/tools/rates_probe.py`.

## The tests

| Test | Where | What it proves |
|---|---|---|
| `python3 -I gwadmin-web/tests/test_framework.py --db <your copy>` | VPS | catalogue (every ID type, every item), glossary + write rules, capability parsing, `char_write` (409 / 403 / 423, Undo), the economy-baseline guard, `rows_write` + Undo. 60 checks, `ALL OK`. |
| `python3 -I tests/test_live_edit.py --editor http://127.0.0.1:<port> --login YOUR_SERVER_IP:<login-port> --account A --password P --role C` | Mac | needs a `round2` instance: a headless client in the world receives live wallet / level / bag changes made through the editor, Undo of a live change, 423 for a field without a live endpoint, 409 for stale values, then the database path once logged out. `ALL OK`. |
| `curl -s http://127.0.0.1:<port>/api/reborn/selftest?format=text` | Mac (tunnel) | 0 errors on your copy |
| `node tests/test_character_rename_ui.cjs` | anywhere (node) | the Character page keeps a name / gender change pending (the 2026-11-02 "renaming doesn't work" bug: text compared as NaN). PASS |
| `python3 -I tests/test_character_rename.py --editor http://127.0.0.1:<port> --character <offline id> [--online-character <id>] --psql "<psql>" --db <your copy> --i-know-this-database-is-a-copy` | Mac or VPS | rename stored + Undo, no-op, case-only rename, taken name (any case), restorable deleted name, bad / 32-char / non-ASCII names, stale page 409, online 423, only `character_base.name` changes. 24/24 on the rig copy `godswar_ed_rename` (2026-11-02) |

Framework results (2026-10-28): `test_framework.py --db godswar_editor_fw` ALL OK (60 checks);
`test_live_edit.py` on `godswar_editor_fw2` (round2, ports 58805-58807) ALL OK; selftest on
`godswar_editor_fw2`: 6 characters, 0 errors, validators ok.

## When you are done

```sh
./make-test-env.sh <n> <dbname> <login-port> editor-stop <port>
./make-test-env.sh <n> <dbname> <login-port> stop       # PID recorded in ~/gwed/env-<n>/pids.log
```

Leave the database copy (say its name in your report) unless told to `destroy` it.
