#!/bin/sh
# set-resend-key.sh - store the Resend API key for the sign-up site and turn mail on (or off).
#
#   sudo ~/signup/ops/set-resend-key.sh            asks for the key (typing is hidden), stores it, restarts
#   sudo ~/signup/ops/set-resend-key.sh --off      empties the key file: mail off, restarts
#   --name NAME                                    another install (default gwsignup; test: gwsignup-test)
#
# The key goes to /etc/NAME/resend_api_key (root:root 600). systemd hands it to the service with
# LoadCredential, so it is never in the environment, on a command line or in a log. This script
# never prints it. It also writes the egress drop-in (/etc/systemd/system/NAME.service.d/mail.conf)
# that lets the sandboxed service reach api.resend.com, and removes it again with --off.
set -eu
NAME=gwsignup; OFF=0
while [ $# -gt 0 ]; do
  case "$1" in
    --off) OFF=1 ;;
    --name) NAME=$2; shift ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "set-resend-key.sh: unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
[ "$(id -u)" = 0 ] || { echo "run as root (sudo)" >&2; exit 1; }
echo "$NAME" | grep -Eq '^[a-z][a-z0-9-]{1,30}$' || { echo "bad --name" >&2; exit 2; }
ETC=/etc/$NAME
[ -d "$ETC" ] || { echo "$ETC does not exist: run install.sh first" >&2; exit 1; }
[ -f "/etc/systemd/system/$NAME.service" ] || { echo "$NAME.service is not installed: run install.sh first" >&2; exit 1; }
grep -q 'resend_api_key' "/etc/systemd/system/$NAME.service" || { echo "$NAME.service has no resend_api_key credential: re-run install.sh with the new code first" >&2; exit 1; }
DROPIN=/etc/systemd/system/$NAME.service.d
umask 077

if [ "$OFF" = 1 ]; then
  : > "$ETC/resend_api_key"
  rm -f "$DROPIN/mail.conf"; rmdir "$DROPIN" 2>/dev/null || true
  echo "mail is OFF (key file emptied, egress drop-in removed)"
else
  if [ -t 0 ]; then
    stty -echo
    trap 'stty echo' EXIT INT TERM
    printf 'Paste the Resend API key (starts with re_; nothing is shown): '
    IFS= read -r KEY || KEY=""
    stty echo; trap - EXIT INT TERM
    echo
  else
    IFS= read -r KEY || true    # from a pipe, e.g. a password manager CLI
  fi
  KEY=$(printf '%s' "$KEY" | tr -d '\r\n\t ')
  if ! printf '%s' "$KEY" | grep -Eq '^re_[A-Za-z0-9_-]{8,200}$'; then
    KEY=""
    echo "That does not look like a Resend API key (re_ followed by letters, digits, _ or -). Nothing changed." >&2
    exit 1
  fi
  TMP=$(mktemp "$ETC/.resend_api_key.XXXXXX")
  printf '%s\n' "$KEY" > "$TMP"     # printf is a shell builtin: the key is never on a command line
  KEY=""
  chown root:root "$TMP"; chmod 600 "$TMP"
  mv -f "$TMP" "$ETC/resend_api_key"
  install -d -o root -g root -m 755 "$DROPIN"
  printf '%s\n' "# Written by install.sh / set-resend-key.sh because a Resend key is set. Removed when it is empty." \
    "[Service]" "IPAddressAllow=any" > "$DROPIN/mail.conf"
  chmod 644 "$DROPIN/mail.conf"
  echo "key stored in $ETC/resend_api_key (root, 600); outgoing connections allowed for $NAME"
fi

systemctl daemon-reload
SINCE=$(date '+%Y-%m-%d %H:%M:%S')
systemctl restart "$NAME.service"
PORT=$(sed -n 's/^GWSIGNUP_PORT=//p' "$ETC/gwsignup.env" | tail -n 1)
i=0; health=""
while [ $i -lt 20 ]; do
  health=$(curl -s -m 2 "http://127.0.0.1:${PORT:-8090}/healthz" || true)
  [ "$health" = ok ] && break
  sleep 1; i=$((i+1))
done
[ "$health" = ok ] && echo "$NAME is up (healthz ok)" || echo "WARNING: $NAME healthz says: ${health:-nothing}" >&2
sleep 6   # the start-up egress check has a 5 s timeout
echo "-- what the service says about mail since the restart:"
journalctl -u "$NAME.service" --since "$SINCE" --no-pager -o cat | grep -E 'mail (is|egress)' || echo "(no mail lines yet; see journalctl -u $NAME)"
[ "$OFF" = 1 ] || echo "Next: sudo $(dirname "$0")/test-mail.sh you@example.com"
