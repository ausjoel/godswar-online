using Godswar.Server.Application.World.Content;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

// PROF: q07 event drop windows, q09 profession blueprints, q06 Sigil Stone fixed damage.
internal static class ProfessionEventDropChecks
{
    public const string EventDropCheckName = "PROF q07 dated event drop windows gate loot rolls";
    public const string BlueprintCheckName = "PROF q09 profession blueprints match the client data";
    public const string SigilCheckName = "PROF q06 Sigil Stone fixed damage is opt-in";

    private const uint Holiday = 3800;

    private static readonly DateTimeOffset Christmas = new(2026, 12, 25, 12, 0, 0, TimeSpan.Zero);

    private static Guid Death(int index) => new(index, 7, 3, 1, 2, 3, 4, 5, 6, 7, 8);

    private static MonsterLootContentSnapshot Table() => new(
        [new MonsterLootTableRule("PROF_event", 3), new MonsterLootTableRule("PROF_event@15", 3)],
        [
            new MonsterLootRule("PROF_event", 0, 4001, 5_000, 1, 2),
            new MonsterLootRule("PROF_event", 1, Holiday, 10_000, 1, 1),
            new MonsterLootRule("PROF_event", 2, 4002, 5_000, 1, 1),
            new MonsterLootRule("PROF_event@15", 0, 4001, 5_000, 1, 2),
            new MonsterLootRule("PROF_event@15", 1, Holiday, 10_000, 1, 1),
            new MonsterLootRule("PROF_event@15", 2, 4002, 5_000, 1, 1)
        ]);

    private static EventDropWindow Window(bool enabled, string? table = null, bool yearly = false,
        int startYear = 2026) => new(
        "christmas", new(startYear, 12, 20, 0, 0, 0, TimeSpan.Zero), new(startYear + 1, 1, 5, 0, 0, 0, TimeSpan.Zero),
        enabled, yearly, [Holiday], table);

    private static int Count(MonsterLootContentSnapshot table, string key, DateTimeOffset at, uint item) =>
        Enumerable.Range(1, 400).Sum(i => table.RollLoot(key, Death(i), at).Count(drop => drop.ItemId == item));

    public static Task RunEventDropsAsync()
    {
        try
        {
            var table = Table();
            var stock = Enumerable.Range(1, 400).Select(i => table.RollLoot("PROF_event", Death(i), Christmas)).ToArray();
            Check.Equal(400, Count(table, "PROF_event", Christmas, Holiday), "no windows: the 100% rule always drops");

            EventDropWindowCatalog.Install(new([Window(enabled: true)]));
            var open = Enumerable.Range(1, 400).Select(i => table.RollLoot("PROF_event", Death(i), Christmas)).ToArray();
            Check.True(stock.Zip(open).All(static p => p.First.SequenceEqual(p.Second)), "open window = stock rolls, item for item");
            Check.Equal(0, Count(table, "PROF_event", Christmas.AddDays(30), Holiday), "closed window: the event item never drops");
            Check.Equal(0, Count(table, "PROF_event", new(2026, 12, 19, 23, 59, 59, TimeSpan.Zero), Holiday), "one second before the start");
            Check.Equal(400, Count(table, "PROF_event", new(2026, 12, 20, 0, 0, 0, TimeSpan.Zero), Holiday), "start is inclusive");
            Check.Equal(0, Count(table, "PROF_event", new(2027, 1, 5, 0, 0, 0, TimeSpan.Zero), Holiday), "end is exclusive");
            Check.Equal(Count(table, "PROF_event", Christmas, 4001) - 0, Count(table, "PROF_event", Christmas.AddDays(30), 4001),
                "other rules roll the same whether or not the window is open");
            Check.Equal(400, Count(table, "PROF_event@15", Christmas, Holiday), "an all-tables window covers area tables");

            EventDropWindowCatalog.Install(new([Window(enabled: false)]));
            Check.Equal(0, Count(table, "PROF_event", Christmas, Holiday), "a disabled window switches the item off");

            EventDropWindowCatalog.Install(new([Window(enabled: true, table: "OTHER_table")]));
            Check.Equal(400, Count(table, "PROF_event", Christmas.AddDays(30), Holiday), "a window for another table leaves this one alone");

            EventDropWindowCatalog.Install(new([Window(enabled: true, table: "PROF_event")]));
            Check.Equal(0, Count(table, "PROF_event@15", Christmas.AddDays(30), Holiday), "a template window also gates its @map table");
            Check.Equal(400, Count(table, "PROF_event@15", Christmas, Holiday), "and opens it inside the dates");

            EventDropWindowCatalog.Install(new([Window(enabled: true, yearly: true, startYear: 2025)]));
            Check.Equal(400, Count(table, "PROF_event", new(2028, 1, 2, 0, 0, 0, TimeSpan.Zero), Holiday), "yearly: next year's copy across New Year");
            Check.Equal(0, Count(table, "PROF_event", new(2028, 6, 1, 0, 0, 0, TimeSpan.Zero), Holiday), "yearly: closed mid-year");
            Check.Equal(0, Count(table, "PROF_event", new(2025, 1, 2, 0, 0, 0, TimeSpan.Zero), Holiday), "yearly: never before the first start");

            EventDropWindowCatalog.Install(new([Window(enabled: false), Window(enabled: true) with { Name = "second" }]));
            Check.Equal(400, Count(table, "PROF_event", Christmas, Holiday), "any open window listing the item lets it drop");
        }
        finally
        {
            EventDropWindowCatalog.Install(EventDropWindowSnapshot.Empty);
        }

        return Task.CompletedTask;
    }

