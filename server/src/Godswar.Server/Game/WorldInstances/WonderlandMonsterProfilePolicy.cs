using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

internal static class WonderlandMonsterProfilePolicy
{
    public static MonsterCombatProfile Resolve(MonsterCombatProfile original, WonderlandSpawnPolicy spawn)
    {
        var stats = spawn.Stats;
        return original with
        {
            AttackKind = stats.UsesMagicDamage ? MonsterAttackDamageKind.Magical : MonsterAttackDamageKind.Physical,
            CollisionRange = stats.AttackRange,
            AuthoredAttackRange = stats.AttackRange,
            Level = stats.Level,
            PhysicalAttack = stats.PhysicalAttack,
            MagicAttack = stats.MagicAttack,
            PhysicalDefense = stats.PhysicalDefense,
            MagicDefense = stats.MagicDefense,
            Hit = stats.Hit,
            Dodge = stats.Dodge,
            IsElite = false,
            IsBoss = spawn.IsBoss
        };
    }
}
