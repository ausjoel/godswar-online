#!/bin/sh
# Start/stop the sign-up TEST reborn instance (NOT the live godswar251 unit).
#   run-reborn.sh start | stop | status
# ~/signup-test/server + ~/signup-test/appsettings.json: DB godswar_signup, ports 62999/63000/127.0.0.1:63090.
# PID recorded in ~/signup-test/reborn.pid; stop only ever signals that pid, and only if it is our binary.
D=$HOME/signup-test
case "${1:-status}" in
start)
  if [ -f "$D/reborn.pid" ] && kill -0 "$(cat "$D/reborn.pid")" 2>/dev/null; then
    echo "already running pid $(cat "$D/reborn.pid")"; exit 0
  fi
  cd "$D/server" || exit 1
  echo "=== start $(date -u +%FT%TZ)" >> "$D/reborn.log"
  setsid -f sh -c "echo \$\$ > '$D/reborn.pid'; exec env GODSWAR_RUNTIME_PROFILE=LocalDevelopment GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 '$D/server/Godswar.Server' '$D/appsettings.json' >> '$D/reborn.log' 2>&1" < /dev/null
  i=0
  while [ $i -lt 120 ]; do
    if curl -s -m 1 http://127.0.0.1:63090/readyz 2>/dev/null | grep -q '"ready"'; then break; fi
    sleep 1; i=$((i+1))
  done
  echo "pid $(cat "$D/reborn.pid" 2>/dev/null) after ${i}s: $(curl -s -m 1 http://127.0.0.1:63090/readyz)"
  ss -ltn | grep -E ':(62999|63000|63090) ' ;;
stop)
  if [ -f "$D/reborn.pid" ]; then
    P=$(cat "$D/reborn.pid")
    if kill -0 "$P" 2>/dev/null && tr '\0' ' ' < /proc/$P/cmdline | grep -q "$D/server/Godswar.Server"; then
      kill -TERM "$P"; i=0
      while kill -0 "$P" 2>/dev/null && [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done
      kill -0 "$P" 2>/dev/null && kill -KILL "$P"
      echo "stopped $P"
    else echo "pid $P not ours/not running"; fi
    rm -f "$D/reborn.pid"
  else echo "no pid file"; fi ;;
status)
  [ -f "$D/reborn.pid" ] && ps -o pid,rss,etime,cmd -p "$(cat "$D/reborn.pid")"
  curl -s -m 1 http://127.0.0.1:63090/readyz; echo
  ss -ltn | grep -E ':(62999|63000|63090) ' ;;
esac
