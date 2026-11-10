using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// ATTR: checks for random attributes, quality and grade per drop.
internal static class LootRollProfileChecks
{
    public const string CheckName =
        "ATTR random drop attributes, quality and grade (roll profiles)";

    /// <summary>Set to a file path to write the distribution report there.</summary>
    private const string ReportVariable = "GODSWAR_ATTR_REPORT";

    private const int Drops = 10_000;

    // Lost Sword (1013) as item_allowed_attributes lists it on live: class ids
    // 200/201/210/211 included, AttackA..IgnorePhyPer, stat types as on live.
    private static readonly LootAttributeOption[] LostSword =
    [
        new(0, 0), new(40, 4), new(60, 6), new(80, 8), new(110, 11),
        new(130, 13), new(140, 14), new(150, 15), new(160, 16), new(180, 18),
        new(240, 19), new(200, 0), new(201, 4), new(210, 8), new(211, 6)
    ];

    // Red Star Ring (3209): two tiers of the same stat type (AttackA/AttackB).
    private static readonly LootAttributeOption[] RedStarRing =
    [
        new(0, 0), new(1, 0), new(20, 2), new(21, 2), new(60, 6), new(80, 8),
        new(90, 9), new(110, 11), new(130, 13), new(131, 13), new(180, 18),
        new(240, 19), new(250, 20)
    ];

    // Burning Cuirass (2113) lists AcceptCure four times.
    private static readonly LootAttributeOption[] Duplicates =
    [
        new(10, 1), new(170, 17), new(170, 17), new(170, 17), new(170, 17)
    ];

    private static readonly LootRollProfile Normal = new(
        "normal_lv120", 0, 0, 9_850, 135, 15, 1, 2, 5_000, 1, 2, 5_000);

    private static readonly LootRollProfile Elite = new(
        "elite_lv120", 0, 0, 9_700, 265, 35, 2, 3, 5_000, 2, 4, 5_000);

    private static readonly LootRollProfile Boss = new(
        "boss_lv120", 0, 0, 9_400, 500, 100, 3, 4, 5_000, 4, 5, 5_000);

    private static readonly LootRollProfile Spread = new(
        "spread", 2_000, 2_000, 2_000, 2_000, 2_000, 1, 6, 10_000, 1, 6, 10_000);

