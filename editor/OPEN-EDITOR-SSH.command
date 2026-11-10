#!/bin/bash
# OPEN-EDITOR.command - opens the GodsWar editor (characters + Server rates) in your browser.
#   1. A dialog asks: Read-only (look safely) or Edit (change the LIVE server; a database
#      backup is taken first; players you edit must be logged out).
#   2. It starts the editor on the VPS, connects it to this Mac and opens
#      http://127.0.0.1:8788/ once the page answers.
#   3. The editor keeps running in the background; this window can be closed.
#      STOP-EDITOR.command stops it. Running this again while it runs just reopens the page
#      (or restarts it in the other mode).
# For tests: GW_EDITOR_MODE=Read-only|Edit skips the dialog, GW_EDITOR_NO_BROWSER=1 skips the browser.
set -u
TOOLS="$(cd "$(dirname "$0")/.." && pwd)"
PORT=8788
URL="http://127.0.0.1:$PORT/"
SSH=(ssh -o BatchMode=yes -o ConnectTimeout=15)
note() { /usr/bin/osascript - "$1" >/dev/null 2>&1 <<'AS'
on run argv
    display notification (item 1 of argv) with title "GodsWar editor"
end run
AS
echo "$1"; }
fail() { /usr/bin/osascript - "$1" >/dev/null 2>&1 <<'AS'
on run argv
    display dialog (item 1 of argv) buttons {"OK"} default button 1 with icon caution with title "GodsWar editor"
end run
AS
echo "FAILED: $1"; exit 1; }
show() { [ "${GW_EDITOR_NO_BROWSER:-0}" = 1 ] || open "$URL"; }

mode="${GW_EDITOR_MODE:-}"
if [ -z "$mode" ]; then
    mode=$(/usr/bin/osascript -e 'button returned of (display dialog "Open the GodsWar editor (characters and Server rates)." & return & return & "Read-only: look around safely." & return & "Edit: change the LIVE server. A backup is taken first; players you edit must be logged out." buttons {"Cancel", "Edit", "Read-only"} default button "Read-only" with title "GodsWar editor")' 2>/dev/null) || exit 0
fi
want_live=0; [ "$mode" = "Edit" ] && want_live=1

# Already running in the wanted mode, with the connection up? Then just open the page.
running=$("${SSH[@]}" vps "pgrep -af '^python3 -I gwadmin-web/server.py .*--port $PORT'" 2>/dev/null </dev/null)
have_live=0; case "$running" in *--live*) have_live=1 ;; esac
if [ -n "$running" ] && [ "$have_live" = "$want_live" ] && curl -s -m 5 -o /dev/null "$URL"; then
    note "The editor is already running ($mode). Opening $URL"; show; exit 0
fi

note "Starting the editor ($mode)..."
"${SSH[@]}" vps "mkdir -p ~/gwadmin/tools" </dev/null \
  && rsync -a --delete --exclude __pycache__ -e "ssh -o BatchMode=yes -o ConnectTimeout=15" "$TOOLS/" vps:gwadmin/tools/ \
  || fail "Could not reach the VPS. Check that the Mac is online, then try 'ssh vps' in Terminal."
FLAGS="--db-suffix _test --port $PORT --reborn-only"
if [ "$want_live" = 1 ]; then
    note "Taking a database backup first..."
    "${SSH[@]}" vps 'sudo gw251 backup' >/dev/null </dev/null || fail "The backup failed, so edit mode was not started."
    FLAGS="$FLAGS --live --reborn-db godswar"
fi
"${SSH[@]}" vps "pkill -f '^python3 -I gwadmin-web/server.py .*--port $PORT' 2>/dev/null; cd ~/gwadmin/tools && setsid -f python3 -I gwadmin-web/server.py $FLAGS > ~/gwadmin/web.log 2>&1 < /dev/null; curl -s --retry 30 --retry-delay 1 --retry-connrefused -o /dev/null http://127.0.0.1:$PORT/" </dev/null \
  || fail "The editor did not start on the VPS. Its log: ssh vps 'tail ~/gwadmin/web.log'"

# The connection from this Mac to the editor (an SSH tunnel); replace any old one.
pkill -f "ssh .*-L $PORT:127.0.0.1:$PORT vps" 2>/dev/null
"${SSH[@]}" -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -f -N -L "$PORT:127.0.0.1:$PORT" vps \
  || fail "Could not connect this Mac to the editor. Is another program using port $PORT?"
curl -s --retry 15 --retry-delay 1 --retry-all-errors -m 5 -o /dev/null "$URL" \
  || fail "The editor started, but the page does not answer."
show
note "Editor open ($mode): $URL"
echo
echo "The editor keeps running in the background. You can close this window."
echo "Server rates: ${URL}#rates"
echo "To stop it: double-click 'Stop Editor.command' (README folder)."
