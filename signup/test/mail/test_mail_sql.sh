#!/bin/sh
# test_mail_sql.sh DIR - test sql/mail-grants.sql for real on a private throwaway PostgreSQL 17
# cluster (pg-cluster.sh, 127.0.0.1:55530, schema built only from the repo's sql/ files and
# accounts-stub.sql). Runs the checks as the site's role, the way the app calls the functions.
# Never touches the live database. Prints PASS/FAIL per check and exits non-zero on a failure.
set -eu
export LC_ALL=C LANG=C
DIR=${1:?usage: test_mail_sql.sh SCRATCH_DIR}
HERE=$(cd "$(dirname "$0")" && pwd)
SQL=$HERE/../../sql
BIN=${PGBIN:-/opt/homebrew/opt/postgresql@17/bin}
SU="$BIN/psql -X -q -At -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 55530 -U postgres -d godswar_mailtest"
ROLE_PSQL="$BIN/psql -X -q -At -h 127.0.0.1 -p 55530 -U gw_signup_mailtest -d godswar_mailtest"
export PGPASSWORD=mailtest-db-password-1234
fails=0
check() {   # check NAME EXPECTED ACTUAL
  if [ "$2" = "$3" ]; then echo "PASS $1"; else echo "FAIL $1: expected [$2] got [$3]"; fails=$((fails+1)); fi
}
as_role() { $ROLE_PSQL -c "$1" 2>&1 | tr '\n' ' ' | sed 's/ *$//'; }

sh "$HERE/pg-cluster.sh" "$DIR" reset >/dev/null
trap 'sh "$HERE/pg-cluster.sh" "$DIR" stop' EXIT

# Idempotent: both files a second time.
$SU -v role=gw_signup_mailtest -v owner=godswar -f "$SQL/signup-db.sql" >/dev/null
$SU -v role=gw_signup_mailtest -v owner=godswar -f "$SQL/mail-grants.sql" >/dev/null
check "re-run is idempotent (4 SECURITY DEFINER functions owned by godswar)" "4" \
  "$($SU -c "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='signup' and p.prosecdef and pg_get_userbyid(p.proowner)='godswar'")"

# Guard: wrong owner -> refused before anything changes.
out=$($SU -v role=gw_signup_mailtest -v owner=postgres -f "$SQL/mail-grants.sql" 2>&1 || true)
case "$out" in *"not owned by"*) r=refused ;; *) r="ran: $out" ;; esac
check "guard refuses a wrong owner" "refused" "$r"

