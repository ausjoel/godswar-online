-- Editor "Wipe account": one narrow database function, installed once.
--
-- Why: the game keeps one row per settled kill, loot pick-up and claim, and a trigger on each table rejects every DELETE
-- ("... are immutable"), so no account that has ever killed a monster can be deleted. The editor's role (gweditor) is not the
-- tables' owner and must stay that way. This function is owned by the tables' owner (SECURITY DEFINER), takes ONE integer,
-- refuses an account that is online, switches off only the DELETE-blocking triggers of the listed evidence tables, deletes the
-- rows that belong to that account's characters, pets, items and the account itself, and switches the triggers back on
-- before it returns. It is the same transaction as the editor's account delete, so a failure anywhere undoes all of it.
-- Nothing else can reach the switched-off triggers: the transaction holds the table locks until it commits.
--
-- Run as the postgres superuser inside the game database (deploy-editor-changes.sh does this). Safe to run again.
-- Remove with: DROP FUNCTION public.gwadmin_wipe_account_evidence(integer);
\set ON_ERROR_STOP on
\set QUIET on
SELECT tableowner AS owner FROM pg_tables WHERE schemaname = 'public' AND tablename = 'accounts' \gset
SET ROLE :"owner";

CREATE OR REPLACE FUNCTION public.gwadmin_wipe_account_evidence(p_account_id integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $fn$
DECLARE
  tbls constant text[] := ARRAY['faction_crier_daily_claims', 'faction_crier_exchange_settlements', 'faction_crier_weekly_reclaims',
    'monster_death_reward_settlements', 'monster_death_pet_experience', 'monster_loot_pickup_claims',
    'online_award_claim_settlements', 'medusa_completion_reward_members', 'wonderland_boss_loot_claims',
    'character_title_ownership', 'sealed_pet_items'];
  trg record;
  disabled jsonb := '[]';
  result jsonb := '{}';
  n bigint;
BEGIN
  IF p_account_id IS NULL OR p_account_id <= 0 THEN
    RAISE EXCEPTION 'gwadmin: bad account id';
  END IF;
  PERFORM 1 FROM accounts WHERE id = p_account_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'gwadmin: no account %', p_account_id;
  END IF;
  IF EXISTS (SELECT 1 FROM accounts WHERE id = p_account_id AND (login_presence_token IS NOT NULL OR login_status <> 0)) OR
     EXISTS (SELECT 1 FROM character_base WHERE account_id = p_account_id AND checkpoint_owner_id IS NOT NULL) THEN
    RAISE EXCEPTION 'gwadmin: account % is online; nothing wiped', p_account_id;
  END IF;

  FOR trg IN
    SELECT c.relname AS tbl, t.tgname FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace s ON s.oid = c.relnamespace
    WHERE s.nspname = 'public' AND c.relname = ANY (tbls) AND NOT t.tgisinternal AND t.tgenabled = 'O' AND (t.tgtype & 8) <> 0   -- DELETE
  LOOP
    EXECUTE format('ALTER TABLE public.%I DISABLE TRIGGER %I', trg.tbl, trg.tgname);
    disabled := disabled || jsonb_build_array(jsonb_build_array(trg.tbl, trg.tgname));
  END LOOP;

  -- children of the other evidence first; every statement names its account, characters, pets or items
  DELETE FROM sealed_pet_items WHERE owner_character_id IN (SELECT id FROM character_base WHERE account_id = p_account_id)
     OR item_instance_id IN (SELECT id FROM character_items WHERE user_id IN (SELECT id FROM character_base WHERE account_id = p_account_id))
     OR pet_id IN (SELECT id FROM character_pets WHERE user_id IN (SELECT id FROM character_base WHERE account_id = p_account_id));
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('sealed_pet_items', n);
  DELETE FROM monster_death_pet_experience WHERE account_id = p_account_id
     OR character_id IN (SELECT id FROM character_base WHERE account_id = p_account_id)
     OR pet_id IN (SELECT id FROM character_pets WHERE user_id IN (SELECT id FROM character_base WHERE account_id = p_account_id));
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('monster_death_pet_experience', n);
  DELETE FROM monster_loot_pickup_claims WHERE account_id = p_account_id
     OR character_id IN (SELECT id FROM character_base WHERE account_id = p_account_id);
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('monster_loot_pickup_claims', n);
  DELETE FROM monster_death_reward_settlements WHERE account_id = p_account_id;
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('monster_death_reward_settlements', n);
  DELETE FROM online_award_claim_settlements WHERE account_id = p_account_id;
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('online_award_claim_settlements', n);
  DELETE FROM faction_crier_daily_claims WHERE account_id = p_account_id;
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('faction_crier_daily_claims', n);
  DELETE FROM faction_crier_exchange_settlements WHERE account_id = p_account_id;
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('faction_crier_exchange_settlements', n);
  DELETE FROM faction_crier_weekly_reclaims WHERE account_id = p_account_id;
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('faction_crier_weekly_reclaims', n);
  DELETE FROM medusa_completion_reward_members WHERE character_id IN (SELECT id FROM character_base WHERE account_id = p_account_id);
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('medusa_completion_reward_members', n);
  DELETE FROM wonderland_boss_loot_claims WHERE character_id IN (SELECT id FROM character_base WHERE account_id = p_account_id);
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('wonderland_boss_loot_claims', n);
  DELETE FROM character_title_ownership WHERE character_id IN (SELECT id FROM character_base WHERE account_id = p_account_id);
  GET DIAGNOSTICS n = ROW_COUNT; result := result || jsonb_build_object('character_title_ownership', n);

  FOR trg IN SELECT e->>0 AS tbl, e->>1 AS tgname FROM jsonb_array_elements(disabled) e LOOP
    EXECUTE format('ALTER TABLE public.%I ENABLE TRIGGER %I', trg.tbl, trg.tgname);
  END LOOP;
  RETURN result;
END
$fn$;

RESET ROLE;
REVOKE ALL ON FUNCTION public.gwadmin_wipe_account_evidence(integer) FROM PUBLIC;
SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gweditor')
            THEN 'GRANT EXECUTE ON FUNCTION public.gwadmin_wipe_account_evidence(integer) TO gweditor' END AS stmt \gexec
COMMENT ON FUNCTION public.gwadmin_wipe_account_evidence(integer) IS
  'Editor Wipe account: deletes one offline account''s kill, loot and claim evidence (the delete-blocking triggers are off only inside this call). See GodsWar-VPS/tools/gwadmin-web/sql/wipe-account-evidence.sql.';
\set QUIET off
SELECT p.proname, pg_get_userbyid(p.proowner) AS owner, p.prosecdef AS security_definer
FROM pg_proc p WHERE p.proname = 'gwadmin_wipe_account_evidence';
