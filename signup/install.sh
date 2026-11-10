#!/bin/sh
# install.sh - install (or update) the GodsWar account sign-up site on a Debian host.
#
#   sudo ./install.sh --live      the real site: database godswar, 127.0.0.1:8090, Caddy on 80/443
#   sudo ./install.sh --test      a test copy:   database godswar_signup, 127.0.0.1:8091, no Caddy
#   sudo ./install.sh --test --remove     take a test copy away again (service, files, role)
#
# Options (after --live or --test):
#   --db NAME           game database               (live: godswar, test: godswar_signup)
#   --port N            app port on 127.0.0.1        (live: 8090,    test: 8091)
#   --name NAME         service / folder name       (live: gwsignup, test: gwsignup-test)
#   --role ROLE         PostgreSQL login role       (live: gw_signup, test: gw_signup_test)
#   --origin URLS       public address(es) of the page, comma separated
#                       (live: required, e.g. https://your.domain; the FIRST one is
#                       used in links in mails. test: http://127.0.0.1:PORT,http://localhost:PORT)
#   --download-url URL  link shown after sign-up for the client download (kept from the last run if left out)
#   --no-caddy          do not install or configure Caddy (live only)
#   --no-firewall       do not open 80/443 in ufw (live only)
#
# Mail (Resend): the key lives in /etc/NAME/resend_api_key (root 600; made EMPTY = mail off if
# missing). Set it with ops/set-resend-key.sh. Mail settings (GWSIGNUP_MAIL*, GWSIGNUP_RESEND_*,
# GWSIGNUP_LIMIT_MAIL*) added by hand to gwsignup.env are kept across runs. sql/mail-grants.sql
# runs after sql/signup-db.sql on every run.
#
# Running it again is safe: secrets are kept, files and grants are brought up to date, the
# service is restarted. Uses Debian packages only (python3-jinja2, python3-psycopg2, caddy).
set -eu

SRC=$(cd "$(dirname "$0")" && pwd)
MODE=""; DB=""; PORT=""; NAME=""; ROLE=""; ORIGIN=""; DOWNLOAD_URL="__keep__"
CADDY=1; FIREWALL=1; REMOVE=0

die() { echo "install.sh: $*" >&2; exit 1; }
say() { echo "== $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --live) MODE=live ;;
    --test) MODE=test ;;
    --db) DB=$2; shift ;;
    --port) PORT=$2; shift ;;
    --name) NAME=$2; shift ;;
    --role) ROLE=$2; shift ;;
    --origin) ORIGIN=$2; shift ;;
    --download-url) DOWNLOAD_URL=$2; shift ;;
    --no-caddy) CADDY=0 ;;
    --no-firewall) FIREWALL=0 ;;
    --remove) REMOVE=1 ;;
    -h|--help) sed -n '2,31p' "$0"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
  shift
done

[ -n "$MODE" ] || die "say --live or --test (see --help)"
[ "$(id -u)" = 0 ] || die "run as root (sudo)"

if [ "$MODE" = live ]; then
  : "${DB:=godswar}" "${PORT:=8090}" "${NAME:=gwsignup}" "${ROLE:=gw_signup}" "${ORIGIN:?pass --origin https://your.domain (the public address of the sign-up site)}"
else
  : "${DB:=godswar_signup}" "${PORT:=8091}" "${NAME:=gwsignup-test}" "${ROLE:=gw_signup_test}"
  : "${ORIGIN:=http://127.0.0.1:$PORT,http://localhost:$PORT}"
  CADDY=0; FIREWALL=0
  [ "$DB" != godswar ] || die "--test must not use the live database 'godswar'"
  [ "$NAME" != gwsignup ] || die "--test must not use the live service name 'gwsignup'"
  [ "$ROLE" != gw_signup ] || die "--test must not use the live role 'gw_signup'"
  [ "$PORT" != 8090 ] || die "--test must not use the live port 8090"
fi
echo "$NAME" | grep -Eq '^[a-z][a-z0-9-]{1,30}$' || die "bad --name"
echo "$ROLE" | grep -Eq '^[a-z][a-z0-9_]{1,30}$' || die "bad --role"
echo "$DB" | grep -Eq '^[a-z][a-z0-9_]{1,62}$' || die "bad --db"
echo "$PORT" | grep -Eq '^[0-9]{2,5}$' || die "bad --port"

