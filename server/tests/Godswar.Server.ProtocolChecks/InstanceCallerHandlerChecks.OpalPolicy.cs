using Godswar.Server.Application.Realms;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckAtlantisOpalRetryAsync()
    {
        await CheckAtlantisFirstFreeEntryAsync();
        await CheckAtlantisFreeActionRejectsPaidRetryAsync();
        await CheckAtlantisPaidActionRejectsFirstEntryAsync();
        await CheckAtlantisFiniteLimitClearsPaidConsentAsync();
        await CheckAtlantisFollowerConsentIsExplicitAsync();
        await CheckAtlantisLeaderWaitsForPayerConsentAsync();
        await CheckAtlantisSuccessfulConsentConsumesPartySnapshotAsync();
        await CheckAtlantisPartyChangeBeforeTransferRefundsAsync();
        await CheckAtlantisFollowerPartyChangeIsRevalidatedAsync();
        await CheckAtlantisMissingPaymentProviderFailsClosedAsync();
        await CheckAtlantisMissingDailyEntryStoreFailsClosedAsync();
        await CheckAtlantisInsufficientOpalRollsBackAsync();
        await CheckAtlantisPaidPayerProjectionAsync();
        await CheckAtlantisFailedLeaderSettlementAsync();
        await CheckAtlantisFailedFollowerSettlementAsync();
    }

    private static async Task CheckAtlantisFirstFreeEntryAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);

        await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisEnterSubId);

        Check.True(
            daily.Claims.Count == 1 &&
            daily.FullReleases.Count == 0 &&
            daily.MemberReleases.Count == 0 &&
            daily.Admissions
                .SelectMany(static admission => admission.CharacterIds)
                .ToHashSet()
                .SetEquals(fixture.Characters.Select(character =>
                    character.Id)) &&
            payments.Charges.Count == 0 &&
            payments.Admissions
                .SelectMany(static admission => admission.CharacterIds)
                .ToHashSet()
                .SetEquals(fixture.Characters.Select(character =>
                    character.Id)) &&
            payments.Settlements.Count == 0 &&
            fixture.Characters.All(character =>
                character.CurrentMap == 205) &&
            AllSessionsShareCurrentInstance(fixture),
            "Atlantis action 210 durably admits the first free " +
            "three-player entry without an Opal charge");
    }

    private static async Task
        CheckAtlantisFreeActionRejectsPaidRetryAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisEnterSubId);

        AssertPolicyMismatchRolledBack(
            fixture,
            daily,
            payments,
            sourceInstance,
            emitted,
            "free action 210 never silently spends an Opal on a retry");
    }

    private static async Task
        CheckAtlantisPaidActionRejectsFirstEntryAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);

        AssertPolicyMismatchRolledBack(
            fixture,
            daily,
            payments,
            sourceInstance,
            emitted,
            "paid action 209 cannot consume an Opal for a free entry");

        Check.True(
            AtlantisOpalConsentsAreCleared(fixture),
            "a rejected paid action with no payer discards every consent " +
            "recorded for that exact party attempt");
    }

    private static async Task
        CheckAtlantisFiniteLimitClearsPaidConsentAsync()
    {
        var daily = new ScriptedLegacyInstanceDailyEntryStore
        {
            ClaimStatus =
                LegacyInstanceDailyEntryClaimStatus.DailyLimitReached
        };
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);

        Check.True(
            daily.Claims.Count == 1 &&
            daily.FullReleases.Count == 0 &&
            daily.MemberReleases.Count == 0 &&
            payments.Charges.Count == 0 &&
            payments.Admissions.Count == 0 &&
            payments.Settlements.Count == 0 &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol
                        .AtlantisMaximumEntriesResultSubId)) &&
            AtlantisOpalConsentsAreCleared(fixture),
            "an exhausted finite paid allowance returns stock 1500, " +
            "charges nobody, and clears the terminal party consent");
    }

    private static async Task
        CheckAtlantisMissingPaymentProviderFailsClosedAsync()
    {
        var daily = ReturningPartyDailyEntries();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            opalPayments: null);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);

        Check.True(
            ClaimWasFullyReleased(daily) &&
            AtlantisOpalConsentsAreCleared(fixture) &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(PacketBuilder.ServerNote(
                "The instance is temporarily unavailable.")),
            "paid Atlantis retry fails closed and releases its attempt when " +
            "the durable Opal provider is unavailable");
    }

    private static async Task
        CheckAtlantisMissingDailyEntryStoreFailsClosedAsync()
    {
        var payments = new ScriptedLegacyInstanceOpalPaymentStore();
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            dailyEntries: null,
            payments);
        var characterIds = fixture.Characters
            .Select(static character => character.Id)
            .ToArray();
        var day = RealmCalendar.CreateForTesting(
                RealmId.Tempest,
                "Asia/Manila")
            .GetDay(DateTimeOffset.UtcNow);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var free = fixture.Leader.Registry
                .TryReserveLocalLegacyInstanceDailyEntry(
                    Guid.NewGuid(),
                    RealmId.Tempest,
                    day,
                    InstanceCallerEntryKind.Atlantis,
                    characterIds);
            Check.True(
                free.Status == LegacyInstanceDailyEntryClaimStatus.Claimed &&
                free.PaymentRequiredCharacterIds.Count == 0,
                $"local fallback reserves free Atlantis attempt {attempt}");
        }
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);
        var probeReservation = Guid.NewGuid();
        var releasedRetry = fixture.Leader.Registry
            .TryReserveLocalLegacyInstanceDailyEntry(
                probeReservation,
                RealmId.Tempest,
                day,
                InstanceCallerEntryKind.Atlantis,
                characterIds);
        fixture.Leader.Registry.ReleaseLocalLegacyInstanceDailyEntry(
            probeReservation);

        Check.True(
            releasedRetry.Status ==
                LegacyInstanceDailyEntryClaimStatus.Claimed &&
            releasedRetry.PaymentRequiredCharacterIds.SetEquals(
                characterIds) &&
            payments.Charges.Count == 0 &&
            payments.Admissions.Count == 0 &&
            payments.Settlements.Count == 0 &&
            AtlantisOpalConsentsAreCleared(fixture) &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(PacketBuilder.ServerNote(
                "The instance is temporarily unavailable.")),
            "paid Atlantis fallback requires a durable daily claim store, " +
            "releases the local retry, clears consent, and charges nobody");
    }

    private static async Task
        CheckAtlantisInsufficientOpalRollsBackAsync()
    {
        var daily = ReturningPartyDailyEntries();
        var payments = new ScriptedLegacyInstanceOpalPaymentStore
        {
            ChargeStatus = LegacyInstanceOpalChargeStatus.InsufficientOpal
        };
        await using var fixture = await CreateAtlantisOpalFixtureAsync(
            daily,
            payments);
        var sourceInstance = GetSourceInstanceId(fixture.Leader);

        var emitted = await EnterAtlantisAsync(
            fixture,
            InstanceCallerProtocol.AtlantisOpalSubId);

        Check.True(
            ClaimWasFullyReleased(daily) &&
            payments.Charges.Single().Payers
                .Select(static payer => payer.CharacterId)
                .SequenceEqual(fixture.Characters.Select(x => x.Id)) &&
            payments.Settlements.Count == 0 &&
            payments.Admissions.Count == 0 &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol
                        .AtlantisInsufficientOpalResultSubId)),
            "an atomic insufficient-Opal result releases all claims, moves " +
            "nobody, and returns the stock 1502 result");
    }

    private static ScriptedLegacyInstanceDailyEntryStore
        ReturningPartyDailyEntries() => new()
        {
            PaymentRequiredIndexes = new HashSet<int> { 0, 1, 2 }
        };

    private static bool AtlantisOpalConsentsAreCleared(
        AtlantisOpalFixture fixture)
    {
        var destination = ResolveAtlantisOpalDestination();
        var captureStatus = fixture.Leader.Registry
            .TryCaptureLegacyInstanceParty(
                fixture.Leader.Session,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                out var party);
        var expectedMissing = fixture.Characters
            .Select(static character => character.Id)
            .ToHashSet();
        var consentStatus = fixture.Leader.Registry
            .TryConsumeLegacyInstanceOpalRetryConsents(
                party,
                destination,
                fixture.Leader.Character.PositionX,
                fixture.Leader.Character.PositionZ,
                InstanceCallerProtocol.MaximumInteractionDistance,
                expectedMissing,
                DateTimeOffset.UtcNow,
                out var missing);
        return captureStatus == LegacyInstanceEntryStatus.Ready &&
               consentStatus ==
                   LegacyInstanceOpalConsentValidationStatus.MissingConsent &&
               missing.ToHashSet().SetEquals(expectedMissing);
    }

    private static async Task<IReadOnlyList<byte[]>> EnterAtlantisAsync(
        AtlantisOpalFixture fixture,
        int actionSubId,
        bool consentFollowers = true)
    {
        if (actionSubId == InstanceCallerProtocol.AtlantisOpalSubId &&
            consentFollowers)
        {
            foreach (var follower in fixture.Followers)
            {
                await ConsentAtlantisRetryAsync(follower);
            }
        }
        await OpenAtlantisPageAsync(fixture.Leader);
        var before = fixture.Leader.ReadPackets().Count;
        // The application publishes the leader's own native Enter window to every
        // admitted member. Members answer it before the leader commits, so the
        // party enters together exactly as the client shows it.
        await InvokeAsync(
            fixture.Leader.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                actionSubId),
            confirmEntry: false);
        foreach (var follower in fixture.Followers)
        {
            await InvokeAsync(
                follower.Handler,
                CreateRepetitionResponse(
                    InstanceCallerProtocol.AtlantisClientSceneId,
                    0,
                    true),
                confirmEntry: false);
        }
        await InvokeAsync(
            fixture.Leader.Handler,
            CreateRepetitionResponse(
                InstanceCallerProtocol.AtlantisClientSceneId,
                0,
                true),
            confirmEntry: false);
        return ReadAdmissionPackets(fixture.Leader.ReadPackets().Skip(before));
    }

    private static async Task<IReadOnlyList<byte[]>>
        ConsentAtlantisRetryAsync(AtlantisOpalFollower follower)
    {
        await InvokeAsync(
            follower.Handler,
            CreateActionPacket(InstanceCallerProtocol.AtlantisRootSubId));
        var before = follower.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            follower.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.AtlantisRootSubId,
                InstanceCallerProtocol.AtlantisOpalSubId));
        return follower.Transport.ReadLegacyPackets().Skip(before).ToArray();
    }

    private static void AssertPolicyMismatchRolledBack(
        AtlantisOpalFixture fixture,
        ScriptedLegacyInstanceDailyEntryStore daily,
        ScriptedLegacyInstanceOpalPaymentStore payments,
        Godswar.Server.Domain.World.Instances.WorldInstanceId sourceInstance,
        IReadOnlyList<byte[]> emitted,
        string description)
    {
        Check.True(
            ClaimWasFullyReleased(daily) &&
            payments.Charges.Count == 0 &&
            payments.Admissions.Count == 0 &&
            payments.Settlements.Count == 0 &&
            AllSessionsRemainAtSource(fixture, sourceInstance) &&
            emitted.Single().SequenceEqual(
                PacketBuilder.NpcFunctionActionResponse(
                    InstanceCallerProtocol.AthensNpcId,
                    InstanceCallerProtocol.DialogIndex,
                    InstanceCallerProtocol.AtlantisEntryPolicyResultSubId)),
            description);
    }

    private static bool ClaimWasFullyReleased(
        ScriptedLegacyInstanceDailyEntryStore daily) =>
        daily.Claims.Count == 1 &&
        daily.FullReleases.SequenceEqual(
            [daily.Claims[0].ReservationId]) &&
        daily.MemberReleases.Count == 0;

    private static bool AllSessionsRemainAtSource(
        AtlantisOpalFixture fixture,
        Godswar.Server.Domain.World.Instances.WorldInstanceId sourceInstance)
    {
        if (fixture.Characters.Any(character =>
                character.CurrentMap != fixture.Leader.SourceMapId))
        {
            return false;
        }
        foreach (var session in fixture.Sessions)
        {
            if (!fixture.Leader.Registry.TryGetSessionWorldInstanceId(
                    session,
                    out var instanceId) ||
                instanceId != sourceInstance)
            {
                return false;
            }
        }
        return true;
    }

    private static bool AllSessionsShareCurrentInstance(
        AtlantisOpalFixture fixture)
    {
        var instanceIds = new HashSet<Godswar.Server.Domain.World.Instances
            .WorldInstanceId>();
        foreach (var session in fixture.Sessions)
        {
            if (!fixture.Leader.Registry.TryGetSessionWorldInstanceId(
                    session,
                    out var instanceId))
            {
                return false;
            }
            instanceIds.Add(instanceId);
        }
        return instanceIds.Count == 1;
    }
}
