using Godswar.Server.Networking;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal MonsterControlResult? TryCommitMonsterControl(ClientSession session,
        MonsterRuntimeSnapshot expectedTarget, PlayerMonsterCombatAuthority authority,
        HostileStatusEffectDefinition definition, DateTimeOffset now)
    {
        lock (_medusaOwnershipGate)
        lock (_membershipGate)
        lock (_monsterRuntimeGate)
        {
            if (_medusaInstanceOwner is not null || !_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
                context.MapId != MapId || context.Character.CurrentMap != MapId ||
                context.WorldInstanceId != WorldInstanceId || context.WorldInstanceId != authority.WorldInstanceId ||
                context.WorldRevision != authority.WorldRevision ||
                context.WorldMembershipEpoch != authority.WorldMembershipEpoch || context.Ownership != authority.Ownership ||
                context.Character.Profession != definition.RequiredProfession || context.Character.CurrentHp <= 0 ||
                _monsterRuntime is null || !_monsterRuntime.TryGetSnapshot(expectedTarget.ObjectId, out var current) ||
                current.RuntimeInstanceId != expectedTarget.RuntimeInstanceId ||
                current.SpawnGeneration != expectedTarget.SpawnGeneration ||
                !current.IsAlive || !current.IsSpawned ||
                !IsWithinMonsterControlRange(context.Character, current, definition)) return null;
            return _monsterRuntime.TryApplyControl(current.ObjectId, context.CharacterId,
                definition, current.SpawnGeneration, now, out var result) ? result : null;
        }
    }

    private static bool IsWithinMonsterControlRange(GameCharacter source, MonsterRuntimeSnapshot target,
        HostileStatusEffectDefinition definition)
    {
        var radius = definition.Range + SkillCombatResolver.TargetCollisionAllowance;
        var dx = (double)source.PositionX - target.X;
        var dz = (double)source.PositionZ - target.Z;
        return double.IsFinite(dx) && double.IsFinite(dz) && dx * dx + dz * dz <= radius * radius;
    }
}
