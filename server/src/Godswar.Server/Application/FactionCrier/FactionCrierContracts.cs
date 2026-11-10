using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.FactionCrier;

internal enum FactionCrierOperation : byte
{
    DailyClaim = 1,
    WeeklyReclaim = 2,
    RenewNameplate = 3,
    TurnIn = 4
}

internal enum FactionCrierCurrency : byte
{
    None = 0,
    Silver = 1,
    BoundGold = 2,
    Gold = 3
}

internal enum FactionCrierRewardKind : byte
{
    None = 0,
    Experience = 1,
    TalentPoints = 2,
    ExperienceAndTalentPoints = 3
}

internal readonly record struct FactionCrierOperationIdentity(
    CommandIdentityStrength Strength,
    Guid OperationId,
    Guid RawLocalConnectionId)
{
    public static FactionCrierOperationIdentity SecureClient(Guid value) =>
        new(CommandIdentityStrength.ClientOperationId, value, Guid.Empty);

    public static FactionCrierOperationIdentity RawLocalServer(
        Guid value,
        Guid connectionId) =>
        new(CommandIdentityStrength.ServerOperationId, value, connectionId);

    public bool IsSecureClient =>
        Strength == CommandIdentityStrength.ClientOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId == Guid.Empty;

    public bool IsRawLocalServer =>
        Strength == CommandIdentityStrength.ServerOperationId &&
        OperationId != Guid.Empty &&
        RawLocalConnectionId != Guid.Empty;
}

internal readonly record struct FactionCrierNameplateSelection(
    int KitBagSlot,
    int ItemId,
    string ExpectedCompactItemState);

internal readonly record struct FactionCrierReplayIntent(
    int RealmId,
    int SubId)
{
    public FactionCrierOperation Operation =>
        FactionCrierCommandEnvelope.ResolveOperation(SubId);

    public bool IsValid => RealmId > 0 && Operation != 0;
}

internal readonly record struct FactionCrierCommand(
    FactionCrierOperationIdentity Identity,
    FactionCrierOperation Operation,
    int RealmId,
    int NpcId,
    int DialogIndex,
    int SubId,
    int RealmPeriodDayNumber,
    FactionCrierNameplateSelection? RenewalSource);

internal readonly record struct FactionCrierBalanceTier(
    int MinimumLevel,
    int MaximumLevel,
    int BaseExperience,
    int BaseTalentPoints,
    int TripleSilverCost,
    int AllSixSilverCost);

internal readonly record struct FactionCrierBalanceOption(
    int SubId,
    FactionCrierCurrency Currency,
    int Cost,
    int Multiplier,
    FactionCrierRewardKind RewardKind);

