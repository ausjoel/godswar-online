namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateAtlantisCompletionRewards() => new(
        "20260908_143_atlantis_completion_rewards",
        "Persist admitted Atlantis completion HardPoints and native titles",
        """
        CREATE TABLE public.atlantis_completion_rewards (
            world_instance_id uuid PRIMARY KEY,
            admission_reservation_id uuid NOT NULL UNIQUE,
            realm_id smallint NOT NULL CHECK (realm_id > 0),
            request_hash varchar(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            policy_revision varchar(64) NOT NULL CHECK (policy_revision = 'atlantis-completion-v1'),
            started_at_ticks bigint NOT NULL CHECK (started_at_ticks > 0),
            completed_at_ticks bigint NOT NULL CHECK (completed_at_ticks >= started_at_ticks),
            final_score integer NOT NULL CHECK (final_score = 850),
            hard_points integer NOT NULL CHECK (hard_points = 2800),
            title_id integer NOT NULL CHECK (title_id IN (5013, 5014)),
            admitted_character_ids integer[] NOT NULL CHECK (cardinality(admitted_character_ids) BETWEEN 1 AND 5),
            character_ids integer[] NOT NULL CHECK (cardinality(character_ids) BETWEEN 1 AND 5),
            settled_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            UNIQUE (world_instance_id, title_id),
            CHECK (completed_at_ticks - started_at_ticks < 24000000000),
            CHECK (character_ids <@ admitted_character_ids),
            CHECK ((cardinality(admitted_character_ids) = 1 AND title_id = 5014) OR
                   (cardinality(admitted_character_ids) BETWEEN 2 AND 5 AND title_id = 5013))
        );

        -- Member rows retain immutable acquisition evidence independently of
        -- later character lifecycle cleanup; settlement validates identity.
        CREATE TABLE public.atlantis_completion_reward_members (
            world_instance_id uuid NOT NULL,
            account_id integer NOT NULL CHECK (account_id > 0),
            character_id integer NOT NULL CHECK (character_id > 0),
            camp smallint NOT NULL CHECK (camp IN (0, 1)),
            honor_before integer NOT NULL CHECK (honor_before >= 0),
            honor_after integer NOT NULL CHECK (honor_after::bigint - honor_before = 2800),
            reward_revision bigint NOT NULL CHECK (reward_revision > 0),
            awarded_title_id integer NOT NULL,
            PRIMARY KEY (world_instance_id, character_id),
            UNIQUE (world_instance_id, account_id),
            UNIQUE (world_instance_id, character_id, awarded_title_id),
            FOREIGN KEY (world_instance_id, awarded_title_id)
                REFERENCES public.atlantis_completion_rewards(world_instance_id, title_id)
        );

        CREATE TABLE public.atlantis_character_title_ownership (
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            title_id integer NOT NULL CHECK (title_id IN (5013, 5014)),
            source_world_instance_id uuid NOT NULL,
            acquired_at timestamptz NOT NULL,
            PRIMARY KEY (character_id, title_id),
            FOREIGN KEY (source_world_instance_id, character_id, title_id)
                REFERENCES public.atlantis_completion_reward_members(
                    world_instance_id, character_id, awarded_title_id)
        );
        """);
}
