using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task RecoverLegacyInstanceStateBeforeSnapshotAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var recoveredPayments = 0;
        if (_legacyInstanceOpalPayments is not null)
        {
            recoveredPayments =
                await _legacyInstanceOpalPayments.RecoverCharacterAsync(
                    _processRealmId,
                    accountId,
                    characterId,
                    ownership,
                    cancellationToken);
        }

        var recoveredClaims = 0;
        if (_legacyInstanceDailyEntries is not null)
        {
            recoveredClaims =
                await _legacyInstanceDailyEntries.RecoverCharacterAsync(
                    _processRealmId,
                    accountId,
                    characterId,
                    ownership,
                    cancellationToken);
        }

        if (recoveredPayments != 0 || recoveredClaims != 0)
        {
            Console.WriteLine(
                "[instance-caller] reconciled pre-snapshot state " +
                $"character={characterId} payments={recoveredPayments} " +
                $"claims={recoveredClaims}");
        }
    }
}
