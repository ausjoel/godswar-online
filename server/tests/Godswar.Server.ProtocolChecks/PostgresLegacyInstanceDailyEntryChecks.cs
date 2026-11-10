using System.Text.RegularExpressions;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresLegacyInstanceDailyEntryChecks
{
    public const string CheckName =
        "PostgreSQL atomic Atlantis and Wonderland daily entries";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        "^godswar_(?:legacy_instance_[a-f0-9]{8}|" +
        "b(?:03|12)_[a-z0-9_]{8,48})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            throw new CheckSkippedException($"{CheckName} requires a disposable database; " +
                $"received '{database}'");
        }

        var migrationRunner = new PostgresSchemaMigrationRunner(dataSource);
        var exerciseUpgrade = !await IsMigrationAppliedAsync(
            dataSource,
            "20260901_133_legacy_instance_daily_entry_limit");
        if (exerciseUpgrade)
        {
            await migrationRunner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                MigrationsThrough(
                    "20260901_132_legacy_instance_daily_entry"));
        }
        else
        {
            await migrationRunner.InitializeGodswarSchemaAsync();
        }
        await using (var gameStore = new PostgresGameStore(connectionString))
        {
            await gameStore.EnsureSeedDataAsync();
        }

        var accountIds = new List<int>();
        try
        {
            if (exerciseUpgrade)
            {
                var upgradeCharacters = new int[3];
                for (var index = 0; index < upgradeCharacters.Length; index++)
                {
                    upgradeCharacters[index] = await CreateCharacterAsync(
                        dataSource,
                        accountIds);
                }
                var upgradeDay = new DateOnly(2026, 8, 31);
                await InsertVersion132ClaimAsync(
                    dataSource,
                    RealmId.Tempest,
                    upgradeDay,
                    upgradeCharacters);
                await migrationRunner.InitializeAsync(
                    LegacySchemaBootstrap.LoadAsync,
                    MigrationsThrough(
                        "20260901_135_legacy_instance_opal_payment"));
                await SetVersion133PolicyAsync(
                    dataSource,
                    InstanceCallerEntryKind.Atlantis,
                    dailyLimit: 1,
                    freeLimit: 0);
                await SetVersion133PolicyAsync(
                    dataSource,
                    InstanceCallerEntryKind.Wonderland,
                    dailyLimit: 2,
                    freeLimit: 2);
                await migrationRunner.InitializeGodswarSchemaAsync();

                Check.Equal(
                    3,
                    await CountDayClaimsAsync(
                        dataSource,
                        RealmId.Tempest,
                        upgradeDay),
                    "migration 133 preserves each version-132 entry as " +
                    "attempt one");
                Check.Equal(
                    3,
                    await CountAdmittedDayClaimsAsync(
                        dataSource,
                        RealmId.Tempest,
                        upgradeDay),
                    "migration 133 backfills pre-lifecycle entries as " +
                    "admitted instead of stale pending claims");
                Check.True(
                    await ReadFreeLimitAsync(
                        dataSource,
                        InstanceCallerEntryKind.Atlantis) == 3 &&
                    await ReadPaidRetryLimitAsync(
                        dataSource,
                        InstanceCallerEntryKind.Atlantis) == 1 &&
                    await ReadFreeLimitAsync(
                        dataSource,
                        InstanceCallerEntryKind.Wonderland) == 3 &&
                    await ReadPaidRetryLimitAsync(
                        dataSource,
                        InstanceCallerEntryKind.Wonderland) == 0,
                    "migration 136 safely replaces every valid version-133 " +
                    "policy with the clarified stock defaults");
                var upgradedClaim = await new
                    PostgresLegacyInstanceDailyEntryClaimStore(dataSource)
                    .TryClaimAsync(Request(
                        Guid.NewGuid(),
                        RealmId.Tempest,
                        upgradeDay,
                        InstanceCallerEntryKind.Atlantis,
                        upgradeCharacters));
                Check.True(
                    upgradedClaim.Status ==
                        LegacyInstanceDailyEntryClaimStatus.Claimed &&
                    upgradedClaim.PaymentRequiredCharacterIds.Count == 0,
                    "migration 136 makes an upgraded character's second " +
                    "Atlantis entry one of three free entries");
            }

            var characters = new int[4];
            for (var index = 0; index < characters.Length; index++)
            {
                characters[index] = await CreateCharacterAsync(
                    dataSource,
                    accountIds);
            }

            var store = new PostgresLegacyInstanceDailyEntryClaimStore(
                dataSource);
            var realm = RealmId.Tempest;
            var day = new DateOnly(2026, 9, 1);
            Check.Equal(
                3,
                await ReadFreeLimitAsync(
                    dataSource,
                    InstanceCallerEntryKind.Atlantis),
                "Atlantis defaults to three free entries");
            Check.Equal(
                3,
                await ReadFreeLimitAsync(
                    dataSource,
                    InstanceCallerEntryKind.Wonderland),
                "all three Wonderland attempts are free");
            Check.True(
                await ReadPaidRetryLimitAsync(
                    dataSource,
                    InstanceCallerEntryKind.Atlantis) == 1,
                "Atlantis defaults to one Opal-funded retry");
            Check.True(
                await ReadPaidRetryLimitAsync(
                    dataSource,
                    InstanceCallerEntryKind.Wonderland) == 0,
                "Wonderland defaults to no paid retries");
            await CheckWonderlandPaidPolicyRejectedAsync(dataSource);

            var firstReservation = Guid.NewGuid();
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var reservationId = attempt == 1
                    ? firstReservation
                    : Guid.NewGuid();
                var result = await store.TryClaimAsync(Request(
                        reservationId,
                        realm,
                        day,
                        InstanceCallerEntryKind.Atlantis,
                        characters[..3]));
                Check.True(
                    result.Status ==
                        LegacyInstanceDailyEntryClaimStatus.Claimed &&
                    result.DailyEntryLimit == 4 &&
                    result.FreeEntryLimit == 3 &&
                    result.PaidRetryLimit == 1 &&
                    result.PaymentRequiredCharacterIds.Count == 0,
                    $"Atlantis attempt {attempt} is free");
            }

            var race = await Task.WhenAll(
                store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    characters[..3])),
                store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    characters[..3])));
            Check.Equal(
                1,
                race.Count(result => result.Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed),
                "concurrent paid retry and fifth entry have one winner");
            Check.Equal(
                1,
                race.Count(result => result.Status ==
                    LegacyInstanceDailyEntryClaimStatus.AlreadyUsed),
                "the fifth concurrent entry is rejected atomically");
            Check.Equal(
                12,
                await CountDayClaimsAsync(dataSource, realm, day),
                "three free entries and one paid retry persist per member");
            Check.True(
                race.Single(result => result.Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed)
                    .PaymentRequiredCharacterIds.SetEquals(characters[..3]),
                "the fourth Atlantis entry requires each member's Opal");

            Check.True(
                (await store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    [characters[0], characters[1], characters[3]]))).Status ==
                    LegacyInstanceDailyEntryClaimStatus.AlreadyUsed,
                "one exhausted member rejects the complete party");
            Check.Equal(
                0,
                await CountCharacterClaimsAsync(
                    dataSource,
                    realm,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    characters[3]),
                "a rejected party consumes no eligible-member attempt");

            await store.ReleaseMembersAsync(
                firstReservation,
                [characters[0], characters[1]]);
            Check.Equal(
                4,
                await CountCharacterClaimsAsync(
                    dataSource,
                    realm,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    characters[2]),
                "partial release retains the third member's four entries");
            var partiallyRestored = await store.TryClaimAsync(Request(
                Guid.NewGuid(),
                realm,
                day,
                InstanceCallerEntryKind.Atlantis,
                [characters[0], characters[1], characters[3]]));
            Check.True(
                partiallyRestored.Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed &&
                partiallyRestored.PaymentRequiredCharacterIds.SetEquals(
                    characters[..2]),
                "release restores the failed members while only returning " +
                "members cross into the paid retry");
            Check.True(
                (await store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    [characters[2], characters[0], characters[1]]))).Status ==
                    LegacyInstanceDailyEntryClaimStatus.DailyLimitReached,
                "the unreleased exhausted member remains capped");

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var wonderland = await store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day,
                    InstanceCallerEntryKind.Wonderland,
                    [characters[0]]));
                Check.True(
                    wonderland.Status ==
                        LegacyInstanceDailyEntryClaimStatus.Claimed &&
                    wonderland.DailyEntryLimit == 3 &&
                    wonderland.FreeEntryLimit == 3 &&
                    wonderland.PaidRetryLimit == 0 &&
                    wonderland.PaymentRequiredCharacterIds.Count == 0,
                    $"Wonderland attempt {attempt} is free");
            }
            Check.True(
                (await store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day,
                    InstanceCallerEntryKind.Wonderland,
                    [characters[0]]))).Status ==
                    LegacyInstanceDailyEntryClaimStatus.AlreadyUsed,
                "Wonderland rejects a fourth entry without a paid tier");
            Check.True(
                (await store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    day.AddDays(1),
                    InstanceCallerEntryKind.Atlantis,
                    characters[..3]))).Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed,
                "Atlantis resets on the next realm day");

            await AssertConfigurablePoliciesAsync(
                dataSource,
                store,
                realm,
                day,
                characters);
            await AssertStaleFreeRecoveryAsync(
                dataSource,
                store,
                day,
                accountIds);
        }
        finally
        {
            await DeleteAccountsAsync(dataSource, accountIds);
            await SetPolicyAsync(
                dataSource,
                InstanceCallerEntryKind.Atlantis,
                freeLimit: 3,
                paidRetryLimit: 1);
            await SetPolicyAsync(
                dataSource,
                InstanceCallerEntryKind.Wonderland,
                freeLimit: 3,
                paidRetryLimit: 0);
        }
    }

    private static LegacyInstanceDailyEntryClaimRequest Request(
        Guid reservationId,
        RealmId realmId,
        DateOnly day,
        InstanceCallerEntryKind kind,
        IReadOnlyCollection<int> characterIds) => new(
        reservationId,
        realmId,
        day,
        kind,
        characterIds,
        new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero));

    private static async Task<int> CreateCharacterAsync(
        NpgsqlDataSource dataSource,
        ICollection<int> accountIds)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var command = dataSource.CreateCommand(
            """
            WITH account AS (
                INSERT INTO public.accounts (username, password)
                VALUES (@username, 'test')
                RETURNING id
            ), character AS (
                INSERT INTO public.character_base (
                    account_id, server_id, name, "Map")
                SELECT id, 1, @characterName, 0
                FROM account
                RETURNING id, account_id
            )
            SELECT id, account_id FROM character;
            """);
        command.Parameters.AddWithValue(
            "username",
            $"legacy_instance_{token}");
        command.Parameters.AddWithValue("characterName", $"LI{token}");
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "legacy-instance fixture creates one character");
        accountIds.Add(reader.GetInt32(1));
        return reader.GetInt32(0);
    }

    private static async Task<int> CountCharacterClaimsAsync(
        NpgsqlDataSource dataSource,
        RealmId realmId,
        DateOnly day,
        InstanceCallerEntryKind kind,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.legacy_instance_daily_entries
            WHERE realm_id = @realmId
              AND realm_day = @realmDay
              AND instance_kind = @instanceKind
              AND character_id = @characterId;
            """);
        command.Parameters.AddWithValue("realmId", (short)realmId.Value);
        command.Parameters.AddWithValue("realmDay", day);
        command.Parameters.AddWithValue("instanceKind", (short)kind);
        command.Parameters.AddWithValue("characterId", characterId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountDayClaimsAsync(
        NpgsqlDataSource dataSource,
        RealmId realmId,
        DateOnly day)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.legacy_instance_daily_entries
            WHERE realm_id = @realmId
              AND realm_day = @realmDay
              AND instance_kind = 1;
            """);
        command.Parameters.AddWithValue("realmId", (short)realmId.Value);
        command.Parameters.AddWithValue("realmDay", day);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountAdmittedDayClaimsAsync(
        NpgsqlDataSource dataSource,
        RealmId realmId,
        DateOnly day)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.legacy_instance_daily_entries
            WHERE realm_id = @realmId
              AND realm_day = @realmDay
              AND instance_kind = 1
              AND admitted_at IS NOT NULL;
            """);
        command.Parameters.AddWithValue("realmId", (short)realmId.Value);
        command.Parameters.AddWithValue("realmDay", day);
        return (int)(await command.ExecuteScalarAsync())!;
    }

}
