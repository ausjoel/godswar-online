#!/usr/bin/env bash
# GodsWar one-click setup: installs what is missing, builds and starts the server, loads the game
# database (no players), creates your administrator account and, on a VPS, the sign-up website
# behind HTTPS. Safe to run again: secrets, the database and your answers are kept.
#
#   sudo ./setup.sh --vps        internet-facing server (Ubuntu 22.04+/Debian 12+, root or sudo)
#   sudo ./setup.sh --local      this computer only (Debian/Ubuntu; on macOS without sudo, via Homebrew)
#   ./setup.sh --docker          Docker Compose instead (any OS with Docker; see docs/INSTALL-LOCAL.md)
#   ./setup.sh                   asks which one
#
# Answers (flags win over the config file, which wins over the last run's answers):
#   --config FILE            read answers from FILE (see config/godswar.env.example)
#   --ip ADDRESS             address players connect to (VPS: detected public IP; local: 127.0.0.1)
#   --domain NAME            domain for the HTTPS sign-up site (VPS); --no-domain = <ip>.sslip.io name
#   --server-name NAME       name shown on the sign-up site            (default GodsWar)
#   --admin NAME             administrator account                     (default admin)
#   --login-port N --game-port N --management-port N   (defaults 6999 7000 9090)
#   --edition reborn|classic rule set (docs/EDITIONS.md)               (default reborn)
#   --signup / --no-signup   sign-up website                           (default: yes on a VPS)
#   --editor / --no-editor   admin web editor on 127.0.0.1:8787        (default no)
#   --resend-key-file FILE   Resend API key for sign-up mail (file holding the key; never on the command line)
#   --mail-from ADDRESS      sender, e.g. "GodsWar <noreply@example.com>"   --no-mail = mail off
#   --firewall / --no-firewall   open the needed ports in ufw          (default: yes on a VPS)
#   --prefix DIR             install folder (Linux)                    (default /opt/godswar)
#   --skip-build             reuse the installed server build
#   --reset-admin-password   give the administrator a new random password
#   -y, --yes                accept defaults, ask nothing (also automatic without a terminal)
#   --dry-run                print what would be done, change nothing
set -euo pipefail
umask 022
REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
cd "$REPO"

# ------------------------------------------------------------------------------------- options
MODE=""; CONFIG=""; YES=0; DRY=0; SKIP_BUILD=0; RESET_ADMIN=0
usage() { sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'; }
need_arg() { [ $# -ge 2 ] && [ -n "$2" ] || { echo "setup.sh: $1 needs a value" >&2; exit 2; }; }
while [ $# -gt 0 ]; do
  case "$1" in
    --vps) MODE=vps ;; --local) MODE=local ;; --docker) MODE=docker ;;
    --config) need_arg "$@"; CONFIG=$2; shift ;;
    --ip|--host) need_arg "$@"; GODSWAR_HOST=$2; shift ;;
    --domain) need_arg "$@"; GODSWAR_DOMAIN=$2; shift ;;
    --no-domain) GODSWAR_DOMAIN=none ;;
    --server-name) need_arg "$@"; GODSWAR_SERVER_NAME=$2; shift ;;
    --admin) need_arg "$@"; GODSWAR_ADMIN_USER=$2; shift ;;
    --login-port) need_arg "$@"; GODSWAR_LOGIN_PORT=$2; shift ;;
    --game-port) need_arg "$@"; GODSWAR_GAME_PORT=$2; shift ;;
    --management-port) need_arg "$@"; GODSWAR_MANAGEMENT_PORT=$2; shift ;;
    --edition) need_arg "$@"; GODSWAR_EDITION=$2; shift ;;
    --signup) GODSWAR_SIGNUP=yes ;; --no-signup) GODSWAR_SIGNUP=no ;;
    --editor) GODSWAR_EDITOR=yes ;; --no-editor) GODSWAR_EDITOR=no ;;
    --resend-key-file) need_arg "$@"; GODSWAR_RESEND_API_KEY_FILE=$2; shift ;;
    --mail-from) need_arg "$@"; GODSWAR_MAIL_FROM=$2; shift ;;
    --no-mail) GODSWAR_MAIL_FROM=none ;;
    --firewall) GODSWAR_FIREWALL=yes ;; --no-firewall) GODSWAR_FIREWALL=no ;;
    --prefix) need_arg "$@"; GODSWAR_PREFIX=$2; shift ;;
    --skip-build) SKIP_BUILD=1 ;;
    --reset-admin-password) RESET_ADMIN=1 ;;
    -y|--yes|--non-interactive) YES=1 ;;
    --dry-run) DRY=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "setup.sh: unknown option $1 (see --help)" >&2; exit 2 ;;
  esac
  shift
done
INTERACTIVE=1
{ [ "$YES" = 1 ] || ! [ -t 0 ] || ! [ -r /dev/tty ]; } && INTERACTIVE=0

