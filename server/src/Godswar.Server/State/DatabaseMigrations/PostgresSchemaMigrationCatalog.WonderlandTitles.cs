namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateWonderlandTitles() => new(
        "20260916_148_wonderland_titles",
        "Persist admitted Wonderland milestone title ownership without equipping",
        """
        CREATE TABLE public.wonderland_title_runs (
            world_instance_id uuid PRIMARY KEY,
            admission_reservation_id uuid NOT NULL UNIQUE,
            realm_id smallint NOT NULL CHECK (realm_id > 0),
            run_hash varchar(64) NOT NULL CHECK (run_hash ~ '^[0-9A-F]{64}$'),
            policy_revision varchar(64) NOT NULL CHECK (policy_revision = 'wonderland-titles-v1'),
            started_at_ticks bigint NOT NULL CHECK (started_at_ticks > 0),
            admitted_character_ids integer[] NOT NULL CHECK (cardinality(admitted_character_ids) BETWEEN 1 AND 5),
            admitted_account_ids integer[] NOT NULL,
            admitted_owner_ids uuid[] NOT NULL,
            admitted_owner_generations bigint[] NOT NULL,
            CHECK (cardinality(admitted_account_ids) = cardinality(admitted_character_ids)),
            CHECK (cardinality(admitted_owner_ids) = cardinality(admitted_character_ids)),
            CHECK (cardinality(admitted_owner_generations) = cardinality(admitted_character_ids))
        );
        CREATE TABLE public.wonderland_title_milestones (
            world_instance_id uuid NOT NULL REFERENCES public.wonderland_title_runs(world_instance_id),
            island_number smallint NOT NULL,
            title_id integer NOT NULL,
            request_hash varchar(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            cleared_at_ticks bigint NOT NULL CHECK (cleared_at_ticks > 0),
            character_ids integer[] NOT NULL CHECK (cardinality(character_ids) BETWEEN 1 AND 5),
            settled_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY (world_instance_id, island_number),
            UNIQUE (world_instance_id, island_number, title_id),
            CHECK ((island_number = 2 AND title_id = 5114) OR (island_number = 4 AND title_id = 5115) OR
                   (island_number = 6 AND title_id = 5116) OR (island_number = 7 AND title_id = 5117) OR
                   (island_number = 8 AND title_id = 5118))
        );
        CREATE TABLE public.wonderland_title_members (
            world_instance_id uuid NOT NULL,
            island_number smallint NOT NULL,
            account_id integer NOT NULL CHECK (account_id > 0),
            character_id integer NOT NULL CHECK (character_id > 0),
            eligible_owner_id uuid NOT NULL CHECK (eligible_owner_id <> '00000000-0000-0000-0000-000000000000'),
            eligible_owner_generation bigint NOT NULL CHECK (eligible_owner_generation > 0),
            title_id integer NOT NULL,
            honor_points integer NOT NULL CHECK (honor_points >= 0),
            selected_title_id integer NOT NULL CHECK (selected_title_id >= 0),
            reward_revision bigint NOT NULL CHECK (reward_revision >= 0),
            newly_owned boolean NOT NULL,
            PRIMARY KEY (world_instance_id, island_number, character_id),
            UNIQUE (world_instance_id, island_number, account_id),
            UNIQUE (world_instance_id, island_number, character_id, title_id),
            FOREIGN KEY (world_instance_id, island_number, title_id)
                REFERENCES public.wonderland_title_milestones(world_instance_id, island_number, title_id)
        );
        CREATE TABLE public.wonderland_character_title_ownership (
            character_id integer NOT NULL REFERENCES public.character_base(id) ON DELETE CASCADE,
            title_id integer NOT NULL CHECK (title_id BETWEEN 5114 AND 5118),
            source_world_instance_id uuid NOT NULL,
            source_island_number smallint NOT NULL,
            acquired_at timestamptz NOT NULL,
            PRIMARY KEY (character_id, title_id),
            FOREIGN KEY (source_world_instance_id, source_island_number, character_id, title_id)
                REFERENCES public.wonderland_title_members(world_instance_id, island_number, character_id, title_id)
        );
        """);
}
