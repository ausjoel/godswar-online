namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The character's own guild duty.
    /// </summary>
    /// <remarks>
    /// The attribute panel does not read the guild member list: its
    /// <c>Positionpro</c> row (公会职位, from <c>Text/Message.dat</c>) is filled
    /// from the character's persisted attributes, which travel with the login
    /// snapshot. The duty therefore lives on the character as well as on the
    /// membership row, and both are written inside the same transaction so a
    /// character can never show a duty its membership does not have.
    ///
    /// Zero is the client's own "none" (Consortia_Job.ini lists 0 = 无), so it is
    /// the default for every character that is not in a guild.
    /// </remarks>
    private static PostgresSchemaMigration CreateCharacterGuildDuty() =>
        new(
            "20260916_160_character_guild_duty",
            "Persist each character's guild duty",
            """
            ALTER TABLE public.character_base
                ADD COLUMN guild_duty smallint NOT NULL DEFAULT 0,
                ADD CONSTRAINT fk_character_base_guild_duty
                    FOREIGN KEY (guild_duty)
                    REFERENCES public.guild_duties(duty)
                    ON DELETE RESTRICT;

            COMMENT ON COLUMN public.character_base.guild_duty IS
                'The character''s guild duty (Consortia_Job.ini: 0 无, 1 见习会员 ... 6 会长). Kept in step with guild_members.duty inside the same transaction; the attribute panel reads this persisted value.';
            """);
}
