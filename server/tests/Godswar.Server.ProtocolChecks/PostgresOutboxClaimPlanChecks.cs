using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresOutboxClaimPlanChecks
{
    private const string MigrationId =
        "20260830_123_outbox_claim_candidate_index";

    public static void Run()
    {
        CheckMigration();
        CheckCandidateQuery();
    }

    private static void CheckMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;

        Check.True(
            HasOrderedFragments(
                sql,
                "CREATE INDEX ix_outbox_events_claimable_stream",
                "consumer_key",
                "aggregate_type",
                "aggregate_key",
                "available_at",
                "id") &&
            HasOrderedFragments(
                sql,
                "WHERE delivered_at IS NULL",
                "AND poisoned_at IS NULL",
                "AND lease_token IS NULL") &&
            HasOrderedFragments(
                sql,
                "CREATE INDEX ix_outbox_events_ordered_stream_version",
                "consumer_key",
                "aggregate_type",
                "aggregate_key",
                "aggregate_version",
                "id",
                "WHERE delivered_at IS NULL"),
            "claim indexes find scheduled and version-ordered stream heads without scanning terminal history");
        Check.True(
            !sql.Contains("DELETE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DROP ", StringComparison.OrdinalIgnoreCase),
            "the claim-plan migration changes no outbox data or existing index");
    }

    private static void CheckCandidateQuery()
    {
        var sql = PostgresOutboxDispatcher.CandidateQuerySql;

        Check.True(
            HasOrderedFragments(
                sql,
                "stream_heads AS MATERIALIZED",
                "FROM public.outbox_consumer_positions AS p",
                "CROSS JOIN LATERAL (",
                "p.ordering_policy = 'ordered_sparse'",
                "candidate.delivered_at IS NULL",
                "candidate.aggregate_version",
                "LIMIT 1",
                "ordered_head.poisoned_at IS NULL",
                "ordered_head.lease_token IS NULL",
                "ordered_head.available_at <= clock_timestamp()"),
            "ordered-sparse streams select their minimum undelivered version before checking poison, lease, or schedule state");
        Check.True(
            HasOrderedFragments(
                sql,
                "p.ordering_policy = 'strict'",
                "candidate.poisoned_at IS NULL",
                "candidate.lease_token IS NULL",
                "candidate.aggregate_version",
                "LIMIT 1",
                "strict_head.available_at <= clock_timestamp()"),
            "strict streams cannot skip a lower pending version because a later retry is due first");
        Check.True(
            HasOrderedFragments(
                sql,
                "p.ordering_policy NOT IN (",
                "'strict'",
                "'ordered_sparse'",
                "candidate.available_at <= clock_timestamp()",
                "ORDER BY candidate.available_at, candidate.id",
                "LIMIT 1"),
            "latest-wins streams retain chronological due-work scheduling");
        Check.True(
            sql.Contains(
                "FOR UPDATE OF e, p SKIP LOCKED",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FROM stream_heads AS head",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE p.inflight_event_id IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "e.state_changed_at = head.event_state_changed_at",
                StringComparison.Ordinal),
            "claiming locks one unchanged materialized stream head and its durable position while skipping concurrent owners");
    }

    private static bool HasOrderedFragments(
        string value,
        params string[] fragments)
    {
        var offset = 0;
        foreach (var fragment in fragments)
        {
            var found = value.IndexOf(
                fragment,
                offset,
                StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            offset = found + fragment.Length;
        }

        return true;
    }
}
