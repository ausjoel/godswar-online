using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// REVIVE (Polish/REVIVE.md): the free revive as the reference server does
    /// it - restore 10 % HP/MP, send the restored vitals (10097), then ONE
    /// landing frame (10018 MSG_SCENE_CHANGE) to the revive point. The client
    /// reloads the scene and the ordinary map-transition readiness flow
    /// (ClientReady -> PlayerDetail -> world objects) finishes it, exactly like
    /// a portal.
    /// </summary>
    /// <remarks>
    /// The stock path below this one re-sends the whole login EnterMain (10019)
    /// to a client that is already in the world. Origin's 10019 handler is the
    /// login path: it re-creates the main UI on top of the old one (every UI
    /// script loads a second time, the screen fills with the raw text of hidden
    /// windows) and the avatar stays in its death pose although HP is back.
    /// Seen with the real client on the revive test instance, 2026-10-28.
    /// Returns false, before anything is changed, when this path does not
    /// apply; the caller then keeps the stock behaviour.
    /// </remarks>
    private async Task<bool> TryBeginNativeFreeReviveAsync(
        long lifeRevision,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            _account is null ||
            !_registered ||
            !_worldPresenceAnnounced ||
            _pendingMapTransition is not null)
        {
            return false;
        }

        var deathMap = _character.CurrentMap;
        byte targetMap;
        float targetX;
        float targetZ;
        var medusa = false;
        if (DynamicDungeonContentMapPolicy.IsMedusaMap(deathMap))
        {
            if (!MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                    "first-entry",
                    out var entrance))
            {
                return false;
            }

            targetMap = deathMap;
            targetX = entrance.X;
            targetZ = entrance.Z;
            medusa = true;
        }
        else if (DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(deathMap))
        {
            // Other instance maps keep the stock path (their own run rules).
            return false;
        }
        else if (TryResolveArenaReviveLanding(deathMap, out targetMap, out targetX, out targetZ)) // ARENA
        {
        }
        else if (ReviveLandingCatalog.TryResolve(deathMap, out var landing))
        {
            targetMap = landing.MapId;
            targetX = landing.X;
            targetZ = landing.Z;
        }
        else
        {
            // Same fallback as ApplyFreeRevivalLanding: the faction capital.
            targetMap = _character.Camp == GameDefaults.SpartaCamp
                ? GameDefaults.SpartaCapitalMap
                : GameDefaults.AthensCapitalMap;
            targetX = GameDefaults.StartingPositionX;
            targetZ = GameDefaults.StartingPositionZ;
        }

        if (targetMap != deathMap &&
            (DynamicDungeonContentMapPolicy.IsDynamicDungeonMap(targetMap) ||
             !_gameplayCatalogs.MapTraversal.TryGetMap(deathMap, out _) ||
             !_gameplayCatalogs.MapTraversal.TryGetMap(targetMap, out _)))
        {
            return false;
        }

        // Monsters that were fighting the corpse drop it, as in the stock path.
        _registry.ClearMonsterAggroForCharacter(
            deathMap,
            _session,
            _character.Id,
            DateTimeOffset.UtcNow);

        // Server-side HP stays 0 until the player has left the death scene:
        // restoring it first (first test build) let the same monster kill the
        // revived player again before the transfer. The client still needs
        // the restored HP BEFORE the 10018: Origin keeps the local avatar
        // across a scene change and picks its pose from the HP it holds
        // (SetState 0x490C80, docs/wonderland-native-death-revival-20260911.md),
        // so the 10097 carries the values that are applied right after.
        var restoredPercent = medusa ? 30 : 10;
        var restoredHp = Math.Max(
            1,
            checked(_character.MaxHp * restoredPercent / 100));
        var restoredMp = Math.Max(
            0,
            checked(_character.MaxMp * restoredPercent / 100));
        await _session.SendAsync(
            PacketBuilder.PlayerVitalsUpdate(
                LocalPlayerObjectId,
                restoredHp,
                restoredMp),
            cancellationToken,
            "RevivalVitals");

        SceneTransitionOutcome outcome;
        if (targetMap == deathMap)
        {
            outcome = await TryBeginSameMapSceneTransitionAsync(
                targetX,
                targetZ,
                "free-revive",
                () => _registry.TryGetPlayerLifeRevision(
                          _session,
                          out var currentLife) &&
                      currentLife == lifeRevision,
                cancellationToken);
        }
        else
        {
            outcome = await TryBeginMapTransitionAsync(
                targetMap,
                targetX,
                targetZ,
                "free-revive",
                cancellationToken);
        }

        if (outcome != SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            // Hidden in the destination scene now (WorldReady=false), so no
            // monster can reach the player before the client is ready.
            lock (_character.VitalsSync)
            {
                _character.CurrentHp = restoredHp;
                _character.CurrentMp = restoredMp;
                _character.MarkVitalsChanged();
            }

            if (!await PersistVitalsCheckpointAsync(
                    _character,
                    force: true,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    "The revival vitals checkpoint was not durable.");
            }
        }

        Console.WriteLine(
            $"[revive] native free revival character={_character.Name} " +
            $"map={deathMap}->{targetMap} arrival={targetX:F2},{targetZ:F2} " +
            $"hp={_character.CurrentHp}/{_character.MaxHp} " +
            $"mp={_character.CurrentMp}/{_character.MaxMp} " +
            $"life={lifeRevision} outcome={outcome}");

        if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
        {
            // Nothing moved. Save the character alive at the revive point (the
            // stock helper: landing + 10 % HP/MP) and drop the connection: the
            // next login enters there cleanly, which is better than the broken
            // re-enter of the stock path.
            await RestoreFreeRevivalStateAsync(cancellationToken);
            Console.WriteLine(
                $"[revive] native revival rejected, saved at the revive point " +
                $"and disconnected character={_character.Name} " +
                $"map={_character.CurrentMap} " +
                $"pos={_character.PositionX:F2},{_character.PositionZ:F2}");
            _session.Disconnect();
        }

        return true;
    }
}
