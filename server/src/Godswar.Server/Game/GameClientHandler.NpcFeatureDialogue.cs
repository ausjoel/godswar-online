using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleNpcFeatureDialogueAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (route.Behavior == NpcDialogueBehavior.CapturedCapital)
        {
            await HandleCapturedCapitalNpcFunctionAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.CreditExchange)
        {
            await HandleCapitalNpcCreditExchangeAsync(
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.ClassSuit)
        {
            await HandleClassSuitAsync(
                packet,
                route,
                npcId,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.FactionCrier)
        {
            await HandleFactionCrierAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.WarehouseManager)
        {
            await HandleWarehouseManagerAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.InstanceCaller)
        {
            await HandleInstanceCallerAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.Transporter)
        {
            await HandleTransporterAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior ==
            NpcDialogueBehavior.BattlefieldTransporter)
        {
            await HandleBattlefieldTransporterAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior ==
            NpcDialogueBehavior.DuelArenaTransporter)
        {
            await HandleDuelArenaTransporterAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.FarmReturnTeleporter)
        {
            await HandleLelantineFarmReturnAsync(
                route,
                npcId,
                dialogIndex,
                subId,
                cancellationToken);
            return true;
        }

        if (route.Behavior == NpcDialogueBehavior.Farm)
        {
            await HandleLelantineFarmAsync(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments,
                cancellationToken);
            return true;
        }

        if (route.Behavior is not (
                NpcDialogueBehavior.PetManager or
                NpcDialogueBehavior.PetPointReset))
        {
            return false;
        }

        await HandlePetManagerAsync(
            packet,
            npcId,
            dialogIndex,
            subId,
            arguments,
            cancellationToken);
        return true;
    }
}
