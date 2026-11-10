using System.Collections.Immutable;

namespace Godswar.Server.Application.Progression;

internal interface IExperienceBoostStateReader
{
    Task<ExperienceBoostSnapshot> ReadAsync(
        ExperienceBoostReadRequest request,
        CancellationToken cancellationToken = default);
}

internal readonly record struct ExperienceBoostReadRequest(
    int AccountId,
    int CharacterId,
    byte Camp,
    short MapId,
    DateTimeOffset ReadAtUtc);

internal sealed record ExperienceBoostEntry(
    int StatusId,
    int Kind,
    int BonusBasisPoints,
    int Priority,
    DateTimeOffset? ExpiresAtUtc,
    string Source)
{
    public uint RemainingSeconds(DateTimeOffset nowUtc)
    {
        ExperienceBoostContract.RequireUtc(nowUtc, nameof(nowUtc));
        if (Kind == ExperienceBoostKinds.Donator ||
            ExpiresAtUtc is null)
        {
            return uint.MaxValue;
        }

        return (uint)Math.Clamp(
            (long)Math.Ceiling(
                (ExpiresAtUtc.Value - nowUtc).TotalSeconds),
            0L,
            uint.MaxValue);
    }
}

internal sealed record ExperienceBoostSnapshot(
    ImmutableArray<ExperienceBoostEntry> ActiveBoosts)
{
    public static ExperienceBoostSnapshot Empty { get; } =
        new(ImmutableArray<ExperienceBoostEntry>.Empty);

    public int TotalBonusBasisPoints => TotalFor(
        static boost => ExperienceBoostKinds.AffectsFighter(boost.Kind));

    public int TotalTalentBonusBasisPoints => TotalFor(
        static boost => ExperienceBoostKinds.AffectsTalent(boost.Kind));

    public int TotalPetBonusBasisPoints => TotalFor(
        static boost => ExperienceBoostKinds.AffectsPet(boost.Kind));

    public int ApplyTo(int baseExperience) =>
        ApplyBonus(baseExperience, TotalBonusBasisPoints);

    public int ApplyToTalent(int baseTalentExperience) =>
        ApplyBonus(
            baseTalentExperience,
            TotalTalentBonusBasisPoints);

    public int ApplyToPet(int basePetExperience) =>
        ApplyBonus(basePetExperience, TotalPetBonusBasisPoints);

    private int TotalFor(Func<ExperienceBoostEntry, bool> predicate)
    {
        var total = ActiveBoosts
            .Where(predicate)
            .Aggregate(
                0L,
                static (sum, boost) =>
                    sum + boost.BonusBasisPoints);
        return (int)Math.Clamp(total, int.MinValue, int.MaxValue);
    }

    private static int ApplyBonus(
        int baseExperience,
        int bonusBasisPoints)
    {
        if (baseExperience <= 0)
        {
            return 0;
        }

        var multiplierBasisPoints =
            Math.Max(0L, 10_000L + bonusBasisPoints);
        var adjusted =
            ((long)baseExperience * multiplierBasisPoints) / 10_000L;
        return (int)Math.Min(adjusted, int.MaxValue);
    }
}

internal enum DonatorTier : short
{
    None = 0,
    KijinPatron = 1,
    OniPatron = 2,
    DemonLordSeed = 3,
    TrueDemonLord = 4,
    OctagramPatron = 5
}

internal static class ExperienceStatusIds
{
    public const int Weekend = 511;
    public const int TrickOrTreat = 512;
    public const int MaxExperiencePotion = 586;
    public const int TalentExperience50Percent = 580;
    public const int TalentPotion50Percent = 587;
    public const int TalentExperience100Percent = 581;
    public const int HighTalentBoost100Percent = 509;
    public const int TalentExperience200Percent = 582;
    public const int SuperTalentPotion200Percent = 588;
    public const int TalentExperience300Percent = 583;
    public const int IncredibleTalentPotion300Percent = 589;
    public const int TalentExperience400Percent = 584;
    public const int MaxTalentPotion400Percent = 590;
    public const int GuildDoubleExperience16Hours = 1007;

    /// <summary>
    /// The enduring experience-potion statuses. <c>508</c> is
    /// <c>持久经验药剂</c> (Enduring Medium, +100%), <c>585</c> is the weak
    /// +50% grant, and <c>590</c> is the +400% grant.
    /// </summary>
    public const int EnduringExperiencePotion = 508;
    public const int EnduringWeakExperiencePotion = 585;
    public const int EnduringHolyExperiencePotion = 590;

    public const int KijinPatron = 1500;
    public const int OniPatron = 1501;
    public const int DemonLordSeed = 1502;
    public const int TrueDemonLord = 1503;
    public const int FactionAreaExperience = 1504;
    public const int OctagramPatron = 1506;
    public const int PremiumBattlePass = 1507;
}

internal static class ExperienceBoostKinds
{
    public const int Consumable = 14;
    public const int Talent = 20;
    public const int Weekend = 22;
    public const int TrickOrTreat = 23;
    public const int Pet = 24;