# ------------------------------------------------------------------------------------- helpers
bold=""; red=""; off=""
if [ -t 1 ]; then bold=$'\033[1m'; red=$'\033[31m'; off=$'\033[0m'; fi
say()  { printf '\n%s==> %s%s\n' "$bold" "$*" "$off"; }
note() { printf '    %s\n' "$*"; }
die()  { printf '\n%sERROR:%s %s\n' "$red" "$off" "$*" >&2; exit 1; }
# run CMD...: executes, or only prints it in --dry-run
run()  { if [ "$DRY" = 1 ]; then printf '    [dry-run] %s\n' "$*"; else "$@"; fi; }
# write_file PATH MODE OWNER:GROUP  < content   (atomic; in --dry-run only reports)
write_file() {
  local path=$1 mode=$2 owner=${3:-} tmp
  if [ "$DRY" = 1 ]; then cat >/dev/null; printf '    [dry-run] write %s (mode %s%s)\n' "$path" "$mode" "${owner:+, $owner}"; return; fi
  tmp=$(mktemp "$(dirname "$path")/.setup.XXXXXX")
  cat > "$tmp"; chmod "$mode" "$tmp"
  [ -z "$owner" ] || chown "$owner" "$tmp"
  mv -f "$tmp" "$path"
}
# ask VAR "question" default  -> sets VAR unless it already has a value
ask() {
  local var=$1 q=$2 def=${3:-} a=""
  [ -n "${!var:-}" ] && return 0
  if [ "$INTERACTIVE" = 1 ]; then read -r -p "    $q${def:+ [$def]}: " a </dev/tty || a=""; fi
  printf -v "$var" '%s' "${a:-$def}"
}
ask_yn() {  # ask_yn VAR "question" default(yes|no)
  local var=$1
  ask "$@"
  case "${!var}" in y|Y|yes|YES|Yes|true|1) printf -v "$var" yes ;; *) printf -v "$var" no ;; esac
}
randpw() { python3 -I -c 'import secrets,string,sys; a=string.ascii_letters+string.digits; print("".join(secrets.choice(a) for _ in range(int(sys.argv[1]))))' "$1"; }
valid_port() { [[ $1 =~ ^[0-9]{1,5}$ ]] && [ "$1" -ge 1 ] && [ "$1" -le 65535 ]; }
valid_host() { [[ $1 =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]]; }
is_ipv4()    { [[ $1 =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; }

# load_env FILE: KEY=VALUE lines (no shell code is run); only fills variables that are still empty
CONF_KEYS="GODSWAR_MODE GODSWAR_HOST GODSWAR_DOMAIN GODSWAR_SERVER_NAME GODSWAR_ADMIN_USER GODSWAR_ADMIN_PASSWORD
 GODSWAR_LOGIN_PORT GODSWAR_GAME_PORT GODSWAR_MANAGEMENT_PORT GODSWAR_EDITION GODSWAR_ALLOW_REGISTRATION GODSWAR_SIGNUP
 GODSWAR_EDITOR GODSWAR_RESEND_API_KEY_FILE GODSWAR_MAIL_FROM GODSWAR_FIREWALL GODSWAR_PREFIX GODSWAR_PG_PORT
 GODSWAR_DOWNLOAD_URL POSTGRES_DB POSTGRES_USER POSTGRES_PASSWORD"
load_env() {
  local f=$1 k v
  [ -f "$f" ] || return 0
  while IFS= read -r line || [ -n "$line" ]; do
    [[ $line =~ ^[[:space:]]*([A-Z_][A-Z0-9_]*)=(.*)$ ]] || continue
    k=${BASH_REMATCH[1]}; v=${BASH_REMATCH[2]}
    v=${v%$'\r'}; v=${v#\"}; v=${v%\"}; v=${v#\'}; v=${v%\'}
    case " $CONF_KEYS " in *" $k "*) ;; *) continue ;; esac
    [ -n "${!k:-}" ] || printf -v "$k" '%s' "$v"
  done < "$f"
}

# selftest: headless login to the character screen. Uses the players' address first (the game server ties
# the session to the address the login came from), then 127.0.0.1.
selftest() {
  local h
  for h in "$GODSWAR_HOST" 127.0.0.1; do
    python3 -I "$REPO/signup/test/game_login.py" "$GODSWAR_ADMIN_USER" "$1" "$h:$GODSWAR_LOGIN_PORT" >/dev/null 2>&1 && return 0
  done
  return 1
}

# ------------------------------------------------------------------------------------- answers
if [ "$DRY" = 1 ]; then say "GodsWar setup (dry run: nothing is changed)"; else say "GodsWar setup"; fi
[ -z "$CONFIG" ] || { [ -f "$CONFIG" ] || die "config file $CONFIG not found"; load_env "$CONFIG"; }
[ -n "$MODE" ] || MODE=${GODSWAR_MODE:-}
if [ -z "$MODE" ]; then
  if [ "$INTERACTIVE" = 1 ]; then
    note "1) A VPS / server other people will join (Ubuntu/Debian, installs everything, HTTPS sign-up site)"
    note "2) This computer only (testing, or friends on your home network)"
    note "3) Docker Compose (any OS with Docker)"
    c=""; ask c "Choose 1, 2 or 3" 2
    case "$c" in 1) MODE=vps ;; 3) MODE=docker ;; *) MODE=local ;; esac
  else
    die "say --vps, --local or --docker (or GODSWAR_MODE in the config file)"
  fi
fi
case "$MODE" in vps|local|docker) ;; *) die "mode must be vps, local or docker" ;; esac

OS=$(uname -s)
if [ "$MODE" = docker ]; then PLATFORM=docker
elif [ "$OS" = Darwin ] && [ "$MODE" = local ]; then PLATFORM=macos
elif [ "$OS" = Linux ] || [ "$DRY" = 1 ]; then PLATFORM=linux
else die "--vps needs Ubuntu or Debian. On macOS use --local (Homebrew) or --docker."; fi

PREFIX=${GODSWAR_PREFIX:-/opt/godswar}
if [ "$PLATFORM" = macos ]; then
  ETC="$REPO/local/secrets"; STATE="$REPO/local/setup-state.env"; PREFIX="$REPO/local"
else
  ETC=/etc/godswar; STATE=$ETC/setup.env
fi
[ "$PLATFORM" = docker ] || load_env "$STATE"     # last run's answers (re-runs keep them)

if [ "$PLATFORM" = linux ] && [ "$DRY" = 0 ]; then
  [ "$(id -u)" = 0 ] || die "run as root: sudo ./setup.sh --$MODE"
  command -v apt-get >/dev/null || die "this script supports Debian and Ubuntu (apt). Use --docker elsewhere."
fi
if [ "$PLATFORM" = macos ] && [ "$DRY" = 0 ]; then
  [ "$(id -u)" != 0 ] || die "on macOS run it as yourself, not with sudo (Homebrew refuses root)"
  command -v brew >/dev/null || die "install Homebrew first (https://brew.sh), or use ./setup.sh --docker"
fi

detect_ip() {
  local ip=""
  if command -v curl >/dev/null; then ip=$(curl -fsS -m 6 https://api.ipify.org 2>/dev/null || true); fi
  if ! is_ipv4 "$ip" && command -v ip >/dev/null; then
    ip=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for(i=1;i<NF;i++) if($i=="src"){print $(i+1); exit}}')
  fi
  is_ipv4 "$ip" && echo "$ip"
}

say "Settings"
if [ -z "${GODSWAR_HOST:-}" ]; then
  if [ "$MODE" = vps ]; then guess=$(detect_ip || true); note "detected public IP: ${guess:-none}"
  else guess=127.0.0.1; fi
  ask GODSWAR_HOST "Address players connect to (IP; a LAN IP lets your network join)" "$guess"
fi
valid_host "${GODSWAR_HOST:-}" || die "invalid address '${GODSWAR_HOST:-}'"
is_ipv4 "$GODSWAR_HOST" || note "warning: $GODSWAR_HOST is not an IPv4 address; the 2.51 client expects an IP"
ask GODSWAR_SERVER_NAME "Server name (shown on the sign-up site)" GodsWar
[[ $GODSWAR_SERVER_NAME =~ ^[A-Za-z0-9][A-Za-z0-9\ ._-]{0,31}$ ]] || die "server name: letters, digits, space . _ - (max 32)"
ask GODSWAR_ADMIN_USER "Administrator account name" admin
[[ $GODSWAR_ADMIN_USER =~ ^[a-z0-9]{4,16}$ ]] || die "admin name: 4-16 lowercase letters/digits (the client cannot log in with capitals)"
ask GODSWAR_LOGIN_PORT "Login port" 6999
ask GODSWAR_GAME_PORT "Game port" 7000
ask GODSWAR_MANAGEMENT_PORT "Management port (127.0.0.1 only)" 9090
for p in "$GODSWAR_LOGIN_PORT" "$GODSWAR_GAME_PORT" "$GODSWAR_MANAGEMENT_PORT"; do valid_port "$p" || die "invalid port $p"; done
[ "$GODSWAR_LOGIN_PORT" != "$GODSWAR_GAME_PORT" ] && [ "$GODSWAR_MANAGEMENT_PORT" != "$GODSWAR_LOGIN_PORT" ] \
  && [ "$GODSWAR_MANAGEMENT_PORT" != "$GODSWAR_GAME_PORT" ] || die "the three ports must differ"
