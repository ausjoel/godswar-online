#!/bin/sh
# pg-cluster.sh DIR start|stop|reset - a private, throwaway PostgreSQL 17 cluster for the mail tests.
# Port 55530, listens on 127.0.0.1 only, trust auth for the local superuser, md5/scram for roles.
# Builds the schema ONLY from the repo's sql/ files plus accounts-stub.sql. Restores nothing from live.
set -eu
export LC_ALL=C LANG=C
DIR=$1; ACT=$2
BIN=${PGBIN:-/opt/homebrew/opt/postgresql@17/bin}
PORT=55530
HERE=$(cd "$(dirname "$0")" && pwd)
SQL=$HERE/../../sql
PSQL="$BIN/psql -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p $PORT -U postgres"
case "$ACT" in
  stop) [ -d "$DIR/data" ] && "$BIN/pg_ctl" -D "$DIR/data" -m fast stop >/dev/null 2>&1 || true; exit 0 ;;
  reset) "$0" "$DIR" stop; rm -rf "$DIR/data"; exec "$0" "$DIR" start ;;
  start) ;;
  *) echo "usage: $0 DIR start|stop|reset" >&2; exit 2 ;;
esac
mkdir -p "$DIR"
if [ ! -d "$DIR/data" ]; then
  "$BIN/initdb" -D "$DIR/data" -U postgres --auth-local=trust --auth-host=scram-sha-256 -E UTF8 --no-locale >/dev/null
  # Superuser over TCP from 127.0.0.1 without a password; everyone else needs a password.
  { echo "host all postgres 127.0.0.1/32 trust"; cat "$DIR/data/pg_hba.conf"; } > "$DIR/data/pg_hba.tmp"
  mv "$DIR/data/pg_hba.tmp" "$DIR/data/pg_hba.conf"
fi
"$BIN/pg_ctl" -D "$DIR/data" -l "$DIR/pg.log" -w -o "-p $PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=''" start >/dev/null
$PSQL -d postgres -c "DROP DATABASE IF EXISTS godswar_mailtest" -c "DROP ROLE IF EXISTS gw_signup_mailtest" \
  -c "DROP ROLE IF EXISTS gw_outsider" -c "DROP ROLE IF EXISTS godswar"
$PSQL -d postgres -c "CREATE ROLE godswar LOGIN" -c "CREATE DATABASE godswar_mailtest OWNER godswar"
$PSQL -d godswar_mailtest -c "SET ROLE godswar" -f "$HERE/accounts-stub.sql"
$PSQL -d godswar_mailtest -v role=gw_signup_mailtest -v owner=godswar -f "$SQL/signup-db.sql" >/dev/null
$PSQL -d godswar_mailtest -v role=gw_signup_mailtest -v owner=godswar -f "$SQL/mail-grants.sql" >/dev/null
$PSQL -d postgres -c "ALTER ROLE gw_signup_mailtest PASSWORD 'mailtest-db-password-1234'" \
  -c "CREATE ROLE gw_outsider LOGIN PASSWORD 'outsider-password-1234'"
echo "cluster up on 127.0.0.1:$PORT, database godswar_mailtest"
