#!/bin/bash
# make-test-env.sh - a private test environment for one module verifier, ON THE VPS (run it there).
#
#   make-test-env.sh <n> <dbname> <login-port> [stock|round2] [up|clone|start|stop|status|reset|destroy|editor <port>|editor-stop <port>]
#
#   <n>          your environment number (a folder ~/gwed/env-<n> holds its files)
#   <dbname>     your database copy, e.g. godswar_ed_inventory (cloned from godswar_editor_base)
#   <login-port> your reborn instance's login port; the game port is <login-port>+1 and the management port
#                127.0.0.1:<login-port>+2 (the editor's --rates-mgmt). Use the ports you were given (10000-65000).
#
#   stock|round2  which server build the instance runs (default: the one this env used last, else stock):
#                 stock  = a copy of the live /opt/godswar251/server (stock reborn + RATES)
#                 round2 = the round-2 build uploaded to ~/gwed/round2-build (live edits of online characters,
#                          GET /admin/capabilities); upload it from the Mac with
#                          rsync -a GodsWar-2.51/Polish/reborn-round2-linux-x64/ vps:gwed/round2-build/
#   up (default)  clone the database if it does not exist yet, then start the instance and wait until it is ready
#   clone         only create <dbname> from godswar_editor_base (realm row -> YOUR_SERVER_IP:<login-port>+1)
#   start / stop  start / stop the second reborn instance (stop records the PID in ~/gwed/env-<n>/pids.log)
#   status        is it running, is it ready
#   reset         stop, drop <dbname>, clone it again (a clean copy)
#   destroy       stop, drop <dbname>, remove ~/gwed/env-<n>
#   editor <p>    start gwadmin-web from THIS tools copy on 127.0.0.1:<p> against <dbname> + this instance
#   editor-stop <p>
#
# Safety: refuses the live database 'godswar' and the base copy, the live ports (6999, 7000, 9090) and the admin's
# editor port 8788; never touches godswar251.service or /opt/godswar251 (it only READS the server files and
# appsettings.json to copy them). The instance runs as you, from a copy of /opt/godswar251/server.
set -euo pipefail

BASE_DB="${GW_EDITOR_BASE:-godswar_editor_base}"
ROOT="${GW_EDITOR_ROOT:-$HOME/gwed}"
PSQL=(sudo -n -u postgres psql -X -q -v ON_ERROR_STOP=1)
HERE="$(cd "$(dirname "$0")" && pwd)"
TOOLS="$(cd "$HERE/../.." && pwd)"          # the tools/ folder this script belongs to

die() { echo "make-test-env: $*" >&2; exit 1; }
[ $# -ge 3 ] || { awk 'NR > 1 && /^#/ { print; next } NR > 1 { exit }' "$0"; exit 2; }
N="$1"; DB="$2"; LOGIN="$3"; shift 3
BUILD=""
case "${1:-}" in stock|round2) BUILD="$1"; shift ;; esac
CMD="${1:-up}"; ARG="${2:-}"
[[ "$N" =~ ^[A-Za-z0-9_-]{1,32}$ ]] || die "bad environment number '$N'"
[[ "$DB" =~ ^[a-z_][a-z0-9_]{0,62}$ ]] || die "bad database name '$DB' (lower-case letters, digits, _)"
[ "$DB" != "godswar" ] || die "refusing the LIVE database 'godswar'"
[ "$DB" != "$BASE_DB" ] || die "refusing to use the base copy '$BASE_DB' itself; give your own database name"
[[ "$LOGIN" =~ ^[0-9]+$ ]] && [ "$LOGIN" -ge 10000 ] && [ "$LOGIN" -le 65000 ] || die "login port must be 10000-65000 (the VPS firewall)"
GAME=$((LOGIN + 1)); MGMT=$((LOGIN + 2))
for p in "$LOGIN" "$GAME" "$MGMT"; do case "$p" in 6999|7000|9090|8788|5432) die "port $p belongs to the live server / the admin's editor" ;; esac; done
ENV="$ROOT/env-$N"
PIDF="$ENV/instance.pid"
[ -n "$BUILD" ] || BUILD="$(cat "$ENV/build" 2>/dev/null || echo stock)"
ROUND2_BUILD="${GW_ROUND2_BUILD:-$ROOT/round2-build}"

