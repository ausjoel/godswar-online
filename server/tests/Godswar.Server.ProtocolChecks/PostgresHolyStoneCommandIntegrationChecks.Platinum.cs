using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertPlatinumSignetTransactionsAsync(string connectionString)
    {
        await using var source = NpgsqlDataSource.Create(connectionString);
        foreach (var (stoneId, roll) in new[] { (9030u, 20), (9031u, 19), (9032u, 99) })
        {
            const short catalystSlot = 17;
            var signet = SimpleItem(9054, stack: 2);
            var fixture = await CreateFixtureAsync(connectionString, "plat" + stoneId,
                target: SimpleItem(stoneId, grade: 7), stone: SimpleItem(9042, stack: 2),
                additionalBagItems: [(catalystSlot, signet)]);
            var random = new FixedUpgradeRandomSource(roll);
            var executor = CreateExecutor(source, upgradeRandomSource: random);
            var operation = Guid.NewGuid();
            var succeeded = roll < 20;
            var expectedStatus = succeeded ? HolyStoneCommandResultStatus.Upgraded :
                HolyStoneCommandResultStatus.UpgradeFailedProtected;
            var receipt = RequireReceipt(await ExecuteUpgradeAsync(executor, fixture, operation,
                catalystSlot, signet.ToCompactString()), HolyStoneExecutionDisposition.Committed,
                expectedStatus, "Platinum 7-to-8 transition");
            var replay = RequireReceipt(await executor.TryReplayAsync(fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(fixture.CharacterId),
                HolyStoneCommandOperation.Upgrade, operation), HolyStoneExecutionDisposition.Duplicate,
                expectedStatus, "Platinum receipt replay");
            Check.Equal(receipt, replay, "Platinum replay returns the stored result including protection evidence");
            Check.Equal(1, random.CallCount, "Platinum replay never resamples the roll");
            var target = (await ReadItemAsync(connectionString, fixture.CharacterId, 1,
                fixture.TargetSlot))!.Value.Item;
            var eclipse = (await ReadItemAsync(connectionString, fixture.CharacterId, 1,
                fixture.StoneSlot))!.Value.Item;
            var remaining = (await ReadItemAsync(connectionString, fixture.CharacterId, 1,
                catalystSlot))!.Value.Item;
            Check.True(target.Grade == (succeeded ? 8 : 7) && eclipse.Stack == 1 && remaining.Stack == 1,
                "Platinum consumes each catalyst once and preserves level seven on failure for every element");
            var state = await ReadStateAsync(connectionString, fixture, HolyStoneCommandOperation.Upgrade);
            Check.True(state.InventoryRevision == 1 && state.LedgerCount == 3 &&
                state.InboxCount == 1 && state.AuditCount == 1 && state.OutboxCount == 1 && state.DuplicateCount == 1,
                "Platinum stores exactly one complete transaction and a duplicate receipt");
            var audit = await ReadUpgradeAuditAsync(connectionString, fixture);
            Check.True(audit.Rate == 20 && audit.Roll == roll,
                "Platinum retains the reviewed ten-percent base plus ten-point catalyst bonus");
        }
    }
}
