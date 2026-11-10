using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// ROUND3 PROF soul stones (2026-11-09, the admin F-129 "Wait for professions"). The Mount Feeder upgrade (F-52) eats
// Soul Stones 14201-14208. The client's own Smithing blueprints make them (20401 ... 22501), their recipe items
// (13209 ... 13263) are on the Forging Recipe Vendor's captured shelf, and every material comes from another
// Smithing blueprint whose recipe is on that shelf, from the Ingredients Vendor, or from gathering (PR #101).
// This check walks the whole chain so a catalog or shop change that cuts it fails here.
internal static class SoulStoneSourceChecks
{
    public const string CheckName = "PROF soul stones: Smithing makes every Mount Feeder stone from obtainable inputs";

    // Gathered on Romance Island (PROF slice 2, PR #101): Copper Ore is the only raw input of the chain.
    private static readonly HashSet<uint> Gathered = [12010];

    public static Task RunAsync()
    {
        var balances = new CapitalNpcShopBalances(Silver: 1, Gold: 1, BindingGold: 1);
        HashSet<uint> Shelf(string key, uint npc, CapitalNpcServiceKind expected)
        {
            Check.True(CapitalNpcServiceProtocol.TryResolve(key, npc, out var kind) && kind == expected &&
                CapitalNpcServiceProtocol.IsShop(kind), $"{key}/{npc} is the {expected} shop");
            return [.. CapitalNpcServiceProtocolChecks
                .ReadCatalogFrames(PacketBuilder.CapitalNpcShopCatalog(npc, balances, kind))
                .SelectMany(CapitalNpcServiceProtocolChecks.ReadItemIds)];
        }

        var forging = Shelf("Athens_135", 5273, CapitalNpcServiceKind.AthensForgingRecipeVendor);
        var ingredients = Shelf("Athens_122", 5260, CapitalNpcServiceKind.AthensIngredientsVendor);
        // Sparta's vendors sell the same shelves (GENERATED; Sparta's were never captured).
        Check.True(Shelf("Sparta_135", 5132, CapitalNpcServiceKind.AthensForgingRecipeVendor).SetEquals(forging) &&
            Shelf("Sparta_122", 5119, CapitalNpcServiceKind.AthensIngredientsVendor).SetEquals(ingredients),
            "Sparta's Forging Recipe and Ingredients vendors sell the Athens shelves");

        bool Craftable(ProfessionBlueprint blueprint) =>
            blueprint.Type == 0 && blueprint.Profession == 2 &&
            (forging.Contains(blueprint.RecipeItemId) ||
             blueprint.Id == ProfessionTables.StarterBlueprint(blueprint.Profession));

        bool Obtainable(uint item, int depth) =>
            depth < 8 && (Gathered.Contains(item) || ingredients.Contains(item) || forging.Contains(item) ||
                ProfessionBlueprintCatalog.Producing(item).Any(blueprint => Craftable(blueprint) &&
                    blueprint.Materials.All(material => Obtainable(material.ItemId, depth + 1))));

        for (var level = 50; level <= 120; level += 10)
        {
            var stone = MountFamilyCatalog.SoulStoneFor(level);
            Check.Equal(14201u + (uint)((level - 50) / 10), stone, $"Soul Stone (Lvl {level}) is item {stone}");
            var makers = ProfessionBlueprintCatalog.Producing(stone).ToArray();
            Check.True(makers.Length == 1 && makers[0].ProductCount == 1 && Craftable(makers[0]),
                $"Soul Stone {stone}: one Type-0 Smithing blueprint whose recipe item the Forging vendor sells");
            Check.True(makers[0].Materials.All(material => Obtainable(material.ItemId, 0)),
                $"Soul Stone {stone}: every material of blueprint {makers[0].Id} is gathered, sold or craftable");
        }

        Check.Equal(20401, ProfessionBlueprintCatalog.Producing(14201).Single().Id, "Soul Stone (Lvl 50) is blueprint 20401");
        Check.Equal(13209u, ProfessionBlueprintCatalog.Producing(14201).Single().RecipeItemId,
            "taught by Recipe: Soul Stone (Level 50) 13209");
        return Task.CompletedTask;
    }
}
