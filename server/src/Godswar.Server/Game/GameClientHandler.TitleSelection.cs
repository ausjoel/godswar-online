using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleTitleSelectionAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || !TitleSelectionProtocol.TryRead(packet, out var titleId))
        {
            return;
        }
        await _registry.SelectCharacterTitleAsync(_session, titleId, cancellationToken);
    }
}
