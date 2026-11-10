namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateWonderlandChestClaims() => new(
        "20260916_152_wonderland_chest_claims",
        "Claim native Wonderland island sacks once with atomic inventory evidence",
        """
        CREATE TABLE public.wonderland_chest_claims (
            world_instance_id uuid NOT NULL,
            island_number smallint NOT NULL CHECK (island_number BETWEEN 1 AND 8),
            character_id integer NOT NULL,
            account_id integer NOT NULL CHECK (account_id > 0),
            realm_id smallint NOT NULL CHECK (realm_id > 0),
            party_camp smallint NOT NULL CHECK (party_camp IN (0,1)),
            milestone_hash varchar(64) NOT NULL CHECK (milestone_hash ~ '^[0-9A-F]{64}$'),
            reward_policy varchar(64) NOT NULL CHECK (reward_policy = 'wonderland-native-sacks-v1'),
            rewards jsonb NOT NULL CHECK (jsonb_typeof(rewards) = 'array' AND jsonb_array_length(rewards) BETWEEN 1 AND 4),
            inventory_revision bigint NOT NULL CHECK (inventory_revision > 0),
            command_inbox_id bigint NOT NULL UNIQUE REFERENCES public.command_inbox(id),
            claimed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY (world_instance_id, island_number, character_id),
            UNIQUE (world_instance_id, island_number, account_id),
            FOREIGN KEY (world_instance_id, island_number, character_id)
                REFERENCES public.wonderland_title_members(world_instance_id, island_number, character_id)
        );
        """);
}
