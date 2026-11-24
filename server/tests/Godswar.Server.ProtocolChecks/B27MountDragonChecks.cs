using System.Reflection;
using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// B27 (batch 2026-11-22, agent B27-M): Mount Feeder "Upgrading Mount Gear" (the admin d138) and the Mid-autumn Gift Pack
// Giver's Blue Crystal Dragon pieces (the admin d139).
internal static class B27MountDragonChecks
{
    public const string GearCheckName = "B27: Mount Feeder gear upgrade ladder (one grade, level +10, top grade stops)";
    public const string DragonCheckName = "B27: Mid-autumn Gift Pack gives one dragon piece; the Giver's pages lead to the exchanges";

    private static readonly int[] GearLines = [145, 146, 147, 148, 149];

    public static Task RunGearAsync()
    {
        var seeds = ItemTemplateSeeds.All.Concat(ClientCatalogItemSeeds.All).GroupBy(static s => s.Id).ToDictionary(static g => g.Key, static g => g.First());
        var walked = 0;
        foreach (var line in GearLines)
        {
            for (var grade = 0; grade <= 8; grade++)
            {
                var id = line * 100 + grade;
                var next = GameClientHandler.MountGearNextGrade(id);
                if (grade == 8)
                {
                    Check.Equal(0, next, $"{id} is the top grade (answers 991)");
                    continue;
                }

                Check.Equal(id + 1, next, $"{id} upgrades to {id + 1}");
                Check.True(seeds.ContainsKey(id) && seeds.ContainsKey(next), $"{id} and {next} are both item templates");
                var (from, to) = (seeds[id], seeds[next]);
                Check.Equal(from.Kind, to.Kind, $"{id} -> {next} stays the same gear slot");
                Check.Equal((from.MinLevel ?? -100) + 10, to.MinLevel ?? -1, $"{id} -> {next} raises the level requirement by 10");

                // The attribute rule (MountGearAttributeSql): every attribute the old grade allows has an allowed tier
                // of the same kind at or above it on the new grade, so no attribute is ever lowered or dropped.
                var before = Allowed(from);
                var after = Allowed(to);
                foreach (var attribute in before)
                {
                    var target = after.Where(a => a / 10 == attribute / 10 && a >= attribute).DefaultIfEmpty(-1).Min();
                    Check.True(target >= 0, $"{id} attribute {attribute} has a place on {next}");
                }

                walked++;
            }
        }

        Check.Equal(40, walked, "40 grade steps (5 lines x 8)");
        foreach (var other in new[] { 14510, 14408, 4259, 14499, 14509, 14909, 1000 })
            Check.Equal(0, GameClientHandler.MountGearNextGrade(other), $"{other} is not upgradable mount gear");

        var feeder = Dialogue("MountFeederDialogue");
        Check.True(feeder.TryGetValue(105, out var form) && form.SequenceEqual([205]),
            "Mount Feeder 105 -> 205 (the two-slot upgrade form)");
        Check.True(seeds.TryGetValue(4259, out var gem) && gem.DisplayName == "Golden Gem", "4259 is the Golden Gem");
        return Task.CompletedTask;
    }

    private static HashSet<int> Allowed(ItemTemplateSeed seed)
    {
        using var stats = JsonDocument.Parse(seed.StatsJson);
        return stats.RootElement.TryGetProperty("MainAttribute", out var list)
            ? list.GetString()!.Split(',').Select(int.Parse).ToHashSet()
            : [];
    }

    public static Task RunDragonAsync()
    {
        var pieces = GameClientHandler.BlueCrystalDragonPieces;
        Check.True(pieces.SequenceEqual(Enumerable.Range(14014, 8)), "the 8 pieces are 14014-14021");
        var seeds = ItemTemplateSeeds.All.Concat(ClientCatalogItemSeeds.All).GroupBy(static s => s.Id).ToDictionary(static g => g.Key, static g => g.First());
        foreach (var piece in pieces)
            Check.True(seeds.TryGetValue(piece, out var seed) && seed.DisplayName.StartsWith("Dragon piece - "),
                $"{piece} is a Dragon piece template");
        Check.Equal(11061, GameClientHandler.BlueCrystalDragonJade, "the set makes Magic Jade: Blue Crystal Dragon");
        Check.Equal(2_000L, GameClientHandler.DragonPieceExperiencePerLevel, "1 piece = 2,000 EXP x level before exp.events (GENERATED)");

        Check.True(GiftPackPolicy.TryGetDefinitionForChecks(14012, out var pack), "14012 is a gift pack");
        Check.True(pack.Rewards.Select(static r => (r.ItemId, r.Quantity)).SequenceEqual([(3920u, 5), (4231u, 5)]),
            "14012 keeps its made-up fixed contents");
        var picks = pack.Picks ?? [];
        Check.Equal(2, picks.Length, "14012 has two picks: the old crystal/jade pick and the dragon piece");
        Check.True(picks[1].Count == 1 && picks[1].Options.Select(static o => (int)o.ItemId).SequenceEqual(pieces) &&
                   picks[1].Options.All(static o => o.Quantity == 1 && o.Weight == 1 && o.ClassMask == 15),
            "the piece pick: 1 of the 8, equal chances, every class");

        var seen = new HashSet<uint>();
        for (var n = 0; n < 4000; n++)
        {
            var rolled = GiftPackPolicy.RollRewards(pack, n % 4).ToList();
            Check.Equal(1, rolled.Count(r => pieces.Contains((int)r.ItemId)), "every open gives exactly one piece");
            Check.True(rolled.All(static r => r.Quantity is >= 1 and <= 99), "no reward stack above 99");
            seen.UnionWith(rolled.Select(static r => r.ItemId).Where(i => pieces.Contains((int)i)));
        }

        Check.Equal(8, seen.Count, "all 8 pieces can come out of the pack");

        var giver = Dialogue("SummerGiftEnvoyDialogue");
        Check.True(giver.TryGetValue(101, out var jade) && jade.SequenceEqual([201]), "Giver 101 -> 201 (Exchange for Magic Jade)");
        Check.True(giver.TryGetValue(102, out var one) && one.SequenceEqual([202]), "Giver 102 -> 202 (the piece slot)");
        return Task.CompletedTask;
    }

    private static IReadOnlyDictionary<int, int[]> Dialogue(string field)
    {
        var value = typeof(GameClientHandler).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return (IReadOnlyDictionary<int, int[]>)value.GetType().GetProperty("Steps")!.GetValue(value)!;
    }
}
