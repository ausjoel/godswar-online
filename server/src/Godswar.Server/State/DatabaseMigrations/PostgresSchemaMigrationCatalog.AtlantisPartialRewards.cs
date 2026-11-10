namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Atlantis pays by progress now, not only on completion: an ending run earns
    /// the operator table's incomplete tier for its team points, and only a run
    /// that reached the completion threshold earns the party award and its title.
    /// The original table hard-coded "850 points, 2800 HardPoints, a title", which
    /// rejected every partial settlement.
    /// </summary>
    /// <remarks>
    /// The original constraints were declared inline, so PostgreSQL generated their
    /// names. They are dropped by definition-independent discovery and re-added with
    /// explicit names, which keeps later migrations able to target them. Existing
    /// completed rows satisfy every replacement constraint, and the v1 policy
    /// revision stays accepted so already-settled runs remain readable.
    /// </remarks>
    private static PostgresSchemaMigration CreateAtlantisPartialRewards() => new(
        "20260930_217_atlantis_partial_rewards",
        "Pay Atlantis rewards by progress and allow an ending run to settle",
        """
        DO $$
        DECLARE constraint_name text;
        BEGIN
            FOR constraint_name IN
                SELECT con.conname
                FROM pg_constraint con
                JOIN pg_class rel ON rel.oid = con.conrelid
                JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                WHERE nsp.nspname = 'public'
                  AND rel.relname = 'atlantis_completion_rewards'
                  AND con.contype = 'c'
            LOOP
                EXECUTE format(
                    'ALTER TABLE public.atlantis_completion_rewards DROP CONSTRAINT %I',
                    constraint_name);
            END LOOP;

            FOR constraint_name IN
                SELECT con.conname
                FROM pg_constraint con
                JOIN pg_class rel ON rel.oid = con.conrelid
                JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                WHERE nsp.nspname = 'public'
                  AND rel.relname = 'atlantis_completion_reward_members'
                  AND con.contype = 'c'
            LOOP
                EXECUTE format(
                    'ALTER TABLE public.atlantis_completion_reward_members DROP CONSTRAINT %I',
                    constraint_name);
            END LOOP;
        END $$;

        ALTER TABLE public.atlantis_completion_rewards
            ADD CONSTRAINT ck_atlantis_completion_rewards_realm
                CHECK (realm_id > 0),
            ADD CONSTRAINT ck_atlantis_completion_rewards_request_hash
                CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            ADD CONSTRAINT ck_atlantis_completion_rewards_policy_revision
                CHECK (policy_revision IN ('atlantis-completion-v1', 'atlantis-completion-v2')),
            ADD CONSTRAINT ck_atlantis_completion_rewards_started_at
                CHECK (started_at_ticks > 0),
            ADD CONSTRAINT ck_atlantis_completion_rewards_completed_at
                CHECK (completed_at_ticks >= started_at_ticks),
            ADD CONSTRAINT ck_atlantis_completion_rewards_final_score
                CHECK (final_score BETWEEN 0 AND 850),
            ADD CONSTRAINT ck_atlantis_completion_rewards_hard_points
                CHECK (hard_points BETWEEN 200 AND 2800),
            ADD CONSTRAINT ck_atlantis_completion_rewards_title_id
                CHECK (title_id IN (0, 5013, 5014)),
            ADD CONSTRAINT ck_atlantis_completion_rewards_admitted
                CHECK (cardinality(admitted_character_ids) BETWEEN 1 AND 5),
            ADD CONSTRAINT ck_atlantis_completion_rewards_characters
                CHECK (cardinality(character_ids) BETWEEN 1 AND 5),
            ADD CONSTRAINT ck_atlantis_completion_rewards_duration
                CHECK (completed_at_ticks - started_at_ticks < 24000000000),
            ADD CONSTRAINT ck_atlantis_completion_rewards_characters_admitted
                CHECK (character_ids <@ admitted_character_ids),
            -- A title is granted only by a completed run, and its identity follows
            -- the registered party size: solo runs take the solo title and parties
            -- take the party title. An incomplete run grants no title.
            ADD CONSTRAINT ck_atlantis_completion_rewards_title_completion
                CHECK (title_id = 0 OR
                       (final_score = 850 AND
                        ((cardinality(admitted_character_ids) = 1 AND title_id = 5014) OR
                         (cardinality(admitted_character_ids) BETWEEN 2 AND 5 AND title_id = 5013)))),
            -- The tier paid by an incomplete run is fixed per score by the
            -- published table; the scores that may carry a title are the two the
            -- completion award defines.
            ADD CONSTRAINT ck_atlantis_completion_rewards_incomplete_tier
                CHECK (title_id <> 0 OR
                       hard_points IN (200, 600, 900, 1000, 1200, 1300, 1420, 1540,
                                       1660, 1820, 2000, 2200, 2400, 2800));

        ALTER TABLE public.atlantis_completion_reward_members
            ADD CONSTRAINT ck_atlantis_completion_reward_members_account
                CHECK (account_id > 0),
            ADD CONSTRAINT ck_atlantis_completion_reward_members_character
                CHECK (character_id > 0),
            ADD CONSTRAINT ck_atlantis_completion_reward_members_camp
                CHECK (camp IN (0, 1)),
            ADD CONSTRAINT ck_atlantis_completion_reward_members_honor_before
                CHECK (honor_before >= 0),
            -- The credited amount is the run's own award; the durable settle and
            -- its readback compare every member's delta with it.
            ADD CONSTRAINT ck_atlantis_completion_reward_members_honor_after
                CHECK (honor_after::bigint > honor_before),
            ADD CONSTRAINT ck_atlantis_completion_reward_members_revision
                CHECK (reward_revision > 0);
        """);
}
