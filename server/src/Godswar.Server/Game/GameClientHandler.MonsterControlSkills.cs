using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleHostileMonsterControlSkillAsync(GamePacket packet, SkillCastRequest cast,
        SkillCombatDefinition combat, HostileStatusEffectDefinition definition,
        MonsterRuntimeSnapshot target, PlayerMonsterCombatAuthority authority,
        bool publishCastVisual, CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null || character.Profession != definition.RequiredProfession ||
            !RevalidateCurrentWorldEffectOwnership("monster_control"))
        {
            if (!publishCastVisual) await FinishRejectedMonsterControlAsync(cast, target, cancellationToken);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (!TryReserveLegacyHostileSkill(character, combat, now, out var mana,
                out var cooldown, out var cooldownRejected))
        {
            if (cooldownRejected) await SendSkillCooldownRejectionAsync(cancellationToken, "MonsterControlCooldown");
            else await SendInsufficientManaRejectionAsync(mana, cancellationToken, "MonsterControlMana");
            if (!publishCastVisual) await FinishRejectedMonsterControlAsync(cast, target, cancellationToken);
            return;
        }
        if (!_registry.TryCommitPlayerMonsterControl(_session, target, authority, definition, now, out var result))
        {
            ReleaseHostileSkillCooldown(cooldown);
            lock (character.VitalsSync)
            {
                character.CurrentMp = (int)Math.Min(character.MaxMp, (long)character.CurrentMp + Math.Max(0, combat.Mp));
                if (combat.Mp > 0) character.MarkVitalsChanged();
                mana = character.CurrentMp;
            }
            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            if (!publishCastVisual) await FinishRejectedMonsterControlAsync(cast, target, cancellationToken);
            await _session.SendAsync(PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, mana),
                cancellationToken, "MonsterControlRefund");
            return;
        }
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        try
        {
            if (publishCastVisual) await _registry.DeliverMonsterPacketToViewerAsync(_session,
                character.CurrentMap, target.ObjectId, PacketBuilder.SkillCastVisual(packet.Buffer, LocalPlayerObjectId),
                target.SpawnGeneration, cancellationToken, "MonsterControlCastSelf");
            await _registry.PublishMonsterControlsAsync(_session, result.Monster, cancellationToken);
            // Close a started native cast even if the target's viewer lease disappeared.
            await _session.SendAsync(PacketBuilder.SkillCastImpact(LocalPlayerObjectId, target.ObjectId,
                cast.SkillId, target.X, target.Z), cancellationToken, "MonsterControlImpactSelf");
            await _session.SendAsync(PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, mana),
                cancellationToken, "MonsterControlManaSelf");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { Console.WriteLine($"[skill] monster control publication deferred: {error.GetType().Name}"); }
        if (publishCastVisual) await _registry.BroadcastToMonsterViewersAsync(character.CurrentMap, target.ObjectId,
            PacketBuilder.SkillCastVisual(packet.Buffer, CurrentPlayerObjectId), cancellationToken, _session,
            "MonsterControlCastWorld", expectedSpawnGeneration: target.SpawnGeneration);
        await _registry.BroadcastToMonsterViewersAsync(character.CurrentMap, target.ObjectId,
            PacketBuilder.SkillCastImpact(CurrentPlayerObjectId, target.ObjectId, cast.SkillId, target.X, target.Z),
            cancellationToken, _session, "MonsterControlImpactWorld", expectedSpawnGeneration: target.SpawnGeneration);
        await PersistSkillVitalsAsync(character, areaSkill: false, cancellationToken);
        Console.WriteLine($"[skill] monster control character={character.Name} skill={cast.SkillId} " +
            $"target={target.ObjectId} status={definition.StatusId} seconds={definition.Duration.TotalSeconds:0}");
    }

    private async Task<(MonsterRuntimeSnapshot, PlayerMonsterCombatAuthority)?> CaptureHostileMonsterSkillTargetAsync(
        SkillCastRequest cast, SkillCombatDefinition combat, uint? expectedGeneration,
        bool finishRejectedControl, CancellationToken cancellationToken)
    {
        if (_character is null) return null;
        if (!_registry.TryCapturePlayerMonsterTarget(_session, _character.CurrentMap, cast.TargetObjectId,
                out var target, out var authority) ||
            expectedGeneration.HasValue && target.SpawnGeneration != expectedGeneration.Value ||
            !_registry.IsMonsterVisibleTo(_session, cast.TargetObjectId, target.SpawnGeneration) ||
            !target.IsSpawned || !target.IsAlive)
        {
            Console.WriteLine($"[skill] rejected unavailable monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            if (finishRejectedControl) await FinishRejectedMonsterControlAsync(cast, null, cancellationToken);
            return null;
        }
        if (!SkillCombatResolver.IsWithinRange(_character.PositionX, _character.PositionZ, target.X, target.Z, combat))
        {
            Console.WriteLine($"[skill] rejected out-of-range monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} " +
                $"player={_character.PositionX:F2},{_character.PositionZ:F2} monster={target.X:F2},{target.Z:F2} range={combat.Distance:F2}");
            if (finishRejectedControl) await FinishRejectedMonsterControlAsync(cast, target, cancellationToken);
            return null;
        }
        return (target, authority);
    }

    private Task FinishRejectedMonsterControlAsync(SkillCastRequest cast, MonsterRuntimeSnapshot? target,
        CancellationToken cancellationToken) => _session.SendAsync(PacketBuilder.SkillCastImpact(LocalPlayerObjectId,
            cast.TargetObjectId, cast.SkillId, target?.X ?? _character?.PositionX ?? 0,
            target?.Z ?? _character?.PositionZ ?? 0), cancellationToken, "MonsterControlRejectedFinish");
}
