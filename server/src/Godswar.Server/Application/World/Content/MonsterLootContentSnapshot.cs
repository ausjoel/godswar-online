using System.Collections.Frozen;
using System.Security.Cryptography;

using Godswar.Server.State;

namespace Godswar.Server.Application.World.Content;

/// <summary>
/// One editable monster loot header: which monster drops, and how many items a
/// single kill may leave on the ground.
/// </summary>
internal readonly record struct MonsterLootTableRule(
    string TemplateKey,
    int MaximumDrops);

/// <summary>One editable drop rule of a monster loot table.</summary>
/// <param name="BoundOnPickup">
/// Whether this rule's drop binds to the character that picks it up;
/// <c>null</c> follows the item template's own <c>BindType</c>.
/// </param>
/// <param name="RollProfile">
/// ATTR: a <c>loot_roll_profiles</c> key. When set (and the profile and the
/// item's attribute pool exist) quality, grade and attributes are rolled per
/// drop instead of copied from <paramref name="Attributes"/>.
/// </param>
internal readonly record struct MonsterLootRule(
    string TemplateKey,
    int LootIndex,
    uint ItemId,
    int ChanceBasisPoints,
    int MinimumQuantity,
    int MaximumQuantity,
    bool? BoundOnPickup = null,
    ItemGrantAttributes Attributes = default,
    string? RollProfile = null);

/// <summary>A drop that survived its chance roll for one kill.</summary>
/// <param name="ChanceBasisPoints">
/// F-166: the stock chance it rolled at (rule or extra), used only to rank overflow beyond the corpse's 8 slots.
/// </param>
internal readonly record struct RolledMonsterLoot(
    int LootIndex,
    uint ItemId,
    int Quantity,
    bool? BoundOnPickup = null,
    ItemGrantAttributes Attributes = default,
    int ChanceBasisPoints = 10_000);

/// <summary>
/// One repeatable-read snapshot of the database-owned monster loot tables,
/// shaped like <c>MedusaMonsterContentSnapshot</c>. Schema creation stays
/// migration-owned; this type only validates and indexes what was read.
/// </summary>
internal sealed class MonsterLootContentSnapshot
{
    private readonly FrozenDictionary<string, MonsterLootTableRule> _tables;
    private readonly FrozenDictionary<string, MonsterLootRule[]> _loot;
    private readonly LootRollContent _rollContent;

    public MonsterLootContentSnapshot(
        IReadOnlyList<MonsterLootTableRule> tables,
        IReadOnlyList<MonsterLootRule> loot,
        LootRollContent? rollContent = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(loot);
        Validate(tables, loot);
        _rollContent = rollContent ?? LootRollContent.Empty;
        _tables = tables.ToFrozenDictionary(
            static table => table.TemplateKey,
            StringComparer.Ordinal);
        _loot = loot
            .GroupBy(static rule => rule.TemplateKey, StringComparer.Ordinal)
            .ToFrozenDictionary(
                static group => group.Key,
                static group => group
                    .OrderBy(static rule => rule.LootIndex)
                    .ToArray(),
                StringComparer.Ordinal);
    }

    /// <summary>ATTR: the roll profiles and attribute pools of this snapshot.</summary>
    public LootRollContent RollContent => _rollContent;

    public IReadOnlyCollection<MonsterLootTableRule> Tables =>
        _tables.Values;

    public IReadOnlyCollection<MonsterLootRule> Loot =>
        _loot.Values.SelectMany(static rules => rules).ToArray();

