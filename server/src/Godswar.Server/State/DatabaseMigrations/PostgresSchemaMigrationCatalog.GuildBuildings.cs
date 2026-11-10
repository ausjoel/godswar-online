namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// What the guild window needs beyond identity and funds: its buildings and
    /// the online member count it prints as "online / cap".
    /// </summary>
    /// <remarks>
    /// The building array travelled on the wire before it was understood: the
    /// client renders <c>BASE_INFO +0x164</c> (count) and <c>+0x168</c>
    /// (eight-byte <c>(building type, level)</c> entries) as the guild's
    /// buildings, and a single building's level tops out at 12. Buildings are
    /// therefore rows rather than a column, so the count is the row count.
    ///
    /// The online count is a snapshot, not a source of truth: the server
    /// recomputes it from the live sessions every time it answers the guild
    /// window and stores the number it last sent, which is what the window
    /// showed. It is deliberately not treated as authority for anything else.
    /// </remarks>
    private static PostgresSchemaMigration CreateGuildBuildingState() =>
        new(
            "20260916_158_guild_buildings",
            "Persist guild buildings and the last published online count",
            """
            ALTER TABLE public.guilds
                ADD COLUMN online_count integer NOT NULL DEFAULT 0
                    CHECK (online_count >= 0);

            CREATE TABLE public.guild_buildings (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                guild_id bigint NOT NULL
                    REFERENCES public.guilds(id)
                    ON DELETE CASCADE,
                slot smallint NOT NULL CHECK (slot >= 0),
                building_type integer NOT NULL CHECK (building_type >= 0),
                level smallint NOT NULL CHECK (level BETWEEN 1 AND 12),
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                CONSTRAINT uq_guild_buildings_slot UNIQUE (guild_id, slot)
            );

            CREATE INDEX ix_guild_buildings_guild
                ON public.guild_buildings (guild_id);

            COMMENT ON COLUMN public.guilds.online_count IS
                'Members online as of the last guild window answer. Recomputed from live sessions on every push; not authority for anything else.';
            COMMENT ON TABLE public.guild_buildings IS
                'Guild buildings as the window lists them: BASE_INFO +0x164 is the count and +0x168 holds (building_type, level) pairs. A building''s level is 1..12.';
            """);
}
