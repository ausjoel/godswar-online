-- Sign-up site: its own schema and a least-privilege login role.
--
-- Run as the postgres superuser, inside the game database (install.sh does this):
--   psql -v ON_ERROR_STOP=1 -v role=gw_signup -v owner=godswar -d godswar -f signup-db.sql
-- Safe to run again (idempotent). The role's password is set separately by install.sh,
-- from the root-only file /etc/<name>/db_password.
--
-- Nothing in reborn's own tables is changed: no new columns, indexes, triggers or
-- foreign keys on public.accounts. The sign-up site only INSERTs whole account rows
-- (the same columns reborn's own registration writes) and reads four columns (the password
-- verifier only to check a settings-sync request).

\set QUIET on
SET client_min_messages = warning;

-- 1. The login role. No inheritance, no special powers, few connections.
SELECT format('CREATE ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 8', :'role')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role') \gexec
SELECT format('ALTER ROLE %I LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 8', :'role') \gexec
SELECT format('ALTER ROLE %I SET statement_timeout = %L', :'role', '5s') \gexec
SELECT format('ALTER ROLE %I SET idle_in_transaction_session_timeout = %L', :'role', '10s') \gexec
SELECT format('ALTER ROLE %I SET search_path = %L', :'role', 'pg_catalog') \gexec

-- 2. The site's own schema, owned by the game database's owner like everything else.
SELECT format('CREATE SCHEMA IF NOT EXISTS signup AUTHORIZATION %I', :'owner') \gexec
SELECT format('SET ROLE %I', :'owner') \gexec

-- Email of each account made on the site, and whether it has been verified.
-- (accounts.email holds a copy, so GM tools and psql see it too.)
-- No foreign key to public.accounts on purpose: that would add triggers to reborn's table.
-- The app removes rows whose account no longer exists before it checks an address.
CREATE TABLE IF NOT EXISTS signup.account_emails (
    account_id      integer PRIMARY KEY,
    email           varchar(255) NOT NULL,
    email_lower     varchar(255) NOT NULL,
    verified_at     timestamptz,                         -- NULL = not verified yet
    created_at      timestamptz NOT NULL DEFAULT now(),
    signup_ip_hash  varchar(64),                         -- keyed hash, not the IP itself
    CONSTRAINT ck_account_emails_lower CHECK (email_lower = lower(email))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_account_emails_email_lower ON signup.account_emails (email_lower);

-- HOOK for later (not used yet, no grants): one-time links for email verification and
-- password reset. Store only the SHA-256 of a token; the link carries the token itself.
CREATE TABLE IF NOT EXISTS signup.email_tokens (
    token_sha256    bytea PRIMARY KEY,
    account_id      integer NOT NULL,
    purpose         varchar(32) NOT NULL CHECK (purpose IN ('verify_email', 'reset_password')),
    email           varchar(255) NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    used_at         timestamptz
);
CREATE INDEX IF NOT EXISTS ix_email_tokens_account ON signup.email_tokens (account_id, purpose);

-- Each account's game settings files (Settings/User: skill bars, auto-fight, bag and map
-- settings), so the Mac launcher can carry them between computers. Keyed by file name.
CREATE TABLE IF NOT EXISTS signup.account_settings (
    account_id      integer NOT NULL,
    name            varchar(48) NOT NULL,
    content         bytea NOT NULL CHECK (octet_length(content) <= 65536),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, name)
);

COMMENT ON SCHEMA signup IS 'Account sign-up website (GodsWar-VPS/signup). Not used by the game server.';
RESET ROLE;

-- 3. Grants: exactly what creating an account needs, nothing more.
SELECT format('REVOKE ALL ON SCHEMA signup FROM %I', :'role') \gexec
SELECT format('REVOKE ALL ON ALL TABLES IN SCHEMA signup FROM %I', :'role') \gexec
SELECT format('REVOKE ALL ON public.accounts FROM %I', :'role') \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), :'role') \gexec
SELECT format('GRANT USAGE ON SCHEMA public TO %I', :'role') \gexec
SELECT format('GRANT USAGE ON SCHEMA signup TO %I', :'role') \gexec
-- read: is the name or email taken?
SELECT format('GRANT SELECT (id, username, email) ON public.accounts TO %I', :'role') \gexec
-- write: a new row, with the same columns reborn's TryCreateAccountWithCredentialAsync writes
SELECT format('GRANT INSERT (uuid, email, username, password, login_status, last_login_time, last_logout_time, '
              'last_login_ip, last_login_mac, total_online_time, status) ON public.accounts TO %I', :'role') \gexec
SELECT format('GRANT SELECT, INSERT, DELETE ON signup.account_emails TO %I', :'role') \gexec
-- settings sync: the launcher proves the account with what the game client sends (md5 of the
-- password), checked against reborn's PBKDF2 verifier, so the verifier column is readable here.
SELECT format('GRANT SELECT (password) ON public.accounts TO %I', :'role') \gexec
SELECT format('GRANT SELECT, INSERT, UPDATE ON signup.account_settings TO %I', :'role') \gexec

-- Rankings page: only these columns of ordinary characters, never accounts or staff characters.
CREATE OR REPLACE VIEW signup.public_rankings WITH (security_barrier = true) AS
  SELECT name::text AS name, fighter_job_lv::bigint AS level, profession::int AS profession, camp::int AS camp,
         prestige::bigint AS prestige, "Money"::bigint AS money
  FROM public.character_base
  WHERE lifecycle_state = 'active' AND "GM" = 0 AND fighter_job_lv >= 2;
SELECT format('GRANT SELECT ON signup.public_rankings TO %I', :'role') \gexec

-- Show the result.
\set QUIET off
SELECT grantee, table_schema || '.' || table_name AS "table", privilege_type, string_agg(column_name, ', ' ORDER BY column_name) AS columns
FROM information_schema.column_privileges
WHERE grantee = :'role'
GROUP BY 1, 2, 3 ORDER BY 2, 3;