ask GODSWAR_EDITION "Edition: reborn (raised limits) or classic (original limits)" reborn
case "$GODSWAR_EDITION" in reborn|classic) ;; *) die "edition must be reborn or classic" ;; esac
: "${POSTGRES_DB:=godswar}" "${POSTGRES_USER:=godswar}"
[[ $POSTGRES_DB =~ ^[a-z][a-z0-9_]{0,62}$ && $POSTGRES_USER =~ ^[a-z][a-z0-9_]{0,62}$ ]] || die "bad POSTGRES_DB/POSTGRES_USER"
: "${GODSWAR_ALLOW_REGISTRATION:=false}"
case "$GODSWAR_ALLOW_REGISTRATION" in true|false) ;; *) die "GODSWAR_ALLOW_REGISTRATION must be true or false" ;; esac
if [ -n "${POSTGRES_PASSWORD:-}" ]; then
  [[ $POSTGRES_PASSWORD =~ ^[A-Za-z0-9_-]{12,}$ ]] || die "POSTGRES_PASSWORD: 12+ letters, digits, - or _ (or leave it empty: a random one is made)"
fi

if [ "$PLATFORM" = docker ]; then :; else
  if [ "$PLATFORM" = linux ]; then
    ask_yn GODSWAR_SIGNUP "Install the sign-up website (y/n)" "$([ "$MODE" = vps ] && echo yes || echo no)"
  else GODSWAR_SIGNUP=no; fi
  SITE=""; ORIGIN=""
  if [ "$GODSWAR_SIGNUP" = yes ]; then
    if [ "$MODE" = vps ]; then
      ask GODSWAR_DOMAIN "Domain for the HTTPS site (blank = ${GODSWAR_HOST//./-}.sslip.io, needs no DNS)" ""
      case "${GODSWAR_DOMAIN:-}" in ""|none) SITE="${GODSWAR_HOST//./-}.sslip.io"; GODSWAR_DOMAIN=none ;; *) SITE=$GODSWAR_DOMAIN ;; esac
      valid_host "$SITE" || die "invalid domain $SITE"
      ORIGIN="https://$SITE"
    else
      ORIGIN="http://127.0.0.1:8090"
    fi
    # mail (optional)
    if [ -z "${GODSWAR_MAIL_FROM:-}" ] && [ "$INTERACTIVE" = 1 ]; then
      note "Sign-up mail (email confirmation, password reset) uses Resend (resend.com). Leave blank to skip."
      ask GODSWAR_MAIL_FROM "Sender address, e.g. GodsWar <noreply@your.domain> (blank = mail off)" none
    fi
    : "${GODSWAR_MAIL_FROM:=none}"
    if [ "$GODSWAR_MAIL_FROM" != none ]; then
      [[ $GODSWAR_MAIL_FROM == *@* && $GODSWAR_MAIL_FROM != *[$'\n\r"\\']* ]] || die "invalid sender address"
    fi
  fi
  ask_yn GODSWAR_EDITOR "Install the admin web editor (127.0.0.1:8787, reach it through an SSH tunnel) (y/n)" no
  [ "$PLATFORM" = linux ] || GODSWAR_EDITOR=no
  if [ "$PLATFORM" = linux ]; then
    ask_yn GODSWAR_FIREWALL "Open the game ports$([ "$GODSWAR_SIGNUP" = yes ] && [ "$MODE" = vps ] && echo ' and 80/443') in ufw (y/n)" "$([ "$MODE" = vps ] && echo yes || echo no)"
  fi
fi

# A Resend key is read from a file or typed (hidden), never taken from the command line.
RESEND_KEY=""
if [ "${GODSWAR_SIGNUP:-no}" = yes ] && [ "${GODSWAR_MAIL_FROM:-none}" != none ]; then
  if [ -n "${GODSWAR_RESEND_API_KEY_FILE:-}" ]; then
    [ -r "$GODSWAR_RESEND_API_KEY_FILE" ] || die "cannot read $GODSWAR_RESEND_API_KEY_FILE"
    RESEND_KEY=$(tr -d '[:space:]' < "$GODSWAR_RESEND_API_KEY_FILE")
  elif [ "$INTERACTIVE" = 1 ] && [ "$DRY" = 0 ]; then
    read -r -s -p "    Resend API key (hidden; blank = keep the stored key or mail off): " RESEND_KEY </dev/tty || RESEND_KEY=""; echo
  fi
  [ -z "$RESEND_KEY" ] || [[ $RESEND_KEY =~ ^re_[A-Za-z0-9_]{10,}$ ]] || die "that does not look like a Resend API key (re_...)"
fi

cat <<EOF

    mode            $MODE ($PLATFORM)
    players connect $GODSWAR_HOST  login $GODSWAR_LOGIN_PORT  game $GODSWAR_GAME_PORT
    administrator   $GODSWAR_ADMIN_USER   edition $GODSWAR_EDITION   database $POSTGRES_DB (user $POSTGRES_USER)
EOF
if [ "$PLATFORM" != docker ]; then
  note "sign-up site     ${GODSWAR_SIGNUP:-no}${ORIGIN:+  ($ORIGIN)}   mail: ${GODSWAR_MAIL_FROM:-none}"
  note "editor           ${GODSWAR_EDITOR:-no}    firewall: ${GODSWAR_FIREWALL:-no}    install folder: $PREFIX"
fi
if [ "$INTERACTIVE" = 1 ] && [ "$DRY" = 0 ]; then
  go=""; ask go "Go ahead? (y/n)" y
  case "$go" in y|Y|yes) ;; *) die "stopped; nothing was changed" ;; esac
fi

# ======================================================================================= DOCKER
if [ "$PLATFORM" = docker ]; then
  if [ "$DRY" = 1 ] && ! docker info >/dev/null 2>&1; then note "[dry-run] (Docker is not available here; a real run needs it)"; else
  { command -v docker >/dev/null && docker compose version >/dev/null 2>&1; } || \
    die "Docker with the compose plugin is required (Docker Desktop, Colima, or https://docs.docker.com/engine/install/)."
  docker info >/dev/null 2>&1 || die "Docker is installed but not running (start Docker Desktop / Colima / the docker service)."
  fi
  command -v python3 >/dev/null || die "python3 is required"
  COMPOSE=(docker compose --env-file config/godswar.env)
  say "config/godswar.env"
  if [ -f config/godswar.env ]; then note "exists; keeping it (delete it to start over)"
  else
    run cp config/godswar.env.example config/godswar.env
    if [ "$DRY" = 0 ]; then
      : "${POSTGRES_PASSWORD:=$(randpw 28)}"; : "${GODSWAR_ADMIN_PASSWORD:=$(randpw 16)}"
      python3 -I - config/godswar.env <<PY
import re, sys
p = sys.argv[1]; t = open(p).read()
vals = {"GODSWAR_MODE": "docker", "GODSWAR_HOST": "$GODSWAR_HOST", "GODSWAR_ADMIN_USER": "$GODSWAR_ADMIN_USER",
        "POSTGRES_PASSWORD": "$POSTGRES_PASSWORD", "GODSWAR_ADMIN_PASSWORD": "$GODSWAR_ADMIN_PASSWORD",
        "GODSWAR_EDITION": "$GODSWAR_EDITION", "GODSWAR_LOGIN_PORT": "$GODSWAR_LOGIN_PORT", "GODSWAR_GAME_PORT": "$GODSWAR_GAME_PORT"}
