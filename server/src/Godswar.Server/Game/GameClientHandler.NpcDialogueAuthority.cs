using Godswar.Server.Application.Npc; // B22
using Godswar.Server.Application.World;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async ValueTask<(
        IReadOnlyList<NpcDialogueRouteDefinition> Routes,
        NpcTextDefinition? Text)> ResolveNpcDialogueRoutesAsync(
            NpcSpawnDefinition npc,
            CancellationToken cancellationToken)
    {
        if (CapitalNpcServiceProtocol.TryResolve(npc, out var service) &&
            service == CapitalNpcServiceKind.ExchangeMentor)
        {
            return ([CapitalNpcServiceProtocol.ExchangeRoute(npc)], null);
        }
        if (CapitalNpcServiceProtocol.TryGetDialogueRoutes(
                npc,
                out var capturedRoutes))
        {
            return (capturedRoutes, null);
        }

        NpcDialogueContent dialogue;
        try
        {
            dialogue = await _worldContent.ReadNpcDialogueAsync(
                npc.NpcKey,
                cancellationToken);
        }
        catch (WorldContentUnavailableException ex) when (
            ex.Family == "npc-dialogues" &&
            ex.Reason == WorldContentFailureReason.Missing)
        {
            Console.WriteLine(
                $"[npc] dialogue missing npc={npc.InteractionId} " +
                $"key={npc.NpcKey}");
            return ([], null);
        }

        var text = dialogue.Text;
        if (dialogue.Routes.Count == 0)
        {
            // A published NPC may own dialogue text without any extended
            // function; the caller opens its description window instead. Report
            // it as a diagnostic only when there is no text to show either.
            if (string.IsNullOrWhiteSpace(text.Description))
            {
                Console.WriteLine(
                    $"[npc] dialogue has no implemented behavior " +
                    $"npc={npc.InteractionId} key={npc.NpcKey}");
                return ([], null);
            }

            return ([], text);
        }

        if (dialogue.Routes.Any(route =>
                !NpcDialogueBehaviorRegistry.IsAllowed(npc, route)))
        {
            Console.Error.WriteLine(
                "[npc] rejected dialogue capability mismatch " +
                $"npc={npc.InteractionId} key={npc.NpcKey}");
            return ([], null);
        }

        return (dialogue.Routes, text);
    }

    private async ValueTask<NpcDialogueRouteDefinition?>
        ResolveNpcDialogueRouteAsync(
            NpcSpawnDefinition npc,
            int dialogIndex,
            CancellationToken cancellationToken)
    {
        var (routes, _) = await ResolveNpcDialogueRoutesAsync(
            npc,
            cancellationToken);
        return routes.FirstOrDefault(route =>
            route.DialogIndex == dialogIndex);
    }

    private async Task SendNpcInitialMenuAsync(
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route,
        CancellationToken cancellationToken)
    {
        ClearGearEnhancerSelection();
        ClearInstanceCallerPageContext();
        ClearTransporterDialogueContext();
        ClearBattlefieldTransporterDialogueContext();
        ClearDuelArenaTransporterDialogueContext();
        if (route.Behavior == NpcDialogueBehavior.CapturedCapital)
        {
            await SendCapturedCapitalNpcInitialMenuAsync(
                npc.InteractionId,
                route,
                cancellationToken);
            return;
        }
        if (route.Behavior == NpcDialogueBehavior.WarehouseManager)
        {
            await SendWarehouseManagerMenuAsync(
                npc,
                route,
                cancellationToken);
            return;
        }
        if (route.Behavior == NpcDialogueBehavior.Transporter &&
            !TryIssueTransporterDialogueContext(npc))
        {
            Console.Error.WriteLine(
                "[transporter] initial menu rejected outside interaction " +
                $"authority npc={npc.InteractionId} " +
                $"character={_character?.Name ?? "<none>"}");
            return;
        }
        if (route.Behavior ==
                NpcDialogueBehavior.BattlefieldTransporter &&
            !TryIssueBattlefieldTransporterDialogueContext(npc))
        {
            Console.Error.WriteLine(
                "[battlefield-transporter] initial menu rejected outside " +
                $"interaction authority npc={npc.InteractionId} " +
                $"character={_character?.Name ?? "<none>"}");
            return;
        }
        if (route.Behavior ==
                NpcDialogueBehavior.DuelArenaTransporter &&
            !TryIssueDuelArenaTransporterDialogueContext(npc))
        {
            Console.Error.WriteLine(
                "[duel-arena-transporter] initial menu rejected outside " +
                $"interaction authority npc={npc.InteractionId} " +
                $"character={_character?.Name ?? "<none>"}");
            return;
        }
        if (route.Behavior == NpcDialogueBehavior.HolySuitDesign &&
            _character is { Level: < 70 })
        {
            await _session.SendAsync(
                HolySuitDesignProtocol.BuildResultResponse(
                    npc.InteractionId,
                    HolySuitDesignProtocol.StoreLevelTooLowResultSubId),
                cancellationToken,
                "HolySuitLevelRequirement");
            return;
        }

        // W6-B: the shipped NpcFunEnhancer.lua (dialog 118) is a copy of
        // NpcFunBreak.lua. It also changes page on the client and reports the
        // three items one by one through opcode 10193, so the physical NPC 143
        // needs the same operation-unbound staging context as the Gear Mentor.
        // Without it every selection was ignored and each Start answered 1019.
        if (route.Behavior is
            NpcDialogueBehavior.GearMentor or
            NpcDialogueBehavior.OriginEnhancer or
            NpcDialogueBehavior.ClassSuit)
        {
            if (_account is null || _character is null)
            {
                return;
            }

            _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
                _account.Id,
                _character.Id,
                npc.InteractionId,
                route.DialogIndex,
                operation: null,
                expiresAt:
                    DateTimeOffset.UtcNow +
                    GearEnhancerProtocol.SelectionContextLifetime);
        }

        var menuSubIds = route.Behavior == NpcDialogueBehavior.Transporter
            // B22 (d32): the editor's menu (order, hidden, added) when it has rows for this NPC, else today's list.
            ? TransporterMenuPolicy.EffectiveMenu(
                route.InitialMenuSubIds,
                npc.NpcKey,
                npc.InteractionId,
                _character?.CurrentMap ?? (short)-1,
                NpcContentOverrides.Current)
            : route.Behavior == NpcDialogueBehavior.BattlefieldTransporter // ARENA: Parnassus button
                ? BattlefieldTransporterMenu(route.InitialMenuSubIds)
            : route.Behavior == NpcDialogueBehavior.ClassSuit // B22 G2 (d36): option 109, Class Suit V-VII
                ? ClassSuitProtocol.MenuWithTierBeyond(route.InitialMenuSubIds)
            : route.InitialMenuSubIds.ToArray();
        if (route.Behavior == NpcDialogueBehavior.Transporter && _transporterDialogueContext is { } issued)
        {
            _transporterDialogueContext = issued with { MenuSubIds = menuSubIds }; // B22
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                route.DialogIndex,
                menuSubIds),
            cancellationToken,
            "NpcFunctionActionResponse");
        Console.WriteLine(
            $"[npc] initial menu npc={npc.InteractionId} " +
            $"behavior={route.Behavior} dialog={route.DialogIndex} " +
            $"items={string.Join(',', menuSubIds)}");
    }
}
