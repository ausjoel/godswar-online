using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Infrastructure.OnlineAwards;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresOnlineAwardIntegrationChecks
{
    private static async Task<OnlineAwardBalanceSnapshot>
        AssertManagementAsync(
        NpgsqlDataSource dataSource,
        IItemTemplateCatalog items,
        IPetContentCatalog pets,
        OnlineAwardBalanceSnapshot startupPinned)
    {
        var store = new PostgresOnlineAwardBalanceSettingsStore(
            dataSource,
            items, pets);
        var invalidNull = await store.TryPublishSuccessorAsync(new(
            startupPinned.Revision,
            "online-award-check",
            null!));
        Check.Equal(
            (int)OnlineAwardBalanceUpdateStatus.Invalid,
            (int)invalidNull.Status,
            "management fails closed on a runtime-null reward list");

        var unchanged = await store.TryPublishSuccessorAsync(new(
            startupPinned.Revision,
            "online-award-check",
            BaselineRewards()));
        Check.True(
            unchanged.Status == OnlineAwardBalanceUpdateStatus.Unchanged &&
            unchanged.Snapshot?.Revision == startupPinned.Revision,
            "management no-op does not create a release");

        var invalidCap = BaselineRewards();
        invalidCap[2] = invalidCap[2] with { StackCap = 98 };
        Check.Equal(
            (int)OnlineAwardBalanceUpdateStatus.Invalid,
            (int)(await store.TryPublishSuccessorAsync(new(
                startupPinned.Revision,
                "online-award-check",
                invalidCap))).Status,
            "management validates reward capacity against pinned item content");

        var unsupportedEggAptitude = BaselineRewards();
        unsupportedEggAptitude[1] = unsupportedEggAptitude[1] with
        {
            ItemQuality = 16
        };
        Check.Equal(
            (int)OnlineAwardBalanceUpdateStatus.Invalid,
            (int)(await store.TryPublishSuccessorAsync(new(
                startupPinned.Revision,
                "online-award-check",
                unsupportedEggAptitude))).Status,
            "management rejects pet eggs without a native hatch profile");

        var balanceB = BaselineRewards();
        balanceB[2] = balanceB[2] with { Quantity = 6 };
        var publishedB = await store.TryPublishSuccessorAsync(new(
            startupPinned.Revision,
            "online-award-check-b",
            balanceB));
        Check.True(
            publishedB.Status == OnlineAwardBalanceUpdateStatus.Updated &&
            publishedB.Snapshot?.Revision == 2 &&
            publishedB.Snapshot.Sha256 != startupPinned.Sha256,
            "matching CAS publishes one sealed successor");

        var stale = await store.TryPublishSuccessorAsync(new(
            startupPinned.Revision,
            "online-award-stale",
            BaselineRewards()));
        Check.Equal(
            (int)OnlineAwardBalanceUpdateStatus.RevisionConflict,
            (int)stale.Status,
            "stale management CAS does not publish");

        var revertedA = await store.TryPublishSuccessorAsync(new(
            2,
            "online-award-check-revert-a",
            BaselineRewards()));
        Check.True(
            revertedA.Status == OnlineAwardBalanceUpdateStatus.Updated &&
            revertedA.Snapshot?.Revision == 3 &&
            revertedA.Snapshot.Sha256 == startupPinned.Sha256,
            "A to B to A publication can reuse a reviewed balance digest");

        var balanceD = BaselineRewards();
        balanceD[2] = balanceD[2] with { Quantity = 7 };
        var balanceE = BaselineRewards();
        balanceE[2] = balanceE[2] with { Quantity = 8 };
        var races = await Task.WhenAll(
            store.TryPublishSuccessorAsync(new(
                3, "online-award-race-d", balanceD)),
            store.TryPublishSuccessorAsync(new(
                3, "online-award-race-e", balanceE)));
        Check.True(
            races.Count(static result => result.Status ==
                OnlineAwardBalanceUpdateStatus.Updated) == 1 &&
            races.Count(static result => result.Status ==
                OnlineAwardBalanceUpdateStatus.RevisionConflict) == 1,
            "concurrent management successors yield one release and one conflict");

        var finalA = await store.TryPublishSuccessorAsync(new(
            4,
            "online-award-check-final-a",
            BaselineRewards()));
        Check.True(
            finalA.Status == OnlineAwardBalanceUpdateStatus.Updated &&
            finalA.Snapshot?.Revision == 5 &&
            finalA.Snapshot.Sha256 == startupPinned.Sha256,
            "management returns to the reviewed reward set after contention");

        var current = await new PostgresOnlineAwardBalanceSnapshotReader(
            dataSource,
            items, pets).ReadAsync();
        Check.True(
            current.Revision == 5 &&
            current.Sha256 == startupPinned.Sha256 &&
            startupPinned.Revision == 1,
            "new startup reads revision five while the running snapshot stays pinned");
        await AssertManagementEvidenceAsync(dataSource, current);
        return current;
    }

    private static async Task AssertManagementEvidenceAsync(
        NpgsqlDataSource dataSource,
        OnlineAwardBalanceSnapshot current)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT publication.revision,
                   publication.balance_sha256,
                   publication.publication_version,
                   count(audit.publication_version)::integer,
                   min(audit.publication_version),
                   max(audit.publication_version),
                   bool_and(revision.sealed_at IS NOT NULL)
            FROM public.online_award_balance_publication publication
            JOIN public.online_award_publication_audit audit ON true
            JOIN public.online_award_balance_revisions revision
              ON revision.revision = audit.revision
             AND revision.sha256 = audit.balance_sha256
            WHERE publication.family = 'online-award'
            GROUP BY publication.revision,
                     publication.balance_sha256,
                     publication.publication_version;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == current.Revision &&
            reader.GetString(1) == current.Sha256 &&
            reader.GetInt64(2) == 5 && reader.GetInt32(3) == 5 &&
            reader.GetInt64(4) == 1 && reader.GetInt64(5) == 5 &&
            reader.GetBoolean(6) && !await reader.ReadAsync(),
            "every management publication is sealed and has contiguous audit evidence");
    }
}