for k, v in vals.items():
    t = re.sub(rf"^{k}=.*$", lambda m: f"{k}={v}", t, flags=re.M)
open(p, "w").write(t)
PY
      chmod 600 config/godswar.env
    fi
  fi
  [ "$DRY" = 1 ] && { note "[dry-run] docker compose up -d postgres; load baseline; up -d --build server; admin; self-test"; exit 0; }
  GODSWAR_ADMIN_PASSWORD=""; load_env config/godswar.env
  PGU=$(sed -n 's/^POSTGRES_USER=//p' config/godswar.env); PGD=$(sed -n 's/^POSTGRES_DB=//p' config/godswar.env)
  say "Starting the database"
  "${COMPOSE[@]}" up -d postgres
  for _ in $(seq 1 60); do case "$("${COMPOSE[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null)" in *"postgres healthy"*) break ;; esac; sleep 2; done
  dpsql() { "${COMPOSE[@]}" exec -T postgres psql -X -U "$PGU" -d "$PGD" -v ON_ERROR_STOP=1 -q "$@"; }
  if [ "$(echo "SELECT to_regclass('public.schema_migrations') IS NOT NULL;" | dpsql -t -A | tr -d '[:space:]')" != t ]; then
    say "Loading the game database (items, monsters, maps, quests; no players)"
    gunzip -c server/database/baseline.sql.gz | dpsql >/dev/null
  else note "database already loaded; keeping it"; fi
  say "Realm address and administrator account"
  dpsql -v ip="$GODSWAR_HOST" -v port="$GODSWAR_GAME_PORT" <<<"UPDATE server SET ip_address = :'ip', game_port = :'port'::int;"
  if [ -z "$(echo "SELECT id FROM accounts WHERE username='$GODSWAR_ADMIN_USER';" | dpsql -t -A)" ] || [ "$RESET_ADMIN" = 1 ]; then
    printf '%s' "$GODSWAR_ADMIN_PASSWORD" | python3 -I tools/make-admin.py "$GODSWAR_ADMIN_USER" - | dpsql
  fi
  ADMIN_ID=$(echo "SELECT id FROM accounts WHERE username='$GODSWAR_ADMIN_USER';" | dpsql -t -A | tr -d '[:space:]')
  sed -i.bak -e "s/^GODSWAR_DEVELOPER_COMMANDS_ENABLED=.*/GODSWAR_DEVELOPER_COMMANDS_ENABLED=true/" \
    -e "s/^GODSWAR_DEVELOPER_ACCOUNT_IDS=.*/GODSWAR_DEVELOPER_ACCOUNT_IDS=$ADMIN_ID/" config/godswar.env && rm -f config/godswar.env.bak
  say "Building and starting the server (the first build downloads the .NET SDK image; allow several minutes)"
  "${COMPOSE[@]}" up -d --build server
  st=""
  for _ in $(seq 1 90); do st=$("${COMPOSE[@]}" ps --format '{{.Service}} {{.Health}}' 2>/dev/null | tr '\n' ' '); case "$st" in *"server healthy"*) break ;; esac; sleep 4; done
  case "$st" in *"server healthy"*) note "server is healthy" ;; *) die "server did not become healthy: ${COMPOSE[*]} logs server" ;; esac
  if selftest "$GODSWAR_ADMIN_PASSWORD"; then
    note "login self-test passed (reached the character screen)"; else note "WARNING: login self-test failed: ${COMPOSE[*]} logs server"; fi
  cat <<DONE

GodsWar is running (Docker).
  Players connect to  $GODSWAR_HOST  login port $GODSWAR_LOGIN_PORT (game port $GODSWAR_GAME_PORT)
  Administrator       $GODSWAR_ADMIN_USER  (password: GODSWAR_ADMIN_PASSWORD in config/godswar.env)
  Logs / stop / start ${COMPOSE[*]} logs -f server | stop | up -d
DONE
  exit 0
fi

# ======================================================================================= NATIVE
ARCH=$(uname -m)
case "$PLATFORM-$ARCH" in
  linux-x86_64) RID=linux-x64 ;; linux-aarch64|linux-arm64) RID=linux-arm64 ;;
  macos-arm64) RID=osx-arm64 ;; macos-x86_64) RID=osx-x64 ;;
  *) die "unsupported CPU $ARCH" ;;
esac
HAS_SYSTEMD=0; { [ -d /run/systemd/system ] || [ "$DRY" = 1 ]; } && HAS_SYSTEMD=1
SVC_USER=godswar
DOTNET_DIR=$PREFIX/dotnet
SERVER_DIR=$PREFIX/server

# ---- 1. packages ----------------------------------------------------------------------------
apt_has() { [ "$(apt-cache policy "$1" 2>/dev/null | awk '/Candidate:/{print $2}')" ] && [ "$(apt-cache policy "$1" | awk '/Candidate:/{print $2}')" != "(none)" ]; }
apt_install() { run env DEBIAN_FRONTEND=noninteractive apt-get install -y -q --no-install-recommends "$@"; }
if [ "$PLATFORM" = linux ]; then
  say "Installing packages (apt)"
  run apt-get update -q
  pkgs=(ca-certificates curl gnupg lsb-release openssl python3 tar gzip)
  [ "$GODSWAR_SIGNUP" = yes ] && pkgs+=(python3-jinja2 python3-psycopg2)
  [ "$GODSWAR_FIREWALL" = yes ] && pkgs+=(ufw)
  apt_install "${pkgs[@]}"
  if [ "$DRY" = 0 ]; then
    icu=$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -1 || true)
    [ -z "$icu" ] || apt_install "$icu"           # .NET globalization
  else note "[dry-run] install libicuNN (.NET globalization)"; fi
  say "PostgreSQL 17"
  if [ "$DRY" = 1 ] || ! apt_has postgresql-17; then
    note "postgresql-17 is not in this release's archive: adding the PostgreSQL project's apt repository (apt.postgresql.org)"
    apt_install postgresql-common
    run /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
  fi
  apt_install postgresql-17 postgresql-client-17
  if [ "$DRY" = 0 ]; then
    pg_lsclusters -h 2>/dev/null | awk '$1=="17" && $2=="main"' | grep -q . || run pg_createcluster 17 main
    if [ "$HAS_SYSTEMD" = 1 ]; then run systemctl enable --now postgresql >/dev/null 2>&1 || true; fi
    pg_lsclusters -h | awk '$1=="17" && $2=="main" {print $4}' | grep -q online || run pg_ctlcluster 17 main start
    PG_PORT=${GODSWAR_PG_PORT:-$(pg_lsclusters -h | awk '$1=="17" && $2=="main" {print $3}')}
  else PG_PORT=${GODSWAR_PG_PORT:-5432}; fi
  note "PostgreSQL 17 on port $PG_PORT"
  pg_super() { runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -p "$PG_PORT" "$@"; }
