-- Sign-up site, mail features: email verification, password reset, forgotten username.
--
-- Run as the postgres superuser AFTER signup-db.sql, in the same database (install.sh does both,
-- every time, in that order):
--   psql -v ON_ERROR_STOP=1 -v role=gw_signup -v owner=godswar -d godswar -f mail-grants.sql
-- Idempotent (CREATE OR REPLACE / IF NOT EXISTS, grants re-applied). Undo: mail-grants-undo.sql.
-- Guard: it stops before changing anything unless the signup schema, its two tables and
-- public.accounts exist, and public.accounts is owned by the database owner given as :owner
-- (the functions run as that owner).
-- No new columns are needed: signup.account_emails.verified_at and signup.email_tokens already
-- exist (signup-db.sql).
--
-- Least privilege. The login role gets NO new table rights at all:
--   * no UPDATE on public.accounts (not even the password column),
--   * no INSERT/UPDATE/SELECT on signup.email_tokens.
-- Instead it may EXECUTE four SECURITY DEFINER functions, owned by the database owner. They
-- are the only way to make or use a token, and the only way the site can change a password:
--   signup.issue_token(account, purpose, sha256)  make a one-time link (fixed lifetime, max 5/hour/account)
--   signup.peek_reset(sha256)                      the username behind a valid reset link (or NULL)
--   signup.verify_email(sha256)                    use a verify link: marks the email verified
--   signup.reset_password(sha256, verifier)        use a reset link: writes a new reborn verifier
-- A stolen gw_signup password can therefore only reset an account's password by first
-- issuing a token for it (which is rate-limited and leaves a row behind), and can only write
-- a well-formed reborn verifier, never an arbitrary value into any other column.

\set QUIET on
SET client_min_messages = warning;

-- Guard (see above). RAISE stops psql (ON_ERROR_STOP) before anything is created.
DO $guard$
BEGIN
    IF to_regclass('signup.account_emails') IS NULL OR to_regclass('signup.email_tokens') IS NULL THEN
        RAISE EXCEPTION 'run signup-db.sql first: signup.account_emails / signup.email_tokens missing';
    END IF;
    IF to_regclass('public.accounts') IS NULL THEN
        RAISE EXCEPTION 'public.accounts does not exist in %', current_database();
    END IF;
END
$guard$;
SELECT CASE WHEN (SELECT tableowner FROM pg_tables WHERE schemaname = 'public' AND tablename = 'accounts') <> :'owner'
            THEN 'DO $x$ BEGIN RAISE EXCEPTION ''public.accounts is not owned by %'', ' || quote_literal(:'owner') || '; END $x$' END \gexec

SELECT format('SET ROLE %I', :'owner') \gexec

-- Tokens are issued per (account, purpose); the 5-per-hour cap counts rows by created_at.
CREATE INDEX IF NOT EXISTS ix_email_tokens_created ON signup.email_tokens (account_id, purpose, created_at);

CREATE OR REPLACE FUNCTION signup.issue_token(p_account integer, p_purpose text, p_hash bytea)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fn$
DECLARE
    v_email    text;
    v_verified timestamptz;
    v_life     interval;
BEGIN
    IF p_purpose = 'verify_email' THEN
        v_life := interval '24 hours';
    ELSIF p_purpose = 'reset_password' THEN
        v_life := interval '1 hour';
    ELSE
        RETURN false;
    END IF;
    IF p_hash IS NULL OR octet_length(p_hash) <> 32 THEN
        RETURN false;
    END IF;

    SELECT e.email, e.verified_at INTO v_email, v_verified
      FROM signup.account_emails e
      JOIN public.accounts a ON a.id = e.account_id
     WHERE e.account_id = p_account;
    IF NOT FOUND THEN
        RETURN false;
    END IF;
    IF p_purpose = 'verify_email' AND v_verified IS NOT NULL THEN
        RETURN false;
    END IF;
    -- Reset links go only to an address that has been confirmed.
    IF p_purpose = 'reset_password' AND v_verified IS NULL THEN
        RETURN false;
    END IF;

    -- At most 5 links per account and purpose per hour, whatever the app thinks.
    IF (SELECT count(*) FROM signup.email_tokens
         WHERE account_id = p_account AND purpose = p_purpose
           AND created_at > now() - interval '1 hour') >= 5 THEN
        RETURN false;
    END IF;

    -- A new link replaces the older ones of the same kind (they expire now).
    UPDATE signup.email_tokens SET expires_at = least(expires_at, now())
     WHERE account_id = p_account AND purpose = p_purpose AND used_at IS NULL AND expires_at > now();
    -- Housekeeping: forget links that ended more than a week ago.
    DELETE FROM signup.email_tokens WHERE expires_at < now() - interval '7 days';

    INSERT INTO signup.email_tokens (token_sha256, account_id, purpose, email, expires_at)
    VALUES (p_hash, p_account, p_purpose, v_email, now() + v_life);
    RETURN true;