PREFIX=/opt/$NAME
ETC=/etc/$NAME
USER_NAME=$NAME
UNIT=/etc/systemd/system/$NAME.service
PSQL="runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1"

# ------------------------------------------------------------------ remove (test copies)
if [ "$REMOVE" = 1 ]; then
  [ "$MODE" = test ] || die "--remove is only for --test installs; remove the live site by hand"
  say "removing $NAME (database $DB keeps its signup schema and any accounts made)"
  systemctl disable --now "$NAME.service" 2>/dev/null || true
  rm -f "$UNIT"; systemctl daemon-reload
  rm -rf "$PREFIX" "$ETC"
  if $PSQL -d postgres -Atc "SELECT 1 FROM pg_roles WHERE rolname='$ROLE'" | grep -q 1; then
    $PSQL -d "$DB" -c "DROP OWNED BY \"$ROLE\"" || true
    $PSQL -d postgres -c "DROP ROLE \"$ROLE\"" || echo "role $ROLE still has rights in another database; drop it by hand"
  fi
  id -u "$USER_NAME" >/dev/null 2>&1 && userdel "$USER_NAME" || true
  echo "removed"; exit 0
fi

# ------------------------------------------------------------------ packages
need=""
for p in python3 python3-jinja2 python3-psycopg2 curl ca-certificates; do
  dpkg -s "$p" >/dev/null 2>&1 || need="$need $p"
done
if [ "$CADDY" = 1 ]; then dpkg -s caddy >/dev/null 2>&1 || need="$need caddy"; fi
if [ -n "$need" ]; then
  say "installing Debian packages:$need"
  apt-get update -q
  DEBIAN_FRONTEND=noninteractive apt-get install -y -q --no-install-recommends $need
fi

# ------------------------------------------------------------------ checks
$PSQL -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname='$DB'" | grep -q 1 || die "database $DB does not exist"
$PSQL -d "$DB" -Atc "SELECT to_regclass('public.accounts') IS NOT NULL" | grep -q t || die "$DB has no public.accounts table"
OWNER=$($PSQL -d postgres -Atc "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='$DB'")
[ -n "$OWNER" ] || die "cannot find the owner of $DB"

# ------------------------------------------------------------------ user and files
if ! id -u "$USER_NAME" >/dev/null 2>&1; then
  say "creating system user $USER_NAME"
  useradd --system --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$USER_NAME"
fi

if [ -d "$PREFIX" ]; then
  BK=/var/backups/$NAME-before-install-$(date -u +%Y%m%dT%H%M%SZ).tgz
  install -d -o root -g root -m 700 /var/backups
  tar -czf "$BK" -C / "${PREFIX#/}" "${ETC#/}" 2>/dev/null || tar -czf "$BK" -C / "${PREFIX#/}"
  chmod 600 "$BK"
  echo "backup of the old $PREFIX (and $ETC) -> $BK"
