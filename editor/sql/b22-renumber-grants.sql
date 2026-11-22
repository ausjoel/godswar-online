-- B22 d56 (the admin 2026-11-21: "grant" = account renumbering is allowed from the LIVE editor, zero-player releases only).
--
-- The live editor connects as gweditor (not a superuser). Measured on live 2026-11-21 (read only), a renumber there
-- needs rights gweditor does not have:
--   * SET ON PARAMETER session_replication_role: the renumber turns the foreign-key and append-only triggers off for
--     its one transaction (has_parameter_privilege = false today);
--   * SELECT + UPDATE of the account column on harbor_attack_completion_reward_members, legacy_game_login_handoffs,
--     login_window_award_claims, session_health, wonderland_medal_awards, signup.account_settings, signup.email_tokens
--     (no SELECT at all on several, so even the dry run's row counts fail);
--   * UPDATE on the sequence public.accounts_id_seq (setval after the renumber).
-- This file grants exactly that, discovered at run time the same way the editor finds account columns (a column named
-- account_id / *_account_id / *_account_ids, or a foreign key to public.accounts, in schemas public and signup):
-- table SELECT, column-level UPDATE of the account column only, the sequence, the parameter. Safe to run again.
--
-- Run as the postgres superuser inside the game database (no restart; the editor sees it at its next dry run):
--   sudo -u postgres psql -d godswar -v ON_ERROR_STOP=1 -v editor_role=gweditor -f b22-renumber-grants.sql
-- What it widens: gweditor may switch session_replication_role in its own transactions (the editor uses it only for
-- the renumber and the existing hatch-evidence Undo), and may change account numbers in those columns.
-- Remove with: REVOKE SET ON PARAMETER session_replication_role FROM gweditor; plus the matching REVOKEs below.
\set ON_ERROR_STOP on
\set QUIET on

SELECT format('GRANT SET ON PARAMETER session_replication_role TO %I', :'editor_role') \gexec

SELECT format('GRANT USAGE ON SCHEMA signup TO %I', :'editor_role')
WHERE EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'signup') \gexec

SELECT DISTINCT format('GRANT SELECT ON %I.%I TO %I', n.nspname, cl.relname, :'editor_role')
FROM pg_attribute at JOIN pg_class cl ON cl.oid = at.attrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
WHERE n.nspname IN ('public', 'signup') AND at.attnum > 0 AND NOT at.attisdropped AND cl.relkind IN ('r', 'p')
  AND (at.attname = 'account_id' OR at.attname LIKE '%\_account\_id' OR at.attname LIKE '%\_account\_ids'
       OR (n.nspname = 'public' AND cl.relname = 'accounts' AND at.attname = 'id')
       OR EXISTS (SELECT 1 FROM pg_constraint k WHERE k.contype = 'f' AND k.conrelid = cl.oid AND at.attnum = ANY (k.conkey)
                  AND k.confrelid = 'public.accounts'::regclass)) \gexec

SELECT format('GRANT UPDATE (%I) ON %I.%I TO %I', at.attname, n.nspname, cl.relname, :'editor_role')
FROM pg_attribute at JOIN pg_class cl ON cl.oid = at.attrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
WHERE n.nspname IN ('public', 'signup') AND at.attnum > 0 AND NOT at.attisdropped AND cl.relkind IN ('r', 'p')
  AND (at.attname = 'account_id' OR at.attname LIKE '%\_account\_id' OR at.attname LIKE '%\_account\_ids'
       OR (n.nspname = 'public' AND cl.relname = 'accounts' AND at.attname = 'id')
       OR EXISTS (SELECT 1 FROM pg_constraint k WHERE k.contype = 'f' AND k.conrelid = cl.oid AND at.attnum = ANY (k.conkey)
                  AND k.confrelid = 'public.accounts'::regclass)) \gexec

SELECT format('GRANT USAGE, SELECT, UPDATE ON SEQUENCE %s TO %I', pg_get_serial_sequence('public.accounts', 'id'), :'editor_role')
WHERE pg_get_serial_sequence('public.accounts', 'id') IS NOT NULL \gexec

\set QUIET off
SELECT has_parameter_privilege(:'editor_role', 'session_replication_role', 'SET') AS replica_switch,
       has_sequence_privilege(:'editor_role', pg_get_serial_sequence('public.accounts', 'id'), 'UPDATE') AS sequence;
