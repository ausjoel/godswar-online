using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Rewards;

/// <summary>
/// One startup-pinned view of the mutable PostgreSQL monster-reward policy.
/// A coordinated restart activates later management updates.
/// </summary>
internal sealed record MonsterRewardPolicySnapshot(
    int MaximumLowerLevelGap,
    int GlobalExperienceMultiplierBasisPoints,
    long Revision,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy)
{
    public const int DefaultMaximumLowerLevelGap = 20;
    public const int MaximumSupportedLowerLevelGap = 199;
    public const int DefaultGlobalExperienceMultiplierBasisPoints = 10_000;
    public const int MaximumGlobalExperienceMultiplierBasisPoints = 50_000;

    public static MonsterRewardPolicySnapshot Default { get; } = new(
        DefaultMaximumLowerLevelGap,
        DefaultGlobalExperienceMultiplierBasisPoints,
        0,
        DateTimeOffset.UnixEpoch,
        "compiled-default");

    public void Validate()
    {
        if (MaximumLowerLevelGap is < 0 or
            > MaximumSupportedLowerLevelGap)
        {
            throw new InvalidDataException(
                "The monster-reward lower-level gap is outside its safe bounds.");
        }
        if (GlobalExperienceMultiplierBasisPoints is <
                DefaultGlobalExperienceMultiplierBasisPoints or
            > MaximumGlobalExperienceMultiplierBasisPoints)
        {
            throw new InvalidDataException(
                "The global experience multiplier must be between 1x and 5x.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(Revision);
        if (UpdatedAtUtc == default ||
            string.IsNullOrWhiteSpace(UpdatedBy) ||
            UpdatedBy.Length > 128)
        {
            throw new InvalidDataException(
                "Monster-reward policy provenance is invalid.");
        }
    }

    public bool IsEligible(int playerLevel, int monsterLevel)
    {
        if (monsterLevel < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(monsterLevel));
        }

        var normalizedPlayerLevel = Math.Max(1, playerLevel);
        return (long)normalizedPlayerLevel - monsterLevel <=
            MaximumLowerLevelGap;
    }

    public int ApplyExperienceMultipliers(
        int baseExperience,
        int additiveBonusBasisPoints)
    {
        if (baseExperience <= 0)
        {
            return 0;
        }

        if (GlobalExperienceMultiplierBasisPoints is <
                DefaultGlobalExperienceMultiplierBasisPoints or
            > MaximumGlobalExperienceMultiplierBasisPoints)
        {
            throw new InvalidDataException(
                "The global experience multiplier must be between 1x and 5x.");
        }

        var additiveMultiplierBasisPoints =
            Math.Max(0L, 10_000L + additiveBonusBasisPoints);
        var adjusted =
            (Int128)baseExperience *
            additiveMultiplierBasisPoints *
            GlobalExperienceMultiplierBasisPoints /
            100_000_000;
        return adjusted >= int.MaxValue
            ? int.MaxValue
            : (int)adjusted;
    }

    public string CoordinationRevision()
    {
        Validate();
        var canonical = Encoding.UTF8.GetBytes(
            "monster-reward-policy-v2\n" +
            $"revision:{Revision.ToString(CultureInfo.InvariantCulture)}\n" +
            "maximum-lower-level-gap:" +
            MaximumLowerLevelGap.ToString(CultureInfo.InvariantCulture) +
            "\n" +
            "global-experience-multiplier-basis-points:" +
            GlobalExperienceMultiplierBasisPoints.ToString(
                CultureInfo.InvariantCulture) +
            "\n");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }
}
