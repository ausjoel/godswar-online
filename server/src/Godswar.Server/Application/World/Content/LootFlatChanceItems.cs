using System.Collections.Frozen;
using System.Globalization;
using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// GUILDSTONE (the admin, round 2 answer 1, 2026-11-03): the Guild Stone (4100)
/// must drop at 0.01% per kill. A loot rule stores whole basis points
/// (CHECK chance_basis_points &gt;= 1) and the roll multiplies them by the hot
/// <c>drop.chance</c> rate (3 on live), so the rarest a stored rule can be is
/// trunc(1 x 3) = 3 bp = 0.03%. Items listed in
/// <c>GODSWAR_LOOT_FLAT_CHANCE_ITEMS</c> (comma-separated item ids, e.g.
/// "4100") skip <c>drop.chance</c>: their stored basis points are the
/// effective chance, so 1 bp stored = exactly 1 in 10,000.
/// Unset or empty (the default) = every rule rolls exactly as before.
/// Read once at start-up, like the loot snapshot itself.
/// </summary>
internal static class LootFlatChanceItems
{
    public const string EnvironmentVariable = "GODSWAR_LOOT_FLAT_CHANCE_ITEMS";

    private static readonly Lazy<FrozenSet<uint>> FromEnvironment = new(
        static () => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)));

    private static FrozenSet<uint>? _checkOverride;

    internal static FrozenSet<uint> Current => _checkOverride ?? FromEnvironment.Value;

    /// <summary>Protocol checks only: replace the list (null = back to the environment).</summary>
    internal static void OverrideForChecks(string? value) =>
        _checkOverride = value is null ? null : Parse(value);

    internal static FrozenSet<uint> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FrozenSet<uint>.Empty;
        }

        var ids = new HashSet<uint>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids.ToFrozenSet();
    }

    /// <summary>The roll threshold (0..10000) for one rule of one kill.</summary>
    public static int ThresholdBasisPoints(ServerRatesSnapshot rates, uint itemId, int storedBasisPoints) =>
        Current.Contains(itemId)
            ? Math.Clamp(storedBasisPoints, 0, 10_000)
            : rates.MultiplyBasisPoints(ServerRateKeys.DropChance, storedBasisPoints);
}
