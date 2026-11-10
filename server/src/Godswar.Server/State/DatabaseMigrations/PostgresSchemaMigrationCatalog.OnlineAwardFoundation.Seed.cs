namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string OnlineAwardSeedSql =
        """
        INSERT INTO public.online_award_balance_revisions (
            revision, sha256, entry_count, source, created_by)
        VALUES (
            1,
            'A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE',
            4,
            'reviewed-online-award-balance-v1',
            'server-baseline-v1');

        INSERT INTO public.online_award_balance_entries (
            revision, reward_order, item_id, quantity, item_quality,
            bound, stack_cap)
        VALUES
            (1, 0, 10150, 1, 14, 0, 1),
            (1, 1, 10150, 4, 10, 0, 1),
            (1, 2, 10134, 5, 1, 0, 99),
            (1, 3, 11005, 5, 1, 0, 99);

        INSERT INTO public.online_award_balance_publication (
            family, revision, balance_sha256,
            publication_version, updated_by)
        VALUES (
            'online-award', 1,
            'A11516DCF5A5CAC3AAAC9CECEFF416C6510789F4386BBAF4C8D7CC9FB2B704FE',
            1, 'server-baseline-v1');

        COMMENT ON TABLE public.online_award_balance_entries IS
            'Immutable item grants owned by one sealed Online Award revision.';
        COMMENT ON TABLE public.online_award_balance_publication IS
            'Audited singleton CAS pointer; workers pin it until restart.';
        COMMENT ON TABLE public.online_award_publication_audit IS
            'Trigger-owned append-only publication evidence.';
        COMMENT ON TABLE public.online_award_claim_settlements IS
            'Immutable once-per-realm-day Online Award evidence.';
        """;
}
