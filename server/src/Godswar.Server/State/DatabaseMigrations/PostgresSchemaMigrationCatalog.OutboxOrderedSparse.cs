namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateOutboxOrderedSparsePolicy() => new(
            "20260830_122_outbox_ordered_sparse",
            "Preserve ordered validation for sparse shared revision streams",
            """
            ALTER TABLE public.outbox_events
                DROP CONSTRAINT ck_outbox_events_ordering,
                ADD CONSTRAINT ck_outbox_events_ordering CHECK (
                    ordering_policy IN (
                        'strict',
                        'latest_wins',
                        'ordered_sparse'
                    )
                );

            ALTER TABLE public.outbox_consumer_positions
                DROP CONSTRAINT ck_outbox_positions_ordering,
                ADD CONSTRAINT ck_outbox_positions_ordering CHECK (
                    ordering_policy IN (
                        'strict',
                        'latest_wins',
                        'ordered_sparse'
                    )
                );

            LOCK TABLE public.outbox_events
                IN SHARE ROW EXCLUSIVE MODE;
            LOCK TABLE public.outbox_consumer_positions
                IN SHARE ROW EXCLUSIVE MODE;

            DO $validate_sparse_outbox_conversion$
            DECLARE
                target_consumers constant text[] := ARRAY[
                    'inventory_projection_v1',
                    'progression_reward_projection_v1'
                ];
            BEGIN
                IF current_setting('session_replication_role') <> 'origin'
                THEN
                    RAISE EXCEPTION
                        'Ordered-sparse conversion requires active triggers.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.outbox_events event
                    WHERE event.consumer_key = ANY(target_consumers)
                      AND event.ordering_policy NOT IN (
                          'strict',
                          'ordered_sparse'
                      )
                ) OR EXISTS (
                    SELECT 1
                    FROM public.outbox_consumer_positions position
                    WHERE position.consumer_key = ANY(target_consumers)
                      AND position.ordering_policy NOT IN (
                          'strict',
                          'ordered_sparse'
                      )
                ) THEN
                    RAISE EXCEPTION
                        'A sparse target already has an incompatible policy.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.outbox_events event
                    WHERE event.consumer_key = ANY(target_consumers)
                      AND event.lease_token IS NOT NULL
                ) OR EXISTS (
                    SELECT 1
                    FROM public.outbox_consumer_positions position
                    WHERE position.consumer_key = ANY(target_consumers)
                      AND position.inflight_event_id IS NOT NULL
                ) THEN
                    RAISE EXCEPTION
                        'Stop outbox workers before ordered-sparse conversion.';
                END IF;

                IF (
                    SELECT count(*)
                    FROM pg_trigger
                    WHERE tgrelid IN (
                        'public.outbox_events'::regclass,
                        'public.outbox_consumer_positions'::regclass
                    )
                      AND tgname IN (
                          'trg_outbox_events_guard',
                          'trg_outbox_events_lease_consistency',
                          'trg_outbox_consumer_positions_guard',
                          'trg_outbox_positions_lease_consistency'
                      )
                      AND tgenabled = 'O'
                ) <> 4 THEN
                    RAISE EXCEPTION
                        'Required outbox mutation guards are not enabled.';
                END IF;
            END;
            $validate_sparse_outbox_conversion$;

            -- Only the two identity-immutability triggers reject this
            -- one-time discriminator conversion. Deferred lease-consistency
            -- triggers remain active and validate the final paired state.
            ALTER TABLE public.outbox_events
                DISABLE TRIGGER trg_outbox_events_guard;
            ALTER TABLE public.outbox_consumer_positions
                DISABLE TRIGGER trg_outbox_consumer_positions_guard;

            UPDATE public.outbox_events event
            SET ordering_policy = 'ordered_sparse',
                available_at = CASE
                    WHEN event.delivered_at IS NULL
                         AND event.poisoned_at IS NULL
                        THEN clock_timestamp()
                    ELSE event.available_at
                END,
                state_changed_at = CASE
                    WHEN event.delivered_at IS NULL
                         AND event.poisoned_at IS NULL
                        THEN clock_timestamp()
                    ELSE event.state_changed_at
                END
            WHERE event.consumer_key IN (
                    'inventory_projection_v1',
                    'progression_reward_projection_v1'
                )
              AND event.ordering_policy = 'strict';

            UPDATE public.outbox_consumer_positions position
            SET ordering_policy = 'ordered_sparse',
                updated_at = clock_timestamp()
            WHERE position.consumer_key IN (
                    'inventory_projection_v1',
                    'progression_reward_projection_v1'
                )
              AND position.ordering_policy = 'strict';

            SET CONSTRAINTS ALL IMMEDIATE;

            ALTER TABLE public.outbox_events
                ENABLE TRIGGER trg_outbox_events_guard;
            ALTER TABLE public.outbox_consumer_positions
                ENABLE TRIGGER trg_outbox_consumer_positions_guard;

            DO $verify_sparse_outbox_conversion$
            DECLARE
                target_consumers constant text[] := ARRAY[
                    'inventory_projection_v1',
                    'progression_reward_projection_v1'
                ];
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.outbox_events event
                    WHERE event.consumer_key = ANY(target_consumers)
                      AND event.ordering_policy <> 'ordered_sparse'
                ) OR EXISTS (
                    SELECT 1
                    FROM public.outbox_consumer_positions position
                    WHERE position.consumer_key = ANY(target_consumers)
                      AND position.ordering_policy <> 'ordered_sparse'
                ) THEN
                    RAISE EXCEPTION
                        'Ordered-sparse policy conversion was incomplete.';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.outbox_events event
                    JOIN public.outbox_consumer_positions position
                      ON position.consumer_key = event.consumer_key
                     AND position.aggregate_type = event.aggregate_type
                     AND position.aggregate_key = event.aggregate_key
                    WHERE event.consumer_key = ANY(target_consumers)
                      AND position.ordering_policy <>
                            event.ordering_policy
                ) OR EXISTS (
                    SELECT 1
                    FROM public.outbox_events event
                    WHERE event.consumer_key = ANY(target_consumers)
                      AND event.lease_token IS NOT NULL
                ) OR EXISTS (
                    SELECT 1
                    FROM public.outbox_consumer_positions position
                    WHERE position.consumer_key = ANY(target_consumers)
                      AND position.inflight_event_id IS NOT NULL
                ) THEN
                    RAISE EXCEPTION
                        'Ordered-sparse conversion broke stream consistency.';
                END IF;

                IF (
                    SELECT count(*)
                    FROM pg_trigger
                    WHERE tgrelid IN (
                        'public.outbox_events'::regclass,
                        'public.outbox_consumer_positions'::regclass
                    )
                      AND tgname IN (
                          'trg_outbox_events_guard',
                          'trg_outbox_events_lease_consistency',
                          'trg_outbox_consumer_positions_guard',
                          'trg_outbox_positions_lease_consistency'
                      )
                      AND tgenabled = 'O'
                ) <> 4 THEN
                    RAISE EXCEPTION
                        'Outbox mutation guards changed during conversion.';
                END IF;
            END;
            $verify_sparse_outbox_conversion$;

            ALTER TABLE public.outbox_events
                ADD CONSTRAINT ck_outbox_events_sparse_consumer_policy
                CHECK (
                    consumer_key NOT IN (
                        'inventory_projection_v1',
                        'progression_reward_projection_v1'
                    ) OR ordering_policy = 'ordered_sparse'
                );

            ALTER TABLE public.outbox_consumer_positions
                ADD CONSTRAINT ck_outbox_positions_sparse_consumer_policy
                CHECK (
                    consumer_key NOT IN (
                        'inventory_projection_v1',
                        'progression_reward_projection_v1'
                    ) OR ordering_policy = 'ordered_sparse'
                );
            """);
}