    public static Task RunAsync()
    {
        try
        {
            ServerRates.ResetToDefaults();
            CheckStockPathUnchanged();
            CheckFallbacks();
            CheckProfileRolls();
            CheckRates();
            CheckProfileValidation();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static Guid Death(int index) =>
        new(index, 11, 17, 3, 1, 4, 1, 5, 9, 2, 6);

    private static LootRollContent Content() => new(
        [Normal, Elite, Boss, Spread],
        new Dictionary<uint, IReadOnlyList<LootAttributeOption>>
        {
            [1013] = LostSword,
            [3209] = RedStarRing,
            [2113] = Duplicates
        });

    private static ItemGrantAttributes Fixed => new(
        3, 4, 130, 2, 80, null, null, null, null, null, null, null);

    private static (MonsterLootTableRule[] Tables, MonsterLootRule[] Rules) StockTable() =>
    (
        [new MonsterLootTableRule("ATTR_stock", 3)],
        [
            new MonsterLootRule("ATTR_stock", 0, 4001, 2_500, 1, 3),
            new MonsterLootRule("ATTR_stock", 1, 1013, 5_000, 1, 1, null, Fixed),
            new MonsterLootRule("ATTR_stock", 2, 3209, 4_000, 1, 1, true, ItemGrantAttributes.None),
            new MonsterLootRule("ATTR_stock", 3, 4002, 9_000, 2, 5, false, ItemGrantAttributes.None)
        ]
    );

    /// <summary>
    /// The stock RollLoot (reborn-src-v2), kept here as an oracle: a rule
    /// without a profile must produce exactly this, item for item.
    /// </summary>
    private static List<RolledMonsterLoot> StockRoll(
        MonsterLootTableRule table,
        MonsterLootRule[] rules,
        Guid deathEventId)
    {
        var rates = ServerRates.Current;
        var maximumDrops = Math.Min(
            rules.Length,
            rates.Multiply(ServerRateKeys.DropMaximumItems, table.MaximumDrops, 32));
        var rolled = new List<RolledMonsterLoot>(maximumDrops);
        Span<byte> input = stackalloc byte[20];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        deathEventId.TryWriteBytes(input[..16]);
        foreach (var rule in rules.OrderBy(static rule => rule.LootIndex))
        {
            if (rolled.Count >= maximumDrops)
            {
                break;
            }

            BitConverter.TryWriteBytes(input[16..], rule.LootIndex);
            SHA256.HashData(input, hash);
            if (BitConverter.ToUInt32(hash) % 10_000 >= rates.MultiplyBasisPoints(
                    ServerRateKeys.DropChance, rule.ChanceBasisPoints))
            {
                continue;
            }

            var range = rule.MaximumQuantity - rule.MinimumQuantity + 1;
            var quantity = rule.MinimumQuantity +
                (int)(BitConverter.ToUInt32(hash[4..]) % (uint)range);
            quantity = rates.Multiply(ServerRateKeys.DropQuantity, quantity, 255);
            rolled.Add(new(rule.LootIndex, rule.ItemId, quantity, rule.BoundOnPickup, rule.Attributes,
                rule.ChanceBasisPoints)); // F-166: the roll now carries its rule chance (overflow ranking)
        }

        return rolled;
    }

    private static void CheckStockPathUnchanged()
    {
        var (tables, rules) = StockTable();
        var plain = new MonsterLootContentSnapshot(tables, rules);
        var withContent = new MonsterLootContentSnapshot(tables, rules, Content());
        foreach (var rates in new (string, decimal)[][]
                 {
                     [],
                     [(ServerRateKeys.DropChance, 3m), (ServerRateKeys.DropQuantity, 2m),
                         (ServerRateKeys.DropMaximumItems, 2m)],
                     [(ServerRateKeys.DropRareAttributes, 5m), (ServerRateKeys.DropQualityUp, 3m),
                         (ServerRateKeys.DropGradeUp, 0m)]
                 })
        {
            Install(rates);
            var mismatches = 0;
            for (var index = 1; index <= 5_000; index++)
            {
                var death = Death(index);
                var expected = StockRoll(tables[0], rules, death);
                if (!expected.SequenceEqual(plain.RollLoot("ATTR_stock", death)) ||
                    !expected.SequenceEqual(withContent.RollLoot("ATTR_stock", death)))
                {
                    mismatches++;
                }
            }

            Check.Equal(0, mismatches,
                $"rules without a profile drop exactly what the stock roll drops ({rates.Length} rate overrides, 5,000 deaths)");
        }

        ServerRates.ResetToDefaults();
    }

    private static void CheckFallbacks()
    {
        var tables = new[] { new MonsterLootTableRule("ATTR_fallback", 3) };
        var rules = new[]
        {
            // unknown profile, item without a pool, profile on a material
            new MonsterLootRule("ATTR_fallback", 0, 1013, 10_000, 1, 1, null, Fixed, "missing_profile"),
            new MonsterLootRule("ATTR_fallback", 1, 1099, 10_000, 1, 1, null, Fixed, "boss_lv120"),
            new MonsterLootRule("ATTR_fallback", 2, 4001, 10_000, 1, 1, null, ItemGrantAttributes.None, "boss_lv120")
        };
        var snapshot = new MonsterLootContentSnapshot(tables, rules, Content());
        var drops = snapshot.RollLoot("ATTR_fallback", Death(1));
        Check.Equal(3, drops.Count, "fallback rules still drop");
        Check.Equal(Fixed, drops[0].Attributes, "an unknown profile keeps the rule's fixed columns");
        Check.Equal(Fixed, drops[1].Attributes, "an item without a pool keeps the rule's fixed columns");
        Check.Equal(ItemGrantAttributes.None, drops[2].Attributes, "a material named with a profile stays plain");

        var empty = new MonsterLootContentSnapshot(tables, rules);
        Check.Equal(Fixed, empty.RollLoot("ATTR_fallback", Death(1))[0].Attributes,
            "a snapshot without roll content keeps the fixed columns");
    }

    private static void CheckProfileRolls()
    {
        var report = new StringBuilder();
        report.AppendLine($"ATTR distribution: {Drops:N0} simulated drops per profile (rates at 1).");
        foreach (var (profile, item, pool) in new[]
                 {
                     (Normal, 1013u, LostSword),
                     (Elite, 1013u, LostSword),
                     (Boss, 1013u, LostSword),
                     (Boss, 3209u, RedStarRing),
                     (Spread, 1013u, LostSword)
                 })
        {
            var stats = Simulate(profile, item);
            var poolIds = pool.Select(static option => option.AttributeId).ToHashSet();
            var statType = pool.ToDictionary(static o => o.AttributeId, static o => o.StatType);
            Check.Equal(Drops, stats.Drops, $"{profile.ProfileKey}: every 100% rule drops");
            Check.Equal(0, stats.Bad.Count, $"{profile.ProfileKey}/{item}: no invalid drop ({string.Join("; ", stats.Bad.Take(3))})");
            Check.True(stats.All.All(attributes =>
                    attributes.Slots().Where(static slot => slot.Id.HasValue)
                        .All(slot => poolIds.Contains(slot.Id!.Value)) &&
                    attributes.Slots().Where(static slot => slot.Id.HasValue)
                        .Select(slot => statType[slot.Id!.Value]).Distinct().Count() ==
                        attributes.Slots().Count(static slot => slot.Id.HasValue)),
                $"{profile.ProfileKey}/{item}: ids come from the item's list, one per stat type");

            var total = profile.AttributeWeight0 + profile.AttributeWeight1 +
                profile.AttributeWeight2 + profile.AttributeWeight3 + profile.AttributeWeight4;
            int[] weights =
            [
                profile.AttributeWeight0, profile.AttributeWeight1, profile.AttributeWeight2,
                profile.AttributeWeight3, profile.AttributeWeight4
            ];
            for (var count = 0; count <= 4; count++)
            {
                var expected = 100.0 * weights[count] / total;
                var actual = 100.0 * stats.Counts[count] / Drops;
                Check.True(Math.Abs(expected - actual) <= 1.5,
                    $"{profile.ProfileKey}: {count} attributes {actual:F2}% is within 1.5 points of {expected:F2}%");
            }

            CheckLevels(profile.ProfileKey + " quality", stats.Quality,
                profile.QualityMinimum, profile.QualityMaximum, profile.QualityStepBasisPoints);
            CheckLevels(profile.ProfileKey + " grade", stats.Grade,
                profile.GradeMinimum, profile.GradeMaximum, profile.GradeStepBasisPoints);

            report.AppendLine(
                $"{profile.ProfileKey,-13} item {item}: attributes 0/1/2/3/4 = " +
                string.Join(" / ", stats.Counts.Select(static c => $"{100.0 * c / Drops:F2}%")) +
                $"; quality {Histogram(stats.Quality)}; grade {Histogram(stats.Grade)}");
        }

        // Determinism and the stat-type cap.
        var content = Content();
        Check.True(
            Enumerable.Range(1, 500).All(index =>
                content.TryRoll("boss_lv120", 1013, Death(index), 7, ServerRates.Current, out var first) &&
                content.TryRoll("boss_lv120", 1013, Death(index), 7, ServerRates.Current, out var second) &&
                first == second),
            "the same death and loot index roll the same item");
        var capped = Enumerable.Range(1, Drops)
            .Select(index =>
            {
                content.TryRoll("spread", 2113, Death(index), 0, ServerRates.Current, out var attributes);
                return attributes;
            })
            .ToArray();
        Check.True(capped.All(static a => a.Slots().Count(static s => s.Id.HasValue) <= 2),
            "an item with two stat types never gets more than two attributes");
        Check.True(capped.All(static a =>
                a.Slots().Where(static s => s.Id.HasValue).Select(static s => s.Id).Distinct().Count() ==
                a.Slots().Count(static s => s.Id.HasValue)),
            "a duplicated list entry is rolled at most once");

        // Exact roll vectors, so an independent implementation (the rig test's
        // Python roller) can be compared with this code.
        foreach (var key in new[] { "boss_lv120", "spread" })
        {
            for (var index = 1; index <= 8; index++)
            {
                content.TryRoll(key, 1013, Death(index), 3, ServerRates.Current, out var v);
                report.AppendLine(
                    $"VECTOR {Death(index):D} 3 {key} 1013 Q{v.Quality} G{v.Grade} " +
                    string.Join(",", v.Slots().Select(static s => s.Id?.ToString() ?? "-")));
            }
        }

        var path = Environment.GetEnvironmentVariable(ReportVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(path, report.ToString());
        }
    }

    /// <summary>Level minimum+k should come up with weight step^k.</summary>
    private static void CheckLevels(string name, Dictionary<int, int> seen, int minimum, int maximum, int stepBasisPoints)
    {
        var weights = Enumerable.Range(0, maximum - minimum + 1)
            .Select(k => Math.Pow(stepBasisPoints / 10_000.0, k))
            .ToArray();
        var total = weights.Sum();
        for (var k = 0; k < weights.Length; k++)
        {
            var expected = 100.0 * weights[k] / total;
            var actual = 100.0 * seen.GetValueOrDefault(minimum + k) / Drops;
            Check.True(Math.Abs(expected - actual) <= 1.5,
                $"{name} {minimum + k}: {actual:F2}% is within 1.5 points of {expected:F2}%");
        }
    }

    private static void CheckRates()
    {
        Install([(ServerRateKeys.DropRareAttributes, 0m)]);
        var none = Simulate(Boss, 1013);
        Check.Equal(0, none.Counts[3] + none.Counts[4], "drop.attr_rare = 0 removes 3 and 4 attribute rolls");

        ServerRates.ResetToDefaults();
        var stock = Simulate(Boss, 1013);
        Install([(ServerRateKeys.DropRareAttributes, 2m)]);
        var doubled = Simulate(Boss, 1013);
        Check.True(doubled.Counts[3] + doubled.Counts[4] > 1.5 * (stock.Counts[3] + stock.Counts[4]),
            "drop.attr_rare = 2 makes 3-4 attributes about twice as common");

        Install([(ServerRateKeys.DropQualityUp, 0m), (ServerRateKeys.DropGradeUp, 0m)]);
        var floor = Simulate(Boss, 1013);
        Check.True(floor.Quality.Keys.All(static q => q == 3) && floor.Grade.Keys.All(static g => g == 4),
            "quality/grade step 0 always gives the profile minimum");

        Install([(ServerRateKeys.DropQualityUp, 10m), (ServerRateKeys.DropGradeUp, 10m)]);
        var ceiling = Simulate(Boss, 1013);
        Check.True(ceiling.Quality.Keys.All(static q => q is >= 3 and <= 4) &&
                ceiling.Grade.Keys.All(static g => g is >= 4 and <= 5) &&
                ceiling.Quality.GetValueOrDefault(4) > ceiling.Quality.GetValueOrDefault(3),
            "a higher step shifts toward the top but never past the profile range");
        ServerRates.ResetToDefaults();
    }

    private static void CheckProfileValidation()
    {
        Check.True(!(Normal with { QualityMaximum = 11 }).IsValid, "quality above 10 is rejected");
        Check.True(!(Normal with { GradeMaximum = 13 }).IsValid, "grade above 12 is rejected");
        Check.True(!(Normal with { GradeMinimum = 3, GradeMaximum = 2 }).IsValid, "an empty grade range is rejected");
        Check.True(!(Normal with { AttributeWeight2 = 0, AttributeWeight3 = 0, AttributeWeight4 = 0 }).IsValid,
            "all-zero weights are rejected");
        Check.Throws<InvalidDataException>(
            () => _ = new LootRollContent(
                [Normal with { QualityMinimum = 0 }],
                new Dictionary<uint, IReadOnlyList<LootAttributeOption>>()),
            "the content refuses an invalid profile");
        Check.True(!LootRollContent.IsSlotAttribute(200) && !LootRollContent.IsSlotAttribute(231) &&
                !LootRollContent.IsSlotAttribute(480) && !LootRollContent.IsSlotAttribute(500) &&
                LootRollContent.IsSlotAttribute(0) && LootRollContent.IsSlotAttribute(240),
            "class and elemental ids are never slot attributes");
    }

    private sealed record Stats(
        int Drops,
        int[] Counts,
        Dictionary<int, int> Quality,
        Dictionary<int, int> Grade,
        List<string> Bad,
        List<ItemGrantAttributes> All);

    private static Stats Simulate(LootRollProfile profile, uint item)
    {
        var tables = new[] { new MonsterLootTableRule("ATTR_sim", 1) };
        var rules = new[]
        {
            new MonsterLootRule("ATTR_sim", 5, item, 10_000, 1, 1, null, ItemGrantAttributes.None, profile.ProfileKey)
        };
        var snapshot = new MonsterLootContentSnapshot(tables, rules, Content());
        var stats = new Stats(0, new int[5], [], [], [], []);
        var drops = 0;
        for (var index = 1; index <= Drops; index++)
        {
            foreach (var drop in snapshot.RollLoot("ATTR_sim", Death(index)))
            {
                drops++;
                var a = drop.Attributes;
                var ids = a.Slots().Where(static s => s.Id.HasValue).Select(static s => s.Id!.Value).ToArray();
                stats.Counts[ids.Length]++;
                stats.Quality[a.Quality] = stats.Quality.GetValueOrDefault(a.Quality) + 1;
                stats.Grade[a.Grade] = stats.Grade.GetValueOrDefault(a.Grade) + 1;
                stats.All.Add(a);
                if (a.Quality < profile.QualityMinimum || a.Quality > profile.QualityMaximum ||
                    a.Grade < profile.GradeMinimum || a.Grade > profile.GradeMaximum ||
                    a.Attribute5.HasValue || ids.Length > 4 ||
                    a.AttributeLevel1.HasValue || a.AttributeLevel2.HasValue ||
                    a.AttributeLevel3.HasValue || a.AttributeLevel4.HasValue ||
                    ids.Any(static id => !LootRollContent.IsSlotAttribute(id)) ||
                    ids.Distinct().Count() != ids.Length ||
                    // slots fill from 1 with no gaps
                    (a.Attribute2.HasValue && !a.Attribute1.HasValue) ||
                    (a.Attribute3.HasValue && !a.Attribute2.HasValue) ||
                    (a.Attribute4.HasValue && !a.Attribute3.HasValue))
                {
                    stats.Bad.Add(a.ToString());
                }
            }
        }

        return stats with { Drops = drops };
    }

    private static string Histogram(Dictionary<int, int> values) =>
        string.Join(" ", values.OrderBy(static p => p.Key)
            .Select(static p => $"{p.Key}:{100.0 * p.Value / Drops:F1}%"));

    private static void Install((string Key, decimal Value)[] values) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            values.Select(static value => KeyValuePair.Create(value.Key, value.Value)),
            1,
            DateTimeOffset.UtcNow,
            "check",
            "test"));
}
