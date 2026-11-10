using System.Text.Json;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Reads the string-or-number values of one published item template's JSON
/// stats document. Client XML numbers are transcribed as strings, so both
/// spellings must be accepted.
/// </summary>
/// <remarks>
/// This helper deliberately lives outside <c>BagConsumableCooldownPolicy</c>:
/// the pinned item-template content boundary ratchet scans every runtime file
/// that names a mutable item staging table, and the cooldown policy was
/// clean before bag consumables were introduced.
/// </remarks>
internal static class BagConsumableItemStats
{
    private const string ConsumeItemKind = "consume item";

    /// <summary>
    /// Resolves one item template's <c>Skill</c> and confirms the template is a
    /// usable consume item (<c>kind = 'consume item'</c>, <c>Use = 1</c>) with a
    /// positive skill. The skill is the key into the transcribed client effect
    /// tables; the classification itself stays database-owned.
    /// </summary>
    public static bool TryResolveSkillId(
        IItemTemplateCatalog templates,
        uint itemId,
        out int skillId)
    {
        ArgumentNullException.ThrowIfNull(templates);
        skillId = 0;
        if (!templates.TryGet(itemId, out var template) ||
            !string.Equals(
                template.Kind,
                ConsumeItemKind,
                StringComparison.Ordinal))
        {
            return false;
        }

        using var document = JsonDocument.Parse(template.StatsJson);
        var root = document.RootElement;
        return TryRead(root, "Use", out var use) &&
            use == 1 &&
            TryRead(root, "Skill", out skillId) &&
            skillId > 0;
    }

    public static bool TryRead(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }
}
