using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> SendPetSkillOwnerStatRefreshAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var status = await _registry.SendStatusSnapshotToSelfAsync(
            _session,
            now,
            cancellationToken,
            $"{reason}ExtendedStatus");
        if (status is null)
        {
            return false;
        }
        return true;
    }
}
