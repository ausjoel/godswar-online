using System.Text.RegularExpressions;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Rates;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

// RATES: checks for the server-rates patch (GodsWar-2.51/Polish).
internal static class ServerRatesChecks
{
    public const string CheckName =
        "RATES server-wide multipliers and limits";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        try
        {
            CheckCatalog();
            CheckIdentityAtDefaults();
            CheckSnapshotClamping();
            CheckScaling();
            CheckInstanceRewards();
            CheckZodiacEnergy();
            CheckMonsterLoot();
            await CheckPostgresTableAsync();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
    }

    // Runs only against a disposable test database (the same variable the
    // other PostgreSQL integration checks use). Tables it creates are dropped.
    private static async Task CheckPostgresTableAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        async Task<object?> Scalar(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            return await command.ExecuteScalarAsync();
        }

        var existed = await Scalar(
            "SELECT to_regclass('public.server_rates') IS NOT NULL;") is true;
        try
        {
            await PostgresServerRatesStore.EnsureAsync(dataSource);
            await PostgresServerRatesStore.EnsureAsync(dataSource);
            var first = await PostgresServerRatesStore.LoadAsync(dataSource);
            Check.Equal(
                ServerRateCatalog.Definitions.Count,
                first.Rows.Count(static row =>
                    ServerRateCatalog.TryGet(row.Key, out _)),
                "ensure seeds one row per rate and is idempotent");
            var revision = await PostgresServerRatesStore.ReadRevisionAsync(dataSource);
            await Scalar(
                "SET godswar.rates_actor = 'rates-check'; " +
                "UPDATE public.server_rates SET value = 2, updated_by = 'rates-check' " +
                "WHERE rate_key = 'exp.monster';");
            var after = await PostgresServerRatesStore.LoadAsync(dataSource);
            Check.Equal(revision + 1, after.Revision, "one UPDATE statement bumps the revision once");
            var snapshot = ServerRatesSnapshot.Create(
                after.Rows.Select(static row => KeyValuePair.Create(row.Key, row.Value)),
                after.Revision,
                after.UpdatedAtUtc,
                after.UpdatedBy,
                "database");
            Check.Equal(2m, snapshot.Get(ServerRateKeys.MonsterExperience), "the stored value loads");
            Check.True(
                await Scalar(
                    "SELECT count(*) FROM public.server_rates_log " +
                    "WHERE rate_key = 'exp.monster' AND old_value = 1 AND new_value = 2 " +
                    "AND changed_by = 'rates-check';") is 1L,
                "the change log records the update");
            Check.Throws<PostgresException>(
                () => Scalar(
                    "UPDATE public.server_rates SET value = 51 " +
                    "WHERE rate_key = 'exp.monster';").GetAwaiter().GetResult(),
                "the database refuses a value outside the bounds");
            await Scalar(
                "UPDATE public.server_rates SET max_value = 100, value = 80 " +
                "WHERE rate_key = 'exp.monster';");
            await PostgresServerRatesStore.EnsureAsync(dataSource);
            var reclamped = await PostgresServerRatesStore.LoadAsync(dataSource);
            Check.Equal(
                50m,
                reclamped.Rows.Single(static row => row.Key == "exp.monster").Value,
                "start-up restores the binary's bounds and clamps the stored value");
        }
        finally
        {
            if (!existed)
            {
                await Scalar(
                    "DROP TABLE IF EXISTS public.server_rates, " +
                    "public.server_rates_state, public.server_rates_log; " +
                    "DROP FUNCTION IF EXISTS public.server_rates_bump_revision(); " +
                    "DROP FUNCTION IF EXISTS public.server_rates_log_change();");
            }
        }
    }

    private static void CheckCatalog()
    {
        var keys = ServerRateCatalog.Definitions
            .Select(static definition => definition.Key)
            .ToArray();
        Check.Equal(
            keys.Length,
            keys.Distinct(StringComparer.Ordinal).Count(),
            "rate keys are unique");
        Check.True(
            keys.All(static key =>
                Regex.IsMatch(key, "^[a-z][a-z0-9_.]{2,63}$")),
            "rate keys satisfy the database key constraint");
        Check.True(
            ServerRateCatalog.Definitions.All(static definition =>
                definition.MinimumValue <= definition.DefaultValue &&
                definition.DefaultValue <= definition.MaximumValue &&
                definition.Step > 0 &&
                definition.Label.Length > 0 &&
                definition.Description.Length > 0),
            "every default lies inside its bounds and is described");
        Check.True(
            ServerRateCatalog.Definitions
                .Where(static definition =>
                    definition.Kind == ServerRateKind.Multiplier &&
                    definition.Key != ServerRateKeys.WonderlandMedals)
                .All(static definition => definition.DefaultValue == 1m),
            "every multiplier defaults to 1 (stock behaviour; RELEASE: the new Wonderland Medals multiplier defaults to 0 = off)");
        Check.Equal(
            2_000_000_000m,
            ServerRateCatalog.Get(ServerRateKeys.HolyBoxDailyLimit).DefaultValue,
            "Holy Box daily allowance defaults to the sealed 2,000,000,000");
        Check.Equal(
            400_000_000m,
            ServerRateCatalog.Get(ServerRateKeys.HolyBoxPerStoreMaximum).DefaultValue,
            "Holy Box per-store maximum defaults to the sealed 400,000,000");
        Check.Equal(
            70m,
            ServerRateCatalog.Get(ServerRateKeys.HolyBoxMinimumLevel).DefaultValue,
            "forger level gate defaults to the sealed 70");
    }

    private static void CheckIdentityAtDefaults()
    {
        ServerRates.ResetToDefaults();
        var rates = ServerRates.Current;
        Check.Equal(0, rates.NonDefaultKeys.Count, "defaults have no overrides");
        foreach (var value in new[] { -5, 0, 1, 7, 1_116, 99_999_999, int.MaxValue })
        {
            Check.Equal(
                value,
                rates.Multiply(ServerRateKeys.MonsterExperience, value, 100),
                $"rate 1 returns {value} unchanged even above the cap");
        }
        Check.Equal(
            5_000_000_000L,
            rates.Multiply(ServerRateKeys.HolyBoxCapacity, 5_000_000_000L, 10L),
            "rate 1 keeps long values unchanged");
        Check.Equal(
            37,
            rates.MultiplyPercent(ServerRateKeys.ForgeSuccess, 37),
            "rate 1 keeps a forge chance unchanged");
        Check.Equal(
            1_234,
            rates.MultiplyBasisPoints(ServerRateKeys.DropChance, 1_234),
            "rate 1 keeps a drop chance unchanged");
        Check.Equal(
            2_800,
            AtlantisCompletionRewardPolicy.Resolve(850, 1).HardPoints,
            "Atlantis completion pays the stock 2,800 at rate 1");
    }

    private static void CheckSnapshotClamping()
    {
        var warnings = new List<string>();
        var snapshot = ServerRatesSnapshot.Create(
            [
                KeyValuePair.Create(ServerRateKeys.MonsterExperience, 2m),
                KeyValuePair.Create(ServerRateKeys.PetMonsterExperience, 9_999_999m), // ROUND2: new bound 1,000,000
                KeyValuePair.Create(ServerRateKeys.DropQuantity, 0m),
                KeyValuePair.Create(ServerRateKeys.HolyBoxMinimumLevel, 55.9m),
                KeyValuePair.Create("no.such_rate", 3m)
            ],
            42,
            DateTimeOffset.UtcNow,
            "check",
            "test",
            warnings);
        Check.Equal(2m, snapshot.Get(ServerRateKeys.MonsterExperience), "a valid value is kept");
        Check.Equal(1_000_000m, snapshot.Get(ServerRateKeys.PetMonsterExperience), "a value above the bound is clamped"); // ROUND2
        Check.Equal(1m, snapshot.Get(ServerRateKeys.DropQuantity), "a value below the bound is clamped");
        Check.Equal(55m, snapshot.Get(ServerRateKeys.HolyBoxMinimumLevel), "an absolute limit is a whole number");
        Check.Equal(1m, snapshot.Get(ServerRateKeys.QuestExperience), "an absent key keeps its default");
        Check.Equal(42L, snapshot.Revision, "the database revision is kept");
        Check.True(
            warnings.Count == 3 &&
            warnings.Any(static warning => warning.Contains("no.such_rate", StringComparison.Ordinal)),
            "clamped and unknown values are reported");
    }

    private static void CheckScaling()
    {
        Install(
            (ServerRateKeys.MonsterExperience, 2m),
            (ServerRateKeys.PetMonsterExperience, 3m),
            (ServerRateKeys.MonsterTalentExperience, 0.5m),
            (ServerRateKeys.ForgeSuccess, 1.5m),
            (ServerRateKeys.DropChance, 3m));
        var rates = ServerRates.Current;
        Check.Equal(2_232, rates.Multiply(ServerRateKeys.MonsterExperience, 1_116), "EXP x2");
        Check.Equal(3_348, rates.Multiply(ServerRateKeys.PetMonsterExperience, 1_116), "pet EXP x3");
        Check.Equal(5, rates.Multiply(ServerRateKeys.MonsterTalentExperience, 10), "Talent EXP x0.5");
        Check.Equal(0, rates.Multiply(ServerRateKeys.MonsterTalentExperience, 1), "truncation can reach zero");
        Check.Equal(
            100_000_000,
            rates.Multiply(ServerRateKeys.MonsterExperience, 90_000_000, 100_000_000),
            "a scaled award saturates at the settlement cap");
        Check.Equal(-3, rates.Multiply(ServerRateKeys.MonsterExperience, -3), "non-positive amounts are untouched");
        Check.Equal(90, rates.MultiplyPercent(ServerRateKeys.ForgeSuccess, 60), "forge chance x1.5");
        Check.Equal(100, rates.MultiplyPercent(ServerRateKeys.ForgeSuccess, 90), "a chance never exceeds 100%");
        Check.Equal(10_000, rates.MultiplyBasisPoints(ServerRateKeys.DropChance, 5_000), "drop chance caps at 10000 bp");
        ServerRates.ResetToDefaults();
    }

    private static void CheckInstanceRewards()
    {
        Install(
            (ServerRateKeys.AtlantisHardPoints, 2m),
            (ServerRateKeys.MedusaHonor, 2m));
        Check.Equal(
            5_600,
            AtlantisCompletionRewardPolicy.Resolve(850, 1).HardPoints,
            "Atlantis completion x2 pays 5,600");
        Check.Equal(
            400,
            AtlantisCompletionRewardPolicy.Resolve(0, 3).HardPoints,
            "an incomplete Atlantis tier x2 pays 400");
        Check.True(
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                completed: true,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(10),
                out var medusa) &&
            medusa.HardPoints == 4_500 &&
            medusa.Title?.Title == MedusaEncounterTitle.MedusaChallengers,
            "Medusa honour x2 doubles the 2,250 tier and keeps the title");
        var request = new MedusaCompletionRewardRequest(
            WorldInstanceId.New(),
            RealmId.Tempest,
            MedusaEncounterDifficulty.Normal,
            completed: false,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            finalScore: 0,
            characterIds: [17]);
        Check.True(
            request.Award.HardPoints == 600 &&
            request.Award.NotificationText.Contains("600 honor", StringComparison.Ordinal),
            "the settlement request and its notice carry the doubled honour");
        ServerRates.ResetToDefaults();
        Check.Equal(
            300,
            new MedusaCompletionRewardRequest(
                WorldInstanceId.New(),
                RealmId.Tempest,
                MedusaEncounterDifficulty.Normal,
                completed: false,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(5),
                finalScore: 0,
                characterIds: [17]).Award.HardPoints,
            "rate 1 restores the stock 300 honour tier");
    }

    private static void CheckZodiacEnergy()
    {
        var policy = new ZodiacEnergyOptions().Snapshot();
        var calendar = RealmCalendar.CreateForTesting(
            RealmId.Tempest,
            "Asia/Manila");
        var start = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

        var stock = new GameCharacter { ZodiacLevel = 1 };
        var stockTick = ZodiacEnergyAccrual.Apply(
            stock,
            start,
            start.AddMinutes(5),
            policy,
            calendar);
        Install((ServerRateKeys.ZodiacEnergy, 2m));
        var doubled = new GameCharacter { ZodiacLevel = 1 };
        var doubledTick = ZodiacEnergyAccrual.Apply(
            doubled,
            start,
            start.AddMinutes(5),
            policy,
            calendar);
        Check.Equal(2_000, stockTick.GainedEnergyX100, "stock first tick grants 20 energy");
        Check.Equal(4_000, doubledTick.GainedEnergyX100, "zodiac x2 grants 40 energy");
        Check.Equal(40, doubled.ZodiacEnergy, "the doubled gain is stored");
        ServerRates.ResetToDefaults();
    }

    private static void CheckMonsterLoot()
    {
        var snapshot = new MonsterLootContentSnapshot(
            [new MonsterLootTableRule("RATES_check", 1)],
            [
                new MonsterLootRule("RATES_check", 0, 4001, 2_500, 1, 1),
                new MonsterLootRule("RATES_check", 1, 4002, 2_500, 2, 2)
            ]);
        var deaths = Enumerable.Range(1, 400)
            .Select(static index => new Guid(index, 7, 7, 1, 2, 3, 4, 5, 6, 7, 8))
            .ToArray();
        int Items() => deaths.Sum(death => snapshot.RollLoot("RATES_check", death).Count);
        int Quantity() => deaths.Sum(death =>
            snapshot.RollLoot("RATES_check", death).Sum(static drop => drop.Quantity));

        ServerRates.ResetToDefaults();
        var stockItems = Items();
        Install((ServerRateKeys.DropChance, 4m));
        Check.Equal(400, Items(), "drop chance x4 turns 25% rules into certain drops (one item per kill)");
        Install(
            (ServerRateKeys.DropChance, 4m),
            (ServerRateKeys.DropMaximumItems, 2m));
        Check.Equal(800, Items(), "maximum items x2 lets both rules drop");
        Install(
            (ServerRateKeys.DropChance, 4m),
            (ServerRateKeys.DropMaximumItems, 2m),
            (ServerRateKeys.DropQuantity, 3m));
        Check.Equal(400 * (3 + 6), Quantity(), "drop quantity x3 triples each stack");
        ServerRates.ResetToDefaults();
        Check.True(
            stockItems is > 40 and < 400 && Items() == stockItems,
            "rate 1 rolls exactly the stock drops for the same deaths");
    }

    private static void Install(params (string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(static value =>
                KeyValuePair.Create(value.Key, value.Value)),
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