    public static Task RunBlueprintsAsync()
    {
        var all = ProfessionBlueprintCatalog.All;
        Check.Equal(332, all.Count, "blueprints shipped in Lifing_Blueprint.xml");
        Check.Equal(ProfessionBlueprintCatalog.ShippedBlueprintCount, all.Select(static b => b.Id).Distinct().Count(), "unique ids");
        Check.Equal(80, all.Count(static b => b.Profession == 1), "Alchemy blueprints");
        Check.Equal(77, all.Count(static b => b.Profession == 2), "Smithing blueprints");
        Check.Equal(81, all.Count(static b => b.Profession == 3), "Artificer blueprints");
        Check.Equal(94, all.Count(static b => b.Profession == 4), "Scholar blueprints");
        Check.Equal(4, all.Count(static b => b.RecipeItemId == 0), "x0000 starter blueprints have no recipe item");

        // The live item_templates check is /craft validate on the rig (and recipe-validation.txt); the profession
        // templates come from migrations, not from one static seed list, so here only the validator itself is checked.
        Check.Equal(0, ProfessionBlueprintCatalog.Validate(static _ => true).Count, "a full template set finds no problem");
        var withoutPotion = ProfessionBlueprintCatalog.Validate(static id => id != 4002);
        Check.Equal(ProfessionBlueprintCatalog.Producing(4002).Count(), withoutPotion.Count, "a missing product is reported once per blueprint");
        Check.True(withoutPotion.All(static p => p.EndsWith("product 4002", StringComparison.Ordinal)), "as the product");
        Check.True(ProfessionBlueprintCatalog.Validate(static id => id != 12030).Count ==
            ProfessionBlueprintCatalog.All.Count(static b => b.Materials.Any(static m => m.ItemId == 12030)),
            "a missing material (Herb) is reported for every blueprint that uses it");

        Check.True(ProfessionBlueprintCatalog.TryGet(10000, out var potion), "blueprint 10000 exists");
        Check.True(potion.ProductId == 4002 && potion.ProductCount == 5 && potion.OddsPercent == 95 &&
            potion.Materials.SequenceEqual([new ProfessionMaterial(12030, 12), new ProfessionMaterial(12830, 1)]),
            "10000 = 12 Herb + 1 Wooden Mortar -> 5 Big Healing Potion at 95%");
        var bag = new Dictionary<uint, int> { [12030] = 11, [12830] = 3 };
        var missing = ProfessionBlueprintCatalog.Missing(potion, bag);
        Check.True(missing.Count == 1 && missing[0] == new ProfessionMaterial(12030, 1), "one Herb short");
        bag[12030] = 12;
        Check.Equal(0, ProfessionBlueprintCatalog.Missing(potion, bag).Count, "exactly enough");
        Check.True(ProfessionBlueprintCatalog.RollSucceeds(potion, 94) && !ProfessionBlueprintCatalog.RollSucceeds(potion, 95),
            "odds are a 0..99 roll below Odds");
        Check.True(ProfessionBlueprintCatalog.TryGet(11000, out var fireArrow) && fireArrow.ProductId == 9120,
            "blueprint 11000 makes Sigil Stone 9120 (Fire Arrow)");
        Check.Equal(130, ProfessionBlueprintCatalog.CharacterLevelFor(30), "Lifing_Skill PlayLv for profession level 30");
        return Task.CompletedTask;
    }

    public static Task RunSigilAsync()
    {
        var wasEnabled = SigilFixedDamage.Enabled;
        try
        {
            // The shape SigilStonePolicy builds for 9120 (skill 4877, Power1 -1, Power2 = the item text's 1800).
            var stone = new PlayerCombatSkillSnapshot(4877, 44, 28, 10f, 0f, 0, 1, -1m, 1_800m);
            // The same skill as Magic.ini publishes it (Power1 0): a direct cast must not become fixed damage.
            var direct = stone with { Power1 = 0m };
            var miss = new CombatResolution(1, 9, 0, CombatDamageChannel.Magic, CombatHitOutcome.Miss, 0,
                new(5_000, 9_000, 0, CombatRollEvidence.NotRolled), default);

            SigilFixedDamage.Enabled = false;
            Check.True(!SigilFixedDamage.TryGet(stone, out _), "off by default: stones keep the normal formula");

            SigilFixedDamage.Enabled = true;
            Check.True(SigilFixedDamage.TryGet(stone, out var damage) && damage == 1_800, "on: 9120 deals exactly 1800");
            Check.True(!SigilFixedDamage.TryGet(direct, out _), "a Magic.ini-shaped cast of 4877 is not fixed");
            Check.True(!SigilFixedDamage.TryGet(stone with { SkillId = 4000 }, out _), "other skills are never fixed");
            var forced = SigilFixedDamage.Apply(miss, damage);
            Check.True(forced.Hit && !forced.IsCritical && forced.Damage == 1_800 && forced.CapturedDamageValue == 1_800 &&
                forced.EventId == miss.EventId, "a formula miss becomes a normal 1800 hit");
            var area = new PlayerCombatSkillSnapshot(4888, 16, 28, 10f, 10f, 0, 1, -1m, 14_510m);
            Check.True(SigilFixedDamage.TryGet(area, out var thor) && thor == 14_510, "area stone 9144 Hammer of Thor: 14510");
        }
        finally
        {
            SigilFixedDamage.Enabled = wasEnabled;
        }

        return Task.CompletedTask;
    }
}
