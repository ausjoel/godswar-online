-- Candidate only. Run as schema owner after migration234, with psql -v editor_role=ACTUAL_EDITOR_ROLE.
-- Role is a quoted psql identifier; no blanket table/sequence grants.
\set ON_ERROR_STOP on
BEGIN;
GRANT SELECT ON public.editor_title_catalog, public.character_title_entitlements TO :"editor_role";
GRANT SELECT, INSERT, DELETE ON public.character_title_grants TO :"editor_role";
-- Generic Undo can restore metadata; identity changes require delete+insert.
GRANT UPDATE (granted_at, granted_by, note) ON public.character_title_grants TO :"editor_role";
COMMIT;
