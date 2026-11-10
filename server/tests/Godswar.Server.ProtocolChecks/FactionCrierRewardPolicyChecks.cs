using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.FactionCrier;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class FactionCrierRewardPolicyChecks
{
    private static readonly RealmCalendar Calendar =
        RealmCalendar.CreateForTesting(
            RealmId.Tempest,
            "Asia/Manila");

    public const string CheckName =
        "Faction Crier calendar and reward balance";

    public static Task RunAsync()
    {
        var balance = FactionCrierRewardPolicy.CreateReviewedDefault();
        balance.Validate();
        CheckDailyCalendar(balance);
        CheckPhilippineDayBoundary(balance);
        CheckClaimsAndRenewal(balance);
        CheckTurnIns(balance);
        CheckBalancePublicationGuards(balance);
        CheckMaximumProgressionReceipt();
        CheckReplayIntent();
        CheckCommandEnvelope();
        return Task.CompletedTask;
    }

    private static void CheckBalancePublicationGuards(
        FactionCrierBalanceSnapshot balance)
    {
        var wrongSemantics = balance.Options
            .Select(option => option.SubId == 110
                ? option with
                {
                    Currency = FactionCrierCurrency.Gold,
                    RewardKind = FactionCrierRewardKind.TalentPoints
                }
                : option)
            .ToArray();
        Check.Throws<InvalidDataException>(
            () => (balance with { Options = wrongSemantics }).Validate(),
            "published actions cannot contradict stock currency/reward text");

        var overflowingTiers = balance.Tiers
            .Select(tier => tier.MinimumLevel == 20
                ? tier with { BaseExperience = int.MaxValue }
                : tier)
            .ToArray();
        Check.Throws<InvalidDataException>(
            () => (balance with { Tiers = overflowingTiers }).Validate(),
            "published reward products must fit the executor integer range");

        var oversizedMultiplier = balance.Options
            .Select(option => option.SubId == 110
                ? option with { Multiplier = 101 }
                : option)
            .ToArray();
        Check.Throws<InvalidDataException>(
            () => (balance with { Options = oversizedMultiplier }).Validate(),
            "published multipliers retain the database upper bound");

        var shiftedTiers = balance.Tiers
            .Select(tier => tier.MinimumLevel switch
            {
                20 => tier with { MaximumLevel = 40 },
                40 => tier with { MinimumLevel = 41 },
                _ => tier
            })
            .ToArray();
        Check.Throws<InvalidDataException>(
            () => (balance with { Tiers = shiftedTiers }).Validate(),
            "published tiers retain the stock client result ladder");
    }

    private static void CheckMaximumProgressionReceipt()
    {
        var levelUps = Enumerable.Range(21, 180)
            .Select(level => new FactionCrierLevelUp(
                level,
                0,
                PlayerExperienceCatalog.GetNextLevelExperience(level)))
            .ToArray();
        var receipt = new FactionCrierExecutionReceipt(
            CharacterId: 41,
            RealmId: 1,
            Operation: FactionCrierOperation.TurnIn,
            SubId: 134,
            NativeResultSubId: 33015,
            Succeeded: true,
            PreviousLevel: 20,
            CurrentLevel: 200,
            PreviousExperience: 0,
            CurrentExperience: 0,
            AwardedExperience: int.MaxValue,
            LevelUps: levelUps,
            PreviousTalentPoints: 0,
            CurrentTalentPoints: 912,
            AwardedTalentPoints: 912,
            Wallet: new(0, 0, 0),
            WalletRevision: 1,
            InventoryRevision: 1,
            ProgressionRevision: 1,
            FactionCrierRevision: 1,
            AuditId: "17",
            EventId: Guid.Parse(
                "4e20b35d-08fe-4df8-a672-fd8a505c50cc"));

        var payload = FactionCrierPersistenceCodec.Encode(receipt);
        var decoded = FactionCrierPersistenceCodec.DecodeAndVerify(
            System.Text.Encoding.UTF8.GetString(payload),
            FactionCrierPersistenceCodec.Hash(payload),
            expectedAuditId: 17);
        Check.True(
            payload.Length > 8_192 &&
            payload.Length <=
                FactionCrierPersistenceCodec.MaximumPayloadBytes &&
            decoded.CharacterId == receipt.CharacterId &&
            decoded.CurrentLevel == 200 &&
            decoded.LevelUps.Count == 180 &&
            decoded.LevelUps[^1].Level == 200,
            "maximum progression receipt remains bounded and replayable");
    }

    private static void CheckDailyCalendar(
        FactionCrierBalanceSnapshot balance)
    {
        var monday = new DateTimeOffset(
            2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
        for (var ordinal = 0; ordinal < 6; ordinal++)
        {
            var instant = monday.AddDays(ordinal);
            var day = Calendar.GetDay(instant);
            var command = Command(
                1,
                day.DayNumber,
                renewal: null);
            Check.True(
                FactionCrierRewardPolicy.TryCreatePlan(
                    command,
                    20,
                    instant,
                    balance,
                    Calendar,
                    out var plan) &&
                plan.Operation == FactionCrierOperation.DailyClaim &&
                plan.GrantedItemId == 3820 + ordinal &&
                plan.NativeSuccessSubId == 360 + ordinal &&
                plan.ClaimDay == day &&
                plan.Currency == FactionCrierCurrency.None,
                $"weekday {ordinal + 1} grants its exact Nameplate");
        }

        var sunday = monday.AddDays(6);
        var sundayDay = Calendar.GetDay(sunday);
        Check.True(
            sundayDay.DayOfWeek == DayOfWeek.Sunday &&
            !FactionCrierRewardPolicy.TryCreatePlan(
                Command(1, sundayDay.DayNumber, null),
                20,
                sunday,
                balance,
                Calendar,
                out _),
            "Sunday has no seventh Nameplate claim");
    }

    private static void CheckPhilippineDayBoundary(
        FactionCrierBalanceSnapshot balance)
    {
        var beforeFriday = new DateTimeOffset(
            2026, 8, 20, 15, 59, 59, TimeSpan.Zero);
        var friday = beforeFriday.AddSeconds(1);
        var thursdayDay = Calendar.GetDay(beforeFriday);
        var fridayDay = Calendar.GetDay(friday);

        Check.True(
            Calendar.TimeZoneId == "Asia/Manila" &&
            thursdayDay == new DateOnly(2026, 8, 20) &&
            fridayDay == new DateOnly(2026, 8, 21) &&
            FactionCrierRewardPolicy.TryCreatePlan(
                Command(1, thursdayDay.DayNumber, null),
                20,
                beforeFriday,
                balance,
                Calendar,
                out var thursdayPlan) &&
            thursdayPlan.GrantedItemId == 3823 &&
            thursdayPlan.NativeSuccessSubId == 363 &&
            FactionCrierRewardPolicy.TryCreatePlan(
                Command(1, fridayDay.DayNumber, null),
                20,
                friday,
                balance,
                Calendar,
                out var fridayPlan) &&
            fridayPlan.GrantedItemId == 3824 &&
            fridayPlan.NativeSuccessSubId == 364,
            "Philippine midnight advances Thursday IV to Friday V");
    }

    private static void CheckClaimsAndRenewal(
        FactionCrierBalanceSnapshot balance)
    {
        var instant = new DateTimeOffset(
            2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
        var weekStart = RealmCalendar.GetWeekStart(
            Calendar.GetDay(instant));
        Check.True(
            FactionCrierRewardPolicy.TryCreatePlan(
                Command(36, weekStart.DayNumber, null),
                160,
                instant,
                balance,
                Calendar,
                out var weekly) &&
            weekly.GrantedItemId == 3825 &&
            weekly.NativeSuccessSubId == 736 &&
            weekly.Currency == FactionCrierCurrency.Gold &&
            weekly.CurrencyCost == 230 &&
            weekly.ClaimWeekStart == weekStart,
            "weekly reclaim grants the selected plate for 230 Gold");

        var selected = new FactionCrierNameplateSelection(
            55,
            3821,
            "[3821,1,1,1,1]");
        Check.True(
            FactionCrierRewardPolicy.TryCreatePlan(
                Command(46, 0, selected),
                160,
                instant,
                balance,
                Calendar,
                out var renewal) &&
            renewal.ConsumedItemIds.SequenceEqual([3821]) &&
            renewal.GrantedItemId == 3825 &&
            renewal.Currency == FactionCrierCurrency.Gold &&
            renewal.CurrencyCost == 105 &&
            renewal.NativeSuccessSubId == 846,
            "renewal converts the selected source into the requested plate");
        Check.True(
            !FactionCrierRewardPolicy.TryCreatePlan(
                Command(42, 0, selected),
                160,
                instant,
                balance,
                Calendar,
                out _),
            "renewal cannot convert a plate into itself");
    }

    private static void CheckTurnIns(
        FactionCrierBalanceSnapshot balance)
    {
        var instant = DateTimeOffset.UtcNow;
        CheckPlan(
            subId: 101,
            level: 20,
            expectedItems: [3820],
            expectedCurrency: FactionCrierCurrency.None,
            expectedCost: 0,
            expectedExperience: 23_500,
            expectedTalent: 0,
            expectedResult: 1901);
        CheckPlan(
            112,
            60,
            [3820, 3822, 3824],
            FactionCrierCurrency.BoundGold,
            312,
            1_044_000,
            0,
            1918);
        CheckPlan(
            121,
            121,
            [3821, 3823, 3825],
            FactionCrierCurrency.Silver,
            140_000,
            0,
            210,
            20161);
        CheckPlan(
            134,
            200,
            [3820, 3821, 3822, 3823, 3824, 3825],
            FactionCrierCurrency.Gold,
            1_377,
            6_000_000,
            912,
            33015);
        Check.True(
            !FactionCrierRewardPolicy.TryCreatePlan(
                Command(101, 0, null),
                19,
                instant,
                balance,
                Calendar,
                out _),
            "characters below level 20 cannot exchange Nameplates");
        Check.True(
            FactionCrierRewardPolicy.NativeFailureSubId(
                FactionCrierOperation.DailyClaim,
                FactionCrierExecutionDisposition.BelowMinimumLevel) == 501 &&
            FactionCrierRewardPolicy.NativeFailureSubId(
                FactionCrierOperation.WeeklyReclaim,
                FactionCrierExecutionDisposition.AlreadyClaimed) == 702 &&
            FactionCrierRewardPolicy.NativeFailureSubId(
                FactionCrierOperation.RenewNameplate,
                FactionCrierExecutionDisposition.InsufficientCurrency) ==
                801 &&
            FactionCrierRewardPolicy.NativeFailureSubId(
                FactionCrierOperation.TurnIn,
                FactionCrierExecutionDisposition.MissingNameplate) == 1900,
            "native failures retain the stock result sub-ID ladder");

        void CheckPlan(
            int subId,
            int level,
            int[] expectedItems,
            FactionCrierCurrency expectedCurrency,
            int expectedCost,
            int expectedExperience,
            int expectedTalent,
            int expectedResult)
        {
            Check.True(
                FactionCrierRewardPolicy.TryCreatePlan(
                    Command(subId, 0, null),
                    level,
                    instant,
                    balance,
                    Calendar,
                    out var plan) &&
                plan.ConsumedItemIds.SequenceEqual(expectedItems) &&
                plan.Currency == expectedCurrency &&
                plan.CurrencyCost == expectedCost &&
                plan.AwardedExperience == expectedExperience &&
                plan.AwardedTalentPoints == expectedTalent &&
                plan.NativeSuccessSubId == expectedResult,
                $"turn-in {subId} retains its reviewed level-tier reward");
        }
    }

    private static void CheckReplayIntent()
    {
        var valid = new FactionCrierReplayIntent(1, 134);
        Check.True(
            valid.IsValid &&
            valid.Operation == FactionCrierOperation.TurnIn &&
            !new FactionCrierReplayIntent(0, 134).IsValid &&
            !new FactionCrierReplayIntent(1, 109).IsValid,
            "replay identity retains realm and terminal action semantics");
    }

    private static void CheckCommandEnvelope()
    {
        var identity = FactionCrierOperationIdentity.SecureClient(
            Guid.Parse("d53563f6-df15-4cda-b1e4-0fe294ea56b3"));
        Check.True(
            FactionCrierCommandEnvelope.TryCreateCommand(
                identity,
                realmId: 1,
                npcId: FactionCrierCommandEnvelope.AthensNpcId,
                dialogIndex: FactionCrierCommandEnvelope.DialogIndex,
                subId: 134,
                realmPeriodDayNumber: 0,
                renewalSource: null,
                out var command),
            "terminal action creates a canonical command");
        var subject = new CommandSubject(7, 41);
        var correlation = new CommandConnectionCorrelation(
            Guid.Parse("7477b932-b88c-4ca2-97bd-39c644518b64"),
            CommandTransportKind.SecureTlsLegacy);
        var envelope = FactionCrierCommandEnvelope.Create(
            subject,
            correlation,
            new DateTimeOffset(2026, 8, 21, 1, 2, 3, TimeSpan.Zero),
            command);
        Check.True(
            FactionCrierCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "canonical Faction Crier envelope validates");

        var changedCommand = command with
        {
            SubId = 133
        };
        var changedEnvelope = FactionCrierCommandEnvelope.Create(
            subject,
            correlation,
            envelope.ReceivedAt,
            changedCommand);
        Check.True(
            changedEnvelope.OperationId == envelope.OperationId &&
            changedEnvelope.RequestHash != envelope.RequestHash &&
            FactionCrierCommandEnvelope.Validate(
                envelope with { Command = changedCommand }) ==
                CommandEnvelopeValidation.RequestHashConflict,
            "one operation ID cannot be reused for a different offer");

        var rawIdentity = FactionCrierOperationIdentity.RawLocalServer(
            Guid.Parse("a62ed1b3-39ba-4165-b190-0342d65cfaaa"),
            correlation.ConnectionId);
        Check.True(
            FactionCrierCommandEnvelope.TryCreateCommand(
                rawIdentity,
                1,
                FactionCrierCommandEnvelope.PublishedSpartaNpcId,
                FactionCrierCommandEnvelope.DialogIndex,
                101,
                0,
                null,
                out var rawCommand) &&
            FactionCrierCommandEnvelope.Validate(
                FactionCrierCommandEnvelope.Create(
                    subject,
                    correlation with
                    {
                        Transport = CommandTransportKind.LegacyTcp
                    },
                    envelope.ReceivedAt,
                    rawCommand)) == CommandEnvelopeValidation.Valid,
            "raw-local operation identity remains connection-bound");
    }

    private static FactionCrierCommand Command(
        int subId,
        int periodDay,
        FactionCrierNameplateSelection? renewal)
    {
        var identity = FactionCrierOperationIdentity.SecureClient(
            Guid.Parse("0f7ad829-5735-4386-b292-b6c25a135164"));
        return new(
            identity,
            FactionCrierCommandEnvelope.ResolveOperation(subId),
            RealmId: 1,
            NpcId: FactionCrierCommandEnvelope.AthensNpcId,
            DialogIndex: FactionCrierCommandEnvelope.DialogIndex,
            SubId: subId,
            RealmPeriodDayNumber: periodDay,
            RenewalSource: renewal);
    }
}
