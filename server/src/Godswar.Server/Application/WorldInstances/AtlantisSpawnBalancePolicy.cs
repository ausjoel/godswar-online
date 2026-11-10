namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct AtlantisSpawnStats(uint Level, uint MaximumHealth);

/// <summary>
/// Authored starting balance, not recovered original-server health values.
/// Counts and points remain fixed. Only HP scales with party size and stage;
/// the existing published combat profile derives damage/defence from level.
/// </summary>
internal static class AtlantisSpawnBalancePolicy
{
    public const string Version = "reborn-atlantis-balance-v2";

    public static AtlantisSpawnStats Resolve(
        IReadOnlyList<(int CharacterId, int Level)> party,
        int stageNumber,
        AtlantisMonsterRank rank)
    {
        ArgumentNullException.ThrowIfNull(party);
        if (party.Count is < 1 or > 5 ||
            party.Any(static member => member.CharacterId <= 0 || member.Level < 90) ||
            party.Select(static member => member.CharacterId).Distinct().Count() != party.Count)
        {
            throw new ArgumentException("Atlantis balance requires 1–5 distinct level 90+ players.",
                nameof(party));
        }
        if (stageNumber is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(stageNumber));
        }
        var baseHealth = rank switch
        {
            AtlantisMonsterRank.Normal => 15_000u,
            AtlantisMonsterRank.Elite => 75_000u,
            AtlantisMonsterRank.Boss => 500_000u,
            _ => throw new ArgumentOutOfRangeException(nameof(rank))
        };
        var level = (uint)party.Max(static member => member.Level);
        var stagePercent = 100u + 5u * (uint)(stageNumber - 1);
        var partyPercent = 100u + 50u * (uint)(party.Count - 1);
        // Admission has no upper level gate. Keep the exact integer formula
        // safe for every positive Int32 level and fit HP into its packet field.
        var health = (UInt128)baseHealth * level * level * stagePercent * partyPercent /
            (90u * 90u * 100u * 100u);
        return new(level, health > uint.MaxValue ? uint.MaxValue : (uint)health);
    }
}
