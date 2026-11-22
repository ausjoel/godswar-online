using System.Buffers.Binary;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// B23-S (the admin d82 "a copy of Bay Under Attack but very strong bosses"): two hot sliders for the bosses of the
/// Aether Sanctum (map 70). The authored boss rows carry the HP of a normal Lv200 boss (Olympus' Lv200 boss,
/// 23,000,040, scaled by level); HP % multiplies that when the map's monsters are built (the first player entering
/// after a server start), damage % multiplies every hit a Sanctum boss lands on a player at once. Elites and normals
/// are not touched (they keep G2's Olympus HP x1.2 at Lv185-200). A hit above 0 never drops below 1.
/// </summary>
internal static class AetherSanctumBossScale
{
    public const string Category = "Aether Sanctum";
    public const string HpKey = "sanctum.boss_hp_percent";
    public const string DamageKey = "sanctum.boss_damage_percent";
    public const decimal DefaultHpPercent = 500m;
    public const decimal DefaultDamagePercent = 200m;
    public const decimal MinimumPercent = 10m;
    public const decimal MaximumPercent = 2000m;

    private const string Source = "Application/WorldInstances/AetherSanctumBossScale.cs";
    private const int CurrentHealthOffset = 20;
    private const int MaximumHealthOffset = 24;

    // Literal texts: editor/data/rate_text.json carries the same words (tests/test_help_text_unit.py).
    public static IReadOnlyList<ServerRateDefinition> Definitions { get; } =
    [
        new(HpKey, Category, "Boss HP", "%", ServerRateKind.Absolute, DefaultHpPercent, MinimumPercent, MaximumPercent, 25m, 10, Source,
            "Health of the two Aether Sanctum bosses, in percent of a normal level 200 boss. 500 = five times as much. Takes effect after the next server restart."),
        new(DamageKey, Category, "Boss damage", "%", ServerRateKind.Absolute, DefaultDamagePercent, MinimumPercent, MaximumPercent, 25m, 11, Source,
            "Damage the two Aether Sanctum bosses do to players, in percent of a normal level 200 boss. 200 = twice as much. Takes effect at once."),
    ];

    public static decimal HpPercent => Percent(HpKey);
    public static decimal DamagePercent => Percent(DamageKey);

    private static decimal Percent(string key) =>
        Math.Clamp(ServerRates.Current.Get(key), MinimumPercent, MaximumPercent);

    public static bool IsSanctumBoss(short mapId, string templateKey) =>
        mapId == AetherSanctumPolicy.MapId &&
        Godswar.Server.Application.World.Content.MedalLootRule.RankOf(templateKey) ==
            Godswar.Server.Application.World.Content.MedalLootRule.Rank.Boss;

    public static uint Scale(uint value, decimal percent)
    {
        if (percent == 100m || value == 0) return value;
        return (uint)Math.Clamp(decimal.Round(value * percent / 100m, 0, MidpointRounding.AwayFromZero), 1m, uint.MaxValue);
    }

    public static uint ScaleDamage(short mapId, string templateKey, uint damage) =>
        IsSanctumBoss(mapId, templateKey) ? Scale(damage, DamagePercent) : damage;

    /// <summary>A Sanctum boss spawn with its packet HP (current and maximum) times the HP slider; others unchanged.</summary>
    public static CapturedMonsterSpawn NormalizeSpawn(CapturedMonsterSpawn spawn) =>
        NormalizeSpawn(spawn, HpPercent);

    internal static CapturedMonsterSpawn NormalizeSpawn(CapturedMonsterSpawn spawn, decimal percent)
    {
        if (!IsSanctumBoss(spawn.MapId, spawn.TemplateKey) || spawn.Packet.Length < MaximumHealthOffset + 4 || percent == 100m)
            return spawn;
        var packet = spawn.Packet.ToArray();
        var baseHp = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(MaximumHealthOffset, 4));
        var hp = Scale(baseHp, percent);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(CurrentHealthOffset, 4), hp);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(MaximumHealthOffset, 4), hp);
        Console.WriteLine(
            $"[sanctum] boss hp object={spawn.ObjectId} template={spawn.TemplateKey} base={baseHp} pct={percent} -> {hp}");
        return spawn with { Packet = packet };
    }
}
