using System.Text.Json;

namespace Godswar.Server.Application.Items;

/// <summary>
/// Pins the guild registration token (Guild Stone) as a developer grant so the
/// consortia creation flow can be exercised without a live economy. The
/// template must match the published stock-client revision exactly; divergent
/// content fails fast rather than granting an unverified item.
/// </summary>
internal sealed partial class PinnedDeveloperItemGrantCatalog
{
    private const uint GuildStoneGrantItemId = 4100;

    internal static bool IsGuildStoneDeveloperGrant(uint itemId) =>
        itemId == GuildStoneGrantItemId;

    private static IReadOnlyList<DeveloperGrantMaterialDefinition>
        CreateGuildStoneGrants(IItemTemplateCatalog templates)
    {
        const uint itemId = GuildStoneGrantItemId;
        if (!templates.TryGet(itemId, out var template))
        {
            return [];
        }

        if (!template.Kind.Equals("consume item", StringComparison.Ordinal) ||
            !template.NameKey.Equals("GuildStone", StringComparison.Ordinal) ||
            !template.DisplayName.Equals(
                "Guild Stone",
                StringComparison.Ordinal) ||
            template.EquipmentSlot != -1 ||
            template.ClassIds.Count != 0 ||
            template.MinLevel.HasValue ||
            template.MaxLevel.HasValue ||
            template.Hand.HasValue ||
            template.SkillFlag.HasValue ||
            !template.Texture.Equals(
                "./Localization/en_us/UI/Texture/Icon.gwo",
                StringComparison.Ordinal) ||
            !template.Icon.Equals("900,252", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Guild Stone does not match stock-client content.");
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var root = document.RootElement;
        if (!HasString(root, "ID", "4100") ||
            !HasString(root, "Type", "consume item") ||
            !HasString(
                root,
                "Texture",
                "./Localization/en_us/UI/Texture/Icon.gwo") ||
            !HasString(root, "Icon", "900,252") ||
            !HasString(root, "Random", "0") ||
            !HasString(root, "Distribution", "0,0") ||
            !HasString(root, "Money", "0") ||
            !HasString(root, "Overlap", "1") ||
            !HasString(root, "BindType", "1") ||
            root.EnumerateObject().Count() != 9)
        {
            throw new InvalidOperationException(
                "Guild Stone has invalid stock-client activation metadata.");
        }

        return
        [
            new DeveloperGrantMaterialDefinition(
                itemId,
                template.DisplayName,
                StackCap: 1,
                GrantedBound: 1)
        ];
    }

    private static Dictionary<string, DeveloperGrantMaterialDefinition>
        CreateGuildStoneAliases(
            IReadOnlyList<DeveloperGrantMaterialDefinition> guildStones)
    {
        var aliases = new Dictionary<string, DeveloperGrantMaterialDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var stone in guildStones)
        {
            AddAlias(aliases, "guildstone", stone);
            AddAlias(aliases, $"guildstone{stone.ItemId}", stone);
            AddAlias(aliases, "天堂之令", stone);
            AddAlias(aliases, stone.DisplayName, stone);
        }

        return aliases;
    }
}
