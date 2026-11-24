-- EDITOR-SMOKE (POLISH-REVIEW #16, 2026-11-22): SELECT for the editor role on every table the account delete plan reads.
--
-- GET /api/m/character/account/<id>/delete-plan (Accounts page, "Delete..." preview) counts the rows of every table
-- that cascades from public.accounts (ON DELETE CASCADE closure) and of every table whose foreign key would block or
-- null the delete. Tables added by later waves (fortune teller log, marriages, mentors, battlefield, Pan labyrinth,
-- Lelantine farm, rank display, relations) are owned by the game server's role and the editor role was never granted
-- SELECT on them, so the preview failed for every account with "permission denied for table
-- character_fortune_teller_log" (found by editor/tools/smoke.py on a copy of the 2026-11-22 backup, as gweditor).
--
-- This grants SELECT only (no INSERT/UPDATE/DELETE: a real delete or wipe of an account that has rows in these tables
-- still needs its own decision). It computes the tables from the foreign keys, so it also covers tables a later wave
-- adds: safe to run again after any server release.
--
-- Run as the postgres superuser inside the game database:
--   sudo -u postgres psql -d godswar -v editor_role=gweditor -f editor-smoke-delete-plan-grants.sql
-- psql prints GRANT once per table it grants (nothing when all are readable already). Undo one with: REVOKE SELECT ON public.<table> FROM gweditor;
\set ON_ERROR_STOP on

WITH RECURSIVE fk AS (
    SELECT c.conrelid AS child, c.confrelid AS parent, c.confdeltype AS del
    FROM pg_constraint c
    WHERE c.contype = 'f'
), cascade_closure(t) AS (
    SELECT 'public.accounts'::regclass::oid
    UNION
    SELECT fk.child FROM fk JOIN cascade_closure cc ON fk.parent = cc.t WHERE fk.del = 'c'
), read_by_plan(t) AS (
    SELECT t FROM cascade_closure
    UNION
    SELECT fk.child FROM fk JOIN cascade_closure cc ON fk.parent = cc.t WHERE fk.del IN ('r', 'a', 'n')
)
SELECT format('GRANT SELECT ON %s TO %I', r.t::regclass, :'editor_role')
FROM read_by_plan r
JOIN pg_class k ON k.oid = r.t AND k.relkind IN ('r', 'p')
WHERE NOT has_table_privilege(:'editor_role', r.t, 'SELECT')
ORDER BY 1 \gexec
