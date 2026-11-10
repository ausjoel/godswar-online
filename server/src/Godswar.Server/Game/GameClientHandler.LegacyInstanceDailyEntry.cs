using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<LegacyInstanceDailyEntryClaimResult?>
        TryClaimLegacyInstanceDailyEntryAsync(
            Guid reservationId,
            RealmId realmId,
            DateOnly realmDay,
            InstanceCallerEntryKind instanceKind,
            IReadOnlyCollection<int> characterIds,
            DateTimeOffset claimedAtUtc,
            CancellationToken cancellationToken)
    {
        if (_legacyInstanceDailyEntries is null)
        {
            return _registry.TryReserveLocalLegacyInstanceDailyEntry(
                reservationId,
                realmId,
                realmDay,
                instanceKind,
                characterIds);
        }

        try
        {
            return await _legacyInstanceDailyEntries.TryClaimAsync(
                new LegacyInstanceDailyEntryClaimRequest(
                    reservationId,
                    realmId,
                    realmDay,
                    instanceKind,
                    characterIds,
                    claimedAtUtc.ToUniversalTime()),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] legacy daily-entry claim failed: " +
                error.Message);
            await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
            return null;
        }
    }

    private async Task ReleaseLegacyInstanceDailyEntryAsync(
        Guid reservationId)
    {
        if (_legacyInstanceDailyEntries is null)
        {
            _registry.ReleaseLocalLegacyInstanceDailyEntry(reservationId);
            return;
        }

        try
        {
            await _legacyInstanceDailyEntries.ReleaseAsync(
                reservationId,
                CancellationToken.None);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] legacy daily-entry release failed: " +
                error.Message);
        }
    }

    private async Task ReleaseLegacyInstanceDailyEntryMembersAsync(
        Guid reservationId,
        IReadOnlyCollection<int> characterIds)
    {
        if (_legacyInstanceDailyEntries is null)
        {
            _registry.ReleaseLocalLegacyInstanceDailyEntryMembers(
                reservationId,
                characterIds);
            return;
        }

        try
        {
            await _legacyInstanceDailyEntries.ReleaseMembersAsync(
                reservationId,
                characterIds,
                CancellationToken.None);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] legacy member daily-entry release " +
                $"failed: {error.Message}");
        }
    }

    private async Task<bool> RecordLegacyInstanceAdmissionsAsync(
        Guid reservationId,
        IReadOnlyCollection<int> admittedCharacterIds)
    {
        if (admittedCharacterIds.Count == 0)
        {
            return true;
        }

        _registry.RecordAtlantisRewardAdmissions(reservationId, admittedCharacterIds);

        if (_legacyInstanceDailyEntries is not null)
        {
            var recorded = false;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await _legacyInstanceDailyEntries.RecordAdmissionsAsync(
                        reservationId,
                        admittedCharacterIds,
                        CancellationToken.None);
                    recorded = true;
                    break;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        "[instance-caller] daily admission marker failed " +
                        $"reservation={reservationId} attempt={attempt}: " +
                        error.Message);
                }
            }
            if (!recorded)
            {
                return false;
            }
        }

        return _legacyInstanceOpalPayments is null ||
               await RecordLegacyInstanceOpalAdmissionsAsync(
                   reservationId,
                   admittedCharacterIds);
    }

    private async Task CleanupFailedLegacyInstanceLaunchAsync(
        WorldInstanceDescriptor target,
        Guid reservationId)
    {
        await RetireFailedLegacyInstanceRuntimeAsync(target);
        await ReleaseLegacyInstanceDailyEntryAsync(reservationId);
    }

    private async Task RetireFailedLegacyInstanceRuntimeAsync(
        WorldInstanceDescriptor target)
    {
        try
        {
            if (!await _registry.TryRetireEmptyLocalWorldInstanceAsync(
                    target))
            {
                Console.Error.WriteLine(
                    "[instance-caller] failed-entry runtime retirement " +
                    $"was rejected instance={target.InstanceId} " +
                    $"revision={target.Revision}");
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "[instance-caller] failed-entry runtime retirement " +
                $"faulted instance={target.InstanceId}: {error.Message}");
        }
    }
}