# A verifier in the exact sign-up format (game_verifier), and a bad one.
VER=$(python3 -I -c 'import base64,hashlib,os
md5=hashlib.md5(b"N3w!Passw0rd").hexdigest().encode()
s=os.urandom(16); dk=hashlib.pbkdf2_hmac("sha256",md5,s,1000,32)
print("gws$pbkdf2-sha256$v1$1000$%s$%s"%(base64.b64encode(s).decode(),base64.b64encode(dk).decode()))')

# An account made the way the site makes one (as the role).
as_role "INSERT INTO public.accounts (uuid,email,username,password,login_status,last_login_time,last_logout_time,last_login_ip,last_login_mac,total_online_time,status) VALUES ('','Hero@Example.com','hero1','old-verifier',0,now(),now(),'','',0,0)" >/dev/null
ID=$($SU -c "select id from public.accounts where username='hero1'")
as_role "INSERT INTO signup.account_emails (account_id,email,email_lower,signup_ip_hash) VALUES ($ID,'Hero@Example.com','hero@example.com','x')" >/dev/null

h() { printf "sha256(convert_to('tok-%s','UTF8'))" "$1"; }

check "reset link refused while the address is unconfirmed" "f" "$(as_role "select signup.issue_token($ID,'reset_password',$(h r0))")"
check "verify link issued" "t" "$(as_role "select signup.issue_token($ID,'verify_email',$(h v1))")"
check "unknown purpose refused" "f" "$(as_role "select signup.issue_token($ID,'other',$(h x1))")"
check "short hash refused" "f" "$(as_role "select signup.issue_token($ID,'verify_email','\\x00'::bytea)")"
check "verify link works" "$ID" "$(as_role "select signup.verify_email($(h v1))")"
check "email now verified" "t" "$($SU -c "select verified_at is not null from signup.account_emails where account_id=$ID")"
check "verify link is single use" "" "$(as_role "select signup.verify_email($(h v1))")"
check "no verify link once verified" "f" "$(as_role "select signup.issue_token($ID,'verify_email',$(h v2))")"
check "reset link issued once verified" "t" "$(as_role "select signup.issue_token($ID,'reset_password',$(h r1))")"
check "peek shows the username" "hero1" "$(as_role "select signup.peek_reset($(h r1))")"
r=$(as_role "select signup.reset_password($(h r1),'plaintext')"); case "$r" in *"bad verifier format"*) r=refused ;; esac
check "reset refuses a non-verifier value" "refused" "$r"
check "reset still usable after the refused value" "hero1" "$(as_role "select signup.peek_reset($(h r1))")"
check "reset writes the verifier" "$ID" "$(as_role "select signup.reset_password($(h r1),'$VER')")"
check "password column holds the new verifier" "$VER" "$($SU -c "select password from public.accounts where id=$ID")"
check "reset link is single use" "" "$(as_role "select signup.reset_password($(h r1),'$VER')")"
check "a newer link ends the older one" "t" "$(as_role "select signup.issue_token($ID,'reset_password',$(h r2))")"
as_role "select signup.issue_token($ID,'reset_password',$(h r3))" >/dev/null
check "older link expired by the newer one" "" "$(as_role "select signup.peek_reset($(h r2))")"
$SU -c "update signup.email_tokens set expires_at = now() - interval '1 second' where token_sha256 = $(h r3)"
check "expired link refused" "" "$(as_role "select signup.peek_reset($(h r3))")"
check "5th reset link in an hour issued" "t" "$(as_role "select signup.issue_token($ID,'reset_password',$(h r4))")"
as_role "select signup.issue_token($ID,'reset_password',$(h r5))" >/dev/null   # r1..r5 = 5 in the last hour
check "6th link in an hour refused (5/hour/account)" "f" "$(as_role "select signup.issue_token($ID,'reset_password',$(h r6))")"
check "unknown account refused" "f" "$(as_role "select signup.issue_token(999999,'verify_email',$(h z1))")"

# Least privilege: no direct table rights.
for q in "UPDATE public.accounts SET password='x' WHERE id=$ID" \
         "SELECT * FROM signup.email_tokens" \
         "INSERT INTO signup.email_tokens (token_sha256,account_id,purpose,email,expires_at) VALUES ('\\x01',$ID,'reset_password','a',now()+interval '1 hour')" \
         "UPDATE signup.account_emails SET verified_at=now()" \
         "DELETE FROM public.accounts WHERE id=$ID"; do
  r=$(as_role "$q"); case "$r" in *"permission denied"*) r=denied ;; esac
  check "role cannot: ${q%% WHERE*}" "denied" "$r"
done
$SU -c "CREATE ROLE gw_other LOGIN PASSWORD 'other-password-1234'" >/dev/null
r=$(PGPASSWORD=other-password-1234 $BIN/psql -X -q -At -h 127.0.0.1 -p 55530 -U gw_other -d godswar_mailtest -c "select signup.peek_reset('\\x00')" 2>&1 || true)
case "$r" in *"permission denied"*) r=denied ;; esac
check "other roles cannot run the functions" "denied" "$r"

# Undo.
$SU -f "$SQL/mail-grants-undo.sql" >/dev/null
check "undo removes the functions" "0" "$($SU -c "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='signup' and p.proname in ('issue_token','peek_reset','verify_email','reset_password')")"
$SU -v role=gw_signup_mailtest -v owner=godswar -f "$SQL/mail-grants.sql" >/dev/null
check "re-apply after undo" "4" "$($SU -c "select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='signup' and p.prosecdef")"

echo "---"
[ "$fails" = 0 ] && echo "ALL SQL CHECKS PASSED" || { echo "$fails SQL CHECK(S) FAILED"; exit 1; }
