namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateOutboxClaimCandidateIndex() => new(
            "20260830_123_outbox_claim_candidate_index",
            "Bound outbox claim scans to policy-aware registered stream heads",
            """
            CREATE INDEX ix_outbox_events_claimable_stream
                ON public.outbox_events (
                    consumer_key,
                    aggregate_type,
                    aggregate_key,
                    available_at,
                    id
                )
                WHERE delivered_at IS NULL
                  AND poisoned_at IS NULL
                  AND lease_token IS NULL;

            CREATE INDEX ix_outbox_events_ordered_stream_version
                ON public.outbox_events (
                    consumer_key,
                    aggregate_type,
                    aggregate_key,
                    aggregate_version,
                    id
                )
                WHERE delivered_at IS NULL;
            """);
}