    public bool TryGetTable(
        string? templateKey,
        out MonsterLootTableRule table)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            table = default;
            return false;
        }

        return _tables.TryGetValue(templateKey, out table);
    }

    /// <summary>
    /// AREA LOOT: separator between a template key and a map id in a per-area
    /// loot table key, e.g. <c>A_normal_snake_008@15</c>. Template keys are
    /// letters, digits and underscores, so the key cannot collide with one.
    /// </summary>
    public const char AreaKeySeparator = '@';

    /// <summary>
    /// AREA LOOT: the loot table key for a monster killed on a map. A table
    /// keyed <c>template@map</c> wins; otherwise the plain template table is
    /// used, exactly as before. Returns <c>null</c> when neither exists, so the
    /// monster drops nothing. Same template, different map = a different
    /// monster for loot (the admin a29: the art is reused at other sizes/colours).
    /// </summary>
    public string? ResolveTableKey(string? templateKey, int mapId)
    {
        if (string.IsNullOrWhiteSpace(templateKey))
        {
            return null;
        }

        var areaKey = string.Concat(
            templateKey,
            AreaKeySeparator.ToString(),
            mapId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_tables.ContainsKey(areaKey))
        {
            return areaKey;
        }

        return _tables.ContainsKey(templateKey) ? templateKey : null;
    }

    /// <summary>
    /// Rolls one kill. The roll is deterministic in the death event so a replay
    /// of the same kill yields the same ground items, and it stops at the
    /// table's drop cap.
    /// </summary>
    /// <param name="rolledAt">
    /// EVENT DROPS (q07): the kill time checked against public.event_drop_windows; null = now. A rule whose item a
    /// window names is skipped outside its windows; every other rule rolls exactly as before.
    /// </param>
    public IReadOnlyList<RolledMonsterLoot> RollLoot(
        string templateKey,
        Guid deathEventId,
        DateTimeOffset? rolledAt = null)
    {
        if (deathEventId == Guid.Empty ||
            !_tables.TryGetValue(templateKey, out var table) ||
            !_loot.TryGetValue(templateKey, out var rules))
        {
            return [];
        }

        // RATES: drop chance / quantity / maximum-items multipliers
        // (public.server_rates). The roll itself is unchanged, so a rate of 1
        // drops exactly what the stock table drops for the same death.
        var rates = Godswar.Server.Application.Rates.ServerRates.Current;
        // F-166: this may still roll more than 8; PrepareMonsterLoot keeps the best 8 (CorpseLootLimit.KeepBest).
        var maximumDrops = Math.Min(
            rules.Length,
            rates.Multiply(
                Godswar.Server.Application.Rates.ServerRateKeys.DropMaximumItems,
                table.MaximumDrops,
                CorpseLootLimit.LootIndexCount));
        var rolled = new List<RolledMonsterLoot>(maximumDrops);
        Span<byte> input = stackalloc byte[20];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        deathEventId.TryWriteBytes(input[..16]);
        var eventWindows = EventDropWindowCatalog.Current; // EVENT DROPS (q07)
        var at = rolledAt ?? DateTimeOffset.UtcNow;
        foreach (var rule in rules)
        {
            if (rolled.Count >= maximumDrops)
            {
                break;
            }

            if (!eventWindows.AllowsDrop(templateKey, rule.ItemId, at))
            {
                continue;
            }

            BitConverter.TryWriteBytes(input[16..], rule.LootIndex);
            SHA256.HashData(input, hash);
            if (BitConverter.ToUInt32(hash) % 10_000 >= LootFlatChanceItems.ThresholdBasisPoints( // GUILDSTONE
                    rates, rule.ItemId, rule.ChanceBasisPoints))
            {
                continue;
            }

            var range = checked(
                rule.MaximumQuantity - rule.MinimumQuantity + 1);
            var quantity = checked(
                rule.MinimumQuantity +
                (int)(BitConverter.ToUInt32(hash[4..]) % range));
            // BAY2: Bay Under Attack gear (profiles harbor_*) is one piece, never
            // an identical pair under drop.quantity (live runs it at 2).
            if (rule.RollProfile?.StartsWith("harbor_", StringComparison.Ordinal) != true)
            {
            quantity = rates.Multiply( // RATES
                Godswar.Server.Application.Rates.ServerRateKeys.DropQuantity,
                quantity,
                255);
            }
            // ATTR: a rule without a roll profile keeps its fixed columns.
            var attributes = rule.Attributes;
            if (rule.RollProfile is not null &&
                _rollContent.TryRoll(
                    rule.RollProfile,
                    rule.ItemId,
                    deathEventId,
                    rule.LootIndex,
                    rates,
                    out var rolledAttributes))
            {
                attributes = rolledAttributes;
            }

            rolled.Add(new(
                rule.LootIndex,
                rule.ItemId,
                quantity,
                rule.BoundOnPickup,
                attributes,
                rule.ChanceBasisPoints)); // F-166 overflow ranking
        }

        return rolled.AsReadOnly();
    }

    private static void Validate(
        IReadOnlyList<MonsterLootTableRule> tables,
        IReadOnlyList<MonsterLootRule> loot)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            if (string.IsNullOrWhiteSpace(table.TemplateKey) ||
                table.MaximumDrops is < 1 or > 32 ||
                !keys.Add(table.TemplateKey))
            {
                throw new InvalidDataException(
                    $"Monster loot table {table.TemplateKey} is invalid.");
            }
        }

        var indexes = new HashSet<(string, int)>();
        foreach (var rule in loot)
        {
            if (!keys.Contains(rule.TemplateKey) ||
                rule.LootIndex is < 0 or >= 32 ||
                rule.ItemId == 0 ||
                rule.ChanceBasisPoints is < 1 or > 10_000 ||
                rule.MinimumQuantity is < 1 or > 255 ||
                rule.MaximumQuantity < rule.MinimumQuantity ||
                rule.MaximumQuantity > 255 ||
                !indexes.Add((rule.TemplateKey, rule.LootIndex)))
            {
                throw new InvalidDataException(
                    $"Monster loot rule {rule.TemplateKey}/{rule.LootIndex} is invalid.");
            }
        }

        foreach (var table in tables)
        {
            var count = loot.Count(rule =>
                string.Equals(
                    rule.TemplateKey,
                    table.TemplateKey,
                    StringComparison.Ordinal));
            if (count < table.MaximumDrops)
            {
                throw new InvalidDataException(
                    $"Monster loot table {table.TemplateKey} cannot drop {table.MaximumDrops} items from {count} rules.");
            }
        }
    }
}

internal static class MonsterLootContentCatalog
{
    private static MonsterLootContentSnapshot? _current;

    public static MonsterLootContentSnapshot Current =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException(
            "The database-owned monster loot content has not been loaded.");

    public static void Install(MonsterLootContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
