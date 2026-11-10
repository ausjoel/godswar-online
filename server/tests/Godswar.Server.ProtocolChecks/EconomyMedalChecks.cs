using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

// ECONOMY r10: Wonderland island-clear Medals follow the IGG guide table and make the 1,200-Medal items reachable.
internal static class EconomyMedalChecks
{
    public const string CheckName = "Economy r10 Wonderland medals follow the guide table";

    public static async Task RunAsync()
    {
        CheckPolicy();
        // RELEASE: the medals rate defaults to 0 (off); the payment checks run with it at 1 (the guide table).
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.WonderlandMedals, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
        try
        {
            await CheckDecoupledFromTitlesAsync();
            await CheckStoreAsync();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
    }

    // A failing medal store must never hold back the island title, its notice or the run's retirement; the medals are
    // retried on the next Wonderland tick and then reach the online finisher's balance.
    private static async Task CheckDecoupledFromTitlesAsync()
    {
        var at = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        WonderlandTitleMember[] members = [new(1, 11, PlayerOwnershipTestFences.ForCharacter(11))];
        var instance = WorldInstanceId.New();
        var request = new WonderlandTitleRequest(instance, RealmId.Tempest, Guid.NewGuid(), at, at.AddMinutes(3), 2,
            members, members);
        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var character = new GameCharacter
        {
            Id = 11, AccountId = 1, Name = "WonderlandMedals", CurrentMap = 7, Level = 120,
            CurrentHp = 1000, MaxHp = 1000, CurrentMp = 100, MaxMp = 100, ExchangeMedal = 5
        };
        var registry = new GameSessionRegistry();
        GameHandlerOwnershipTestFences.Bind(registry, session, 1, character);
        registry.JoinMap(session, 1, character, WorldObjectIds.ForPlayer(11));
        var medals = new FlakyMedalStore();
        registry.ConfigureWonderlandTitles(new AppliedTitleStore());
        registry.ConfigureWonderlandMedals(medals);
        Check.True(registry.QueueWonderlandTitleMilestone(request), "island 2 milestone queued");
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(4), CancellationToken.None);
        Check.True(medals.Calls == 0 || medals.Failures == 1, "the medal store was tried at most once (and failed)");
        Check.True(!registry.HasPendingWonderlandTitles(instance) && character.OwnedTitleIds.Contains(5114u),
            "the island title settles and publishes even though the medal store failed");
        Check.Equal(5, character.ExchangeMedal, "no medals yet");
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(4).AddSeconds(1), CancellationToken.None);
        await registry.RetryWonderlandTitlesAsync(at.AddMinutes(4).AddSeconds(2), CancellationToken.None);
        Check.Equal(1, medals.Successes, "the next ticks pay the island's medals exactly once");
        Check.Equal(135, character.ExchangeMedal, "the online finisher's balance follows the store (5 + 130)");
        Check.True(registry.ForgetSettledWonderlandTitles(instance), "the run can retire");
    }

    private sealed class AppliedTitleStore : IWonderlandTitleStore
    {
        public Task<WonderlandTitleReceipt> SettleAsync(WonderlandTitleRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WonderlandTitleReceipt(WonderlandTitleStatus.Applied, request.WorldInstanceId, request.Award,
                request.FrozenMembers.Select(member =>
                    new WonderlandTitleReceiptMember(member.AccountId, member.CharacterId, 100, 0, 8, true)).ToArray()));
    }

    private sealed class FlakyMedalStore : IWonderlandMedalStore
    {
        public int Calls { get; private set; }
        public int Failures { get; private set; }
        public int Successes { get; private set; }
        private int _balance = 5;

        public Task<IReadOnlyList<WonderlandMedalAward>> AwardAsync(WonderlandTitleRequest request, int medalsPerMember,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                Failures++;
                throw new IOException("medal store unavailable");
            }

            var fresh = Successes == 0;
            if (fresh) _balance += medalsPerMember;
            Successes++;
            return Task.FromResult<IReadOnlyList<WonderlandMedalAward>>(request.FrozenMembers.Select(member =>
                new WonderlandMedalAward(member.AccountId, member.CharacterId, medalsPerMember, _balance, fresh)).ToArray());
        }
    }

    private static void CheckPolicy()
    {
        int[] expected = [0, 50, 130, 80, 90, 100, 110, 120, 570];
        for (var island = 1; island <= 8; island++)
            Check.Equal(expected[island], WonderlandMedalPolicy.Resolve(island), $"island {island} pays the guide's boss medals");
        Check.Equal(0, WonderlandMedalPolicy.Resolve(0), "no island 0");
        Check.Equal(0, WonderlandMedalPolicy.Resolve(9), "no island 9");
        var run = Enumerable.Range(1, 8).Sum(WonderlandMedalPolicy.Resolve);
        Check.Equal(1250, run, "a full run pays 1,250 medals");
        Check.True(run >= 1200, "one full run reaches a 1,200-medal item");
        Check.Equal(0m, ServerRateCatalog.Get(ServerRateKeys.WonderlandMedals).DefaultValue, "RELEASE: the rate defaults to 0 (off; 1 = the guide)");
        Check.Equal(0m, ServerRateCatalog.Get(ServerRateKeys.WonderlandMedals).MinimumValue, "the rate can be set to 0 (off)");

        // The three 1,200-medal Point Exchanger items stay priced 1,200 in Medals (compiled catalogue).
        foreach (var item in new uint[] { 9958, 9959, 14069 })
        {
            var found = false;
            for (var category = 0; category < 4 && !found; category++)
            for (var index = 0; index < 40 && !found; index++)
            {
                if (PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.PointExchanger, category, index, item, out var offer))
                {
                    found = true;
                    Check.Equal(1200, offer.UnitPrice, $"item {item} costs 1,200");
                    Check.True(offer.Currency == CapitalNpcShopCurrency.Medal, $"item {item} is paid in medals");
                }
            }

            Check.True(found, $"item {item} is listed at the Point Exchanger");
        }
    }

    // Runs only against a DISPOSABLE test database (GODSWAR_TEST_POSTGRES_CONNECTION_STRING): pays two active
    // characters once, replays the same island (no second payment), then restores their balances and removes the rows.
    private static async Task CheckStoreAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("GODSWAR_TEST_POSTGRES_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var characters = new List<(int Character, int Account, int Medals, long Revision)>();
        await using (var read = dataSource.CreateCommand(
            "SELECT id, account_id, COALESCE(exchange_medal, 0), wallet_revision FROM public.character_base " +
            "WHERE lifecycle_state = 'active' ORDER BY id LIMIT 2;"))
        await using (var reader = await read.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                characters.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt64(3)));
        if (characters.Count < 2 || characters.Select(c => c.Account).Distinct().Count() < 2) return;
        var instance = WorldInstanceId.New();
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        WonderlandTitleMember[] members = [.. characters.Select(c =>
            new WonderlandTitleMember(c.Account, c.Character, new PlayerOwnershipFence(Guid.NewGuid(), 1)))];
        var request = new WonderlandTitleRequest(instance, RealmId.Tempest, Guid.NewGuid(), start, start.AddMinutes(5), 2,
            members, members);
        var store = new PostgresWonderlandMedalStore(dataSource);
        try
        {
            var first = await store.AwardAsync(request, WonderlandMedalPolicy.Resolve(2));
            Check.Equal(2, first.Count(award => award.NewlyAwarded), "island 2 pays both finishers");
            foreach (var award in first)
            {
                var before = characters.Single(c => c.Character == award.CharacterId);
                Check.Equal(before.Medals + 130, award.MedalsAfter, "the balance grows by island 2's 130 medals");
            }

            var replay = await store.AwardAsync(request, WonderlandMedalPolicy.Resolve(2));
            Check.True(replay.All(award => !award.NewlyAwarded), "a replayed island pays nothing");
            foreach (var c in characters)
            {
                await using var balance = dataSource.CreateCommand(
                    $"SELECT exchange_medal, wallet_revision FROM public.character_base WHERE id = {c.Character};");
                await using var reader = await balance.ExecuteReaderAsync();
                await reader.ReadAsync();
                Check.Equal(c.Medals + 130, reader.GetInt32(0), "paid exactly once in the database");
                Check.Equal(c.Revision + 1, reader.GetInt64(1), "wallet_revision moved once");
            }
        }
        finally
        {
            foreach (var c in characters)
            {
                await using var restore = dataSource.CreateCommand(
                    $"UPDATE public.character_base SET exchange_medal = {c.Medals}, wallet_revision = {c.Revision} " +
                    $"WHERE id = {c.Character};");
                await restore.ExecuteNonQueryAsync();
            }

            await using var cleanup = dataSource.CreateCommand(
                $"DELETE FROM public.wonderland_medal_awards WHERE world_instance_id = '{instance.Value}';");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
