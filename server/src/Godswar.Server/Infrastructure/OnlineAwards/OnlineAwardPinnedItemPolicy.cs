using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.OnlineAwards;

internal static class OnlineAwardPinnedItemPolicy
{
    public static bool IsValid(
        IItemTemplateCatalog templates,
        IPetContentCatalog pets,
        OnlineAwardRewardEntry reward)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(pets);
        if (reward.ItemId <= 0 ||
            !templates.TryGet(checked((uint)reward.ItemId), out var template) ||
            !TryReadStackCap(template.StatsJson, out var stackCap) ||
            reward.StackCap != stackCap)
        {
            return false;
        }

        if (!pets.TryGetSpeciesByEggItemId(
                checked((uint)reward.ItemId),
                out var species))
        {
            return true;
        }

        return stackCap == 1 &&
            pets.TryGetNativeProfile(
                species.SpeciesId,
                reward.ItemQuality,
                out _);
    }

    private static bool TryReadStackCap(
        string statsJson,
        out short stackCap)
    {
        stackCap = 0;
        try
        {
            using var document = JsonDocument.Parse(statsJson);
            if (!document.RootElement.TryGetProperty(
                    "Overlap",
                    out var overlap))
            {
                return false;
            }

            return overlap.ValueKind switch
            {
                JsonValueKind.String => short.TryParse(
                    overlap.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out stackCap) && stackCap is >= 1 and <= 999,
                JsonValueKind.Number => overlap.TryGetInt16(out stackCap) &&
                    stackCap is >= 1 and <= 999,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
