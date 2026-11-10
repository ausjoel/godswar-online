namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The guild's own "refuse applications" switch.
    /// </summary>
    /// <remarks>
    /// The guild window's first tab carries a "Refuse the application?" checkbox
    /// (<c>RejectRequest</c> in <c>Consortia.xml</c>); ticking it makes the client
    /// send <c>10155</c> with its new state as a dword, and the guild list message
    /// carries the same switch per entry, because the row's Join cell is drawn with
    /// the client's <c>"ConReject"</c> text when it is set (measured 2026-10-02:
    /// the row builder branches on the entry's <c>+0x65</c> byte).
    /// </remarks>
    private static PostgresSchemaMigration CreateGuildRefuseApplications() =>
        new(
            "20261002_219_guild_refuse_applications",
            "Let a guild refuse applications to join",
            """
            ALTER TABLE public.guilds
                ADD COLUMN refuse_applications boolean NOT NULL DEFAULT false;

            COMMENT ON COLUMN public.guilds.refuse_applications IS
                'Set from the guild window''s "Refuse the application?" box (client opcode 10155) and sent back in every guild-list entry (10157, entry byte +0x65), where the client draws the Join cell with "ConReject".';
            """);
}
