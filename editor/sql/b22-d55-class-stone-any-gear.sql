-- B22 d55 (the admin 2026-11-21: "allow"): the class-suit 5th stat (class stone, character_items.class_attribute1) may sit on
-- ANY gear, not only on the 144 class-suit pieces. Lifts the one database rule that refuses it; the editor detects the
-- change by itself (modules/_class_stat_anywhere.py) and then allows the stone on any gear.
--
-- Run ONCE as postgres in the game database, at any time (no restart, no player impact; it only removes a CHECK):
--   sudo -u postgres psql -d godswar -v ON_ERROR_STOP=1 -f d55-allow-class-stat-any-gear.sql
-- Checked on the rig copy godswar_b22_e4: the game server starts normally afterwards (no migration re-adds the CHECK;
-- it is created only by the already-applied ClassSuitAttributes migration) and the login dry run accepts a class
-- stone on a non-suit worn item.
-- Still in force (untouched): the stone must be one of 200/201/210/211/220/221/230/231, at most one per item, grade
-- 1-25, never in the append rows, and elemental attributes stay class-suit only.
-- Rollback (only while no non-suit item carries a stone): re-create it from the server source
--   (PostgresSchemaMigrationCatalog.ClassSuitAttributes.cs, ck_character_items_class_attribute_eligible_gear).
BEGIN;
ALTER TABLE public.character_items DROP CONSTRAINT IF EXISTS ck_character_items_class_attribute_eligible_gear;
SELECT count(*) AS still_there FROM pg_constraint
 WHERE conrelid = 'public.character_items'::regclass AND conname = 'ck_character_items_class_attribute_eligible_gear';
COMMIT;
