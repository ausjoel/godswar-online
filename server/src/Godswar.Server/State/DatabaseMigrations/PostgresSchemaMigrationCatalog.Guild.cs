namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// The guild's own tables: one row per guild and one row per membership.
    /// </summary>
    /// <remarks>
    /// The rows carry what the server has to authorise and nothing else. The guild
    /// window's own numbers - the building ranks, the upgrade costs, the member cap
    /// each house level adds - live in the client's
    /// <c>Settings/Sys/Consortia.xml</c>, so the server keeps the guild's identity
    /// (name, lord, level, proclamation), its funds, and who belongs to it at which
    /// duty. The duty numbers are the client's own: <c>Consortia.dat</c> maps
    /// 1 to 见习会员 and 6 to 会长.
    ///
    /// A character can hold exactly one membership, so the membership table is keyed
    /// by the character rather than by a composite, which also makes "is this
    /// character already in a guild" a primary-key probe.
    /// </remarks>
    private static PostgresSchemaMigration CreateGuildState() =>
        new(
            "20260916_157_guild_state",
            "Persist guilds and their members",
            """
            CREATE TABLE public.guilds (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name character varying(64) NOT NULL,
                level integer NOT NULL DEFAULT 1 CHECK (level >= 1),
                member_limit integer NOT NULL DEFAULT 100 CHECK (member_limit > 0),
                lord_character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE RESTRICT,
                silver bigint NOT NULL DEFAULT 0 CHECK (silver >= 0),
                gold bigint NOT NULL DEFAULT 0 CHECK (gold >= 0),
                bijou integer NOT NULL DEFAULT 0 CHECK (bijou >= 0),
                proclamation character varying(256) NOT NULL DEFAULT '',
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                CONSTRAINT ck_guilds_name CHECK (btrim(name::text) <> '')
            );

            CREATE UNIQUE INDEX uq_guilds_name_lower
                ON public.guilds (lower(name::text));

            CREATE TABLE public.guild_members (
                character_id integer PRIMARY KEY
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                guild_id bigint NOT NULL
                    REFERENCES public.guilds(id)
                    ON DELETE CASCADE,
                duty smallint NOT NULL CHECK (duty BETWEEN 1 AND 6),
                joined_at timestamptz NOT NULL,
                contribution_silver bigint NOT NULL DEFAULT 0
                    CHECK (contribution_silver >= 0),
                contribution_gold bigint NOT NULL DEFAULT 0
                    CHECK (contribution_gold >= 0)
            );

            CREATE INDEX ix_guild_members_guild
                ON public.guild_members (guild_id);

            COMMENT ON TABLE public.guilds IS
                'Guild identity and funds. The building ranks and costs the client window shows come from the client''s own Settings/Sys/Consortia.xml.';
            COMMENT ON COLUMN public.guild_members.duty IS
                'Client duty id from Consortia.dat: 1 见习会员, 2 会员, 3 精英, 4 理事, 5 副会长, 6 会长.';
            """);
}
