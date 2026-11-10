using System.Collections.Frozen;
using Godswar.Server.Application.Events;

namespace Godswar.Server.Application.Npc;

// Editor-written NPC shop lists and teleporter destinations. Empty = the compiled content, so a missing or unreadable
// table leaves the stock behaviour in place.
internal sealed record NpcShopOverrideRow(
    uint ItemId,
    int Price,
    byte Category,
    byte Quality,
    byte Grade,
    byte Bound,
    byte StackMarker);

internal sealed record NpcTeleportOverride(
    string NpcKey,
    int SubId,
    short TargetMapId,
    float X,
    float Z,
    int MinimumLevel);

internal readonly record struct ItemActivator(int SkillId, int CooldownSeconds);

/// <summary>FINALISE: per-map/tier "does this kill drop anything at all" chance (table drop_tier_overrides).</summary>
internal readonly record struct DropTierOverride(int AnyDropBasisPoints);

/// <summary>FINALISE: per-map/tier random respawn window (table respawn_tier_overrides).</summary>
internal readonly record struct RespawnTierOverride(int MinimumSeconds, int MaximumSeconds);

/// <summary>FINALISE: one editable level band (table level_band_overrides). A null side keeps the compiled value.</summary>
internal readonly record struct LevelBandOverride(
    int? MinimumLevel,
    int? MaximumLevel,
    string ScheduleMode = "compiled", // FINALISE2: compiled | always | windows | closed
    bool? GmBypass = null);           // FINALISE2: null = the gate's compiled GM rule

internal sealed class NpcContentSnapshot
{
    public static NpcContentSnapshot Empty { get; } = new(
        0,
        new Dictionary<string, IReadOnlyList<NpcShopOverrideRow>>(),
        [],
        []);

    public NpcContentSnapshot(
        long revision,
        IReadOnlyDictionary<string, IReadOnlyList<NpcShopOverrideRow>> shops,
        IEnumerable<NpcTeleportOverride> teleports,
        IEnumerable<int>? editorAdminAccounts = null,
        IReadOnlyDictionary<uint, ItemActivator>? activators = null,
        EventContent? events = null,
        IReadOnlyDictionary<string, LevelBandOverride>? levelBands = null,
        IReadOnlyDictionary<(short MapId, string Tier), DropTierOverride>? dropTiers = null,
        IReadOnlyDictionary<(short MapId, string Tier), RespawnTierOverride>? respawnTiers = null)
    {
        DropTiers = (dropTiers ?? new Dictionary<(short, string), DropTierOverride>()).ToFrozenDictionary();
        RespawnTiers = (respawnTiers ?? new Dictionary<(short, string), RespawnTierOverride>()).ToFrozenDictionary();
        LevelBands = (levelBands ?? new Dictionary<string, LevelBandOverride>())
            .ToFrozenDictionary(StringComparer.Ordinal);
        Events = events ?? EventContent.Empty;
        Activators = (activators ?? new Dictionary<uint, ItemActivator>()).ToFrozenDictionary();
        EditorAdminAccounts = (editorAdminAccounts ?? []).ToFrozenSet();
        Revision = revision;
        Shops = shops.ToFrozenDictionary(StringComparer.Ordinal);
        Teleports = teleports.ToFrozenDictionary(static t => (t.NpcKey, t.SubId));
    }

    public long Revision { get; }

    public EventContent Events { get; }

    public FrozenSet<int> EditorAdminAccounts { get; }

    public FrozenDictionary<uint, ItemActivator> Activators { get; }

    public FrozenDictionary<string, IReadOnlyList<NpcShopOverrideRow>> Shops { get; }

    public FrozenDictionary<(string NpcKey, int SubId), NpcTeleportOverride> Teleports { get; }

    /// <summary>FINALISE: editable level bands by gate key (see <see cref="LevelBandGates"/>).</summary>
    public FrozenDictionary<string, LevelBandOverride> LevelBands { get; }

    public FrozenDictionary<(short MapId, string Tier), DropTierOverride> DropTiers { get; }

    public FrozenDictionary<(short MapId, string Tier), RespawnTierOverride> RespawnTiers { get; }
}

