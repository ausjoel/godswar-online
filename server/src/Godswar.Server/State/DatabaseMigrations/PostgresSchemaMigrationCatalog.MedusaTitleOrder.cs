namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    // MEDUSA-TITLES (owner's call 2026-11-22): the fastest Advanced Medusa clear gives Medusa Executioners
    // (<= 10 min, +3%), then Slayers (<= 15 min, +2%), then Challengers (<= 20 min, +1%). This matches the stock
    // client text (LuaText FB_0001-3). Migration 117 seeded the opposite order and cannot be edited (its checksum
    // is fixed in every database that ran it), so this migration swaps the order for databases that still have
    // the 117 seed: fresh installs and the public baseline. Live was patched by hand the same day
    // (ops/db-patches/2026-11-22-medusa-title-order.sql), so there this is a no-op.
    // Tier numbers and client title ids stay (1 = Challengers 5011, 3 = Executioners 5009); the 10- and
    // 20-minute rules and the stat basis points move between tiers 1 and 3.
    // It only acts when ALL of the old seed is still in place (both rules and all eight basis points), so a
    // database that is already swapped, or that an admin has edited, is left exactly as it is.
    // Rollback to an older build: DELETE FROM public.schema_migrations
    //   WHERE migration_id = '20261122_244_medusa_title_order'; (the data can stay swapped).
    private static PostgresSchemaMigration CreateMedusaTitleOrder() => new(
        "20261122_244_medusa_title_order",
        "Medusa Advanced titles: fastest clear gives Executioners (+3%), Challengers is the slowest (+1%)",
        """
        DO $medusa_title_order$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM public.medusa_completion_reward_rules
                WHERE difficulty = 2 AND reward_kind = 2 AND threshold = 600 AND title = 1)
            AND EXISTS (
                SELECT 1 FROM public.medusa_completion_reward_rules
                WHERE difficulty = 2 AND reward_kind = 2 AND threshold = 1200 AND title = 3)
            AND EXISTS (
                SELECT 1 FROM public.medusa_reward_title_definitions
                WHERE title = 1
                  AND physical_attack_basis_points = 300 AND magic_attack_basis_points = 300
                  AND physical_defense_basis_points = 300 AND magic_defense_basis_points = 300)
            AND EXISTS (
                SELECT 1 FROM public.medusa_reward_title_definitions
                WHERE title = 3
                  AND physical_attack_basis_points = 100 AND magic_attack_basis_points = 100
                  AND physical_defense_basis_points = 100 AND magic_defense_basis_points = 100)
            THEN
                UPDATE public.medusa_completion_reward_rules SET title = 3, updated_at = now()
                WHERE difficulty = 2 AND reward_kind = 2 AND threshold = 600;
                UPDATE public.medusa_completion_reward_rules SET title = 1, updated_at = now()
                WHERE difficulty = 2 AND reward_kind = 2 AND threshold = 1200;
                UPDATE public.medusa_reward_title_definitions SET
                    physical_attack_basis_points = 100, magic_attack_basis_points = 100,
                    physical_defense_basis_points = 100, magic_defense_basis_points = 100,
                    updated_at = now()
                WHERE title = 1;
                UPDATE public.medusa_reward_title_definitions SET
                    physical_attack_basis_points = 300, magic_attack_basis_points = 300,
                    physical_defense_basis_points = 300, magic_defense_basis_points = 300,
                    updated_at = now()
                WHERE title = 3;
            END IF;
        END
        $medusa_title_order$;
        """);
}
