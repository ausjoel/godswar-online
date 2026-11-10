namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Brings every character's stored duty in step with its membership row.
    /// </summary>
    /// <remarks>
    /// The character column arrived after guilds already existed, so members
    /// created before it carry the default. A guild member's duty is the
    /// membership row's; a character without a membership has none, which the
    /// client's own table calls 0 = 无.
    /// </remarks>
    private static PostgresSchemaMigration CreateCharacterGuildDutyBackfill() =>
        new(
            "20260916_161_character_guild_duty_backfill",
            "Align stored character duties with memberships",
            """
            UPDATE public.character_base AS character
            SET guild_duty = membership.duty
            FROM public.guild_members AS membership
            WHERE membership.character_id = character.id
              AND character.guild_duty IS DISTINCT FROM membership.duty;

            UPDATE public.character_base AS character
            SET guild_duty = 0
            WHERE character.guild_duty <> 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM public.guild_members AS membership
                  WHERE membership.character_id = character.id);
            """);
}
