using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresLegacyInstanceDailyEntryChecks
{
    private static async Task AssertConfigurablePoliciesAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceDailyEntryClaimStore store,
        RealmId realm,
        DateOnly day,
        int[] characters)
    {
        await SetPolicyAsync(
            dataSource,
            InstanceCallerEntryKind.Atlantis,
            freeLimit: 2,
            paidRetryLimit: 2);
        var finiteDay = day.AddDays(2);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var finite = await store.TryClaimAsync(Request(
                Guid.NewGuid(),
                realm,
                finiteDay,
                InstanceCallerEntryKind.Atlantis,
                characters[..3]));
            Check.True(
                finite.Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed &&
                finite.DailyEntryLimit == 4 &&
                finite.FreeEntryLimit == 2 &&
                finite.PaidRetryLimit == 2 &&
                (attempt <= 2
                    ? finite.PaymentRequiredCharacterIds.Count == 0
                    : finite.PaymentRequiredCharacterIds.SetEquals(
                        characters[..3])),
                $"finite Atlantis policy classifies attempt {attempt}");
        }
        Check.True(
            (await store.TryClaimAsync(Request(
                Guid.NewGuid(),
                realm,
                finiteDay,
                InstanceCallerEntryKind.Atlantis,
                characters[..3]))).Status ==
                LegacyInstanceDailyEntryClaimStatus.AlreadyUsed,
            "finite paid retries enforce the configured total cap");

        await SetPolicyAsync(
            dataSource,
            InstanceCallerEntryKind.Atlantis,
            freeLimit: 1,
            paidRetryLimit: null);
        var unlimitedDay = day.AddDays(3);
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            var unlimited = await store.TryClaimAsync(Request(
                Guid.NewGuid(),
                realm,
                unlimitedDay,
                InstanceCallerEntryKind.Atlantis,
                characters[..3]));
            Check.True(
                unlimited.Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed &&
                unlimited.DailyEntryLimit is null &&
                unlimited.PaidRetryLimit is null &&
                (attempt == 1
                    ? unlimited.PaymentRequiredCharacterIds.Count == 0
                    : unlimited.PaymentRequiredCharacterIds.SetEquals(
                        characters[..3])),
                $"unlimited Atlantis policy accepts attempt {attempt}");
        }

        var wonderlandDay = day.AddDays(4);
        await SetPolicyAsync(
            dataSource,
            InstanceCallerEntryKind.Wonderland,
            freeLimit: 2,
            paidRetryLimit: 0);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Check.True(
                (await store.TryClaimAsync(Request(
                    Guid.NewGuid(),
                    realm,
                    wonderlandDay,
                    InstanceCallerEntryKind.Wonderland,
                    [characters[3]]))).Status ==
                    LegacyInstanceDailyEntryClaimStatus.Claimed,
                $"Wonderland accepts configured attempt {attempt}");
        }
        Check.True(
            (await store.TryClaimAsync(Request(
                Guid.NewGuid(),
                realm,
                wonderlandDay,
                InstanceCallerEntryKind.Wonderland,
                [characters[3]]))).Status ==
                LegacyInstanceDailyEntryClaimStatus.AlreadyUsed,
            "Wonderland enforces its independently edited limit");
    }

    private static async Task AssertStaleFreeRecoveryAsync(
        NpgsqlDataSource dataSource,
        PostgresLegacyInstanceDailyEntryClaimStore store,
        DateOnly day,
        ICollection<int> accountIds)
    {
        var staleFreeCharacter = await CreateCharacterAsync(
            dataSource,
            accountIds);
        var staleFreeReservation = Guid.NewGuid();
        Check.True(
            (await store.TryClaimAsync(Request(
                staleFreeReservation,
                RealmId.Dwargon,
                day,
                InstanceCallerEntryKind.Wonderland,
                [staleFreeCharacter]))).Status ==
                LegacyInstanceDailyEntryClaimStatus.Claimed,
            "free-recovery fixture reserves an unpaid Wonderland entry");
        Check.Equal(
            1,
            await store.RecoverPendingAsync(
                RealmId.Dwargon,
                new DateTimeOffset(
                    2026, 9, 1, 2, 0, 0, TimeSpan.Zero)),
            "offline stale recovery releases one unpaid free claim");
        Check.Equal(
            0,
            await CountCharacterClaimsAsync(
                dataSource,
                RealmId.Dwargon,
                day,
                InstanceCallerEntryKind.Wonderland,
                staleFreeCharacter),
            "unpaid stale recovery restores the character's attempt");
    }
}
