using Godswar.Server.Application.Operators;
namespace Godswar.Server.Game;
internal sealed partial class GameClientHandler
{
    private Task<bool> HasOperatorAuthorityAsync(CancellationToken cancellationToken) =>
        OperatorAuthority.AllowsAsync(_developerCommands.Enabled, _developerCommands.UseCharacterGmFlags,
            _account?.Id ?? 0, _character?.Id ?? 0, _developerCommands.AllowedAccountIds ?? [],
            OperatorControls.Store is { } store ? store.ReadCharacterGmAsync : null, cancellationToken);
}