/// <summary>
/// FINALISE (the admin 2026-11-05, Elasson): per-map and per-tier loot and respawn controls. The tier comes from the
/// template key (<c>_boss</c> = boss, <c>_elite</c> = elite, anything else = normal). A row for the map wins over
/// the all-maps row (map_id -1). No row = the stock behaviour, so an empty table changes nothing.
/// </summary>
internal static class MonsterTierOverrides
{
    public const short AllMaps = -1;
    public const string Normal = "normal";
    public const string Elite = "elite";
    public const string Boss = "boss";

    public static string Tier(string templateKey) =>
        templateKey.Contains("_boss", StringComparison.OrdinalIgnoreCase) ? Boss
        : templateKey.Contains("_elite", StringComparison.OrdinalIgnoreCase) ? Elite
        : Normal;

    private static bool TryGet<T>(
        System.Collections.Frozen.FrozenDictionary<(short MapId, string Tier), T> table,
        short mapId,
        string tier,
        out T value) =>
        table.TryGetValue((mapId, tier), out value!) || table.TryGetValue((AllMaps, tier), out value!);

    /// <summary>
    /// True when this kill drops nothing: the "any loot" roll (deterministic in the death event, so a replay of
    /// the same kill decides the same way) failed against the map/tier chance. False without a row.
    /// </summary>
    public static bool SkipsAllLoot(short mapId, string templateKey, Guid deathEventId)
    {
        if (!TryGet(NpcContentOverrides.Current.DropTiers, mapId, Tier(templateKey), out var row))
        {
            return false;
        }

        Span<byte> input = stackalloc byte[20];
        deathEventId.TryWriteBytes(input[..16]);
        BitConverter.TryWriteBytes(input[16..], 0x414E5944); // "ANYD": independent of the per-rule rolls
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(input, hash);
        return BitConverter.ToUInt32(hash) % 10_000 >= (uint)Math.Clamp(row.AnyDropBasisPoints, 0, 10_000);
    }

    /// <summary>A random respawn delay inside the map/tier window, or false without a row (bosses excluded).</summary>
    public static bool TryRespawnDelay(short mapId, string templateKey, out TimeSpan delay)
    {
        delay = default;
        var tier = Tier(templateKey);
        if (tier == Boss ||
            !TryGet(NpcContentOverrides.Current.RespawnTiers, mapId, tier, out var row))
        {
            return false;
        }

        var low = Math.Max(15, Math.Min(row.MinimumSeconds, row.MaximumSeconds));
        var high = Math.Max(low, Math.Max(row.MinimumSeconds, row.MaximumSeconds));
        delay = TimeSpan.FromSeconds(Random.Shared.Next(low, high + 1));
        return true;
    }
}

/// <summary>
/// FINALISE (the admin 2026-11-05): the gate keys an editor row in <c>public.level_band_overrides</c> can change.
/// No row = the compiled band, so an empty or missing table changes nothing.
/// </summary>
internal static class LevelBandGates
{
    public const string Pindus = "battlefield.pindus";
    public const string NiMiniLower = "battlefield.nimini_lower";
    public const string NiMiniUpper = "battlefield.nimini_upper";
    public const string LelantineFarm = "battlefield.lelantine_farm";
    public const string DuelArena = "battlefield.duel_arena";
    public const string Atlantis = "instance.atlantis";
    public const string Wonderland = "instance.wonderland";
    public const string HarborAttack = "instance.harbor_attack";
    public const string TrainingGround = "instance.training_ground";

    public static (int Minimum, int Maximum) Apply(string gate, int minimum, int maximum)
    {
        if (!NpcContentOverrides.Current.LevelBands.TryGetValue(gate, out var band))
        {
            return (minimum, maximum);
        }

        return (band.MinimumLevel ?? minimum, band.MaximumLevel ?? maximum);
    }
}

internal static class NpcContentOverrides
{
    private static NpcContentSnapshot _current = NpcContentSnapshot.Empty;

    public static NpcContentSnapshot Current => Volatile.Read(ref _current);

    public static void Install(NpcContentSnapshot snapshot) =>
        Volatile.Write(ref _current, snapshot);

    public static Func<CancellationToken, Task<string>>? DescribeHook { get; set; }

    public static Func<CancellationToken, Task<string>>? ReloadHook { get; set; }

    public static Func<string, CancellationToken, Task<string>>? DescribeShopHook { get; set; }

    public static Func<CancellationToken, Task<string>>? DescribeEventsHook { get; set; }

    /// <summary>FINALISE2: GET /gate-content, every gate with its compiled and effective values.</summary>
    public static Func<CancellationToken, Task<string>>? DescribeGatesHook { get; set; }
}