    /// <summary>
    /// The enduring experience-potion family (items 4534/4535/4539). It is a
    /// channel of its own so its online duration is tracked independently of
    /// both the fighter <see cref="Consumable"/> grant and the pet
    /// <see cref="Pet"/> grant, and it feeds the fighter and pet stacks at the
    /// same time.
    /// </summary>
    public const int PersistentExperiencePotion = 25;

    public const int Guild = 100;
    public const int GuildTalent = 101;
    public const int Donator = 1008;
    public const int FactionArea = 1009;
    public const int BattlePass = 1010;

    public static bool AffectsFighter(int kind) => kind switch
    {
        Talent or GuildTalent or Pet => false,
        _ => true
    };

    public static bool AffectsTalent(int kind) =>
        kind is Talent or GuildTalent or FactionArea or BattlePass;

    public static bool AffectsPet(int kind) => kind is
        Consumable or Weekend or Pet or Guild or FactionArea or BattlePass or
        PersistentExperiencePotion;
}

internal static class DonatorBenefits
{
    public static int ExperienceBonusBasisPoints(DonatorTier tier) => tier switch
    {
        DonatorTier.KijinPatron => 500,
        DonatorTier.OniPatron => 1_000,
        DonatorTier.DemonLordSeed => 1_500,
        DonatorTier.TrueDemonLord => 2_000,
        DonatorTier.OctagramPatron => 2_500,
        _ => 0
    };

    public static int MaximumHitPointsBonusBasisPoints(DonatorTier tier) =>
        tier switch
        {
            DonatorTier.KijinPatron => 200,
            DonatorTier.OniPatron => 400,
            DonatorTier.DemonLordSeed => 600,
            DonatorTier.TrueDemonLord => 800,
            DonatorTier.OctagramPatron => 1_000,
            _ => 0
        };

    public static int AttackBonusBasisPoints(DonatorTier tier) => tier switch
    {
        DonatorTier.KijinPatron => 100,
        DonatorTier.OniPatron => 200,
        DonatorTier.DemonLordSeed => 300,
        DonatorTier.TrueDemonLord => 400,
        DonatorTier.OctagramPatron => 500,
        _ => 0
    };

    public static int StatusId(DonatorTier tier) => tier switch
    {
        DonatorTier.KijinPatron => ExperienceStatusIds.KijinPatron,
        DonatorTier.OniPatron => ExperienceStatusIds.OniPatron,
        DonatorTier.DemonLordSeed => ExperienceStatusIds.DemonLordSeed,
        DonatorTier.TrueDemonLord => ExperienceStatusIds.TrueDemonLord,
        DonatorTier.OctagramPatron => ExperienceStatusIds.OctagramPatron,
        _ => 0
    };

    public static string DisplayName(DonatorTier tier) => tier switch
    {
        DonatorTier.KijinPatron => "Kijin Patron",
        DonatorTier.OniPatron => "Oni Patron",
        DonatorTier.DemonLordSeed => "Demon Lord Seed",
        DonatorTier.TrueDemonLord => "True Demon Lord",
        DonatorTier.OctagramPatron => "Octagram Patron",
        _ => string.Empty
    };
}

internal static class BattlePassBenefits
{
    public const int ExperienceBonusBasisPoints = 500;
}

internal static class ExperienceBoostContract
{
    public const int MaximumActiveBoosts = 66;
    public const int MaximumSourceLength = 160;

    public static void ValidateRequest(ExperienceBoostReadRequest request)
    {
        if (request.AccountId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "An experience-boost read requires a positive account ID.");
        }

        if (request.CharacterId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "An experience-boost read requires a positive character ID.");
        }

        if (request.Camp > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The character camp must be Sparta or Athens.");
        }

        if (request.MapId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The map ID cannot be negative.");
        }

        RequireUtc(request.ReadAtUtc, nameof(request));
    }

    public static void ValidateSnapshot(
        ExperienceBoostSnapshot snapshot,
        DateTimeOffset readAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireUtc(readAtUtc, nameof(readAtUtc));
        if (snapshot.ActiveBoosts.IsDefault ||
            snapshot.ActiveBoosts.Length > MaximumActiveBoosts)
        {
            throw new InvalidDataException(
                "The experience-boost projection exceeds its row bound.");
        }

        var previousKind = int.MinValue;
        foreach (var boost in snapshot.ActiveBoosts)
        {
            if (boost is null ||
                boost.StatusId <= 0 ||
                boost.Kind <= 0 ||
                boost.BonusBasisPoints < 0 ||
                boost.Priority < 0 ||
                boost.Source is null ||
                boost.Source.Length > MaximumSourceLength ||
                boost.Kind <= previousKind)
            {
                throw new InvalidDataException(
                    "The experience-boost projection contains invalid or duplicate rows.");
            }

            if (boost.ExpiresAtUtc is { } expiresAt)
            {
                if (expiresAt == default ||
                    expiresAt.Offset != TimeSpan.Zero ||
                    expiresAt <= readAtUtc)
                {
                    throw new InvalidDataException(
                        "An active experience boost has an invalid expiry.");
                }
            }

            previousKind = boost.Kind;
        }
    }

    internal static void RequireUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The timestamp must be a non-default UTC value.");
        }
    }
}
