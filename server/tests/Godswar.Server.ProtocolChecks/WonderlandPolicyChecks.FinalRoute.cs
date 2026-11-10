using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandPolicyChecks
{
    private static void CheckCapturedSecondAndFinalRoute()
    {
        // external-20260907-194130-562.log lines16275/16293:
        // Fane001 function57, followed by this exact native scene207 arrival.
        var scene = Convert.FromHexString("18002227F00000000000A0414CADBC40000042C30000CF00");
        var walk = Convert.FromHexString("1400D227CF00020010A49D4124CB41C32E58AA40");
        var second = WonderlandTerrainPolicy.GetIsland(2);
        Check.True(BinaryPrimitives.ReadUInt16LittleEndian(scene.AsSpan(2)) == 10018 &&
            BinaryPrimitives.ReadUInt16LittleEndian(scene.AsSpan(22)) == 207 &&
            second.Entrance.X == BinaryPrimitives.ReadSingleLittleEndian(scene.AsSpan(8)) &&
            second.Entrance.Z == BinaryPrimitives.ReadSingleLittleEndian(scene.AsSpan(16)),
            "island two uses the captured first-transporter arrival (20,-194), not the authored southwest landing");
        Check.True(BinaryPrimitives.ReadUInt16LittleEndian(walk.AsSpan(2)) == 10194 &&
            Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(walk.AsSpan(8)) - second.Entrance.X) < 1 &&
            Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(walk.AsSpan(12)) - second.Entrance.Z) < 1,
            "the first subsequent native walk begins beside the captured arrival");
        Check.True(second.SpawnPositions.All(point => WonderlandTerrainPolicy.DistanceSquared(
                second.Entrance, point.X, point.Z) > 100),
            "every captured second-island spawn preserves the arrival's ten-unit safety radius");

        var geometry = WonderlandTerrainPolicy.GetIsland(8);
        var plan = WonderlandMonsterPlan.Create(8, 5, 0);
        var bosses = plan.Where(monster => monster.IsBoss).ToArray();
        WonderlandPosition[] points = [new(61.0597801f, 0, -40.3367691f), new(-12.8723574f, 0, -38.4623795f),
            new(-0.289722651f, 0, 20.5801926f), new(0.612464309f, 0, 46.7211685f)];
        Check.True(geometry.Entrance == new WonderlandPosition(56, 0, -112) &&
            bosses.Select(monster => monster.Role).SequenceEqual(new[] { WonderlandMonsterRole.Minotaur,
                WonderlandMonsterRole.Deer, WonderlandMonsterRole.DragonKing, WonderlandMonsterRole.ScorpionKing }) &&
            bosses.Select(monster => monster.Position).SequenceEqual(points),
            "the final island begins at the bottom and follows the right bend, left bend, upper junction and northern boss");
        Check.True(plan.Length == 13 && plan.Count(monster => monster.RequiredForProgression) == 5 &&
            plan.Take(12).Select(monster => monster.ObjectId).SequenceEqual(Enumerable.Range(46800, 12).Select(id => (uint)id)),
            "four bosses and the guard gate completion while all thirteen actor identities remain stable");
        for (var group = 0; group < 4; group++)
        {
            var boss = bosses[group];
            var atlas = plan.Skip(4 + 2 * group).Take(2).ToArray();
            Check.True(atlas.All(monster => monster.Role == WonderlandMonsterRole.Atlas &&
                    !monster.RequiredForProgression && monster.AggroRadius == 24 && monster.LeashRadius == 64 &&
                    WonderlandTerrainPolicy.DistanceSquared(boss.Position, monster.Position.X, monster.Position.Z) <= 225),
                "each final boss keeps two captured nearby Atlas with local aggro and optional progression");
        }
        var guard = plan[12];
        Check.True(guard.TemplateKey == "B_normale_fairy_004" && guard.Role == WonderlandMonsterRole.ChestGuard &&
            guard.RequiredForProgression && !guard.IsBoss && guard.MechanicKey == "chest" &&
            guard.Position == new WonderlandPosition(-0.176883012f, 0, 93.9803467f),
            "the single original Chest Guard occupies its captured treasure-side position");
        var helper = WonderlandTraversalPolicy.GetTeleporter(8);
        Check.True(geometry.TreasureChest == new WonderlandPosition(4, 0, 93) &&
            geometry.Exit == new WonderlandPosition(0, 0, 80) && helper.X == geometry.Exit.X && helper.Z == geometry.Exit.Z &&
            WonderlandTerrainPolicy.DistanceSquared(guard.Position, helper.X, helper.Z) > 100 &&
            WonderlandTerrainPolicy.DistanceSquared(geometry.TreasureChest, helper.X, helper.Z) > 100 &&
            geometry.TreasureChest.Z > bosses[3].Position.Z &&
            WonderlandTerrainPolicy.DistanceSquared(bosses[3].Position, geometry.TreasureChest.X, geometry.TreasureChest.Z) > 1600,
            "the authored Returning Helper preserves the native treasure and guard without a combat-exclusion overlap");
        CheckFinalGuardUnlock(plan);
    }
}
