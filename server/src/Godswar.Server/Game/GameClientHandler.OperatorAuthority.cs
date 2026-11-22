using Godswar.Server.Application.Operators;
using Godswar.Server.Protocol;
namespace Godswar.Server.Game;
internal sealed partial class GameClientHandler
{
    private Task<bool> HasOperatorAuthorityAsync(CancellationToken cancellationToken) =>
        OperatorAuthority.AllowsAsync(_developerCommands.Enabled, _developerCommands.UseCharacterGmFlags,
            _account?.Id ?? 0, _character?.Id ?? 0, _developerCommands.AllowedAccountIds ?? [],
            OperatorControls.Store is { } store ? store.ReadCharacterGmAsync : null, cancellationToken);

    // W5B: a GM power that is not a chat command (gate skip, GM Mall purchase). Switched off = an ordinary player.
    private async Task<bool> HasOperatorPowerAsync(string powerKey, CancellationToken cancellationToken) =>
        GmPowerPolicy.Allows(powerKey) && await HasOperatorAuthorityAsync(cancellationToken);

    // W5B: /speed, /power and /armor go past their safe ranges only with character GM flags AND the limit lift on.
    private bool OperatorLimitsLifted =>
        _developerCommands.UseCharacterGmFlags && GmPowerPolicy.Allows(GmPowerPolicy.LimitLift);

    /// <summary>
    /// W5B: true (and the GM is told, and operator_log gets a row) when this recognised command is switched off in
    /// gm_power_policy. Call only AFTER the authority check, so a non-GM still learns nothing.
    /// </summary>
    private async Task<bool> RefuseSwitchedOffCommandAsync(
        GamePacket packet,
        string? powerKey,
        CancellationToken cancellationToken)
    {
        if (powerKey is null || GmPowerPolicy.Allows(powerKey))
        {
            return false;
        }

        var word = powerKey.StartsWith(GmPowerPolicy.CommandPrefix, StringComparison.Ordinal)
            ? powerKey[GmPowerPolicy.CommandPrefix.Length..]
            : powerKey;
        var reason = GmPowerPolicy.Allows(GmPowerPolicy.Master) ? powerKey : GmPowerPolicy.Master;
        await SendDeveloperItemFeedbackAsync(packet, $"[{word}] {GmPowerPolicy.RefusedText}", cancellationToken);
        await LogOperatorAsync("/" + word, string.Empty, $"refused: {reason} is switched off", cancellationToken);
        return true;
    }
}
