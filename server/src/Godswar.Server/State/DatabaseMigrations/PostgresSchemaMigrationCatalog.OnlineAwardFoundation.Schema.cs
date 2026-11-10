namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string OnlineAwardSchemaSql =
        """
        ALTER TABLE public.character_base
            ADD COLUMN online_award_revision bigint NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_character_base_online_award_revision
                CHECK (online_award_revision >= 0);

        CREATE TABLE public.online_award_balance_revisions (
            revision bigint PRIMARY KEY,
            sha256 varchar(64) NOT NULL,
            entry_count smallint NOT NULL,
            source varchar(128) NOT NULL,
            created_by varchar(128) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            sealed_at timestamptz,
            CONSTRAINT uq_online_award_balance_revision_identity
                UNIQUE (revision, sha256),
            CONSTRAINT ck_online_award_balance_revision CHECK (revision > 0),
            CONSTRAINT ck_online_award_balance_sha256
                CHECK (sha256 ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_online_award_balance_entry_count
                CHECK (entry_count BETWEEN 1 AND 16),
            CONSTRAINT ck_online_award_balance_source
                CHECK (length(btrim(source)) BETWEEN 1 AND 128),
            CONSTRAINT ck_online_award_balance_created_by
                CHECK (length(btrim(created_by)) BETWEEN 1 AND 128),
            CONSTRAINT ck_online_award_balance_sealed_at
                CHECK (sealed_at IS NULL OR sealed_at >= created_at)
        );

        CREATE TABLE public.online_award_balance_entries (
            revision bigint NOT NULL REFERENCES
                public.online_award_balance_revisions(revision),
            reward_order smallint NOT NULL,
            item_id integer NOT NULL,
            quantity smallint NOT NULL,
            item_quality smallint NOT NULL,
            bound smallint NOT NULL,
            stack_cap smallint NOT NULL,
            PRIMARY KEY (revision, reward_order),
            CONSTRAINT uq_online_award_reward_identity UNIQUE (
                revision, item_id, item_quality, bound
            ),
            CONSTRAINT ck_online_award_reward_order
                CHECK (reward_order BETWEEN 0 AND 15),
            CONSTRAINT ck_online_award_reward_item_id CHECK (item_id > 0),
            CONSTRAINT ck_online_award_reward_quantity
                CHECK (quantity BETWEEN 1 AND 127),
            CONSTRAINT ck_online_award_reward_quality
                CHECK (item_quality BETWEEN 1 AND 16),
            CONSTRAINT ck_online_award_reward_bound CHECK (bound IN (0, 1)),
            CONSTRAINT ck_online_award_reward_stack_cap
                CHECK (stack_cap BETWEEN 1 AND 999)
        );

        CREATE TABLE public.online_award_balance_publication (
            family varchar(32) PRIMARY KEY,
            revision bigint NOT NULL,
            balance_sha256 varchar(64) NOT NULL,
            publication_version bigint NOT NULL,
            updated_by varchar(128) NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            CONSTRAINT fk_online_award_publication_identity
                FOREIGN KEY (revision, balance_sha256) REFERENCES
                    public.online_award_balance_revisions(revision, sha256),
            CONSTRAINT ck_online_award_publication_family
                CHECK (family = 'online-award'),
            CONSTRAINT ck_online_award_publication_version
                CHECK (publication_version > 0),
            CONSTRAINT ck_online_award_publication_sha256
                CHECK (balance_sha256 ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_online_award_publication_updated_by
                CHECK (length(btrim(updated_by)) BETWEEN 1 AND 128)
        );

        CREATE TABLE public.online_award_publication_audit (
            publication_version bigint PRIMARY KEY,
            previous_revision bigint,
            revision bigint NOT NULL,
            previous_sha256 varchar(64),
            balance_sha256 varchar(64) NOT NULL,
            changed_at timestamptz NOT NULL,
            changed_by varchar(128) NOT NULL,
            CONSTRAINT fk_online_award_audit_identity
                FOREIGN KEY (revision, balance_sha256) REFERENCES
                    public.online_award_balance_revisions(revision, sha256),
            CONSTRAINT ck_online_award_audit_version
                CHECK (publication_version > 0),
            CONSTRAINT ck_online_award_audit_previous CHECK (
                (previous_revision IS NULL) = (previous_sha256 IS NULL)
            ),
            CONSTRAINT ck_online_award_audit_sha256 CHECK (
                balance_sha256 ~ '^[0-9A-F]{64}$'
                AND (previous_sha256 IS NULL
                    OR previous_sha256 ~ '^[0-9A-F]{64}$')
            ),
            CONSTRAINT ck_online_award_audit_actor
                CHECK (length(btrim(changed_by)) BETWEEN 1 AND 128)
        );

        CREATE TABLE public.online_award_claim_settlements (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            realm_id integer NOT NULL REFERENCES public.server(id),
            account_id integer NOT NULL REFERENCES public.accounts(id),
            character_id integer NOT NULL,
            claim_day date NOT NULL,
            balance_revision bigint NOT NULL,
            balance_sha256 varchar(64) NOT NULL,
            item_content_revision varchar(64) NOT NULL,
            item_deltas jsonb NOT NULL,
            inventory_revision bigint NOT NULL,
            online_award_revision bigint NOT NULL,
            command_inbox_id bigint NOT NULL UNIQUE REFERENCES
                public.command_inbox(id),
            audit_id bigint NOT NULL UNIQUE REFERENCES public.command_audit(id),
            event_id uuid NOT NULL UNIQUE,
            claimed_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            UNIQUE (realm_id, character_id, claim_day),
            CONSTRAINT fk_online_award_claim_balance_identity
                FOREIGN KEY (balance_revision, balance_sha256) REFERENCES
                    public.online_award_balance_revisions(revision, sha256),
            CONSTRAINT fk_online_award_claim_character
                FOREIGN KEY (character_id, account_id) REFERENCES
                    public.character_economy_baseline(
                        character_id, account_id),
            CONSTRAINT fk_online_award_claim_item_revision
                FOREIGN KEY (item_content_revision) REFERENCES
                    public.item_template_content_revisions(revision),
            CONSTRAINT ck_online_award_settlement_balance_sha
                CHECK (balance_sha256 ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_online_award_settlement_item_revision
                CHECK (item_content_revision ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_online_award_settlement_item_deltas
                CHECK (jsonb_typeof(item_deltas) = 'array'
                    AND jsonb_array_length(item_deltas) BETWEEN 1 AND 16),
            CONSTRAINT ck_online_award_settlement_revisions
                CHECK (inventory_revision > 0
                    AND online_award_revision > 0)
        );

        CREATE INDEX ix_online_award_claim_account_day
            ON public.online_award_claim_settlements (
                realm_id, account_id, claim_day DESC);
        """;
}
