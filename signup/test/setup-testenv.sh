#!/bin/sh
# Test environment for the sign-up site (run ON THE VPS as the ssh user, admin).
#   * DB copy godswar_signup  (pg_dump -Fc of the live 'godswar' to a file, then pg_restore --no-owner --role=godswar)
#   * reborn copy ~/signup-test/server (copied from /opt/godswar251/server), own appsettings:
#       login 62999, game 63000, management 127.0.0.1:63090, Database=godswar_signup
#   * realm row 1 of the copy points at YOUR_SERVER_IP:63000
# Read-only towards the live server: it dumps the live DB and copies the binaries, nothing else.
# Never touches godswar251.service. Refuses to overwrite an existing godswar_signup.
set -eu
D=$HOME/signup-test
DB=godswar_signup
mkdir -p "$D"
cd "$D"
TS=$(date -u +%Y%m%dT%H%M%SZ)
if sudo -n -u postgres psql -Atc "select 1 from pg_database where datname='$DB'" | grep -q 1; then
  echo "$DB already exists - refusing to overwrite (drop it by hand first)"; exit 1
fi
echo "== dump live godswar (read-only) -> $D/godswar-$TS.dump"
sudo -n -u postgres pg_dump -Fc godswar > "$D/godswar-$TS.dump"
ls -la "$D/godswar-$TS.dump"
echo "== create + restore $DB"
sudo -n -u postgres createdb -O godswar "$DB"
sudo -n -u postgres pg_restore --no-owner --role=godswar -d "$DB" < "$D/godswar-$TS.dump"
sudo -n -u postgres psql -d "$DB" -c "UPDATE server SET ip_address='YOUR_SERVER_IP', game_port=63000 WHERE id=1" \
  -c "SELECT id,name,ip_address,game_port,enabled FROM server ORDER BY id"
echo "== copy server binaries"
rm -rf "$D/server"
sudo -n cp -a /opt/godswar251/server "$D/server"
sudo -n chown -R "$(id -un):$(id -gn)" "$D/server"
du -sh "$D/server"
echo "== appsettings (from the live one; own ports + DB)"
umask 077
sudo -n cat /opt/godswar251/appsettings.json | sed \
    -e 's/"port": 6999\b/"port": 62999/' \
    -e 's/"port": 7000\b/"port": 63000/' \
    -e 's/"publicPort": 7000\b/"publicPort": 63000/' \
    -e 's/"port": 9090\b/"port": 63090/' \
    -e 's/Database=godswar;/Database=godswar_signup;/' > "$D/appsettings.json"
chmod 600 "$D/appsettings.json"
grep -n '"port"\|publicPort\|publicHost\|Database=\|allowRegistration' "$D/appsettings.json" | sed -E 's/Password=[^;"]*/Password=***/'
if grep -q 'Database=godswar;' "$D/appsettings.json" || grep -Eq '"port": (6999|7000|9090)\b' "$D/appsettings.json"; then
  echo "appsettings still names a live port or DB - stop"; rm -f "$D/appsettings.json"; exit 1
fi
echo done
