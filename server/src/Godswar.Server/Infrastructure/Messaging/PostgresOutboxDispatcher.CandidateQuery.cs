namespace Godswar.Server.Infrastructure.Messaging;

internal sealed partial class PostgresOutboxDispatcher
{
    internal const string CandidateQuerySql =
        """
        WITH registered(consumer_key) AS (
            SELECT unnest(@consumer_keys::text[])
        ),
        stream_heads AS MATERIALIZED (
            SELECT
                p.consumer_key,
                p.aggregate_type,
                p.aggregate_key,
                first_candidate.id AS event_row_id,
                first_candidate.state_changed_at AS event_state_changed_at
            FROM public.outbox_consumer_positions AS p
            INNER JOIN registered AS r
                ON r.consumer_key = p.consumer_key
            CROSS JOIN LATERAL (
                (
                    SELECT ordered_head.id, ordered_head.state_changed_at
                    FROM (
                        SELECT
                            candidate.id,
                            candidate.state_changed_at,
                            candidate.available_at,
                            candidate.lease_token,
                            candidate.poisoned_at
                        FROM public.outbox_events AS candidate
                        WHERE p.ordering_policy = 'ordered_sparse'
                          AND candidate.consumer_key = p.consumer_key
                          AND candidate.aggregate_type = p.aggregate_type
                          AND candidate.aggregate_key = p.aggregate_key
                          AND candidate.delivered_at IS NULL
                        ORDER BY
                            candidate.aggregate_version,
                            candidate.id
                        LIMIT 1
                    ) AS ordered_head
                    WHERE ordered_head.poisoned_at IS NULL
                      AND ordered_head.lease_token IS NULL
                      AND ordered_head.available_at <= clock_timestamp()
                )
                UNION ALL
                (
                    SELECT strict_head.id, strict_head.state_changed_at
                    FROM (
                        SELECT
                            candidate.id,
                            candidate.state_changed_at,
                            candidate.available_at
                        FROM public.outbox_events AS candidate
                        WHERE p.ordering_policy = 'strict'
                          AND candidate.consumer_key = p.consumer_key
                          AND candidate.aggregate_type = p.aggregate_type
                          AND candidate.aggregate_key = p.aggregate_key
                          AND candidate.delivered_at IS NULL
                          AND candidate.poisoned_at IS NULL
                          AND candidate.lease_token IS NULL
                        ORDER BY
                            candidate.aggregate_version,
                            candidate.id
                        LIMIT 1
                    ) AS strict_head
                    WHERE strict_head.available_at <= clock_timestamp()
                )
                UNION ALL
                (
                    SELECT candidate.id, candidate.state_changed_at
                    FROM public.outbox_events AS candidate
                    WHERE p.ordering_policy NOT IN (
                              'strict',
                              'ordered_sparse')
                      AND candidate.consumer_key = p.consumer_key
                      AND candidate.aggregate_type = p.aggregate_type
                      AND candidate.aggregate_key = p.aggregate_key
                      AND candidate.delivered_at IS NULL
                      AND candidate.poisoned_at IS NULL
                      AND candidate.lease_token IS NULL
                      AND candidate.available_at <= clock_timestamp()
                    ORDER BY candidate.available_at, candidate.id
                    LIMIT 1
                )
            ) AS first_candidate
            WHERE p.inflight_event_id IS NULL
        )
        SELECT
            e.id,
            e.event_id,
            e.consumer_key,
            e.aggregate_type,
            e.aggregate_key,
            e.aggregate_version,
            e.event_type,
            e.contract_version,
            e.ordering_policy,
            e.payload::text,
            e.attempt_count,
            e.max_attempts,
            p.current_version,
            e.created_at,
            clock_timestamp()
        FROM stream_heads AS head
        INNER JOIN public.outbox_consumer_positions AS p
            ON p.consumer_key = head.consumer_key
           AND p.aggregate_type = head.aggregate_type
           AND p.aggregate_key = head.aggregate_key
        INNER JOIN public.outbox_events AS e
            ON e.id = head.event_row_id
           AND e.state_changed_at = head.event_state_changed_at
        WHERE p.inflight_event_id IS NULL
        ORDER BY e.available_at, e.id
        LIMIT 1
        FOR UPDATE OF e, p SKIP LOCKED;
        """;
}
