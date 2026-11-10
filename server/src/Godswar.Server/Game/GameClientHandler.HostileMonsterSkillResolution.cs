using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private CombatResolution ResolveHostileMonsterSkillDamage(
        GameCharacter character,
        SkillCombatDefinition combat,
        MonsterRuntimeSnapshot target,
        ulong admittedCombatRevision,
        int targetOrder,
        DateTimeOffset authoritativeAt,
        in ClientStatusAggregate runtimeModifiers)
    {
        var eventId = CombatEventIdentity.ForPlayerMonsterSkill(
            character.Id,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            admittedCombatRevision,
            (uint)combat.SkillId,
            targetOrder);
        var targetStats = _gameplayCatalogs.MonsterCombatProfiles
            .Resolve(target.Definition)
            .ToTargetStats();
        targetStats = _registry.AdjustPveMonsterTargetStats(
            _session,
            target,
            authoritativeAt,
            targetStats);
        var resolution = SkillCombatResolver.ResolveDamage(
            character,
            combat,
            targetStats,
            eventId,
            targetOrder,
            runtimeModifiers);
        return _registry.AdjustPveOutgoingResolution(
            _session,
            character,
            target,
            CombatEventProvenance.DirectSkill,
            authoritativeAt,
            resolution,
            admittedCombatRevision);
    }

    private async Task PublishUnreportedHostileMonsterSkillMissAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        uint targetSpawnGeneration,
        bool publishCastVisual,
        int currentMana,
        CombatResolution resolution,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);
        // The client only clears its local "casting" state when it observes a
        // cast terminator. The hit path publishes SkillCastImpact for that
        // reason; a miss must publish the same terminator or the caster stays
        // locked in the casting stance forever.
        var (targetX, targetZ) = ResolveMissImpactTarget(cast);
        var casterNotified = true;
        try
        {
            if (publishCastVisual)
            {
                await _registry.DeliverMonsterPacketToViewerAsync(
                    _session,
                    character.CurrentMap,
                    cast.TargetObjectId,
                    PacketBuilder.SkillCastVisual(
                        packet.Buffer,
                        LocalPlayerObjectId),
                    targetSpawnGeneration,
                    cancellationToken,
                    "SkillMissCastSelf");
            }

            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastImpact(
                    LocalPlayerObjectId,
                    cast.TargetObjectId,
                    cast.SkillId,
                    targetX,
                    targetZ),
                targetSpawnGeneration,
                cancellationToken,
                "SkillMissCastImpactSelf");

            if (combat.Mp > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(
                        LocalPlayerObjectId,
                        currentMana),
                    cancellationToken,
                    "SkillMissManaSelf");
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] miss caster notification failed character={character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var visualRecipients = publishCastVisual
            ? await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastVisual(
                    packet.Buffer,
                    CurrentPlayerObjectId),
                cancellationToken,
                _session,
                "SkillMissCastWorld",
                expectedSpawnGeneration: targetSpawnGeneration)
            : 0;
        await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastImpact(
                CurrentPlayerObjectId,
                cast.TargetObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            _session,
            "SkillMissCastImpactWorld",
            expectedSpawnGeneration: targetSpawnGeneration);
        await PersistSkillVitalsAsync(
            character,
            areaSkill: false,
            cancellationToken);
        Console.WriteLine(
            $"[skill] unreported miss character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId} event={resolution.EventId} hit={resolution.Rolls.HitRollBasisPoints}/{resolution.Rolls.HitChanceBasisPoints} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={visualRecipients}");
    }

    /// <summary>
    /// The impact frame mirrors the authoritative monster position when the
    /// runtime still holds the target, and otherwise falls back to the
    /// requested ground point. A miss is exactly the case where no damage
    /// snapshot exists, so the position cannot come from the hit pipeline.
    /// </summary>
    private (float X, float Z) ResolveMissImpactTarget(
        in SkillCastRequest cast)
    {
        if (_registry.TryGetMonsterSnapshot(
                _session,
                _character!.CurrentMap,
                cast.TargetObjectId,
                out var target))
        {
            return (target.X, target.Z);
        }

        return (cast.TargetX, cast.TargetZ);
    }
}