db_exists() { [ "$("${PSQL[@]}" -d postgres -Atc "SELECT 1 FROM pg_database WHERE datname = '$1'")" = "1" ]; }
port_busy() { ss -ltnH "sport = :$1" 2>/dev/null | grep -q .; }
running() { [ -f "$PIDF" ] && kill -0 "$(cat "$PIDF")" 2>/dev/null; }
ready() { curl -s -m 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$MGMT/readyz" 2>/dev/null | grep -q '^200$'; }
log() { echo "$(date -u +%FT%TZ) $*" >> "$ENV/pids.log"; }

do_clone() {
  db_exists "$BASE_DB" || die "the base copy $BASE_DB does not exist (tests/README.md, 'Base copy')"
  if db_exists "$DB"; then echo "database $DB exists (use 'reset' for a fresh copy)"; return; fi
  # a template clone is a file copy: fast, and it needs nobody connected to the base
  "${PSQL[@]}" -d postgres -c "CREATE DATABASE \"$DB\" OWNER godswar TEMPLATE \"$BASE_DB\"" >/dev/null \
    || die "clone failed (is someone connected to $BASE_DB?)"
  # the realm row of THIS copy points the client at THIS instance's game port
  "${PSQL[@]}" -d "$DB" -c "UPDATE server SET game_port = $GAME, ip_address = 'YOUR_SERVER_IP' WHERE id = 1" >/dev/null
  echo "cloned $BASE_DB -> $DB (realm row -> YOUR_SERVER_IP:$GAME)"
}

server_copy() {
  if [ "$BUILD" = round2 ]; then
    [ -x "$ROUND2_BUILD/Godswar.Server" ] || die "no round-2 build in $ROUND2_BUILD (upload it first: tests/README.md)"
    rm -rf "$ENV/server"; mkdir -p "$ENV/server"; cp -al "$ROUND2_BUILD/." "$ENV/server/"
    echo "round2" > "$ENV/build"; return
  fi
  echo "stock" > "$ENV/build"
  # one copy of the live build per VPS user, refreshed when /opt/godswar251/server changed; each env hard-links it
  local src=/opt/godswar251/server dst="$ROOT/server" stamp
  stamp=$(sudo -n stat -c '%Y-%s' "$src/Godswar.Server.dll") || die "cannot read $src (sudo)"
  if [ ! -f "$dst/.stamp" ] || [ "$(cat "$dst/.stamp")" != "$stamp" ]; then
    rm -rf "$dst.new"; sudo -n cp -a "$src" "$dst.new"; sudo -n chown -R "$(id -u):$(id -g)" "$dst.new"
    echo "$stamp" > "$dst.new/.stamp"; rm -rf "$dst"; mv "$dst.new" "$dst"
    echo "copied $src -> $dst"
  fi
  rm -rf "$ENV/server"; cp -al "$dst" "$ENV/server"
}

write_settings() {
  # appsettings.json = the live one with this env's ports and database (mode 600: it holds the DB password)
  umask 077
  sudo -n cat /opt/godswar251/appsettings.json | python3 -I -c '
import json, re, sys
cfg = json.load(sys.stdin)
db, login, game, mgmt = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
cfg["login"]["port"] = login
cfg["game"]["port"] = game
cfg["game"]["publicPort"] = game
cfg["operations"]["management"]["port"] = mgmt
cfg["operations"]["management"]["bindHost"] = "127.0.0.1"
cs = cfg["storage"]["postgresConnectionString"]
cfg["storage"]["postgresConnectionString"] = re.sub(r"Database=[^;]*", "Database=" + db, cs)
if "secure" in cfg:
    cfg["secure"]["enabled"] = False
json.dump(cfg, sys.stdout, indent=2)
' "$DB" "$LOGIN" "$GAME" "$MGMT" > "$ENV/appsettings.json"
  grep -q "Database=$DB;" "$ENV/appsettings.json" || die "could not set the database in appsettings.json"
}

do_start() {
  mkdir -p "$ENV"
  if running; then echo "instance already running (pid $(cat "$PIDF"))"; return; fi
  db_exists "$DB" || die "database $DB does not exist (run 'clone' or 'up')"
  for p in "$LOGIN" "$GAME" "$MGMT"; do port_busy "$p" && die "port $p is in use"; done
  server_copy
  write_settings
  cd "$ENV/server"
  # the same environment as godswar251.service (+ its gm.conf drop-in)
  setsid nohup env GODSWAR_RUNTIME_PROFILE=LocalDevelopment GODSWAR_LOCAL_GAMEPLAY_LOGS=true \
    GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION=true GODSWAR_DEVELOPER_COMMANDS_ENABLED=true GODSWAR_DEVELOPER_ACCOUNT_IDS=3 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
    "$ENV/server/Godswar.Server" "$ENV/appsettings.json" >> "$ENV/server.log" 2>&1 < /dev/null &
  echo $! > "$PIDF"
  log "started pid $(cat "$PIDF") build=$BUILD db=$DB login=$LOGIN game=$GAME mgmt=127.0.0.1:$MGMT"
  echo -n "instance pid $(cat "$PIDF") starting"
  for _ in $(seq 1 120); do
    if ready; then echo " - ready (login $LOGIN, game $GAME, management 127.0.0.1:$MGMT, log $ENV/server.log)"; return; fi
    running || { echo; tail -n 30 "$ENV/server.log"; die "the instance exited during start-up (log above)"; }
    echo -n "."; sleep 1
  done
  echo; die "not ready after 120 s (tail $ENV/server.log)"
}

do_stop() {
  if ! running; then echo "instance not running"; rm -f "$PIDF"; return; fi
  local pid; pid=$(cat "$PIDF")
  kill -TERM "$pid"
  for _ in $(seq 1 30); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
  if kill -0 "$pid" 2>/dev/null; then kill -KILL "$pid"; log "stopped pid $pid (KILL after 30 s)"; else log "stopped pid $pid"; fi
  rm -f "$PIDF"
  echo "instance pid $pid stopped (recorded in $ENV/pids.log)"
}

do_status() {
  echo "env $ENV  build $BUILD  db $DB ($(db_exists "$DB" && echo exists || echo missing))  ports login $LOGIN game $GAME mgmt 127.0.0.1:$MGMT"
  if running; then echo "instance pid $(cat "$PIDF") running, $(ready && echo ready || echo 'not ready')"; else echo "instance not running"; fi
  for f in "$ENV"/editor-*.pid; do [ -f "$f" ] && echo "editor $(basename "$f" .pid | cut -d- -f2) pid $(cat "$f") $(kill -0 "$(cat "$f")" 2>/dev/null && echo running || echo gone)"; done
  return 0
}

do_editor() {
  local port="$1"
  [[ "$port" =~ ^[0-9]+$ ]] && [ "$port" -ge 10000 ] && [ "$port" -le 65000 ] && [ "$port" != 8788 ] || die "editor port must be 10000-65000 and not 8788"
  mkdir -p "$ENV/backups"
  port_busy "$port" && die "port $port is in use"
  cd "$TOOLS"
  setsid nohup python3 -I gwadmin-web/server.py --db-suffix _test --reborn-only --port "$port" --reborn-db "$DB" \
    --rates-mgmt "$MGMT" --reborn-log "$ENV/server.log" --backup-dir "$ENV/backups" > "$ENV/editor-$port.log" 2>&1 < /dev/null &
  echo $! > "$ENV/editor-$port.pid"
  log "editor pid $(cat "$ENV/editor-$port.pid") port $port tools $TOOLS"
  for _ in $(seq 1 30); do curl -s -m 2 -o /dev/null "http://127.0.0.1:$port/" && { echo "editor on 127.0.0.1:$port (pid $(cat "$ENV/editor-$port.pid"), log $ENV/editor-$port.log); from the Mac: ssh -N -L $port:127.0.0.1:$port vps"; return; }; sleep 1; done
  tail -n 20 "$ENV/editor-$port.log"; die "the editor did not answer on $port"
}

do_editor_stop() {
  local f="$ENV/editor-$1.pid"
  [ -f "$f" ] || { echo "no editor on $1 for this env"; return; }
  kill -TERM "$(cat "$f")" 2>/dev/null || true
  log "editor pid $(cat "$f") port $1 stopped"; rm -f "$f"; echo "editor on $1 stopped"
}

case "$CMD" in
  up) mkdir -p "$ENV"; do_clone; do_start ;;
  clone) mkdir -p "$ENV"; do_clone ;;
  start) do_start ;;
  stop) do_stop ;;
  status) do_status ;;
  reset) do_stop; db_exists "$DB" && "${PSQL[@]}" -d postgres -c "DROP DATABASE \"$DB\"" >/dev/null; mkdir -p "$ENV"; do_clone ;;
  destroy) do_stop; db_exists "$DB" && "${PSQL[@]}" -d postgres -c "DROP DATABASE \"$DB\"" >/dev/null; rm -rf "$ENV"; echo "destroyed $ENV and $DB" ;;
  editor) do_editor "$ARG" ;;
  editor-stop) do_editor_stop "$ARG" ;;
  *) die "unknown command '$CMD'" ;;
esac
