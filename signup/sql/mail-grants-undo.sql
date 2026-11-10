-- Undo mail-grants.sql: remove the mail functions (and with them every right the site's role
-- had to make or use tokens or to change a password). Tables are left as they were before the
-- mail features (signup.email_tokens and account_emails.verified_at already existed); the
-- optional last statement empties the token table.
--
--   psql -v ON_ERROR_STOP=1 -d godswar -f mail-grants-undo.sql
--
-- Run it only after the app without mail support is back in place (otherwise /forgot and
-- /verify answer "something went wrong"), or with mail turned off (empty /etc/gwsignup/resend_api_key or GWSIGNUP_MAIL=0, then restart).

\set QUIET on
SET client_min_messages = warning;
BEGIN;
DROP FUNCTION IF EXISTS signup.issue_token(integer, text, bytea);
DROP FUNCTION IF EXISTS signup.peek_reset(bytea);
DROP FUNCTION IF EXISTS signup.verify_email(bytea);
DROP FUNCTION IF EXISTS signup.reset_password(bytea, text);
DROP INDEX IF EXISTS signup.ix_email_tokens_created;
-- Forget every outstanding link (they cannot be used without the functions anyway).
DELETE FROM signup.email_tokens;
COMMIT;
\set QUIET off
SELECT count(*) AS mail_functions_left FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'signup' AND p.proname IN ('issue_token', 'peek_reset', 'verify_email', 'reset_password');
