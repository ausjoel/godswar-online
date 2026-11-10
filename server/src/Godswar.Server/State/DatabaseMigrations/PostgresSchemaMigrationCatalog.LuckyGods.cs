namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Per-character state for the Wishing Pool's divine wish
    /// (<c>NPC_FLAG_SYS_LUCKYGODS = 50</c>): the daily allowance, the five-minute
    /// interval, the current streak and the experience the player has won but not
    /// claimed yet.
    /// </summary>
    /// <remarks>
    /// The client's own text sets the quota and the interval ("每5分钟可以许愿1次，
    /// 每天可以许愿10次", <c>NF_L0_L001</c>), and it says an unclaimed prize dies at
    /// the daily reset ("得到的经验奖励必须当天领取…第二天可就不算数啦"), which is why
    /// the row carries the realm day the counters belong to: a row from an earlier
    /// day reads as an untouched day, and a claim whose day has rolled over matches
    /// no row. One row per character keeps the whole check a single read and a
    /// single write.
    /// </remarks>
    private static PostgresSchemaMigration
        CreateLuckyGodsWishState() =>
        new(
            "20260922_162_lucky_gods_wish_state",
            "Persist authoritative per-character divine wish streak and unclaimed experience",
            """
            CREATE TABLE public.character_lucky_gods_wish (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                usage_date date NOT NULL,
                used_count integer NOT NULL
                    DEFAULT 0
                    CHECK (used_count >= 0 AND used_count <= 10),
                last_used_at timestamptz NOT NULL,
                streak integer NOT NULL
                    DEFAULT 0
                    CHECK (streak >= 0),
                pending_experience integer NOT NULL
                    DEFAULT 0
                    CHECK (pending_experience >= 0),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (character_id),
                CONSTRAINT ck_character_lucky_gods_wish_future
                    CHECK (last_used_at <= updated_at)
            );

            COMMENT ON TABLE
                public.character_lucky_gods_wish IS
                'Authoritative divine-wish state: usage_date is the realm day the count belongs to, pending_experience is the unclaimed prize pool the client says expires with the day.';
            """);
}
