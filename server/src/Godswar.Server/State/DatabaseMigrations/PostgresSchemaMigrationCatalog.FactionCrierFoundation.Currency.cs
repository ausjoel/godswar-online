namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string FactionCrierCurrencySql =
        """
        ALTER TABLE public.character_base
            ADD COLUMN "BindingGold" integer NOT NULL DEFAULT 0,
            ADD COLUMN faction_crier_revision bigint NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_character_base_binding_gold_nonnegative
                CHECK ("BindingGold" BETWEEN 0 AND 2147483647),
            ADD CONSTRAINT ck_character_base_faction_crier_revision
                CHECK (faction_crier_revision >= 0);

        ALTER TABLE public.character_economy_baseline
            ADD COLUMN binding_gold bigint NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_character_economy_baseline_binding_gold
                CHECK (binding_gold BETWEEN 0 AND 2147483647);

        ALTER TABLE public.character_currency_ledger
            DROP CONSTRAINT ck_character_currency_ledger_currency,
            ADD CONSTRAINT ck_character_currency_ledger_currency CHECK (
                currency_code IN ('silver', 'gold', 'binding_gold')
            );

        CREATE OR REPLACE VIEW public.character_wallet_reconciliation AS
        WITH character_keys AS (
            SELECT character_id
            FROM public.character_economy_baseline
            UNION
            SELECT id
            FROM public.character_base
        ),
        ledger_summary AS (
            SELECT
                character_id,
                max(wallet_revision) AS maximum_ledger_revision,
                count(DISTINCT wallet_revision)::bigint
                    AS distinct_ledger_revisions,
                count(*)::bigint AS ledger_entry_count,
                COALESCE(sum(delta) FILTER (
                    WHERE currency_code = 'silver'
                ), 0)::bigint AS silver_delta,
                COALESCE(sum(delta) FILTER (
                    WHERE currency_code = 'gold'
                ), 0)::bigint AS gold_delta,
                COALESCE(sum(delta) FILTER (
                    WHERE currency_code = 'binding_gold'
                ), 0)::bigint AS binding_gold_delta
            FROM public.character_currency_ledger
            GROUP BY character_id
        )
        SELECT
            key_row.character_id,
            baseline_row.account_id AS baseline_account_id,
            character_row.account_id AS current_account_id,
            baseline_row.character_id IS NOT NULL AS baseline_present,
            character_row.id IS NOT NULL AS character_present,
            baseline_row.wallet_revision AS baseline_wallet_revision,
            character_row.wallet_revision AS current_wallet_revision,
            COALESCE(
                ledger_row.maximum_ledger_revision,
                0
            )::bigint AS maximum_ledger_revision,
            COALESCE(
                ledger_row.distinct_ledger_revisions,
                0
            )::bigint AS distinct_ledger_revisions,
            COALESCE(
                ledger_row.ledger_entry_count,
                0
            )::bigint AS ledger_entry_count,
            baseline_row.silver AS baseline_silver,
            character_row."Money"::bigint AS current_silver,
            (
                baseline_row.silver +
                COALESCE(ledger_row.silver_delta, 0)
            )::bigint AS expected_silver,
            baseline_row.gold AS baseline_gold,
            character_row."Stone"::bigint AS current_gold,
            (
                baseline_row.gold +
                COALESCE(ledger_row.gold_delta, 0)
            )::bigint AS expected_gold,
            (
                baseline_row.account_id IS NOT NULL
                AND baseline_row.account_id = character_row.account_id
            ) AS identity_matches,
            (
                COALESCE(ledger_row.distinct_ledger_revisions, 0) =
                COALESCE(ledger_row.maximum_ledger_revision, 0)
            ) AS revision_sequence_contiguous,
            (
                character_row.wallet_revision =
                COALESCE(ledger_row.maximum_ledger_revision, 0)
            ) AS revision_matches,
            (
                character_row."Money"::bigint =
                    baseline_row.silver +
                    COALESCE(ledger_row.silver_delta, 0)
                AND character_row."Stone"::bigint =
                    baseline_row.gold +
                    COALESCE(ledger_row.gold_delta, 0)
                AND character_row."BindingGold"::bigint =
                    baseline_row.binding_gold +
                    COALESCE(ledger_row.binding_gold_delta, 0)
            ) AS balances_match,
            (
                baseline_row.character_id IS NOT NULL
                AND character_row.id IS NOT NULL
                AND baseline_row.account_id = character_row.account_id
                AND COALESCE(ledger_row.distinct_ledger_revisions, 0) =
                    COALESCE(ledger_row.maximum_ledger_revision, 0)
                AND character_row.wallet_revision =
                    COALESCE(ledger_row.maximum_ledger_revision, 0)
                AND character_row."Money"::bigint =
                    baseline_row.silver +
                    COALESCE(ledger_row.silver_delta, 0)
                AND character_row."Stone"::bigint =
                    baseline_row.gold +
                    COALESCE(ledger_row.gold_delta, 0)
                AND character_row."BindingGold"::bigint =
                    baseline_row.binding_gold +
                    COALESCE(ledger_row.binding_gold_delta, 0)
            ) AS is_reconciled,
            baseline_row.binding_gold AS baseline_binding_gold,
            character_row."BindingGold"::bigint AS current_binding_gold,
            (
                baseline_row.binding_gold +
                COALESCE(ledger_row.binding_gold_delta, 0)
            )::bigint AS expected_binding_gold
        FROM character_keys key_row
        LEFT JOIN public.character_economy_baseline baseline_row
          ON baseline_row.character_id = key_row.character_id
        LEFT JOIN public.character_base character_row
          ON character_row.id = key_row.character_id
        LEFT JOIN ledger_summary ledger_row
          ON ledger_row.character_id = key_row.character_id;

        COMMENT ON COLUMN public.character_base."BindingGold" IS
            'Authoritative B-Gold balance; distinct from premium Gold in Stone.';
        COMMENT ON COLUMN public.character_base.faction_crier_revision IS
            'Strict aggregate version for committed Faction Crier mutations.';
        COMMENT ON COLUMN public.character_economy_baseline.binding_gold IS
            'B-Gold balance at wallet revision zero.';
        """;
}
