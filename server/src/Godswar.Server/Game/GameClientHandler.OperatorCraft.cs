using System.Globalization;
using Godswar.Server.Application.Operators;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// PROF (q09): /craft, a GM test of the profession recipes. Players craft through the client's Manufacture window
// since PROF slice 1 (GameClientHandler.Professions.cs); this command stays a GM shortcut that runs the recipe
// checks on the server. Only registered when GODSWAR_PROFESSIONS=1 (otherwise "/craft" is ordinary chat), and only for accounts
// the operator allowlist accepts. /craft does NOT check profession level, Activity Points or the crafting square
// (it is a GM shortcut that skips them).
internal sealed partial class GameClientHandler
{
    private const string CraftUsage =
        "/craft check <blueprint> [@name] | /craft make <blueprint> [@name]\n" +
        "/craft find <product item id> | /craft validate   (recipes: Development/professions/recipes.csv)";

    private async Task<string> OperatorCraftAsync(string[] args, CancellationToken cancellationToken)
    {
        var target = TakeTarget(ref args);
        if (args.Length < 2)
        {
            return CraftUsage;
        }

        var templates = RequireItemContent().Templates;
        switch (args[1].ToLowerInvariant())
        {
            case "validate":
            {
                var problems = ProfessionBlueprintCatalog.Validate(id => templates.TryGet(id, out _));
                return $"{ProfessionBlueprintCatalog.All.Count} blueprints, {problems.Count} unknown material/product ids" +
                    (problems.Count == 0 ? string.Empty : "\n" + string.Join("\n", problems.Take(8)));
            }
            case "find" when args.Length == 3 &&
                uint.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var productId):
            {
                var found = ProfessionBlueprintCatalog.Producing(productId).Select(static b => b.Id).ToArray();
                return found.Length == 0
                    ? $"no blueprint makes item {productId}"
                    : $"item {productId} is made by blueprint {string.Join(", ", found)}";
            }
            case "check" or "make" when args.Length == 3 &&
                int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out _):
                break;
            default:
                return CraftUsage;
        }

        if (!ProfessionBlueprintCatalog.TryGet(int.Parse(args[2], CultureInfo.InvariantCulture), out var blueprint))
        {
            return $"no blueprint {args[2]}";
        }

        var resolved = await ResolveItemTargetAsync(target, cancellationToken);
        if (resolved is null)
        {
            return $"no character named {target}";
        }

        string Name(uint id) => templates.TryGet(id, out var t) ? t.DisplayName : "unknown item";
        var bag = await RequireOperatorStore().ReadBagAsync(resolved.Value.CharacterId, cancellationToken);
        var counts = bag.Where(static item => item.SealedPetId is null)
            .GroupBy(static item => checked((uint)item.ItemId))
            .ToDictionary(static g => g.Key, static g => g.Sum(static item => Math.Max(1, (int)item.Stack)));
        var missing = ProfessionBlueprintCatalog.Missing(blueprint, counts);
        var header =
            $"blueprint {blueprint.Id} {ProfessionBlueprintCatalog.ProfessionNames[blueprint.Profession]}: " +
            $"{blueprint.ProductCount} x {Name(blueprint.ProductId)} ({blueprint.ProductId}), odds {blueprint.OddsPercent}%";
        if (args[1].Equals("check", StringComparison.OrdinalIgnoreCase))
        {
            var lines = new List<string> { header };
            lines.AddRange(blueprint.Materials.Select(m =>
                $"  {m.Count} x {Name(m.ItemId)} ({m.ItemId}): have {(counts.TryGetValue(m.ItemId, out var c) ? c : 0)}"));
            lines.Add(
                $"  recipe lv {blueprint.RecipeLevel} (char lv {ProfessionBlueprintCatalog.CharacterLevelFor(blueprint.RecipeLevel)} in Lifing_Skill), " +
                $"AP {blueprint.ActivityPoints}, recipe item {(blueprint.RecipeItemId == 0 ? "none" : blueprint.RecipeItemId)} (not enforced)");
            lines.Add(missing.Count == 0 ? "  ready to make" :
                "  missing " + string.Join(", ", missing.Select(m => $"{m.Count} x {Name(m.ItemId)}")));
            return string.Join("\n", lines);
        }

        if (missing.Count > 0)
        {
            return header + "\nnot made: missing " + string.Join(", ", missing.Select(m => $"{m.Count} x {Name(m.ItemId)}"));
        }

        if (!templates.TryGet(blueprint.ProductId, out var product))
        {
            return $"product {blueprint.ProductId} does not exist on this server";
        }

        // Retail behaviour on a failed roll is unknown (the client only says "Manufacturing failed!"), so a
        // failure keeps the materials rather than destroying player items on a guess.
        var roll = Random.Shared.Next(100);
        var success = ProfessionBlueprintCatalog.RollSucceeds(blueprint, roll);
        var result = await RequireOperatorStore().CraftAsync(
            resolved.Value.CharacterId,
            blueprint.Materials.Select(static m => (m.ItemId, m.Count)).ToArray(),
            blueprint.ProductId,
            success ? blueprint.ProductCount : 0,
            ReadItemStackCap(product.StatsJson),
            0,
            cancellationToken);
        var refresh = success && result.Outcome == OperatorItemOutcome.Done
            ? await RefreshAfterOperatorItemChangeAsync(resolved.Value, "craft", cancellationToken)
            : string.Empty;
        await LogOperatorAsync(
            "craft",
            resolved.Value.Name,
            $"blueprint={blueprint.Id} product={blueprint.ProductId}x{blueprint.ProductCount} roll={roll}/{blueprint.OddsPercent} " +
            $"success={success} outcome={result.Outcome} {result.Detail} refresh={refresh}",
            cancellationToken);
        if (result.Outcome != OperatorItemOutcome.Done)
        {
            return header + "\nnot made: " + result.Detail;
        }

        return success
            ? $"{header}\nmade (roll {roll} < {blueprint.OddsPercent}): {result.Detail}"
            : $"{header}\nfailed (roll {roll} >= {blueprint.OddsPercent}); materials kept";
    }
}
