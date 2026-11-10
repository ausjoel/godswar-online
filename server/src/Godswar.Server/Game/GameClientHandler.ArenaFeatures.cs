using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// ARENA build: Parnassus admission and the arena/Parnassus revive points.
/// Everything here is behind <see cref="ArenaFeatureGates"/>; with the gates off
/// each method returns false and the caller keeps its old behaviour. One exception
/// (review fix, 2026-11-04): the two Parnassus return transporters keep answering
/// whatever the switch says, so a character saved on map 68 can always walk out
/// after <c>GODSWAR_PARNASSUS</c> is unset (map 68 has no exit link or revive point
/// of its own). They exist only on map 68, so no other NPC is affected.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>Test switch: <c>GODSWAR_PARNASSUS_ALWAYS_OPEN=1</c> ignores the IGG periods (FINALISE2: read by GateSchedules).</summary>
    private static bool ParnassusAlwaysOpen => Godswar.Server.Application.Gates.GateSchedules.ParnassusAlwaysOpen;

    /// <summary>
    /// The Battlefield Transporter's opening menu. With <c>GODSWAR_PARNASSUS</c> on the
    /// Parnassus button (1002) is appended at send time; the published dialogue
    /// baseline (a hashed revision) is not changed.
    /// </summary>
    internal static int[] BattlefieldTransporterMenu(IReadOnlyList<int> published) =>
        ArenaFeatureGates.Parnassus
            ? ParnassusProtocol.WithEntryButton(published)
            : published.ToArray();

    /// <summary>
    /// Handles a click on the Parnassus button. Returns false (nothing sent) for any
    /// other click, or when the gate is off, so the stock transporter code runs.
    /// The caller has already authorised the transporter context.
    /// </summary>
    private async Task<bool> TryHandleParnassusEntryAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (!ArenaFeatureGates.Parnassus ||
            _character is null ||
            !ParnassusProtocol.IsEntryClick(dialogIndex, subId, arguments) ||
            _character.CurrentMap is not (0 or 1))
        {
            return false;
        }

        ClearBattlefieldTransporterDialogueContext();
        // FINALISE2: the Gates table (band, opening mode, GM rule incl. gm.obey_gates, which Parnassus ignored before).
        var operatorBypass = Godswar.Server.Application.Gates.GateRules.GmMaySkip(Godswar.Server.Application.Gates.GateKeys.Parnassus) &&
            await HasOperatorAuthorityAsync(cancellationToken);
        var band = Godswar.Server.Application.Gates.GateRules.Band(
            Godswar.Server.Application.Gates.GateKeys.Parnassus, ParnassusProtocol.MinimumLevel, ParnassusProtocol.MaximumLevel);
        int? refusal = null;
        if (!operatorBypass &&
            (_character.Level < band.Minimum || // FINALISE2
             _character.Level > band.Maximum))
        {
            refusal = ParnassusProtocol.LevelResultSubId;
        }
        else if (!operatorBypass && // FINALISE2
                 !Godswar.Server.Application.Gates.GateSchedules.IsOpen(
                     Godswar.Server.Application.Gates.GateKeys.Parnassus, _realmCalendar, DateTimeOffset.UtcNow))
        {
            refusal = ParnassusProtocol.ClosedResultSubId;
        }

        if (refusal is { } result)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    ParnassusProtocol.ResultDialogIndex,
                    result),
                cancellationToken,
                "ParnassusAdmissionRejected");
            Console.WriteLine(
                $"[parnassus] refused character={_character.Name} " +
                $"level={_character.Level} result={result}");
            return true;
        }

        var (x, z) = ParnassusProtocol.Arrival(_character.Camp);
        var outcome = await TryBeginMapTransitionAsync(
            ParnassusProtocol.MapId,
            x,
            z,
            $"npc-battlefield-transporter-parnassus:{npcId}",
            cancellationToken);
        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation &&
            !_session.IsDisconnected)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(BattlefieldTransportUnavailableMessage),
                cancellationToken,
                "ParnassusUnavailable");
        }

        Console.WriteLine(
            $"[parnassus] entry character={_character.Name} camp={_character.Camp} " +
            $"level={_character.Level} outcome={outcome} arrival={x:F0},{z:F0}");
        return true;
    }

    /// <summary>
    /// Review fix: the return transporters are served with the Parnassus switch on
    /// AND off. Rolling the switch back must not strand characters saved on map 68,
    /// which has no walk-out link and no revive point of its own.
    /// </summary>
    internal static bool ServesParnassusReturn(int mapId, string npcKey) =>
        ParnassusProtocol.IsReturnTransporter(mapId, npcKey);

    /// <summary>
    /// The two Parnassus "Capital City Transporters" (Parnassus_003/004, placed by the
    /// GENERATED rows in migration.sql). The stock client draws their buttons from
    /// <c>NpcFunTranmit.lua</c> (function 1 = NPC_FLAG_SYS_TRANMIT): sub 1005
    /// "*Do you need to return to the capital? I can help!" and sub 1006 (its Spartan
    /// twin). They have no published dialogue route, so they are answered here, gate on or off.
    /// </summary>
    private async Task<bool> TryHandleParnassusTransporterOpenAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!ServesParnassusReturn(npc.MapId, npc.NpcKey))
        {
            return false;
        }

        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                ParnassusProtocol.ReturnFunction,
                npc.NpcKey),
            cancellationToken,
            "ParnassusTransporterOpen");
        return true;
    }

    private async Task<bool> TryHandleParnassusTransporterActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (!ServesParnassusReturn(npc.MapId, npc.NpcKey))
        {
            return false;
        }

        var button = ParnassusProtocol.ReturnButton(npc.NpcKey);
        if (_character is not { CurrentHp: > 0 } character ||
            character.CurrentMap != ParnassusProtocol.MapId ||
            dialogIndex != ParnassusProtocol.ReturnFunction ||
            !IsWithinBattlefieldTransporterInteractionDistance(npc))
        {
            return true;
        }

        if (subId == BattlefieldTransporterProtocol.InitialRequestSubId)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npc.InteractionId, ParnassusProtocol.ReturnFunction, [button]),
                cancellationToken,
                "ParnassusTransporterMenu");
            return true;
        }

        if (subId != button ||
            arguments.Count != BattlefieldTransporterProtocol.FunctionArgumentCount ||
            arguments.Any(static value => value != -1))
        {
            return true;
        }

        // Back to the player's own capital, at the faction start point.
        var capital = character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        var outcome = await TryBeginMapTransitionAsync(
            capital,
            GameDefaults.StartingPositionX,
            GameDefaults.StartingPositionZ,
            $"npc-parnassus-return:{npc.NpcKey}",
            cancellationToken);
        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation && !_session.IsDisconnected)
        {
            await _session.SendAsync(
                PacketBuilder.ServerNote(BattlefieldTransportUnavailableMessage),
                cancellationToken,
                "ParnassusReturnUnavailable");
        }

        Console.WriteLine(
            $"[parnassus] return character={character.Name} npc={npc.NpcKey} " +
            $"map={ParnassusProtocol.MapId}->{capital} outcome={outcome}");
        return true;
    }

    /// <summary>
    /// Free-revive landing for the two maps this build serves (gate on only):
    /// the Duel Arena waiting area for an arena death, and the own faction's
    /// arrival point for a Parnassus death. GENERATED points; see
    /// <see cref="DuelArenaCombatArea.ReviveLanding"/> and
    /// <see cref="ParnassusProtocol.Arrival"/>.
    /// </summary>
    private bool TryResolveArenaReviveLanding(
        byte deathMap,
        out byte mapId,
        out float x,
        out float z)
    {
        if (deathMap == DuelArenaCombatArea.MapId && ArenaFeatureGates.DuelArenaPvp)
        {
            (mapId, x, z) = DuelArenaCombatArea.ReviveLanding;
            return true;
        }

        if (deathMap == ParnassusProtocol.MapId &&
            ArenaFeatureGates.Parnassus &&
            _character is not null)
        {
            mapId = ParnassusProtocol.MapId;
            (x, z) = ParnassusProtocol.Arrival(_character.Camp);
            return true;
        }

        mapId = 0;
        x = 0;
        z = 0;
        return false;
    }
}
