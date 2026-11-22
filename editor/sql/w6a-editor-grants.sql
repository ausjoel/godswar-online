-- W6A (wave 6 package A): the right the editor's database role needs for the "Player health" page.
--
-- public.session_health is created by the game server (migration 20261113_242_session_health) and owned by its
-- role, so the editor role cannot read it until this runs. The page only reads it. The Login check and its Fix
-- button need nothing new (they use character_items and sealed_pet_items, which the editor already reads and writes).
--
-- Run as the postgres superuser inside the game database AFTER the server release that applies migration 242
-- (safe to run again; skipped while the table does not exist):
--   sudo -u postgres psql -d godswar -v editor_role=gweditor -f w6a-editor-grants.sql
-- Remove with: REVOKE SELECT ON public.session_health FROM gweditor;
\set ON_ERROR_STOP on
\set QUIET on

SELECT format('GRANT SELECT ON public.session_health TO %I', :'editor_role')
WHERE to_regclass('public.session_health') IS NOT NULL \gexec