else
  say "Installing packages (Homebrew)"
  if [ "$DRY" = 0 ]; then
    brew list postgresql@17 >/dev/null 2>&1 || run brew install postgresql@17
    command -v python3 >/dev/null || run brew install python
  else note "[dry-run] brew install postgresql@17 (and python if missing)"; fi
  PGBIN=$(brew --prefix 2>/dev/null || echo /opt/homebrew)/opt/postgresql@17/bin
  PG_PORT=${GODSWAR_PG_PORT:-5432}
  if [ "$DRY" = 0 ]; then
    "$PGBIN/pg_isready" -q -h 127.0.0.1 -p "$PG_PORT" || run brew services start postgresql@17
    for _ in $(seq 1 30); do "$PGBIN/pg_isready" -q -h 127.0.0.1 -p "$PG_PORT" && break; sleep 1; done
    v=$("$PGBIN/psql" -X -h 127.0.0.1 -p "$PG_PORT" -d postgres -Atc 'show server_version_num' 2>/dev/null || true)
    [ "${v:-0}" -ge 170000 ] 2>/dev/null || die "PostgreSQL on port $PG_PORT is not version 17 (or not reachable as $(id -un)). Set GODSWAR_PG_PORT to your 17 server's port."
  fi
  pg_super() { "$PGBIN/psql" -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p "$PG_PORT" -d postgres "$@"; }
  export PATH="$PGBIN:$PATH"
fi

# ---- 2. .NET SDK and server build ----------------------------------------------------------
say ".NET SDK and server build"
DOTNET=""
if [ -x "$DOTNET_DIR/dotnet" ] && "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then DOTNET=$DOTNET_DIR/dotnet
elif [ "$PLATFORM" = macos ] && command -v dotnet >/dev/null && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then DOTNET=$(command -v dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ] && [ "$PLATFORM" = macos ] && "$HOME/.dotnet/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then DOTNET=$HOME/.dotnet/dotnet
fi
if [ -z "$DOTNET" ]; then
  note "installing the .NET SDK named in server/global.json into $DOTNET_DIR (Microsoft's dotnet-install.sh)"
  if [ "$DRY" = 0 ]; then
    mkdir -p "$DOTNET_DIR"; inst=$(mktemp)
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$inst"
    sdkver=$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "$REPO/server/global.json")
    bash "$inst" --version "$sdkver" --install-dir "$DOTNET_DIR" --no-path
    rm -f "$inst"
  else note "[dry-run] curl https://dot.net/v1/dotnet-install.sh | bash -s -- --version <server/global.json sdk.version> --install-dir $DOTNET_DIR"; fi
  DOTNET=$DOTNET_DIR/dotnet
fi
DOTNET_ROOT=$(dirname "$DOTNET")
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_ROOT
if [ "$SKIP_BUILD" = 1 ] && [ -x "$SERVER_DIR/Godswar.Server" ]; then
  note "--skip-build: keeping $SERVER_DIR"
else
  note "building (self-contained $RID; the first build downloads packages and takes a few minutes)"
  BUILD=$PREFIX/build
  run rm -rf "$BUILD" "$SERVER_DIR.new"
  run mkdir -p "$BUILD"
  if [ "$DRY" = 0 ]; then
    tar -C "$REPO/server" --exclude=bin --exclude=obj -cf - . | tar -C "$BUILD" -xf -
    (cd "$BUILD" && "$DOTNET" publish src/Godswar.Server/Godswar.Server.csproj -c Release -r "$RID" --self-contained true \
        -o "$SERVER_DIR.new" -nologo -v quiet) || die "the server build failed (see the messages above)"
    [ -x "$SERVER_DIR.new/Godswar.Server" ] || die "build produced no Godswar.Server"
    rm -rf "$SERVER_DIR.prev"; [ -d "$SERVER_DIR" ] && mv "$SERVER_DIR" "$SERVER_DIR.prev"
    mv "$SERVER_DIR.new" "$SERVER_DIR"; rm -rf "$BUILD"
  else note "[dry-run] dotnet publish src/Godswar.Server -c Release -r $RID --self-contained -> $SERVER_DIR (previous kept as server.prev)"; fi
fi

# ---- 3. user, folders, secrets ---------------------------------------------------------------
say "Service user, folders and secrets"
if [ "$PLATFORM" = linux ]; then
  id -u "$SVC_USER" >/dev/null 2>&1 || run useradd --system --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
  # 711: the service user may reach $ETC/service (its two readable files); everything else stays root-only (600)
  run install -d -o root -g root -m 711 "$ETC"
  run install -d -o root -g "$SVC_USER" -m 750 "$ETC/service"
  run install -d -o root -g "$SVC_USER" -m 750 "$PREFIX/config"
  OWN="root:root"
else
  run mkdir -p "$ETC" "$PREFIX/config" "$PREFIX/logs"; run chmod 700 "$ETC"
  OWN=""
fi
CONN=$ETC/postgres.conn; [ "$PLATFORM" = linux ] && CONN=$ETC/service/postgres.conn   # DB connection string (has the password)
# secret FILE GENERATOR...: create once, never overwrite
secret() { local f=$1; shift; if [ -s "$f" ]; then note "keeping $f"; else "$@" | write_file "$f" 600 "$OWN"; [ "$DRY" = 1 ] || note "made $f"; fi; }
if [ -n "${POSTGRES_PASSWORD:-}" ] && [ ! -s "$ETC/db_password" ]; then printf '%s\n' "$POSTGRES_PASSWORD" | write_file "$ETC/db_password" 600 "$OWN"; fi
secret "$ETC/db_password" randpw 32
if [ -n "${GODSWAR_ADMIN_PASSWORD:-}" ] && { [ ! -s "$ETC/admin_password" ] || [ "$RESET_ADMIN" = 1 ]; }; then
  printf '%s\n' "$GODSWAR_ADMIN_PASSWORD" | write_file "$ETC/admin_password" 600 "$OWN"
elif [ "$RESET_ADMIN" = 1 ] && [ "$DRY" = 0 ]; then
  randpw 16 | write_file "$ETC/admin_password" 600 "$OWN"
fi
secret "$ETC/admin_password" randpw 16
# Ed25519 key that signs client updates for your launcher (tools/make-update-key.sh explains its use).
if [ -s "$ETC/update-private.pem" ]; then note "keeping $ETC/update-private.pem"
else
  run openssl genpkey -algorithm ed25519 -out "$ETC/update-private.pem"; run chmod 600 "$ETC/update-private.pem"
fi
if [ "$DRY" = 0 ]; then
  openssl pkey -in "$ETC/update-private.pem" -pubout -outform DER | tail -c 32 | base64 > "$ETC/update-public.b64"
  chmod 644 "$ETC/update-public.b64"
  DBPW=$(tr -d '[:space:]' < "$ETC/db_password"); ADMINPW=$(tr -d '[:space:]' < "$ETC/admin_password")
else DBPW=dry-run; ADMINPW=dry-run; fi
printf 'Host=127.0.0.1;Port=%s;Database=%s;Username=%s;Password=%s;Pooling=true\n' "$PG_PORT" "$POSTGRES_DB" "$POSTGRES_USER" "$DBPW" \
  | write_file "$CONN" "$([ "$PLATFORM" = linux ] && echo 640 || echo 600)" "${OWN:+root:$SVC_USER}"
