#!/bin/sh
# test-mail.sh - send ONE test mail through Resend with the sign-up site's settings and key.
#
#   sudo ~/signup/ops/test-mail.sh you@example.com
#   --name NAME     another install (default gwsignup)
#
# It checks that the service is running, that its egress drop-in is in effect, and what the
# service logged about reaching Resend at its last start. Then it sends one mail with the app's
# own code (gwsignup.py --send-test) as root, reading the key from the root-only file. It prints
# Resend's answer (the message id, or the error status/name/message, e.g. "domain is not
# verified"). It never prints the key.
set -eu
NAME=gwsignup; ADDR=""
while [ $# -gt 0 ]; do
  case "$1" in
    --name) NAME=$2; shift ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    -*) echo "test-mail.sh: unknown option $1" >&2; exit 2 ;;
    *) ADDR=$1 ;;
  esac
  shift
done
[ "$(id -u)" = 0 ] || { echo "run as root (sudo)" >&2; exit 1; }
[ -n "$ADDR" ] || { echo "usage: sudo $0 you@example.com" >&2; exit 2; }
echo "$NAME" | grep -Eq '^[a-z][a-z0-9-]{1,30}$' || { echo "bad --name" >&2; exit 2; }
ETC=/etc/$NAME; APP=/opt/$NAME/app/gwsignup.py
[ -f "$APP" ] && [ -f "$ETC/gwsignup.env" ] || { echo "$NAME is not installed (run install.sh)" >&2; exit 1; }
grep -q '[^[:space:]]' "$ETC/resend_api_key" 2>/dev/null || { echo "no key in $ETC/resend_api_key: run set-resend-key.sh first" >&2; exit 1; }

ok=1
if systemctl is-active --quiet "$NAME.service"; then echo "service $NAME: running"; else echo "service $NAME: NOT running" >&2; ok=0; fi
if systemctl show "$NAME.service" -p IPAddressAllow --value | grep -Eq '0\.0\.0\.0/0|any'; then
  echo "egress drop-in: in effect (the service may reach api.resend.com)"
else
  echo "egress drop-in: NOT in effect, so the service cannot reach Resend. Run set-resend-key.sh again." >&2; ok=0
fi
echo "service start-up log about mail:"
journalctl -u "$NAME.service" -n 300 --no-pager -o cat | grep -E 'mail (is|egress)' | tail -n 2 | sed 's/^/  /' || echo "  (none)"

echo "sending one test mail to $ADDR ..."
python3 -I - "$ETC" "$APP" "$ADDR" <<'PY'
import os, sys
etc, app, addr = sys.argv[1:4]
# The same settings systemd gives the service (EnvironmentFile), and the same secret files.
with open(os.path.join(etc, "gwsignup.env")) as f:
    for line in f:
        line = line.rstrip("\n")
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            os.environ[k.strip()] = v
os.environ["GWSIGNUP_DB_PASSWORD_FILE"] = os.path.join(etc, "db_password")
os.environ["GWSIGNUP_SECRET_KEY_FILE"] = os.path.join(etc, "secret_key")
os.environ["GWSIGNUP_RESEND_KEY_FILE"] = os.path.join(etc, "resend_api_key")
os.execv(sys.executable, [sys.executable, "-I", app, "--send-test", addr])
PY
[ "$ok" = 1 ] || { echo "NOTE: the test mail used root's network; the service itself may still be blocked (see above)." >&2; exit 1; }
