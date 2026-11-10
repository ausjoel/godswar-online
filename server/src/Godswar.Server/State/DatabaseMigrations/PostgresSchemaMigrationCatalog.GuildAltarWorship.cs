namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// What one member has offered to one altar: the offering points that altar
    /// still holds for them.
    /// </summary>
    /// <remarks>
    /// The altar's page four takes a typed amount and spends the character's guild
    /// contribution at one point per contribution - <c>NF_L0_GH68</c> reads
    /// "1 點供奉需要 1 點的公会贡献" - and the points then drain hourly while the altar
    /// grants its own percentage bonus (<c>NF_L0_GH87</c>). The balance therefore
    /// belongs to a member and an altar rather than to the guild, and one member can
    /// hold only one balance per altar, which is what the primary key says.
    ///
    /// The hourly drain and the bonus the points buy are not applied yet: the client
    /// states the rate for the 10000-30000 and 30000-60000 bands only and points at
    /// its own website for anything above (the same <c>NF_L0_GH86</c>), so that part
    /// stays unimplemented rather than guessed. Only the points and the time they
    /// were last written are kept, so whatever drain is agreed later has what it
    /// needs.
    /// </remarks>
    private static PostgresSchemaMigration CreateGuildAltarWorshipState() =>
        new(
            "20260923_163_guild_altar_worship",
            "Persist each member's offering points per guild altar",
            """
            CREATE TABLE public.guild_altar_worship (
                guild_id bigint NOT NULL
                    REFERENCES public.guilds(id)
                    ON DELETE CASCADE,
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                building_type integer NOT NULL CHECK (building_type >= 0),
                points integer NOT NULL CHECK (points >= 0),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (guild_id, character_id, building_type)
            );

            CREATE INDEX ix_guild_altar_worship_character
                ON public.guild_altar_worship (character_id);

            COMMENT ON TABLE public.guild_altar_worship IS
                'Offering points one member holds on one altar, keyed by guild, member and altar. The offering spends character_base.consortia_contribute 1:1 (NF_L0_GH68); the hourly drain and the bonus those points buy (NF_L0_GH87) are not applied yet.';
            COMMENT ON COLUMN public.guild_altar_worship.points IS
                'Offering points left on this altar for this member, as of updated_at.';
            """);
}
