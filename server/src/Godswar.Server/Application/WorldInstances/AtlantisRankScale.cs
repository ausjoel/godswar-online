using Godswar.Server.Application.Rates;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// ATL-SLIDERS (the admin 2026-11-23, "can we make the view only data like ... atlantis and wonderland to be editable?"):
/// one HP % and one damage % slider per Atlantis monster rank (normal, elite, boss), 100 = the stock balance.
/// HP multiplies what <see cref="AtlantisSpawnBalancePolicy"/> gives (party level and size scaling are kept) when a
/// run's waves are prepared; damage multiplies every hit an Atlantis wave monster lands on a player, at once. The
/// Atlantis pets are not touched. A value above 0 never drops below 1. Same shape as <see cref="WonderlandIslandScale"/>.
/// </summary>
internal static class AtlantisRankScale
{
    public const string Category = "Atlantis monsters";
    public const decimal MinimumPercent = 10m;
    public const decimal MaximumPercent = 500m;

    public static string HpKey(AtlantisMonsterRank rank) => $"instance.atlantis_{Name(rank)}_hp_percent";
    public static string DamageKey(AtlantisMonsterRank rank) => $"instance.atlantis_{Name(rank)}_damage_percent";

    private static string Name(AtlantisMonsterRank rank) => rank switch
    {
        AtlantisMonsterRank.Normal => "normal",
        AtlantisMonsterRank.Elite => "elite",
        AtlantisMonsterRank.Boss => "boss",
        _ => throw new ArgumentOutOfRangeException(nameof(rank))
    };

    private const string Source = "Application/WorldInstances/AtlantisRankScale.cs";

    // Literal texts: editor/data/rate_text.json must carry the same words (tests/test_help_text_unit.py).
    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(HpKey(AtlantisMonsterRank.Normal), Category, "Normal monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 10, Source,
            "Health of the normal monsters in Atlantis, in percent of normal. They still scale with the party's level and size. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(AtlantisMonsterRank.Normal), Category, "Normal monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 11, Source,
            "Damage the normal monsters in Atlantis do to players, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(AtlantisMonsterRank.Elite), Category, "Elite monster HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 20, Source,
            "Health of the elite monsters in Atlantis, in percent of normal. They still scale with the party's level and size. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(AtlantisMonsterRank.Elite), Category, "Elite monster damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 21, Source,
            "Damage the elite monsters in Atlantis do to players, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
        new(HpKey(AtlantisMonsterRank.Boss), Category, "Boss HP", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 30, Source,
            "Health of the five Atlantis bosses, in percent of normal. They still scale with the party's level and size. 100 = unchanged. Takes effect in runs that start afterwards."),
        new(DamageKey(AtlantisMonsterRank.Boss), Category, "Boss damage", "%", ServerRateKind.Absolute, 100m, MinimumPercent, MaximumPercent, 5m, 31, Source,
            "Damage the five Atlantis bosses do to players, in percent of normal. 100 = unchanged. Takes effect at once, also in runs already going."),
    ];

    public static decimal HpPercent(AtlantisMonsterRank rank) => Percent(HpKey(rank));
    public static decimal DamagePercent(AtlantisMonsterRank rank) => Percent(DamageKey(rank));

    private static decimal Percent(string key) =>
        Math.Clamp(ServerRates.Current.Get(key), MinimumPercent, MaximumPercent);

    public static uint Scale(uint value, decimal percent)
    {
        if (percent == 100m || value == 0) return value;
        return (uint)Math.Clamp(decimal.Round(value * percent / 100m, 0, MidpointRounding.AwayFromZero), 1m, uint.MaxValue);
    }

    public static AtlantisSpawnStats ScaleHp(AtlantisSpawnStats stats, AtlantisMonsterRank rank) =>
        ScaleHp(stats, HpPercent(rank));

    internal static AtlantisSpawnStats ScaleHp(AtlantisSpawnStats stats, decimal percent) =>
        stats with { MaximumHealth = Scale(stats.MaximumHealth, percent) };

    /// <summary>The rank of an Atlantis wave monster from its template key (B_normalG_..., B_eliteG_..., B_bossG...).</summary>
    public static bool TryRankOf(string templateKey, out AtlantisMonsterRank rank)
    {
        rank = default;
        switch (Godswar.Server.Application.World.Content.MedalLootRule.RankOf(templateKey))
        {
            case Godswar.Server.Application.World.Content.MedalLootRule.Rank.Normal: rank = AtlantisMonsterRank.Normal; return true;
            case Godswar.Server.Application.World.Content.MedalLootRule.Rank.Elite: rank = AtlantisMonsterRank.Elite; return true;
            case Godswar.Server.Application.World.Content.MedalLootRule.Rank.Boss: rank = AtlantisMonsterRank.Boss; return true;
            default: return false;
        }
    }

    /// <summary>A hit an Atlantis wave monster lands on a player, times its rank's damage slider; pets and other maps unchanged.</summary>
    public static uint ScaleDamage(short mapId, uint objectId, string templateKey, uint damage)
    {
        if (mapId != 205 || damage == 0 || AtlantisPetSpawnPolicy.Matches(objectId, templateKey) ||
            !TryRankOf(templateKey, out var rank))
            return damage;
        return Scale(damage, DamagePercent(rank));
    }
}
