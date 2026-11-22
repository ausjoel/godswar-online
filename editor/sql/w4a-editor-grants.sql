-- W4A (wave 4 package A): the rights the editor's database role needs for the new account and character tools.
--
-- The live editor connects as gweditor (gweditor.service: --reborn-psql "psql -h 127.0.0.1 -U gweditor"), which is not
-- the tables' owner and has no default privileges: tables created after its grants are invisible to it. Measured on the
-- live database 2026-11-12 (read only): gweditor has no rights on character_sealed_skills,
-- character_class_change_receipts or the signup schema, and migration 240's tables will not exist until the server
-- release.
--
-- Run as the postgres superuser inside the game database AFTER the server release that applies migration
-- 20261112_240_account_name_holds (safe to run again; a table that does not exist yet is skipped):
--   sudo -u postgres psql -d godswar -v editor_role=gweditor -f w4a-editor-grants.sql
-- Remove with the matching REVOKEs.
\set ON_ERROR_STOP on
\set QUIET on

-- account soft delete and user-name holds (server migration 240)
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO %I', t, :'editor_role')
FROM unnest(ARRAY['account_username_holds', 'account_deletions']) t
WHERE to_regclass('public.' || t) IS NOT NULL \gexec

-- class change (the server creates these two lazily at the first class change, owned by its own role)
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON public.%I TO %I', t, :'editor_role')
FROM unnest(ARRAY['character_sealed_skills', 'character_class_change_receipts']) t
WHERE to_regclass('public.' || t) IS NOT NULL \gexec

-- e-mail change: the sign-up site's copy of the address and its verification state
SELECT format('GRANT USAGE ON SCHEMA signup TO %I', :'editor_role')
WHERE EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'signup') \gexec
SELECT format('GRANT SELECT, INSERT, UPDATE, DELETE ON signup.account_emails TO %I', :'editor_role')
WHERE to_regclass('signup.account_emails') IS NOT NULL \gexec
SELECT format('GRANT SELECT, DELETE ON signup.email_tokens TO %I', :'editor_role')
WHERE to_regclass('signup.email_tokens') IS NOT NULL \gexec

\set QUIET off
SELECT table_schema || '.' || table_name AS "table", string_agg(privilege_type, ', ' ORDER BY privilege_type) AS rights
FROM information_schema.role_table_grants
WHERE grantee = :'editor_role' AND table_name IN ('account_username_holds', 'account_deletions', 'character_sealed_skills',
      'character_class_change_receipts', 'account_emails', 'email_tokens')
GROUP BY 1 ORDER BY 1;