printf '127.0.0.1:%s:%s:%s:%s\n' "$PG_PORT" "$POSTGRES_DB" "$POSTGRES_USER" "$DBPW" | write_file "$ETC/pgpass" 600 "$OWN"
if [ "$PLATFORM" = linux ] && [ "$GODSWAR_EDITOR" = yes ]; then   # libpq wants its own 600 file, owned by the user
  printf '127.0.0.1:%s:%s:%s:%s\n' "$PG_PORT" "$POSTGRES_DB" "$POSTGRES_USER" "$DBPW" | write_file "$ETC/service/editor.pgpass" 600 "$SVC_USER:$SVC_USER"
fi
export PGPASSFILE=$ETC/pgpass
db_psql() { psql -X -q -v ON_ERROR_STOP=1 -h 127.0.0.1 -p "$PG_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB" "$@"; }

# Save the answers (no secrets) so a re-run asks nothing new.
{
  echo "# Answers from the last ./setup.sh run (no secrets; those are files beside this one)."
  for k in GODSWAR_HOST GODSWAR_DOMAIN GODSWAR_SERVER_NAME GODSWAR_ADMIN_USER GODSWAR_LOGIN_PORT GODSWAR_GAME_PORT \
           GODSWAR_MANAGEMENT_PORT GODSWAR_EDITION GODSWAR_ALLOW_REGISTRATION GODSWAR_SIGNUP GODSWAR_EDITOR GODSWAR_MAIL_FROM \
           GODSWAR_FIREWALL POSTGRES_DB POSTGRES_USER; do printf '%s=%s\n' "$k" "${!k:-}"; done
  printf 'GODSWAR_MODE=%s\nGODSWAR_PREFIX=%s\nGODSWAR_PG_PORT=%s\n' "$MODE" "$PREFIX" "$PG_PORT"
} | write_file "$STATE" 600 "$OWN"

# ---- 4. database ------------------------------------------------------------------------------
say "Database $POSTGRES_DB"
if [ "$DRY" = 0 ]; then
  pg_super -d postgres -v u="$POSTGRES_USER" <<'SQL'
SELECT format('CREATE ROLE %I LOGIN', :'u') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'u') \gexec
SQL
  printf "ALTER ROLE \"%s\" PASSWORD '%s';\n" "$POSTGRES_USER" "$DBPW" | pg_super -d postgres
  pg_super -d postgres -v d="$POSTGRES_DB" -v u="$POSTGRES_USER" <<'SQL'
SELECT format('CREATE DATABASE %I OWNER %I', :'d', :'u') WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = :'d') \gexec
SQL
  if [ "$(db_psql -Atc "SELECT to_regclass('public.schema_migrations') IS NOT NULL")" != t ]; then
    note "loading the game content (items, monsters, maps, quests; no players). The server updates the schema on start."
    gunzip -c "$REPO/server/database/baseline.sql.gz" | db_psql >/dev/null
  else note "already loaded; keeping it (never reloaded over existing data)"; fi
  db_psql -v ip="$GODSWAR_HOST" -v port="$GODSWAR_GAME_PORT" <<<"UPDATE server SET ip_address = :'ip', game_port = :'port'::int;"
  ADMIN_ID=$(db_psql -Atv u="$GODSWAR_ADMIN_USER" <<<"SELECT id FROM accounts WHERE username = :'u';")
  if [ -z "$ADMIN_ID" ] || [ "$RESET_ADMIN" = 1 ]; then
    printf '%s' "$ADMINPW" | python3 -I "$REPO/tools/make-admin.py" "$GODSWAR_ADMIN_USER" - | db_psql
    note "administrator $GODSWAR_ADMIN_USER: password in $ETC/admin_password"
    ADMIN_ID=$(db_psql -Atv u="$GODSWAR_ADMIN_USER" <<<"SELECT id FROM accounts WHERE username = :'u';")
  else note "administrator $GODSWAR_ADMIN_USER exists (id $ADMIN_ID); password unchanged (--reset-admin-password makes a new one)"; fi
  [[ $ADMIN_ID =~ ^[0-9]+$ ]] || die "could not create the administrator account"
  if [ "$GODSWAR_EDITOR" = yes ]; then
    db_psql -v id="$ADMIN_ID" <<<"UPDATE accounts SET editor_admin = true WHERE id = :'id'::int;" 2>/dev/null || note "(no editor_admin column yet; the editor runs without account sign-in)"
  fi
else
  note "[dry-run] create role $POSTGRES_USER + database $POSTGRES_DB, load server/database/baseline.sql.gz if empty,"
  note "[dry-run] UPDATE server SET ip_address='$GODSWAR_HOST', game_port=$GODSWAR_GAME_PORT; create account $GODSWAR_ADMIN_USER"
  ADMIN_ID=1
fi

# ---- 5. server configuration ------------------------------------------------------------------
say "Server configuration"
BIND=0.0.0.0; [ "$GODSWAR_HOST" = 127.0.0.1 ] && BIND=127.0.0.1
# appsettings.json from the repository's, without any secret (the connection string comes from a credential file)
if [ "$DRY" = 0 ]; then
  python3 -I - "$REPO/server/appsettings.json" "$GODSWAR_HOST" "$GODSWAR_LOGIN_PORT" "$GODSWAR_GAME_PORT" "$GODSWAR_MANAGEMENT_PORT" "$BIND" <<'PY' \
    | write_file "$PREFIX/config/appsettings.json" 640 "${OWN:+root:$SVC_USER}"
import json, sys
src, host, lp, gp, mp, bind = sys.argv[1:]
c = json.load(open(src))
c["login"].update(bindHost=bind, port=int(lp))
c["game"].update(bindHost=bind, port=int(gp), publicHost=host, publicPort=int(gp))
c["operations"]["management"].update(enabled=True, bindHost="127.0.0.1", port=int(mp))
c["authentication"]["allowLegacyRawAuthentication"] = True
c["storage"]["postgresConnectionString"] = "Host=127.0.0.1;Database=unused;Username=unused"   # real one: GODSWAR_POSTGRES_CONNECTION_STRING_FILE
print(json.dumps(c, indent=2))
PY
else note "[dry-run] write $PREFIX/config/appsettings.json (ports, public host, no secrets)"; fi
GM_IDS=$ADMIN_ID
cat <<EOF | write_file "$PREFIX/config/server.env" 644 "$OWN"
# Written by setup.sh. Non-secret settings for the GodsWar server; re-run setup.sh to change them.
GODSWAR_RUNTIME_PROFILE=LocalDevelopment
GODSWAR_STORAGE_PROVIDER=postgres
GODSWAR_LOGIN_BIND_HOST=$BIND
GODSWAR_LOGIN_PORT=$GODSWAR_LOGIN_PORT
GODSWAR_GAME_BIND_HOST=$BIND
GODSWAR_GAME_PORT=$GODSWAR_GAME_PORT
GODSWAR_GAME_PUBLIC_HOST=$GODSWAR_HOST
GODSWAR_GAME_PUBLIC_PORT=$GODSWAR_GAME_PORT
GODSWAR_EDITION=$GODSWAR_EDITION
GODSWAR_MONSTER_RUNTIME=Ecs
GODSWAR_PLAYER_RUNTIME=Ecs
GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION=true
GODSWAR_AUTH_ALLOW_REGISTRATION=$GODSWAR_ALLOW_REGISTRATION
GODSWAR_DEVELOPER_COMMANDS_ENABLED=true
GODSWAR_DEVELOPER_ACCOUNT_IDS=$GM_IDS
GODSWAR_MANAGEMENT_ENABLED=true
GODSWAR_MANAGEMENT_BIND_HOST=127.0.0.1
GODSWAR_MANAGEMENT_PORT=$GODSWAR_MANAGEMENT_PORT
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
EOF

