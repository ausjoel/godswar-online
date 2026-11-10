#!/bin/sh
# Settings-sync test (run ON THE VPS as admin, from a folder holding app/ sql/ templates/ static/).
# Builds a throwaway database gw_settings_test with only the schema of public.accounts and
# a stand-in public.character_base (no rows copied), one made-up account, and a throwaway role; runs the app
# on 127.0.0.1:18090; exercises /api/settings/*; then drops the database and the role.
# Never touches godswar, gwsignup.service or godswar251.service.
set -eu
DB=gw_settings_test; ROLE=gw_settings_test; PORT=18090
HERE=$(pwd); T=$(mktemp -d)
PSQL="sudo -n -u postgres psql -X -q -v ON_ERROR_STOP=1"
cleanup() { [ -n "${APP:-}" ] && kill "$APP" 2>/dev/null || true
  $PSQL -c "DROP DATABASE IF EXISTS $DB" >/dev/null 2>&1 || true
  $PSQL -c "DROP ROLE IF EXISTS $ROLE" >/dev/null 2>&1 || true; rm -rf "$T"; }
trap cleanup EXIT
$PSQL -tAc "select 1 from pg_database where datname='$DB'" | grep -q 1 && { echo "$DB exists, refusing"; exit 1; }
$PSQL -c "CREATE DATABASE $DB"
sudo -n -u postgres pg_dump --schema-only -t public.accounts godswar > "$T/schema.sql"
# Only the columns the rankings view reads; the real table has checks that call other functions.
printf 'CREATE TABLE public.character_base (name text, fighter_job_lv int, profession int, camp int, prestige bigint, "Money" bigint, lifecycle_state text, "GM" int);\n' >> "$T/schema.sql"
$PSQL -d $DB < "$T/schema.sql" >/dev/null
$PSQL -d $DB -v role=$ROLE -v owner=postgres < "$HERE/sql/signup-db.sql" >/dev/null
umask 077; head -c 24 /dev/urandom | base64 | tr -dc A-Za-z0-9 > "$T/dbpw"; head -c 32 /dev/urandom | base64 > "$T/secret"
$PSQL -c "ALTER ROLE $ROLE PASSWORD '$(cat "$T/dbpw")'"
VER=$(python3 -c "import sys; sys.path.insert(0,'$HERE/app'); import gwsignup; print(gwsignup.game_verifier('settings-test-pw', 1000))")
$PSQL -d $DB -c "INSERT INTO public.accounts (uuid,email,username,password,login_status,last_login_time,last_logout_time,last_login_ip,last_login_mac,total_online_time,status) VALUES ('','t@example.invalid','synctest','$VER',0,now(),now(),'','',0,0)" -c "INSERT INTO public.accounts (uuid,email,username,password,login_status,last_login_time,last_logout_time,last_login_ip,last_login_mac,total_online_time,status) VALUES ('','u@example.invalid','synclegacy','plaintext1',0,now(),now(),'','',0,0)"
GWSIGNUP_PORT=$PORT GWSIGNUP_DB_NAME=$DB GWSIGNUP_DB_USER=$ROLE GWSIGNUP_DB_PASSWORD_FILE="$T/dbpw" GWSIGNUP_SECRET_KEY_FILE="$T/secret" \
  GWSIGNUP_KDF_ITERATIONS=1000 GWSIGNUP_TRUST_PROXY=0 GWSIGNUP_STATIC_DIR="$HERE/static" GWSIGNUP_TEMPLATE_DIR="$HERE/templates" \
  python3 "$HERE/app/gwsignup.py" >"$T/app.log" 2>&1 & APP=$!
for i in $(seq 1 30); do curl -sf "http://127.0.0.1:$PORT/healthz" >/dev/null && break; sleep 0.5; done
U=http://127.0.0.1:$PORT/api/settings
KEY=$(printf %s settings-test-pw | md5sum | cut -c1-32)
pass=0; fail=0
ok() { if [ "$2" = "$3" ]; then pass=$((pass+1)); echo "ok   $1"; else fail=$((fail+1)); echo "FAIL $1 (want $3, got $2)"; fi; }
code() { curl -s -o "$T/out" -w '%{http_code}' "$@"; }
printf '<?xml version="1.0" encoding="UTF-8" ?>\r\n<SkillConfig>\r\n    <playerDB13 edition="1002" skill1="500" item1="-1" />\r\n</SkillConfig>\r\n' > "$T/Skill.xml"
printf '<AutoFight a="1&2+3 %%x"/>\n' > "$T/AutoFightSet.xml"
ok "pull before any push is an empty tar" "$(code --data-urlencode user=synctest --data-urlencode key=$KEY $U/pull)" 200
ok "empty tar lists nothing" "$(tar -tf "$T/out" | wc -l | tr -d ' ')" 0
ok "push two files" "$(code --data-urlencode user=synctest --data-urlencode key=$KEY --data-urlencode "f_Skill.xml@$T/Skill.xml" --data-urlencode "f_AutoFightSet.xml@$T/AutoFightSet.xml" $U/push)" 200
mkdir "$T/x"; code --data-urlencode user=synctest --data-urlencode key=$KEY $U/pull >/dev/null; tar -xf "$T/out" -C "$T/x"
ok "Skill.xml comes back byte for byte" "$(cmp -s "$T/Skill.xml" "$T/x/Skill.xml" && echo same)" same
ok "special characters survive" "$(cmp -s "$T/AutoFightSet.xml" "$T/x/AutoFightSet.xml" && echo same)" same
ok "wrong password refused" "$(code --data-urlencode user=synctest --data-urlencode key=00000000000000000000000000000000 $U/pull)" 401
ok "unknown account refused the same way" "$(code --data-urlencode user=nobodyhere --data-urlencode key=$KEY $U/pull)" 401
ok "plain-text legacy row refused" "$(code --data-urlencode user=synclegacy --data-urlencode key=$(printf %s plaintext1 | md5sum | cut -c1-32) $U/pull)" 401
ok "machine file refused" "$(code --data-urlencode user=synctest --data-urlencode key=$KEY --data-urlencode "f_SystemSetup.xml@$T/Skill.xml" $U/push)" 400
ok "path in name refused" "$(code --data-urlencode user=synctest --data-urlencode key=$KEY --data-urlencode "f_../x.xml@$T/Skill.xml" $U/push)" 400
printf 'not xml' > "$T/bad.xml"
ok "non-XML refused" "$(code --data-urlencode user=synctest --data-urlencode key=$KEY --data-urlencode "f_BagSet.xml@$T/bad.xml" $U/push)" 400
ok "GET refused" "$(code $U/pull)" 405
ok "bad key format refused" "$(code --data-urlencode user=synctest --data-urlencode key=xyz $U/pull)" 400
for i in 1 2 3 4 5 6 7 8; do code --data-urlencode user=synctest --data-urlencode key=11111111111111111111111111111111 $U/pull >/dev/null; done
ok "after repeated wrong tries requests are refused" "$(code --data-urlencode user=synctest --data-urlencode key=$KEY $U/pull)" 429
ok "signup page still serves" "$(code http://127.0.0.1:$PORT/)" 200
$PSQL -d $DB -tAc "select name, octet_length(content) from signup.account_settings order by 1"
echo "$pass passed, $fail failed"
grep -c Traceback "$T/app.log" | sed 's/^/tracebacks in app log: /'
[ "$fail" = 0 ]