END
$fn$;

CREATE OR REPLACE FUNCTION signup.peek_reset(p_hash bytea)
RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fn$
    SELECT a.username::text
      FROM signup.email_tokens t
      JOIN signup.account_emails e ON e.account_id = t.account_id AND e.email = t.email
      JOIN public.accounts a ON a.id = t.account_id
     WHERE t.token_sha256 = p_hash AND t.purpose = 'reset_password'
       AND t.used_at IS NULL AND t.expires_at > now()
$fn$;

CREATE OR REPLACE FUNCTION signup.verify_email(p_hash bytea)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fn$
DECLARE
    v_account integer;
BEGIN
    UPDATE signup.email_tokens t SET used_at = now()
      FROM signup.account_emails e
     WHERE t.token_sha256 = p_hash AND t.purpose = 'verify_email'
       AND t.used_at IS NULL AND t.expires_at > now()
       AND e.account_id = t.account_id AND e.email = t.email
    RETURNING t.account_id INTO v_account;
    IF v_account IS NULL THEN
        RETURN NULL;
    END IF;
    UPDATE signup.account_emails SET verified_at = coalesce(verified_at, now()) WHERE account_id = v_account;
    RETURN v_account;
END
$fn$;

CREATE OR REPLACE FUNCTION signup.reset_password(p_hash bytea, p_verifier text)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $fn$
DECLARE
    v_account integer;
BEGIN
    -- Only reborn's versioned PBKDF2 verifier (Security/Authentication/PasswordVerifierRecord.cs):
    -- 16-byte salt and 32-byte hash in canonical padded Base64, 1-8 digit iteration count.
    IF p_verifier IS NULL OR p_verifier !~ '^gws\$pbkdf2-sha256\$v1\$[1-9][0-9]{0,7}\$[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=$' THEN
        RAISE EXCEPTION 'bad verifier format' USING ERRCODE = '22023';
    END IF;
    UPDATE signup.email_tokens t SET used_at = now()
      FROM signup.account_emails e
     WHERE t.token_sha256 = p_hash AND t.purpose = 'reset_password'
       AND t.used_at IS NULL AND t.expires_at > now()
       AND e.account_id = t.account_id AND e.email = t.email
    RETURNING t.account_id INTO v_account;
    IF v_account IS NULL THEN
        RETURN NULL;
    END IF;
    UPDATE public.accounts SET password = p_verifier WHERE id = v_account;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'account % is gone', v_account USING ERRCODE = 'P0002';   -- rolls back the token use too
    END IF;
    -- The reset proves the mailbox: mark the email verified, and end every other open link.
    UPDATE signup.account_emails SET verified_at = coalesce(verified_at, now()) WHERE account_id = v_account;
    UPDATE signup.email_tokens SET expires_at = least(expires_at, now())
     WHERE account_id = v_account AND used_at IS NULL AND expires_at > now();
    RETURN v_account;
END
$fn$;

RESET ROLE;

-- New functions are executable by PUBLIC by default: take that away, then give the site's role only.
REVOKE ALL ON FUNCTION signup.issue_token(integer, text, bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION signup.peek_reset(bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION signup.verify_email(bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION signup.reset_password(bytea, text) FROM PUBLIC;
SELECT format('GRANT EXECUTE ON FUNCTION signup.issue_token(integer, text, bytea) TO %I', :'role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION signup.peek_reset(bytea) TO %I', :'role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION signup.verify_email(bytea) TO %I', :'role') \gexec
SELECT format('GRANT EXECUTE ON FUNCTION signup.reset_password(bytea, text) TO %I', :'role') \gexec

\set QUIET off
SELECT p.oid::regprocedure AS function, p.prosecdef AS security_definer, pg_get_userbyid(p.proowner) AS owner
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'signup' ORDER BY 1;