wait_ready() {
  [ "$DRY" = 1 ] && { note "[dry-run] wait for $SERVER_DIR/Godswar.Server --management-probe ready $GODSWAR_MANAGEMENT_PORT"; return 0; }
  for i in $(seq 1 90); do
    "$SERVER_DIR/Godswar.Server" --management-probe ready "$GODSWAR_MANAGEMENT_PORT" >/dev/null 2>&1 && { note "server is ready (${i}x2 s)"; return 0; }
    sleep 2
  done
  return 1
}

# ---- 6a. macOS: control script, start ---------------------------------------------------------
if [ "$PLATFORM" = macos ]; then
  cat <<EOF | write_file "$PREFIX/godswar" 755
#!/bin/sh
# GodsWar server control (written by setup.sh):  local/godswar start|stop|restart|status|logs
cd "$SERVER_DIR" || exit 1
PID="$PREFIX/godswar.pid" LOG="$PREFIX/logs/server.log"
running() { [ -f "\$PID" ] && kill -0 "\$(cat "\$PID")" 2>/dev/null; }
start() {
  running && { echo "already running"; return 0; }
  set -a; . "$PREFIX/config/server.env"; set +a
  GODSWAR_POSTGRES_CONNECTION_STRING_FILE="$CONN" nohup "$SERVER_DIR/Godswar.Server" "$PREFIX/config/appsettings.json" >>"\$LOG" 2>&1 &
  echo \$! > "\$PID"; echo "started (log: \$LOG)"
}
stop() { running && { kill "\$(cat "\$PID")"; i=0; while running && [ \$i -lt 60 ]; do sleep 1; i=\$((i+1)); done; }; rm -f "\$PID"; echo stopped; }
case "\${1:-status}" in
  start) start ;; stop) stop ;; restart) stop; start ;;
  status) if running; then echo "running (pid \$(cat "\$PID"))"; else echo "stopped"; fi ;;
  logs) tail -f "\$LOG" ;;
  *) echo "usage: \$0 start|stop|restart|status|logs"; exit 2 ;;
esac
EOF
  run "$PREFIX/godswar" restart
  wait_ready || die "the server did not become ready: tail -50 $PREFIX/logs/server.log"
  if [ "$DRY" = 0 ] && selftest "$ADMINPW"; then
    note "login self-test passed (reached the character screen)"
  elif [ "$DRY" = 0 ]; then note "WARNING: login self-test failed; see $PREFIX/logs/server.log"; fi
  cat <<DONE

GodsWar is running on this Mac.
  Players connect to   $GODSWAR_HOST  login port $GODSWAR_LOGIN_PORT (game port $GODSWAR_GAME_PORT)
  Administrator        $GODSWAR_ADMIN_USER  (password: $ETC/admin_password; GM commands are on)
  Start/stop/status    local/godswar start | stop | restart | status | logs
  Database             PostgreSQL 17 (Homebrew) port $PG_PORT, database $POSTGRES_DB
  More accounts        printf '%s' 'PASSWORD' | python3 -I tools/make-admin.py NAME - | psql -h 127.0.0.1 -p $PG_PORT -U $POSTGRES_USER $POSTGRES_DB
Point your client at the server: docs/CLIENT.md
DONE
  exit 0
fi

# ---- 6b. Linux: systemd units -----------------------------------------------------------------
say "systemd service godswar"
cat <<EOF | write_file /etc/systemd/system/godswar.service 644 root:root
# Written by setup.sh
[Unit]
Description=GodsWar game server
After=network-online.target postgresql.service
Wants=network-online.target postgresql.service

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_USER
WorkingDirectory=$SERVER_DIR
EnvironmentFile=$PREFIX/config/server.env
StateDirectory=godswar
Environment=HOME=/var/lib/godswar
Environment=GODSWAR_POSTGRES_CONNECTION_STRING_FILE=$CONN
ExecStartPre=/bin/sh -c 'for i in \$(seq 1 60); do pg_isready -q -h 127.0.0.1 -p $PG_PORT && exit 0; sleep 1; done; exit 1'
ExecStart=$SERVER_DIR/Godswar.Server $PREFIX/config/appsettings.json
Restart=on-failure
RestartSec=10
TimeoutStopSec=60
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectControlGroups=true
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
EOF

if [ "$GODSWAR_EDITOR" = yes ]; then
  # a root-owned copy the service user can read (the repository may be in a private home folder)
  run rm -rf "$PREFIX/editor"
  run cp -R "$REPO/editor" "$PREFIX/editor"
  run chmod -R go-w,o+rX "$PREFIX/editor"
  run install -d -o "$SVC_USER" -g "$SVC_USER" -m 750 /var/lib/godswar-editor
  cat <<EOF | write_file /etc/systemd/system/godswar-editor.service 644 root:root
# Written by setup.sh. Admin web editor, 127.0.0.1:8787 only: ssh -L 8787:127.0.0.1:8787 you@server
[Unit]
Description=GodsWar admin editor (127.0.0.1:8787)
After=godswar.service postgresql.service

[Service]
Type=simple
User=$SVC_USER
Group=$SVC_USER
WorkingDirectory=/var/lib/godswar-editor
Environment=PGPASSFILE=$ETC/service/editor.pgpass
ExecStart=/usr/bin/python3 -I $PREFIX/editor/server.py --reborn-only --mysql /bin/false --live --port 8787 --reborn-db $POSTGRES_DB --reborn-psql "psql -X -h 127.0.0.1 -p $PG_PORT -U $POSTGRES_USER" --rates-mgmt $GODSWAR_MANAGEMENT_PORT --backup-dir /var/lib/godswar-editor/backups --audit-log /var/lib/godswar-editor/audit.log
Restart=on-failure
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/godswar-editor
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
fi

if [ "$HAS_SYSTEMD" = 0 ]; then
  say "No systemd here (a container?)"
  note "Everything up to the services is done. Start the server by hand to try it:"
  note "  set -a; . $PREFIX/config/server.env; set +a; GODSWAR_POSTGRES_CONNECTION_STRING_FILE=$CONN runuser -u $SVC_USER -- $SERVER_DIR/Godswar.Server $PREFIX/config/appsettings.json"
  note "The sign-up site, Caddy, the editor service and the firewall need systemd and were skipped."
  exit 0
fi

run systemctl daemon-reload
run systemctl enable godswar.service
run systemctl restart godswar.service
wait_ready || { journalctl -u godswar -n 40 --no-pager || true; die "the server did not become ready: journalctl -u godswar"; }
if [ "$DRY" = 0 ]; then   # the server may rewrite realm rows on its first start: set the address again
  db_psql -v ip="$GODSWAR_HOST" -v port="$GODSWAR_GAME_PORT" <<<"UPDATE server SET ip_address = :'ip', game_port = :'port'::int;"
fi
if [ "$GODSWAR_EDITOR" = yes ]; then run systemctl enable godswar-editor.service; run systemctl restart godswar-editor.service; fi

