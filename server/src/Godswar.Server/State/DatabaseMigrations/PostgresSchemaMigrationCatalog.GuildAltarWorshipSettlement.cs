using Npgsql;

namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// When an altar's offering balance was last drained, which is what makes the
    /// hourly drain payable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The drain rate is fixed by the client's own table
    /// (<c>Consortia.xml</c> <c>BuildingConsume</c>: the rate is the
    /// <c>ConsumeValue</c> of the first row whose <c>ContributePoint</c> the
    /// balance has not passed), so the only thing the server has to remember is
    /// how much time it still owes the altar. <c>updated_at</c> cannot serve that
    /// purpose: it is the moment of the last offering, and an offering is exactly
    /// what should <em>not</em> look like elapsed time.
    /// </para>
    /// <para>
    /// Existing rows are backfilled from <c>updated_at</c>, so no balance is
    /// handed a free drain-free period by being older than the column. No points
    /// are touched by this migration.
    /// </para>
    /// </remarks>
    private static PostgresSchemaMigration CreateGuildAltarWorshipSettlement() =>
        new(
            "20260923_165_guild_altar_worship_settlement",
            "Track when each altar offering balance was last drained",
            """
            ALTER TABLE public.guild_altar_worship
                ADD COLUMN settled_at timestamptz NOT NULL DEFAULT now();

            UPDATE public.guild_altar_worship
            SET settled_at = updated_at;

            ALTER TABLE public.guild_altar_worship
                ALTER COLUMN settled_at DROP DEFAULT;

            COMMENT ON COLUMN public.guild_altar_worship.settled_at IS
                'The moment the hourly drain was last applied to this balance. The rate comes from guild_building_consume (NF_L0_GH87: the ConsumeValue of the first row whose ContributePoint the balance has not passed; the highest band applies above 1000000). updated_at is the last offering instead, so an offering never reads as elapsed time.';
            """);
}
