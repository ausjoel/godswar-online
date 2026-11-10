using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<LegacyEntryResult> HandleLegacyInstanceEntryAsync(uint npcId, int dialogIndex,
        InstanceCallerEntryDestination destination, LegacyInstanceEntryPreparation expectedPreparation,
        CancellationToken cancellationToken)
    {
        var reservationId = Guid.NewGuid();
        try
        {
            var result = await HandleLegacyInstanceEntryCoreAsync(npcId, dialogIndex, destination,
                expectedPreparation, reservationId, cancellationToken);
            if (result.Outcome != LegacyEntryOutcome.Admitted)
                Console.WriteLine("[instance-caller] entry rejected " +
                    $"character={_character?.Name ?? "<none>"} destination={destination.Kind} " +
                    $"reason={result.Outcome} realm={expectedPreparation.Party.RealmId} " +
                    $"day={_realmCalendar.GetDay(DateTimeOffset.UtcNow)} " +
                    $"daily-limit={result.DailyLimit?.ToString() ?? "<none>"} reservation={reservationId}");
            return result;
        }
        finally
        {
            // No Wonderland monster becomes attackable before the original
            // actually admitted roster is immutable, including partial entry.
            _registry.CompleteWonderlandAdmissions(reservationId);
        }
    }

    private bool TryStartLegacyInstanceEncounter(InstanceCallerEntryDestination destination,
        WorldInstanceId instanceId, ushort dailyLimit, LegacyInstancePartySnapshot party,
        Guid reservationId) => destination.Kind switch
    {
        InstanceCallerEntryKind.Atlantis => _registry.TryStartAtlantisEncounter(instanceId,
            dailyLimit, party.Members.Select(member => (member.CharacterId, member.Level)).ToArray(),
            DateTimeOffset.UtcNow, reservationId, party.Members),
        InstanceCallerEntryKind.Wonderland => _registry.TryStartWonderlandEncounter(instanceId,
            dailyLimit, party.Members, DateTimeOffset.UtcNow, reservationId),
        InstanceCallerEntryKind.HarborAttack => _registry.TryStartHarborAttackEncounter(instanceId,
            dailyLimit, party.Members, DateTimeOffset.UtcNow, reservationId),
        var trainingKind when Godswar.Server.Application.WorldInstances.TrainingGroundPolicy.IsTrainingGroundKind(trainingKind) => // WARRIOR (F-51): all grounds
            _registry.TryStartTrainingGroundEncounter(instanceId,
            dailyLimit, party.Members, DateTimeOffset.UtcNow, reservationId), // TRAINING
        _ => false
    };
}
