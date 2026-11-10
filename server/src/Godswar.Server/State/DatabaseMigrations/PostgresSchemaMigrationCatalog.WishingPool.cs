namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Per-character state for the capital Wishing Pool's free skill-book wish.
    /// The client enforces one wish per hour and three per day, so the row keeps
    /// the day the count belongs to plus the last wish instant; the reader treats a
    /// stale day as a fresh count, which is the daily reset without any scheduled
    /// work. One row per character, so the cost stays a single read and a single
    /// upsert per wish.
    /// </summary>
    private static PostgresSchemaMigration
        CreateWishingPoolUsageState() =>
        new(
            "20260916_156_wishing_pool_usage_state",
            "Persist authoritative per-character wishing pool free-wish usage",
            """
            CREATE TABLE public.character_wishing_pool_usage (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                usage_date date NOT NULL,
                used_count integer NOT NULL
                    DEFAULT 0
                    CHECK (used_count >= 0),
                last_used_at timestamptz NOT NULL,
                last_skill_book_item_id integer NOT NULL
                    DEFAULT 0,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (character_id),
                CONSTRAINT ck_character_wishing_pool_usage_future
                    CHECK (last_used_at <= updated_at)
            );

            COMMENT ON TABLE
                public.character_wishing_pool_usage IS
                'Authoritative Wishing Pool free-wish usage: usage_date is the realm-calendar day the count belongs to, so a row from an earlier day reads as zero used wishes.';
            """);
}
