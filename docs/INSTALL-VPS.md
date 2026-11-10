# Run GodsWar on a VPS

## You need
- An x86_64 or ARM64 VPS with Ubuntu 22.04+ or Debian 12+, 2 vCPU / 4 GB RAM / 20 GB disk or better, a public IPv4
- Root (or sudo) over SSH
- Optional: a domain whose DNS A record points at the VPS (for the sign-up site). Without one, setup uses
  `<your-ip-with-dashes>.sslip.io`, which resolves to your IP with no DNS work.
- Optional: a [Resend](https://resend.com) account and API key, with a verified sending domain, for sign-up mail
- Your own GodsWar 2.51 client: [CLIENT.md](CLIENT.md)

## Steps
```bash
ssh root@your.server.ip
git clone <this repository> godswar && cd godswar
./setup.sh --vps            # (use sudo if you are not root)
```
Answer the questions or press Enter for the defaults. What it does, in order:

| Step | What |
|---|---|
| packages | apt: PostgreSQL 17 (from apt.postgresql.org if your release has an older one), python3-jinja2/psycopg2, Caddy (distro or Caddy's own repository), ufw, libicu |
| .NET | the SDK named in `server/global.json`, into `/opt/godswar/dotnet` (Microsoft's `dotnet-install.sh`) |
| build | `dotnet publish` (self-contained) into `/opt/godswar/server`; the previous build is kept as `server.prev` |
| secrets | `/etc/godswar` (root only, 600): `db_password`, `admin_password`, `pgpass`, `update-private.pem`; `/etc/godswar/service/` (group `godswar`, 640): `postgres.conn` |
| database | role + database `godswar`, game content from `server/database/baseline.sql.gz` (only when empty), realm row = your IP, administrator account |
| services | `godswar.service` (runs as the unprivileged user `godswar`; the only secret it can read is its connection string), optional `godswar-editor.service` |
| website | `signup/install.sh` (its own least-privilege DB role, `gwsignup.service` on 127.0.0.1:8090) behind Caddy with a Let's Encrypt certificate |
| firewall | ufw: SSH, login + game ports, 80/443 (SSH is always allowed before ufw is switched on) |

**Also open the login and game ports (and 80/443) in your hosting provider's firewall panel**: many providers
block them even when ufw allows them.

Unattended: copy `config/godswar.env.example` to `config/godswar.env`, fill it in, then
`./setup.sh --vps --yes --config config/godswar.env`. Preview with `--dry-run`.

## Everyday
```bash
systemctl status godswar              # state
systemctl restart godswar             # restart (players are disconnected)
journalctl -u godswar -f              # log
cat /etc/godswar/admin_password       # administrator password
touch /etc/gwsignup/closed            # close sign-ups (rm to open again)
```

## Mail later
Put `GWSIGNUP_MAIL_FROM=Name <noreply@your.domain>` in `/etc/gwsignup/gwsignup.env` (remove a
`GWSIGNUP_MAIL=0` line), then `signup/ops/set-resend-key.sh` (asks for the key, hidden) and
`signup/ops/test-mail.sh you@example.com`. Or re-run `./setup.sh --vps --mail-from ... --resend-key-file FILE`.

## Backups
```bash
sudo -u postgres pg_dump godswar | gzip > /root/godswar-$(date +%F).sql.gz
```
Test a restore before you rely on it. Never restore an old backup over a running server.

## Updating
```bash
git pull && ./setup.sh --vps --yes
```
It rebuilds the server and restarts it; the database and secrets are kept (the server updates the schema
on start). Take a backup first and update when no players are online.