# ---- 7. sign-up website ----------------------------------------------------------------------
if [ "$GODSWAR_SIGNUP" = yes ]; then
  say "Sign-up website ($ORIGIN)"
  run install -d -o root -g root -m 755 /etc/gwsignup
  # install.sh keeps GWSIGNUP_SERVER_NAME and GWSIGNUP_MAIL* lines and an existing key across runs.
  if [ "$DRY" = 0 ]; then
    envf=/etc/gwsignup/gwsignup.env; tmpenv=$(mktemp)
    { [ -f "$envf" ] && grep -vE '^GWSIGNUP_(SERVER_NAME|MAIL_FROM|MAIL)=' "$envf" || true
      echo "GWSIGNUP_SERVER_NAME=$GODSWAR_SERVER_NAME"
      if [ "$GODSWAR_MAIL_FROM" != none ]; then echo "GWSIGNUP_MAIL_FROM=$GODSWAR_MAIL_FROM"; else echo "GWSIGNUP_MAIL=0"; fi
    } > "$tmpenv"
    install -o root -g root -m 600 "$tmpenv" "$envf"; rm -f "$tmpenv"
    if [ -n "$RESEND_KEY" ]; then printf '%s\n' "$RESEND_KEY" | write_file /etc/gwsignup/resend_api_key 600 root:root; note "Resend key stored in /etc/gwsignup/resend_api_key"; fi
  fi
  run env PGPORT="$PG_PORT" sh "$REPO/signup/install.sh" --live --db "$POSTGRES_DB" --origin "$ORIGIN" --no-caddy --no-firewall
fi

# ---- 8. Caddy (HTTPS) --------------------------------------------------------------------------
if [ "$GODSWAR_SIGNUP" = yes ] && [ "$MODE" = vps ]; then
  say "Caddy (HTTPS for $SITE)"
  if ! command -v caddy >/dev/null; then
    if [ "$DRY" = 0 ] && apt_has caddy; then apt_install caddy
    else
      note "adding Caddy's official apt repository"
      apt_install debian-keyring debian-archive-keyring apt-transport-https
      if [ "$DRY" = 0 ]; then
        curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
        curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt > /etc/apt/sources.list.d/caddy-stable.list
      fi
      run apt-get update -q; apt_install caddy
    fi
  fi
  if [ "$DRY" = 0 ]; then
    new=$(mktemp)
    { echo "# Written by setup.sh for $SITE (template: signup/caddy/Caddyfile)."; sed "s/{\$GW_SIGNUP_DOMAIN}/$SITE/" "$REPO/signup/caddy/Caddyfile"; } > "$new"
    caddy validate --config "$new" --adapter caddyfile >/dev/null 2>&1 || { caddy validate --config "$new" --adapter caddyfile || true; rm -f "$new"; die "the Caddyfile does not validate"; }
    if [ -f /etc/caddy/Caddyfile ] && ! cmp -s "$new" /etc/caddy/Caddyfile; then
      cp -p /etc/caddy/Caddyfile "/etc/caddy/Caddyfile.before-godswar-$(date -u +%Y%m%dT%H%M%SZ)"; note "old Caddyfile backed up beside it"
    fi
    install -o root -g root -m 644 "$new" /etc/caddy/Caddyfile; rm -f "$new"
    install -d -m 755 /var/www/gwdownloads /var/www/gwupdates
    install -d -o caddy -g caddy -m 750 /var/log/caddy
  else note "[dry-run] write /etc/caddy/Caddyfile for $SITE -> 127.0.0.1:8090"; fi
  run systemctl enable caddy
  run systemctl reload-or-restart caddy
fi

# ---- 9. firewall -----------------------------------------------------------------------------
if [ "$GODSWAR_FIREWALL" = yes ]; then
  say "Firewall (ufw)"
  sshp=$( (ss -tlnpH 2>/dev/null || true) | awk '/sshd/ {sub(/.*:/,"",$4); print $4; exit}')
  run ufw allow "${sshp:-22}/tcp" comment 'ssh'                        # never lock yourself out
  run ufw allow "$GODSWAR_LOGIN_PORT/tcp" comment 'GodsWar login'
  run ufw allow "$GODSWAR_GAME_PORT/tcp" comment 'GodsWar game'
  if [ "$GODSWAR_SIGNUP" = yes ] && [ "$MODE" = vps ]; then run ufw allow 80/tcp comment 'GodsWar web'; run ufw allow 443/tcp comment 'GodsWar web'; fi
  if [ "$DRY" = 0 ] && ! ufw status | grep -q 'Status: active'; then run ufw --force enable; fi
  note "Many hosting providers also have their own firewall panel: open the same TCP ports there."
fi

# ---- 10. self-test and summary ------------------------------------------------------------------
say "Self-test: logging in as $GODSWAR_ADMIN_USER"
if [ "$DRY" = 1 ]; then note "[dry-run] python3 signup/test/game_login.py ... 127.0.0.1:$GODSWAR_LOGIN_PORT"
elif selftest "$ADMINPW"; then
  note "passed (reached the character screen)"
else note "WARNING: the login self-test did not pass; see: journalctl -u godswar -n 100"
     note "(On clouds that NAT the public IP, e.g. AWS/GCP/Oracle, this test can fail although players can log in.)"; fi

cat <<DONE

GodsWar is installed.

  Players connect to   $GODSWAR_HOST   login port $GODSWAR_LOGIN_PORT   (game port $GODSWAR_GAME_PORT)
  Administrator        $GODSWAR_ADMIN_USER   password: sudo cat $ETC/admin_password   (GM chat commands are on)
DONE
[ "$GODSWAR_SIGNUP" = yes ] && echo "  Sign-up website      $ORIGIN   (close sign-ups: sudo touch /etc/gwsignup/closed)"
[ "$GODSWAR_SIGNUP" = yes ] && [ "${GODSWAR_MAIL_FROM:-none}" = none ] && echo "  Mail                 off. Later: set GWSIGNUP_MAIL_FROM in /etc/gwsignup/gwsignup.env, then sudo signup/ops/set-resend-key.sh"
[ "$GODSWAR_EDITOR" = yes ] && echo "  Editor               ssh -L 8787:127.0.0.1:8787 you@$GODSWAR_HOST   then open http://127.0.0.1:8787"
cat <<DONE
  Start / stop         sudo systemctl start|stop|restart godswar      status: systemctl status godswar
  Logs                 journalctl -u godswar -f
  More accounts        printf '%s' 'PASSWORD' | python3 -I tools/make-admin.py NAME - | sudo -u postgres psql -p $PG_PORT $POSTGRES_DB
  More GMs             add account ids to GODSWAR_DEVELOPER_ACCOUNT_IDS in $PREFIX/config/server.env, restart
  Backups              sudo -u postgres pg_dump -p $PG_PORT $POSTGRES_DB | gzip > godswar-\$(date +%F).sql.gz
  Secrets              $ETC (root only): db_password, admin_password, update-private.pem; service/ (readable by $SVC_USER): postgres.conn
  Launcher update key  public half: $ETC/update-public.b64 (GW_UPDATE_PUBKEY for launcher/build.sh)
Point your game client at the server: docs/CLIENT.md
DONE
