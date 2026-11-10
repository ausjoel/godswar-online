namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Whether a drop binds to the character that picks it up.
    /// </summary>
    /// <remarks>
    /// Per rule, not per item: the same item can be a bound reward from one
    /// monster and a tradeable drop from another, which is what the GM tool needs
    /// to express. <c>NULL</c> keeps following the item template's own
    /// <c>BindType</c>, so every existing rule behaves exactly as it did.
    /// </remarks>
    private static PostgresSchemaMigration CreateMonsterLootBindOnPickup() =>
        new(
            "20260927_213_monster_loot_bind_on_pickup",
            "Let a loot rule choose whether its drop binds on pickup",
            """
            ALTER TABLE public.monster_loot_rules
                ADD COLUMN IF NOT EXISTS bound_on_pickup boolean NULL;

            COMMENT ON COLUMN public.monster_loot_rules.bound_on_pickup IS
                'NULL follows the item template BindType; true binds the picked-up item to its owner, false leaves it tradeable.';
            """);
}