fi
say "copying the site to $PREFIX (owned by root, read-only for the service)"
install -d -o root -g root -m 755 "$PREFIX" "$PREFIX/app" "$PREFIX/templates" "$PREFIX/static" "$PREFIX/sql"
for d in app templates static sql; do
  find "$PREFIX/$d" -mindepth 1 -maxdepth 1 -type f -delete
  for f in "$SRC/$d"/*; do
    [ -f "$f" ] || continue
    case "$f" in *.pyc|*~|*.orig) continue ;; esac
    install -o root -g root -m 644 "$f" "$PREFIX/$d/"
  done
done

# ------------------------------------------------------------------ secrets and settings
# 755 so the service can see the "closed" switch file; the secrets inside stay 0600 root.
install -d -o root -g root -m 755 "$ETC"
umask 077
if [ ! -s "$ETC/secret_key" ]; then
  say "making $ETC/secret_key"
  python3 -I -c 'import secrets; print(secrets.token_hex(32))' > "$ETC/secret_key"
fi
if [ ! -s "$ETC/db_password" ]; then
  say "making $ETC/db_password"
  python3 -I -c 'import secrets; print(secrets.token_urlsafe(30))' > "$ETC/db_password"
fi
# The Resend key (mail). Empty = mail off. Never overwritten here; ops/set-resend-key.sh fills it in.
[ -e "$ETC/resend_api_key" ] || { say "making an empty $ETC/resend_api_key (mail stays off until a key is set)"; : > "$ETC/resend_api_key"; }
chown root:root "$ETC/secret_key" "$ETC/db_password" "$ETC/resend_api_key"
chmod 600 "$ETC/secret_key" "$ETC/db_password" "$ETC/resend_api_key"
MAIL_KEY_SET=0
grep -q '[^[:space:]]' "$ETC/resend_api_key" && MAIL_KEY_SET=1
grep -Eq '^[A-Za-z0-9_-]{20,}$' "$ETC/db_password" || die "$ETC/db_password must be letters, digits, - and _ only"

if [ "$DOWNLOAD_URL" = "__keep__" ]; then
  DOWNLOAD_URL=$(sed -n 's/^GWSIGNUP_DOWNLOAD_URL=//p' "$ETC/gwsignup.env" 2>/dev/null || true)
fi
case "$DOWNLOAD_URL" in ""|https://*|http://*) ;; *) die "--download-url must start with https://" ;; esac
# The site name shown on pages and in mail subjects: keep what the file says (live was set to
# "GodsWar" by hand on 2026-11-02), default GodsWar.
SERVER_NAME=$(sed -n 's/^GWSIGNUP_SERVER_NAME=//p' "$ETC/gwsignup.env" 2>/dev/null | tail -n 1 || true)
[ -n "$SERVER_NAME" ] || SERVER_NAME=GodsWar
# Mail settings added by hand (GWSIGNUP_MAIL, _MAIL_FROM, _MAIL_REPLY_TO, _RESEND_*, _LIMIT_MAIL*): keep them.
MAIL_LINES=$(grep -E '^GWSIGNUP_(MAIL|MAIL_[A-Z_]+|RESEND_[A-Z_]+|LIMIT_MAIL[A-Z_]*)=' "$ETC/gwsignup.env" 2>/dev/null || true)
cat > "$ETC/gwsignup.env" <<EOF
# Settings for $NAME (written by install.sh; secrets are NOT here, see LoadCredential in the unit).
GWSIGNUP_BIND=127.0.0.1
GWSIGNUP_PORT=$PORT
GWSIGNUP_DB_HOST=127.0.0.1
GWSIGNUP_DB_PORT=${PGPORT:-5432}
GWSIGNUP_DB_NAME=$DB
GWSIGNUP_DB_USER=$ROLE
GWSIGNUP_ORIGIN=$ORIGIN
GWSIGNUP_TRUST_PROXY=1
GWSIGNUP_STATIC_DIR=$PREFIX/static
GWSIGNUP_TEMPLATE_DIR=$PREFIX/templates
GWSIGNUP_CLOSED_FLAG=$ETC/closed
GWSIGNUP_DOWNLOAD_URL=$DOWNLOAD_URL
GWSIGNUP_SERVER_NAME=$SERVER_NAME
EOF
if [ -n "$MAIL_LINES" ]; then
  { echo "# Mail settings (kept from the previous file):"; printf '%s\n' "$MAIL_LINES"; } >> "$ETC/gwsignup.env"
fi
chmod 600 "$ETC/gwsignup.env"
umask 022

# ------------------------------------------------------------------ database
say "database $DB: schema 'signup', role $ROLE and its grants"
$PSQL -d "$DB" -v role="$ROLE" -v owner="$OWNER" -f "$PREFIX/sql/signup-db.sql"
# Mail: the token/reset functions and their EXECUTE grants. Every run, after signup-db.sql.
say "database $DB: mail functions (sql/mail-grants.sql)"
$PSQL -d "$DB" -v role="$ROLE" -v owner="$OWNER" -f "$PREFIX/sql/mail-grants.sql"
# The password goes to psql on stdin (printf is a shell builtin), never on a command line.
printf "ALTER ROLE \"%s\" PASSWORD '%s';\n" "$ROLE" "$(cat "$ETC/db_password")" | $PSQL -d postgres

# ------------------------------------------------------------------ service
say "systemd unit $UNIT"
sed -e "s#@NAME@#$NAME#g" -e "s#@PORT@#$PORT#g" -e "s#@DB@#$DB#g" -e "s#@USER@#$USER_NAME#g" -e "s#@PREFIX@#$PREFIX#g" \
  "$SRC/systemd/gwsignup.service.in" > "$UNIT"
chmod 644 "$UNIT"
# Outgoing mail: the unit allows loopback only. With a Resend key, a drop-in lets the service reach
# api.resend.com (and DNS). systemd filters by IP, not by name, and Resend's addresses change, so
# this allows outgoing connections in general; the app itself only connects to GWSIGNUP_RESEND_URL.
DROPIN=/etc/systemd/system/$NAME.service.d
if [ "$MAIL_KEY_SET" = 1 ]; then
  say "mail key is set: allowing outgoing connections for $NAME ($DROPIN/mail.conf)"
  install -d -o root -g root -m 755 "$DROPIN"
  printf '%s\n' "# Written by install.sh / set-resend-key.sh because a Resend key is set. Removed when it is empty." \
    "[Service]" "IPAddressAllow=any" > "$DROPIN/mail.conf"
  chmod 644 "$DROPIN/mail.conf"
else
  rm -f "$DROPIN/mail.conf"
  rmdir "$DROPIN" 2>/dev/null || true
fi
systemctl daemon-reload
systemctl enable "$NAME.service" >/dev/null 2>&1
systemctl restart "$NAME.service"

i=0; health=""
while [ $i -lt 20 ]; do
  health=$(curl -s -m 2 "http://127.0.0.1:$PORT/healthz" || true)
  [ "$health" = ok ] && break
  sleep 1; i=$((i+1))
done
if [ "$health" != ok ]; then
  journalctl -u "$NAME.service" -n 30 --no-pager || true
  die "$NAME did not become healthy on 127.0.0.1:$PORT (got: ${health:-nothing})"
fi
echo "$NAME is up on 127.0.0.1:$PORT and can reach $DB"
if [ "$MAIL_KEY_SET" = 1 ]; then
  echo "mail is ON (key in $ETC/resend_api_key). Test: sudo $SRC/ops/test-mail.sh you@example.com"
else
  echo "mail is OFF (empty $ETC/resend_api_key). Set a key: sudo $SRC/ops/set-resend-key.sh"
fi

# ------------------------------------------------------------------ Caddy (live)
if [ "$CADDY" = 1 ]; then
  say "Caddy"
  NEW=$(mktemp)
  sed -e "s#/opt/gwsignup/static#$PREFIX/static#" -e "s#127.0.0.1:8090#127.0.0.1:$PORT#" "$SRC/caddy/Caddyfile" > "$NEW"
  caddy validate --config "$NEW" --adapter caddyfile >/dev/null 2>&1 || { caddy validate --config "$NEW" --adapter caddyfile; rm -f "$NEW"; die "Caddyfile does not validate"; }
  if [ -f /etc/caddy/Caddyfile ] && ! cmp -s "$NEW" /etc/caddy/Caddyfile; then
    cp -p /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.before-gwsignup-$(date -u +%Y%m%dT%H%M%SZ)"
  fi
  install -o root -g root -m 644 "$NEW" /etc/caddy/Caddyfile; rm -f "$NEW"
  install -d -o caddy -g caddy -m 750 /var/log/caddy
  systemctl enable caddy >/dev/null 2>&1
  if systemctl is-active --quiet caddy; then systemctl reload caddy; else systemctl start caddy; fi
  echo "Caddy serves $ORIGIN -> 127.0.0.1:$PORT (certificates are fetched on first start)"
fi

# ------------------------------------------------------------------ firewall (live)
if [ "$FIREWALL" = 1 ]; then
  if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
    say "ufw: allow 80/tcp and 443/tcp (web)"
    ufw allow 80/tcp comment 'signup website' >/dev/null
    ufw allow 443/tcp comment 'signup website' >/dev/null
  else
    echo "ufw is not active; nothing to open here (the provider firewall must still allow 80 and 443)"
  fi
fi

say "done: $NAME ($MODE) database=$DB role=$ROLE port=$PORT origin=$ORIGIN"
