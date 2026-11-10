using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static AuthoritativeInstanceTransitionCommand
        BuildLegacyInstanceTransitionCommand(
            LegacyInstancePartyMember member,
            WorldInstanceId targetInstanceId,
            InstanceCallerEntryDestination destination) => new(
                member.CharacterId,
                member.SourceWorldInstanceId,
                member.SourceMapId,
                member.Ownership,
                targetInstanceId,
                destination.TargetMapId,
                destination.TargetX,
                destination.TargetZ);

    private bool IsWithinInstanceCallerInteractionDistance(
        NpcSpawnDefinition npc)
    {
        if (_character is null)
        {
            return false;
        }

        var deltaX = (double)_character.PositionX - npc.X;
        var deltaZ = (double)_character.PositionZ - npc.Z;
        var maximum =
            (double)InstanceCallerProtocol.MaximumInteractionDistance;
        return double.IsFinite(deltaX) &&
            double.IsFinite(deltaZ) &&
            deltaX * deltaX + deltaZ * deltaZ <= maximum * maximum;
    }

    private async Task SendLegacyInstanceAdmissionFailureAsync(
        uint npcId,
        int dialogIndex,
        InstanceCallerEntryKind kind,
        LegacyInstanceEntryStatus status,
        CancellationToken cancellationToken)
    {
        var resultSubId = kind == InstanceCallerEntryKind.Atlantis
            ? status switch
            {
                LegacyInstanceEntryStatus.LeaderRequired =>
                    InstanceCallerProtocol.AtlantisLeaderResultSubId,
                LegacyInstanceEntryStatus.PartyTooSmall =>
                    InstanceCallerProtocol.AtlantisPartyTooSmallResultSubId,
                LegacyInstanceEntryStatus.PartyTooLarge =>
                    InstanceCallerProtocol.AtlantisPartyTooLargeResultSubId,
                LegacyInstanceEntryStatus.LevelRequirementNotMet =>
                    InstanceCallerProtocol.AtlantisLevelResultSubId,
                _ => InstanceCallerProtocol.QueueUnavailableResultSubId
            }
            : InstanceCallerProtocol.QueueUnavailableResultSubId;
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                resultSubId),
            cancellationToken,
            "LegacyInstanceAdmissionRejected");
        Console.WriteLine(
            "[instance-caller] admission rejected " +
            $"character={_character?.Name ?? "<none>"} " +
            $"destination={kind} status={status}");
    }

    private async Task SendLegacyInstanceScheduleFailureAsync(
        uint npcId,
        LegacyInstanceScheduleStatus status,
        CancellationToken cancellationToken)
    {
        if (status == LegacyInstanceScheduleStatus.Closed)
        {
            // FINALISE2: an editor-closed instance has no client line of its own.
            await _session.SendAsync(
                PacketBuilder.ServerNote("This instance is closed now. Check the Event window for its opening times."),
                cancellationToken,
                "InstanceGateClosed");
            return;
        }

        var resultSubId = status switch
        {
            LegacyInstanceScheduleStatus.WrongDay =>
                InstanceCallerProtocol.WonderlandWeekendResultSubId,
            LegacyInstanceScheduleStatus.DailyCutoffPassed =>
                InstanceCallerProtocol.WonderlandCutoffResultSubId,
            _ => throw new InvalidOperationException(
                "An open schedule is not an admission failure.")
        };
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                InstanceCallerProtocol.WonderlandResultDialogIndex,
                resultSubId),
            cancellationToken,
            "WonderlandScheduleRejected");
    }

    private Task SendLegacyInstanceUnavailableAsync(
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.ServerNote(LegacyInstanceUnavailableMessage),
            cancellationToken,
            "LegacyInstanceUnavailable");

    private Task SendLegacyInstanceDailyEntryUsedAsync(
        uint npcId,
        InstanceCallerEntryKind kind,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                InstanceCallerProtocol.DialogIndex,
                kind == InstanceCallerEntryKind.Atlantis
                    ? InstanceCallerProtocol
                        .AtlantisMaximumEntriesResultSubId
                    : InstanceCallerProtocol.QueueUnavailableResultSubId),
            cancellationToken,
            "LegacyInstanceDailyEntryAlreadyUsed");
}
