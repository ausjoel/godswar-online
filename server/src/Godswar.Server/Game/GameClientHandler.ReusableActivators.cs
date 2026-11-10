using Godswar.Server.Application.Npc;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly Dictionary<uint, DateTimeOffset> _activatorReadyAt = [];

    // An item in public.item_activators casts its buff and stays in the bag. The table is empty until
    // configured in the editor. The buff lasts the session, like every runtime buff, and so does the cooldown.
    private async Task UseReusableActivatorAsync(uint itemId, ItemActivator activator, CancellationToken cancellationToken)
    {
        await SendKitBagRefreshAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (_activatorReadyAt.TryGetValue(itemId, out var readyAt) && readyAt > now)
        {
            return;
        }

        if (!SkillStatusEffectCatalog.TryGet(activator.SkillId, out var definition) || !definition.Beneficial)
        {
            Console.WriteLine($"[activator] item {itemId} points at skill {activator.SkillId}, which is not an authored buff");
            return;
        }

        await _registry.ApplyRuntimeStatusAndPublishAsync(_session, definition, now, "item-activator", cancellationToken);
        _activatorReadyAt[itemId] = now.AddSeconds(activator.CooldownSeconds);
    }
}
