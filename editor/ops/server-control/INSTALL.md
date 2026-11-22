# Editor server control (helper v2): install notes

Nothing here is installed by the editor. Run on the VPS as root, at a quiet time (the editor is restarted; the game is not).

## What changes

| File (this folder) | Installs to | Owner, mode |
|---|---|---|
| `gweditor-control.py` (v2: adds `backups`, `restore`) | `/usr/local/libexec/gweditor-control.py` (replaces v1, md5 57484725…) | root:root 0755 |
| `gweditor-control@.service` (comment only) | `/etc/systemd/system/gweditor-control@.service` | root 0644 |
| `gweditor-control.socket` (unchanged) | `/etc/systemd/system/gweditor-control.socket` | root 0644 |
| `godswar251-restore` (new) | `/usr/local/sbin/godswar251-restore` | root:root 0755 |
| `godswar251-restore@.service` (new) | `/etc/systemd/system/godswar251-restore@.service` | root 0644 |
| `sudoers.gweditor-control.FALLBACK` | **do not install** (see below) | |

## Steps

```sh
# from the Mac
rsync -a editor/ops/server-control/ vps:gwed/server-control-v2/
# on the VPS
cd ~/gwed/server-control-v2
sudo cp /usr/local/libexec/gweditor-control.py /root/gweditor-control.py.v1      # rollback copy
sudo install -o root -g root -m 0755 gweditor-control.py /usr/local/libexec/gweditor-control.py
sudo install -o root -g root -m 0755 godswar251-restore /usr/local/sbin/godswar251-restore
sudo install -o root -g root -m 0644 gweditor-control@.service godswar251-restore@.service /etc/systemd/system/
sudo systemctl daemon-reload
# no restart of the socket is needed: every request starts a fresh helper process
```

