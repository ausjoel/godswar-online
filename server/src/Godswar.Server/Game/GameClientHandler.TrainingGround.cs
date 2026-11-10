using Godswar.Server.Application.Npc;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// TRAINING: the Training Ground's player-facing half - the Instance Caller
/// entry (page 231), free revive at the entrance, the leader's end control, and
/// the completion-reward payout.
/// </summary>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The Heracles page's own button: a character inside the level band opens the
    /// shared Enter window for the party (one exact run of map 210); one outside it
    /// gets the reference's own level line (1510), exactly as before.
    /// </summary>
    private async Task BeginTrainingGroundEntryAsync(
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken)
    {
        await BeginTrainingGroundEntryAsync(
            InstanceCallerProtocol.TrainingGroundDestination,
            LevelBandGates.TrainingGround,
            "Trials of Hercules",
            npcId,
            dialogIndex,
            cancellationToken);
    }

    /// <summary>
    /// WARRIOR (F-51): a root-13 ground button. Same flow as the Heracles page:
    /// the ground's level band (editor-overridable), the gate's opening mode, the
    /// first-use DDL, then the party's Enter window for one exact run of the map.
    /// </summary>
    private Task BeginWarriorTrainingEntryAsync(
        WarriorTrainingGround ground,
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken) =>
        BeginTrainingGroundEntryAsync(
            ground.Destination,
            ground.GateKey,
            ground.DisplayName,
            npcId,
            dialogIndex,
            cancellationToken);

    private async Task BeginTrainingGroundEntryAsync(
        InstanceCallerEntryDestination destination,
        string gateKey,
        string gateName,
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        // FINALISE: editor level band (level_band_overrides), read hot.
        var (bandMinimum, bandMaximum) = LevelBandGates.Apply(
            gateKey, destination.MinimumLevel, destination.MaximumLevel);
        if (_character.Level < bandMinimum ||
            _character.Level > bandMaximum)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    InstanceCallerProtocol.AtlantisLevelResultSubId),
                cancellationToken,
                "InstanceCallerTrainingGroundLevel");
            return;
        }

        // FINALISE2 (F-64): the Gates table's opening mode, at the page itself (before the Enter window).
        if (await RefuseClosedInstanceGateAsync(gateKey, gateName, cancellationToken))
        {
            return;
        }

        // Daily-entry kinds 10 and 21-24 need their settings rows and widened checks before
        // the first claim; idempotent and done once per process.
        if (_registry.TrainingGroundStore is { } store)
        {
            try
            {
                await store.EnsureSchemaAsync(cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[training] schema unavailable: {error.Message}");
                await _session.SendAsync(
                    PacketBuilder.NpcFunctionActionResponse(
                        npcId,
                        dialogIndex,
                        InstanceCallerProtocol.QueueUnavailableResultSubId),
                    cancellationToken,
                    "InstanceCallerTrainingGroundUnavailable");
                return;
            }
        }

        Console.WriteLine(
            $"[instance-caller] Training Ground entry ground={destination.DisplayName} " +
            $"character={_character.Name} level={_character.Level} " +
            $"map={destination.TargetMapId} scene={destination.ClientSceneId}");
        await BeginLegacyInstanceEntryCountdownAsync(
            npcId,
            dialogIndex,
            destination,
            cancellationToken);
    }

    /// <summary>
    /// IGG acid=166 rule 3: a player killed in the Training Ground revives near the
    /// entrance and carries on. The generic path would restore the capital.
    /// </summary>
    private async Task<bool> TryHandleTrainingGroundReviveAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !TrainingGroundPolicy.TryGetGround(_character.CurrentMap, out var ground) ||
            !_registry.TryGetTrainingGroundInstance(_session, out var instanceId))
        {
            return false;
        }

        if (!_registry.TryReviveTrainingGroundPlayer(
                _session,
                out instanceId,
                out var lifeRevision))
        {
            Console.WriteLine(
                "[training] revive refused character=" +
                $"{_character.Name} instance={instanceId}");
            return true;
        }

        try
        {
            var outcome = await TryBeginSameMapSceneTransitionAsync(
                ground.ArrivalX, // WARRIOR (F-51): this ground's own entrance
                ground.ArrivalZ,
                "training-revive",
                () => _registry.IsSessionInWorldInstance(_session, instanceId) &&
                    _registry.TryGetPlayerLifeRevision(
                        _session,
                        out var currentLife) &&
                    currentLife == lifeRevision,
                cancellationToken,
                publishRevivalVitals: true);
            Console.WriteLine(
                "[training] free revive character=" +
                $"{_character.Name} instance={instanceId} life={lifeRevision} outcome={outcome}");
            if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
            {
                _session.Disconnect();
            }
        }
        catch
        {
            _session.Disconnect();
            throw;
        }

        return true;
    }

    /// <summary>The panel's Terminate control (no id) or the 12-byte end request (scene 256).</summary>
    private bool TryHandleTrainingGroundTermination(
        int? repetitionId,
        int repetitionIndex)
    {
        if (_character is null ||
            !TrainingGroundPolicy.TryGetGround(_character.CurrentMap, out var ground))
        {
            return false;
        }

        return _registry.TryTerminateTrainingGroundRunFromLeader(
            _session,
            repetitionId ?? ground.ClientSceneId,
            repetitionIndex);
    }

    /// <summary>
    /// Pays this character's recorded Training Ground rewards (EXP and talent
    /// points) through the shared progression payout the Zeus' Gift and Lelantine
    /// Farm rewards use (level-ups, client gain packets, the exp.events rate).
    /// Each row is claimed atomically first (so it is paid at most once, whichever
    /// handler or login runs it) and is never put back: an interrupted payment is
    /// logged as lost rather than risked twice. Called from the existing 20-second
    /// reward loop (which also runs once on world entry).
    /// </summary>
    private async Task PayTrainingGroundRewardsAsync(CancellationToken cancellationToken)
    {
        if (_registry.TrainingGroundStore is not { } store || _character is not { } character)
        {
            return;
        }

        IReadOnlyList<TrainingGroundRewardGrant> claims;
        try
        {
            claims = await store.ClaimRewardsAsync(character.Id, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[training] reward claim failed character={character.Name}: {error.Message}");
            return;
        }

        foreach (var claim in claims)
        {
            // At most once: the row stays claimed whatever happens below. The
            // shared payout writes EXP/TP to the database before it refreshes stats
            // and sends packets, so putting the row back after a late failure (a
            // client that just closed, a failed paid mark) would pay it twice.
            try
            {
                await PayZeusGiftProgressionAsync(
                    claim.Experience,
                    checked(claim.TalentPoints * Godswar.Server.Application.ZeusGift.ZeusGiftPolicy.TalentExperiencePerPoint),
                    "training-ground",
                    cancellationToken);
                Console.WriteLine(
                    $"[training] reward paid character={character.Name} run={claim.RunId} " +
                    $"outcome={claim.Outcome} score={claim.Score} exp={claim.Experience} tp={claim.TalentPoints}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[training] reward payment interrupted, NOT retried (claimed) character={character.Name} " +
                    $"run={claim.RunId} exp={claim.Experience} tp={claim.TalentPoints}: {error.Message}");
                if (error is OperationCanceledException)
                {
                    throw;
                }
                continue;
            }

            try
            {
                await store.MarkPaidAsync(claim.RunId, claim.CharacterId, CancellationToken.None);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.Error.WriteLine(
                    $"[training] reward paid but paid_at not written run={claim.RunId}: {error.Message}");
            }
        }
    }
    /// <summary>
    /// Map 210 has no templates of its own; its GM rows may use the listed
    /// cross-map spider / scorpion templates (TrainingGroundPolicy.Templates).
    /// Every other map is untouched.
    /// </summary>
    private static void AddTrainingGroundTemplateKeys(short mapId, HashSet<string> catalogTemplateKeys)
    {
        if (mapId == TrainingGroundPolicy.MapId) // Heracles only; the Warriors' grounds use their own templates
        {
            catalogTemplateKeys.UnionWith(TrainingGroundPolicy.Templates.Keys);
        }
    }

    /// <summary>Adds the Training Ground kill recorder to the settlement's commit callback.</summary>
    private Action? ChainTrainingGroundKill(Action? existing, MonsterDamageResult damageResult)
    {
        try
        {
            var record = _registry.CaptureTrainingGroundMonsterKill(_session, damageResult);
            return record is null ? existing : existing is null ? record : existing + record;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[training] death capture failed monster={damageResult.ObjectId}: {ex.Message}");
            return existing;
        }
    }
}
