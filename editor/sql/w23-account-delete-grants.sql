-- W23 (the admin 2026-11-24: "Still no option to fully delete account"): rights for Character > Account > "Delete account
-- completely" and its one-click Restore (modules/_account_purge.py).
--
-- The live editor connects as gweditor (not a superuser). Measured on live 2026-11-24 (read only), a complete delete
-- and its restore need rights gweditor does not have:
--   * SET ON PARAMETER session_replication_role: the delete and the restore turn the foreign-key and append-only
--     triggers off for their one transaction (the account renumber, b22-renumber-grants.sql, needs the same);
--   * SELECT + INSERT + DELETE on every table that holds rows of an account: the ON DELETE CASCADE closure from
--     public.accounts, the kill / loot / claim evidence tables, every table with an account / character / pet number
--     column (the same column names the editor uses: _account_purge.ACCOUNT_COLS / CHARACTER_COLS / PET_COLS) and the
--     sign-up site's tables (signup.account_settings is SELECT only, signup.email_tokens has no INSERT today);
--   * USAGE on schema signup.
-- Discovered at run time, so a table a later server release adds is covered by running this file again.
--
-- Run as the postgres superuser inside the game database (no restart; the editor sees it at the next plan):
--   sudo -u postgres psql -d godswar -v ON_ERROR_STOP=1 -v editor_role=gweditor -f w23-account-delete-grants.sql
-- What it widens: gweditor may delete and re-insert rows of those tables (also the evidence tables that only the
-- gwadmin_wipe_account_evidence() function could delete before) and may switch session_replication_role in its own
-- transactions. The editor uses that only for the complete delete / restore, the renumber and the hatch-evidence Undo.
-- Remove with: REVOKE SET ON PARAMETER session_replication_role FROM gweditor; plus REVOKE INSERT, DELETE ON <table>.
\set ON_ERROR_STOP on
\set QUIET on

SELECT format('GRANT SET ON PARAMETER session_replication_role TO %I', :'editor_role') \gexec

SELECT format('GRANT USAGE ON SCHEMA signup TO %I', :'editor_role')
WHERE EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'signup') \gexec

WITH RECURSIVE closure(oid) AS (
    SELECT 'public.accounts'::regclass::oid
  UNION
    SELECT k.conrelid FROM pg_constraint k JOIN closure c ON k.confrelid = c.oid
    WHERE k.contype = 'f' AND k.confdeltype = 'c'
), evidence(oid) AS (
    SELECT k.conrelid FROM pg_constraint k JOIN closure c ON k.confrelid = c.oid
    JOIN pg_class cl ON cl.oid = k.conrelid
    WHERE k.contype = 'f' AND cl.relname IN ('monster_death_reward_settlements', 'monster_death_pet_experience',
      'monster_loot_pickup_claims', 'online_award_claim_settlements', 'faction_crier_daily_claims',
      'faction_crier_exchange_settlements', 'faction_crier_weekly_reclaims', 'medusa_completion_reward_members',
      'wonderland_boss_loot_claims', 'character_title_ownership', 'sealed_pet_items')
), bycol(oid) AS (
    SELECT DISTINCT cl.oid FROM pg_attribute a JOIN pg_class cl ON cl.oid = a.attrelid JOIN pg_namespace n ON n.oid = cl.relnamespace
    WHERE n.nspname IN ('public', 'signup') AND cl.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped
      AND format_type(a.atttypid, NULL) IN ('integer', 'bigint', 'smallint')
      AND a.attname IN ('account_id', 'character_id', 'user_id', 'owner_character_id', 'owner_character_id_snapshot',
        'owner_user_id', 'owner_user_id_snapshot', 'user_id_snapshot', 'target_character_id', 'apprentice_id', 'master_id',
        'partner_id', 'groom_id', 'bride_id', 'pet_id')
)
SELECT format('GRANT SELECT, INSERT, DELETE ON %I.%I TO %I', n.nspname, cl.relname, :'editor_role')
FROM pg_class cl JOIN pg_namespace n ON n.oid = cl.relnamespace
WHERE cl.oid IN (SELECT oid FROM closure UNION SELECT oid FROM evidence UNION SELECT oid FROM bycol)
   OR (n.nspname = 'public' AND cl.relname = 'account_username_holds')
ORDER BY 1 \gexec

-- the delete locks the account and its characters (SELECT ... FOR UPDATE needs UPDATE on the table); the live editor
-- has these already for its ordinary edits
SELECT format('GRANT UPDATE ON public.accounts, public.character_base TO %I', :'editor_role') \gexec

-- the name hold is upserted (INSERT ... ON CONFLICT DO UPDATE); w4a-editor-grants.sql gives the same on live already
SELECT format('GRANT UPDATE ON public.account_username_holds TO %I', :'editor_role')
WHERE to_regclass('public.account_username_holds') IS NOT NULL \gexec

\set QUIET off
SELECT has_parameter_privilege(:'editor_role', 'session_replication_role', 'SET') AS replica_switch,
       (SELECT count(*) FROM information_schema.role_table_grants WHERE grantee = :'editor_role' AND privilege_type = 'DELETE') AS tables_with_delete;