internal sealed record FactionCrierBalanceSnapshot(
    long Revision,
    int MinimumLevel,
    int WeeklyReclaimGoldCost,
    int RenewalGoldCost,
    IReadOnlyList<FactionCrierBalanceTier> Tiers,
    IReadOnlyList<FactionCrierBalanceOption> Options)
{
    public void Validate()
    {
        if (Revision < 0 ||
            MinimumLevel != 20 ||
            WeeklyReclaimGoldCost < 0 ||
            RenewalGoldCost < 0 ||
            Tiers.Count != 7 ||
            Options.Count != 25)
        {
            throw new InvalidDataException(
                "The Faction Crier balance snapshot is invalid.");
        }

        var expectedMinimum = MinimumLevel;
        foreach (var tier in Tiers.OrderBy(static value => value.MinimumLevel))
        {
            if (tier.MinimumLevel != expectedMinimum ||
                !HasReviewedTierBounds(tier) ||
                tier.MaximumLevel < tier.MinimumLevel ||
                tier.MaximumLevel > 200 ||
                tier.BaseExperience <= 0 ||
                tier.BaseTalentPoints <= 0 ||
                tier.TripleSilverCost < 0 ||
                tier.AllSixSilverCost < 0)
            {
                throw new InvalidDataException(
                    "Faction Crier level tiers are not contiguous or bounded.");
            }

            expectedMinimum = tier.MaximumLevel + 1;
        }

        if (expectedMinimum != 201 ||
            Options.Select(static value => value.SubId).Distinct().Count() !=
                Options.Count ||
            Options.Any(static value =>
                value.SubId is < 110 or > 134 ||
                !Enum.IsDefined(value.Currency) ||
                value.Currency == FactionCrierCurrency.None ||
                !Enum.IsDefined(value.RewardKind) ||
                value.RewardKind == FactionCrierRewardKind.None ||
                value.Cost < 0 ||
                value.Multiplier is < 1 or > 100 ||
                !HasStockOptionSemantics(value)) ||
            Tiers.Any(tier => Options.Any(option =>
                RewardProductOverflows(tier, option))))
        {
            throw new InvalidDataException(
                "Faction Crier exchange options are incomplete or invalid.");
        }
    }

    private static bool HasStockOptionSemantics(
        FactionCrierBalanceOption option) =>
        option.SubId switch
        {
            110 or 120 => Is(
                option,
                FactionCrierCurrency.Silver,
                FactionCrierRewardKind.Experience),
            111 or 121 => Is(
                option,
                FactionCrierCurrency.Silver,
                FactionCrierRewardKind.TalentPoints),
            112 or 116 or 122 or 126 => Is(
                option,
                FactionCrierCurrency.BoundGold,
                FactionCrierRewardKind.Experience),
            113 or 117 or 123 or 127 => Is(
                option,
                FactionCrierCurrency.BoundGold,
                FactionCrierRewardKind.TalentPoints),
            114 or 118 or 124 or 128 => Is(
                option,
                FactionCrierCurrency.Gold,
                FactionCrierRewardKind.Experience),
            115 or 119 or 125 or 129 => Is(
                option,
                FactionCrierCurrency.Gold,
                FactionCrierRewardKind.TalentPoints),
            130 => Is(
                option,
                FactionCrierCurrency.Silver,
                FactionCrierRewardKind.ExperienceAndTalentPoints),
            131 or 133 => Is(
                option,
                FactionCrierCurrency.BoundGold,
                FactionCrierRewardKind.ExperienceAndTalentPoints),
            132 or 134 => Is(
                option,
                FactionCrierCurrency.Gold,
                FactionCrierRewardKind.ExperienceAndTalentPoints),
            _ => false
        };

    private static bool HasReviewedTierBounds(
        FactionCrierBalanceTier tier) =>
        tier.MinimumLevel switch
        {
            20 => tier.MaximumLevel == 39,
            40 => tier.MaximumLevel == 59,
            60 => tier.MaximumLevel == 79,
            80 => tier.MaximumLevel == 99,
            100 => tier.MaximumLevel == 120,
            121 => tier.MaximumLevel == 130,
            131 => tier.MaximumLevel == 200,
            _ => false
        };

    private static bool Is(
        FactionCrierBalanceOption option,
        FactionCrierCurrency currency,
        FactionCrierRewardKind rewardKind) =>
        option.Currency == currency && option.RewardKind == rewardKind;

    private static bool RewardProductOverflows(
        FactionCrierBalanceTier tier,
        FactionCrierBalanceOption option) =>
        option.RewardKind is FactionCrierRewardKind.Experience or
            FactionCrierRewardKind.ExperienceAndTalentPoints &&
        (long)tier.BaseExperience * option.Multiplier > int.MaxValue ||
        option.RewardKind is FactionCrierRewardKind.TalentPoints or
            FactionCrierRewardKind.ExperienceAndTalentPoints &&
        (long)tier.BaseTalentPoints * option.Multiplier > int.MaxValue;

    public string CoordinationRevision()
    {
        Validate();
        var builder = new StringBuilder(1_024)
            .Append("faction-crier-balance-v2\n");
        builder.AppendFormat(
            CultureInfo.InvariantCulture,
            "revision:{0}\nminimum-level:{1}\n" +
            "weekly-gold:{2}\nrenewal-gold:{3}\n",
            Revision,
            MinimumLevel,
            WeeklyReclaimGoldCost,
            RenewalGoldCost);
        foreach (var tier in Tiers.OrderBy(static value => value.MinimumLevel))
        {
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "tier:{0},{1},{2},{3},{4},{5}\n",
                tier.MinimumLevel,
                tier.MaximumLevel,
                tier.BaseExperience,
                tier.BaseTalentPoints,
                tier.TripleSilverCost,
                tier.AllSixSilverCost);
        }
        foreach (var option in Options.OrderBy(static value => value.SubId))
        {
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                "option:{0},{1},{2},{3},{4}\n",
                option.SubId,
                (byte)option.Currency,
                option.Cost,
                option.Multiplier,
                (byte)option.RewardKind);
        }
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

internal readonly record struct FactionCrierExecutionPlan(
    FactionCrierOperation Operation,
    int NativeSuccessSubId,
    IReadOnlyList<int> ConsumedItemIds,
    int? GrantedItemId,
    FactionCrierCurrency Currency,
    int CurrencyCost,
    int AwardedExperience,
    int AwardedTalentPoints,
    DateOnly? ClaimDay,
    DateOnly? ClaimWeekStart);

internal readonly record struct FactionCrierLevelUp(
    int Level,
    long CurrentExperience,
    int NextLevelExperience);

internal enum FactionCrierExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    InvalidIntent = 3,
    RequestHashConflict = 4,
    PreconditionFailed = 5,
    InsufficientCurrency = 6,
    MissingNameplate = 7,
    BagFull = 8,
    AlreadyClaimed = 9,
    ClosedToday = 10,
    ProviderUnavailable = 11,
    ReplayNotFound = 12,
    BelowMinimumLevel = 13
}

internal sealed record FactionCrierExecutionReceipt(
    int CharacterId,
    int RealmId,
    FactionCrierOperation Operation,
    int SubId,
    int NativeResultSubId,
    bool Succeeded,
    int PreviousLevel,
    int CurrentLevel,
    long PreviousExperience,
    long CurrentExperience,
    int AwardedExperience,
    IReadOnlyList<FactionCrierLevelUp> LevelUps,
    int PreviousTalentPoints,
    int CurrentTalentPoints,
    int AwardedTalentPoints,
    CharacterWalletSnapshot Wallet,
    long WalletRevision,
    long InventoryRevision,
    long ProgressionRevision,
    long FactionCrierRevision,
    string AuditId,
    Guid EventId);

internal sealed record FactionCrierExecutionResult(
    FactionCrierExecutionDisposition Disposition,
    FactionCrierExecutionReceipt? Receipt)
{
    public bool IsDurable =>
        Disposition is FactionCrierExecutionDisposition.Committed or
            FactionCrierExecutionDisposition.Duplicate;

    public static FactionCrierExecutionResult Terminal(
        FactionCrierExecutionDisposition disposition,
        FactionCrierExecutionReceipt? receipt = null) =>
        new(disposition, receipt);

    public static FactionCrierExecutionResult ReplayNotFound() =>
        Terminal(FactionCrierExecutionDisposition.ReplayNotFound);
}
