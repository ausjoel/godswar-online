using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderChecks
{
    private static void CheckMonsterCombatAuthority()
    {
        var physical = MonsterCombatProfileCatalog.Resolve(
            3,
            MonsterAttackDamageKind.Physical);
        Check.Equal(31, physical.PhysicalAttack,
            "authored monster V1 retains the captured tier-three physical attack");
        Check.True(
            physical.AttackKind == MonsterAttackDamageKind.Physical,
            "physical monster attack type remains explicit");
        Check.True(
            physical.PhysicalDefense > 0 &&
            physical.MagicDefense > 0 &&
            physical.Hit > 0 &&
            physical.Dodge > 0 &&
            physical.Critical > 0 &&
            physical.CriticalResistance > 0,
            "authored monster V1 supplies all deterministic defense and rating channels");

        var magicalBoss = MonsterCombatProfileCatalog.Resolve(
            120,
            MonsterAttackDamageKind.Magical,
            isBoss: true);
        Check.True(
            magicalBoss.AttackKind == MonsterAttackDamageKind.Magical,
            "published magical attack type selects magical combat authority");
        Check.True(
            magicalBoss.MagicAttack > physical.PhysicalAttack &&
            magicalBoss.PhysicalDefense > physical.PhysicalDefense,
            "tier and rank deterministically scale monster combat authority");

        var special = MonsterCombatProfileCatalog.Resolve(
            120,
            MonsterAttackDamageKind.Special,
            isBoss: true);
        Check.True(
            special.AttackKind == MonsterAttackDamageKind.Special &&
            !special.UsesMagicDamage,
            "stock attack type three preserves its wire identity and uses the reviewed physical fallback");

        var baseDefinition = new GameplayMonsterTemplateDefinition(
            "source",
            "map",
            1,
            "map",
            "monster",
            "Monster",
            "normal",
            false,
            false,
            false,
            1,
            2.5f);
        var physicalContent = GameplayContentCatalog.Empty with
        {
            MonsterTemplates = [baseDefinition]
        };
        var magicalContent = physicalContent with
        {
            MonsterTemplates = [baseDefinition with { AttackType = 2 }]
        };
        var specialContent = physicalContent with
        {
            MonsterTemplates = [baseDefinition with { AttackType = 3 }]
        };
        Check.True(
            WorldContentRevisionHasher.HashGameplay(physicalContent).Sha256 !=
                WorldContentRevisionHasher.HashGameplay(magicalContent).Sha256 &&
            WorldContentRevisionHasher.HashGameplay(magicalContent).Sha256 !=
                WorldContentRevisionHasher.HashGameplay(specialContent).Sha256,
            "monster attack type participates in the sealed gameplay revision");

        CheckPassiveSpawnCombat();
    }

    /// <summary>
    /// The level 141 spies Athens city spawns never engage and barely hit back.
    /// </summary>
    /// <remarks>
    /// The reference leaves "Spartan Spy" and "Spartan Spy Captain" standing: they
    /// do not chase a passer-by, and what they return is one point whatever the
    /// character's stats. Their level still scales the health and defense they are
    /// spawned with, so only the attack is floored.
    /// </remarks>
    private static void CheckPassiveSpawnCombat()
    {
        static CapturedMonsterSpawn Spawn(string templateKey, uint tier)
        {
            // The level travels in the spawn frame, so the fixture writes it where
            // CapturedMonsterSpawn.Tier reads it.
            var packet = new byte[108];
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), tier);
            return new CapturedMonsterSpawn(
                1,
                "Athens",
                templateKey,
                templateKey,
                10_759u,
                100f,
                -100f,
                packet);
        }

        Check.True(
            !MonsterAggroPolicy.IsAggressive(141, "A_normals_greecewarrior_003") &&
            !MonsterAggroPolicy.IsAggressive(141, "A_normals_greecewarrior_008"),
            "the Athens spies never engage on their own");
        Check.True(
            MonsterAggroPolicy.IsAggressive(141, "A_normal_deer_001") &&
            MonsterAggroPolicy.IsAggressive(30, "A_normal_deer_001"),
            "the passive rule is the spy family and nothing else");

        var spy = MonsterCombatProfileCatalog.Empty.Resolve(
            Spawn("A_normals_greecewarrior_003", 141));
        Check.Equal(141, spy.Level, "the passive spy keeps its own level");
        Check.Equal(1, spy.PhysicalAttack, "the passive spy hits for one point");
        Check.Equal(1, spy.MagicAttack, "the passive spy's magic attack is floored too");
        Check.True(
            spy.PhysicalDefense > 1,
            "the passive spy keeps the defense its level earns");

        var deer = MonsterCombatProfileCatalog.Empty.Resolve(
            Spawn("A_normal_deer_001", 3));
        Check.Equal(31, deer.PhysicalAttack, "an ordinary spawn keeps its tier attack");
    }
}