Then deploy the editor (the lead's release script) so `modules/operations.py` is live, and in the editor:
Operations -> Game server -> List backups (should list `nightly/` dumps). Do NOT test Restore on live; test it on a
rig with a copy database by temporarily pointing the restore script's database name at the copy.

Rollback: `sudo install -m 0755 /root/gweditor-control.py.v1 /usr/local/libexec/gweditor-control.py`; remove
`/usr/local/sbin/godswar251-restore` and `/etc/systemd/system/godswar251-restore@.service`; `systemctl daemon-reload`.

## Why a socket and not sudoers

`gweditor.service` runs with `NoNewPrivileges=true`, `RestrictSUIDSGID=true` and an empty `CapabilityBoundingSet`, so
`sudo` can never gain root from inside the editor. The socket-activated helper (already live since 2026-10-30, v1)
is the narrow root path: it takes one JSON line, a fixed action list, a 32-hex job id and (for restore) a dump file
BASENAME that must be one of the listed files; it never takes unit names, paths or shell text. The editor adds who
may ask (editor_admin sign-in), the player-count acknowledgement for stop/restart, and for restore: zero players and
the file name typed in. The restore script checks zero players again itself, dumps the current database to
`backup/pre-restore/` first (newest 10 kept), and puts that dump back if loading fails.

`sudoers.gweditor-control.FALLBACK` is the sudo version the brief asked for. It only works if a drop-in turns those
three hardening settings off for `gweditor.service`, and it needs `/run/gweditor-control` created by tmpfiles.d.
Not recommended; kept for reference (`visudo -cf` passes).

## What the editor can do without the helper

`status` and `check` (Operations -> Game server) run as the editor user: `systemctl show`, port probes on
6999/7000/9090, `/readyz`, one SELECT, disk space. The resource monitor reads `/proc` and the world-readable cgroup v2
files (`/sys/fs/cgroup/system.slice/...`); no root.

---

# Helper v3 (wave 5 B): set up / take down the game process of an extra server (realm)

Nothing here is installed by the editor. Run on the VPS as root at a quiet time. The game is NOT restarted by these
steps. v3 only adds two actions to the helper (`realm_provision`, `realm_remove`); the seven v2 actions are unchanged.

## What it adds

| File (this folder) | Installs to | Owner, mode |
|---|---|---|
| `gweditor-control.py` (v3) | `/usr/local/libexec/gweditor-control.py` (replaces v2) | root:root 0755 |
| `gweditor-control@.service` (comment only) | `/etc/systemd/system/gweditor-control@.service` | root 0644 |
| `godswar251-realm` (new, Python) | `/usr/local/sbin/godswar251-realm` | root:root 0755 |
| `godswar251-realm-admin@.service` (new, oneshot) | `/etc/systemd/system/godswar251-realm-admin@.service` | root 0644 |
| `godswar251-realm@.service` (new, template for the game process of realm N) | `/etc/systemd/system/godswar251-realm@.service` | root 0644 |
| (a link, step 3) | `/etc/systemd/system/godswar251-realm@.service.d -> godswar251.service.d` | root |

Created later by the script itself: `/etc/godswar251-realms/realm-N.env` (numbers only, no secret),
`/var/lib/godswar251-realms/realm-N.json` (what was done; world-readable, the editor reads it),
`/opt/godswar251/logs/realm-N.log`, a ufw rule `N/tcp` with the comment `godswar realm N`, and (only if the running
game still keeps login handoffs in memory) `/etc/systemd/system/godswar251.service.d/realm-handoff.conf`.

## Steps

```sh
# from the Mac
rsync -a editor/ops/server-control/ vps:gwed/server-control-v3/
# on the VPS
cd ~/gwed/server-control-v3
sudo cp /usr/local/libexec/gweditor-control.py /root/gweditor-control.py.v2            # rollback copy
sudo install -o root -g root -m 0755 gweditor-control.py /usr/local/libexec/gweditor-control.py
sudo install -o root -g root -m 0755 godswar251-realm /usr/local/sbin/godswar251-realm
sudo install -o root -g root -m 0644 gweditor-control@.service godswar251-realm-admin@.service godswar251-realm@.service /etc/systemd/system/
# 3. the extra realms must run with the SAME feature switches as realm 1: share the drop-in folder
sudo ln -s godswar251.service.d /etc/systemd/system/godswar251-realm@.service.d
sudo install -d -o root -g root -m 0755 /etc/godswar251-realms /var/lib/godswar251-realms
sudo systemctl daemon-reload
# 4. checks (nothing is started)
systemctl show godswar251-realm@2.service -p Environment --value | tr ' ' '\n' | sort > /tmp/realm.env
systemctl show godswar251.service        -p Environment --value | tr ' ' '\n' | sort > /tmp/main.env
diff /tmp/main.env /tmp/realm.env && echo "same switches"     # must print "same switches"
test -x /usr/sbin/ufw && test -x /usr/bin/ss && echo "ufw and ss found"
sudo -u postgres psql -d godswar -XAtc "SELECT to_regclass('public.legacy_game_login_handoffs')"   # migration 239: must not be empty
```

If the `diff` shows differences, systemd on this machine did not follow the linked drop-in folder: do NOT go on. The
script checks the same thing before it touches the firewall and refuses ("would start without N of the main server's
feature switches"), so a second realm can never run with different game rules; tell the agent and it will switch to
copying the switches into the settings file.

Then deploy the editor (the lead's release script). Also add the realm logs to log rotation:
`/etc/logrotate.d/godswar251` -> add `/opt/godswar251/logs/realm-*.log` to the file list.

## The one restart the admin must do (before the first extra server takes logins)

The main game hands a login to another process only when it keeps login handoffs in PostgreSQL
(`GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres`; proven on a rig in `docs/evidence/shared-login-handoff/`). Live
runs the default (memory). The first "Set up server" therefore stops with: "The main game server must be restarted
once (at zero players)". At that point the script has written `godswar251.service.d/realm-handoff.conf` and run
`daemon-reload`, nothing else. The restart itself is the admin's (Operations -> Game server -> Restart, zero players), then
"Set up server" again.

For the lead: a release script that compares the unit environment before and after its restart will see the new
variable. Either install the drop-in yourself as part of a normal zero-player release (same content as below) so the
guide never has to ask, or expect that one difference.

```ini
# /etc/systemd/system/godswar251.service.d/realm-handoff.conf
[Service]
Environment=GODSWAR_LEGACY_GAME_LOGIN_HANDOFF_STORE=postgres
```

With `postgres` and one process nothing changes for players (same checks, the table instead of memory). Rollback:
remove the file, `daemon-reload`, restart at zero players (only when no extra realm is enabled).

## What "Set up server" then does (one click in Operations -> Servers -> Add a server)

1. Editor: adds the row disabled (or uses the disabled row, for example Dwargon) at the DETECTED address and the chosen
   port (7001-7099). Audited, Undo.
2. Helper: starts `godswar251-realm-admin@provision-<realm>-<port>.service`. The helper accepts exactly two JSON
   integers (realm 2-255, port 7001-7099); no names, paths or shell text. The script re-validates both.
3. Script: checks (template installed, main game active, port free, shared handoff, same feature switches), writes the
   settings file, `ufw allow <port>/tcp`, listens on the port for 30 seconds ("knock"), `systemctl enable` + `start`
   `godswar251-realm@<realm>`, waits for `/readyz` on `127.0.0.1:19000+realm`. If the process does not become ready it
   is stopped, the port closed and the settings file set aside again.
4. Editor: enables the row only if the job succeeded, the port answers locally, the main game shares handoffs, and the
   knock saw a connection from another machine. Otherwise it says what is missing.

Realm N uses: game port as chosen (public), login `127.0.0.1:17000+N` (unused; players log in on 6999),
management `127.0.0.1:19000+N`, node id `realm-N`, log `/opt/godswar251/logs/realm-N.log`. It runs the same binary and
`appsettings.json` as realm 1 (so a release swaps both), is restarted and stopped with `godswar251.service` (`PartOf=`)
and started with it (`WantedBy=godswar251.service`), and waits for the main game's `/readyz` before it starts.

## The provider firewall (Hosting by Design)

ufw is this machine; the provider firewall is in front of it and only the admin can open it (provider panel: inbound TCP
on the new port). The page does not pretend: during the 30-second knock it shows "Test from this computer", which
opens `http://<address>:<port>/` in a new browser tab (an error page is expected). If that connection reaches the
script, the provider passes the port and the server is listed. If not, the server stays hidden and the page offers
"Test again" (safe: the realm is disabled and empty) or "I opened it: list the server anyway" (recorded in the change
log as "outside test NOT passed, listed on the operator's confirmation").

Optional, for an automatic knock without the extra click: allow the editor page to open a WebSocket to the game
address in Caddy's Content-Security-Policy for the editor site, e.g. add `connect-src 'self' wss://YOUR_SERVER_IP:*`.
Without it the link above is the test.

## Close / Delete

"Close server": refused while anyone is in that realm (database + open connections; the script checks connections
again). Disables the row, then `godswar251-realm-admin@remove-<realm>.service`: `systemctl disable --now`, `ufw delete
allow <port>/tcp` (the port is read from the settings file the script wrote, never from the request), settings file
moved to `/var/lib/godswar251-realms/removed/`. Characters are database rows and are not touched; they are playable
again when the server is set up again. "Delete server" does the same and then removes the row, and is refused while any
character, account slot or other record belongs to the realm. Realm 1 can never be closed; realm 2's row can be closed
but not deleted (the schema migrations check it).

## Test on the VPS without touching live (suggested)

`sudo /usr/local/sbin/godswar251-realm status` prints what is set up (nothing changes). A full dry run needs a second
realm, so do it once with the admin at a quiet time: Add a server -> Dwargon -> Set up; press "Test from this computer";
log in with a test account and pick the new server; then Close it again.

## Rollback of v3

`sudo install -m 0755 /root/gweditor-control.py.v2 /usr/local/libexec/gweditor-control.py`; for every set-up realm
`sudo /usr/local/sbin/godswar251-realm remove <N>` (and disable its row in the editor first); then remove
`/usr/local/sbin/godswar251-realm`, the two `godswar251-realm*` unit files and the `godswar251-realm@.service.d` link;
`systemctl daemon-reload`. `realm-handoff.conf` can stay (harmless) or go at the next zero-player restart.

## Not covered

- The health timer (`godswar251-health`) and the nightly backup do not know realm units. The backup needs nothing (one
  database). A health check for `godswar251-realm@*` is a small follow-up.
- `gw251` (the shell helper) starts/stops only the main unit; extra realms follow it through systemd.
- The Game server tab's player count for stop/restart reads the database for every realm, but its connection count
  only looks at ports 6999/7000.
