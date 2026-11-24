using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

// ATL-SLIDERS (the admin 2026-11-23): Atlantis per-rank HP % / damage % hot sliders, 100 = stock.
internal static class AtlantisRankScaleChecks
{
    public const string CheckName = "ATL-SLIDERS Atlantis rank HP/damage sliders: stock at 100, party scaling kept, pets untouched";

    public static Task RunAsync()
    {
        foreach (var rank in new[] { AtlantisMonsterRank.Normal, AtlantisMonsterRank.Elite, AtlantisMonsterRank.Boss })
        {
            var hp = ServerRateCatalog.Get(AtlantisRankScale.HpKey(rank));
            var damage = ServerRateCatalog.Get(AtlantisRankScale.DamageKey(rank));
            Check.Equal(100m, hp.DefaultValue, $"{rank} HP default 100%");
            Check.Equal(100m, damage.DefaultValue, $"{rank} damage default 100%");
            Check.Equal(AtlantisRankScale.Category, hp.Category, "own rates category");
            Check.Equal(100m, AtlantisRankScale.HpPercent(rank), $"{rank} HP reads 100 with no row change");
        }

        var party = new[] { (1, 120), (2, 95) };
        foreach (var rank in new[] { AtlantisMonsterRank.Normal, AtlantisMonsterRank.Elite, AtlantisMonsterRank.Boss })
        {
            var stock = AtlantisSpawnBalancePolicy.Resolve(party, 3, rank);
            Check.Equal(stock, AtlantisRankScale.ScaleHp(stock, 100m), $"{rank}: 100% = the stock balance exactly");
            Check.Equal(stock, AtlantisRankScale.ScaleHp(stock, rank), $"{rank}: live default = the stock balance exactly");
            var doubled = AtlantisRankScale.ScaleHp(stock, 200m);
            Check.Equal(stock.Level, doubled.Level, $"{rank}: level still the party's highest");
            Check.Equal(stock.MaximumHealth * 2, doubled.MaximumHealth, $"{rank}: 200% doubles HP");
        }
        var huge = new AtlantisSpawnStats(200, uint.MaxValue - 5);
        Check.Equal(uint.MaxValue, AtlantisRankScale.ScaleHp(huge, 500m).MaximumHealth, "HP is clamped to its packet field");

        Check.Equal(1u, AtlantisRankScale.Scale(1, 10m), "a hit above 0 never drops below 1");
        Check.Equal(0u, AtlantisRankScale.Scale(0, 300m), "a miss stays a miss");
        Check.Equal(1_000u, AtlantisRankScale.ScaleDamage(205, 42_010, "B_bossG_octopus_001", 1_000), "boss hit at the default 100%");
        Check.True(AtlantisRankScale.TryRankOf("B_normalG_fish_001", out var n) && n == AtlantisMonsterRank.Normal, "normal rank from key");
        Check.True(AtlantisRankScale.TryRankOf("B_eliteG_mage_018", out var e) && e == AtlantisMonsterRank.Elite, "elite rank from key");
        Check.True(AtlantisRankScale.TryRankOf("B_bossGB_hydra_004", out var b) && b == AtlantisMonsterRank.Boss, "boss rank from key");
        Check.Equal(1_000u, AtlantisRankScale.ScaleDamage(209, 42_010, "B_bossSAQ_mage_018", 1_000), "other maps unchanged");
        foreach (var (objectId, placement) in AtlantisPetSpawnPolicy.Spawns)
            Check.Equal(1_000u, AtlantisRankScale.ScaleDamage(205, objectId, placement.TemplateKey, 1_000), "Atlantis pets unchanged");
        return Task.CompletedTask;
    }
}
