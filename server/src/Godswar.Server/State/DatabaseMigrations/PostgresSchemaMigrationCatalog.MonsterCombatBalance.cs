namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMonsterCombatBalance() => new(
        "20260916_154_monster_combat_balance",
        "Store adjustable dungeon boss critical resistance in PostgreSQL",
        """
        CREATE TABLE IF NOT EXISTS public.monster_combat_balance (
            map_id smallint NOT NULL CHECK (map_id BETWEEN 0 AND 255),
            template_key varchar(128) COLLATE "C" NOT NULL
                CHECK (template_key <> '' AND template_key = btrim(template_key)),
            display_name varchar(160) NOT NULL CHECK (btrim(display_name) <> ''),
            critical_resistance integer NOT NULL CHECK (critical_resistance >= 0),
            PRIMARY KEY (map_id, template_key)
        );

        COMMENT ON TABLE public.monster_combat_balance IS
            'Mutable monster tuning, validated against exact published boss templates at startup. '
            'Loaded with the world-content repeatable-read snapshot; restart the server after edits. '
            'Separate from immutable gameplay publication hashes.';
        COMMENT ON COLUMN public.monster_combat_balance.critical_resistance IS
            'Nonnegative critical resistance rating, not a percentage.';

        -- Explicit identities also work when migrations precede the first
        -- world-content publication. Do not overwrite later operator tuning.
        INSERT INTO public.monster_combat_balance (
            map_id, template_key, display_name, critical_resistance)
        VALUES
            (200, 'B_bossA_mage_010', 'Euryale', 100),
            (200, 'B_bossA_skeleton_001', 'Chrysaor', 100),
            (200, 'B_bossB_mage_010', 'Stheno', 100),
            (200, 'B_bossA_medusa_008', 'Medusa', 100),
            (204, 'B_bossAD_mage_010', 'Euryale', 100),
            (204, 'B_bossAD_skeleton_001', 'Chrysaor', 100),
            (204, 'B_bossBD_mage_010', 'Stheno', 100),
            (204, 'B_bossAD_medusa_008', 'Medusa', 100),
            (205, 'B_bossG_octopus_001', 'Dinna', 100),
            (205, 'B_bossGB_hydra_004', '3-headed Sea Serpent', 100),
            (205, 'B_bossG_wraith_002', 'Raging Spirit', 100),
            (205, 'B_bossGC_mage_017', 'Prophet', 100),
            (205, 'B_bossGD_octopus_001', 'Dinna the Sea Guard', 100),
            (207, 'B_boss_xerxer_001', 'Alpha Demon', 100),
            (207, 'B_bosse_dryad_001', 'Capritaur Derskey', 100),
            (207, 'B_bosse_gadsguard_007', 'Depraved Monkeyface', 100),
            (207, 'B_bosse_flamingo_001', 'Flame Rooster', 100),
            (207, 'B_bosse_male_001', 'Outrageous Rock Spirit', 100),
            (207, 'B_bosse_greecewarrior_001', 'Athenian Marshal Addis', 100),
            (207, 'B_bosse_greecewarrior_002', 'Spartan Marshal Knocker', 100),
            (207, 'B_bosse_dragon_014', 'Depraved Platinum Dragon', 100),
            (207, 'B_bosse_dracoladon_003', 'Iberian Multi-headed', 100),
            (207, 'B_bosse_bull_001', 'Distraught Minotaur', 100),
            (207, 'B_bosse_pan_002', 'Titan''s X''mas Deer', 100),
            (207, 'B_bossf_dracoladon_003', 'Iberian Dragon King', 100),
            (207, 'B_bosse_kingofscorpion_01', 'Scorpion King', 100)
        ON CONFLICT (map_id, template_key) DO NOTHING;
        """);
}
