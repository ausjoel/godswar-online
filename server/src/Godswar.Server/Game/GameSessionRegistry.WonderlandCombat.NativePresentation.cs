using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Captured ordinary attacks have two10046 presentations followed by one
    // 10026 damage result. Effects do not perform additional server damage.
    private static bool UsesCapturedWonderlandBasicAttack(WorldInstanceRuntime runtime, MonsterRuntimeUpdate attack) =>
        runtime.MapId == 207 && attack.WonderlandAbility is null &&
        runtime.Map.TryGetWonderlandSpawnPolicy(attack.Monster.ObjectId, out var policy) &&
        WonderlandCapturedAttackPolicy.PrimarySkillId(policy.TemplateKey) != 0;

    private static byte[][] BuildWonderlandBasicAttackVisuals(WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack, uint targetObjectId, float targetX, float targetZ,
        DateTimeOffset damageResolvedAt)
    {
        if (runtime.MapId != 207 || attack.WonderlandAbility is not null ||
            !runtime.Map.TryGetWonderlandSpawnPolicy(attack.Monster.ObjectId, out var policy)) return [];
        // Never reread current status or wall time after the HP commit. The
        // immutable attack and this timestamp also selected its damage channel.
        var skill = WonderlandCapturedAttackPolicy.ResolvePrimarySkill(policy, attack.Monster, damageResolvedAt);
        if (skill == 0) return [];

        // Preserve the captured pair even when both IDs are2000. The packets
        // only select native effects/poses; their client skill damage, area
        // and cooldown settings do not execute as additional game mechanics.
        return
        [
            PacketBuilder.SkillCastImpact(attack.Monster.ObjectId, targetObjectId, skill, targetX, targetZ),
            PacketBuilder.SkillCastImpact(attack.Monster.ObjectId, targetObjectId,
                DefaultMonsterImpactSkillId, targetX, targetZ)
        ];
    }

    private async Task PublishWonderlandBasicAttackVisualsLegacyAsync(WorldInstanceRuntime runtime,
        MonsterRuntimeUpdate attack, GameSessionContext recipient, uint targetObjectId,
        float targetX, float targetZ, DateTimeOffset damageResolvedAt, CancellationToken cancellationToken)
    {
        foreach (var visual in BuildWonderlandBasicAttackVisuals(runtime, attack, targetObjectId,
            targetX, targetZ, damageResolvedAt))
            await TrySendWorldInstancePacketAsync(runtime, recipient, visual, cancellationToken,
                "WonderlandCapturedAttackVisual");
    }
}
