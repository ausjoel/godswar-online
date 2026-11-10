namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateWonderlandBossLootClaims() => new(
        "20260916_153_wonderland_boss_loot_claims",
        "Claim Wonderland boss sacks from corpses and four gems from island treasure boxes",
        """
        CREATE TABLE public.wonderland_boss_loot_claims (
            world_instance_id uuid NOT NULL,
            boss_object_id integer NOT NULL CHECK (boss_object_id > 0),
            spawn_generation integer NOT NULL CHECK (spawn_generation = 1),
            death_event_id uuid NOT NULL,
            island_number smallint NOT NULL CHECK (island_number BETWEEN 1 AND 8),
            character_id integer NOT NULL REFERENCES public.character_base(id),
            account_id integer NOT NULL CHECK (account_id > 0),
            realm_id smallint NOT NULL CHECK (realm_id > 0),
            party_camp smallint NOT NULL CHECK (party_camp IN (0,1)),
            sack_item_id integer NOT NULL REFERENCES public.item_templates(id),
            request_hash varchar(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            evidence jsonb NOT NULL CHECK (jsonb_typeof(evidence) = 'object'),
            inventory_revision bigint NOT NULL CHECK (inventory_revision > 0),
            command_inbox_id bigint NOT NULL REFERENCES public.command_inbox(id),
            claimed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            PRIMARY KEY (world_instance_id,boss_object_id,character_id),
            UNIQUE (world_instance_id,boss_object_id,account_id)
        );
        ALTER TABLE public.wonderland_chest_claims
            DROP CONSTRAINT wonderland_chest_claims_reward_policy_check;
        ALTER TABLE public.wonderland_chest_claims
            ADD CONSTRAINT wonderland_chest_claims_reward_policy_check
            CHECK (reward_policy IN ('wonderland-native-sacks-v1','wonderland-chest-gems-v1'));
        """);
}
