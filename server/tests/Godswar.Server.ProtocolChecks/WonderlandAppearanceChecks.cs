using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static class WonderlandAppearanceChecks
{
    public const string CheckName = "Wonderland chest appearance identity and allied native camp presentation";

    public static Task RunAsync()
    {
        foreach (byte camp in new byte[] { 0, 1 })
        {
            CheckChests(camp);
            foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
                CheckAlliedCamp(mode, camp);
        }
        return Task.CompletedTask;
    }

    private static void CheckChests(byte camp)
    {
        var chests = WonderlandTreasureChestPolicy.AddChests([], camp);
        string[] expectedKeys = ["Fane_008", "Fane_009", "Fane_010", "Fane_011",
            camp == 0 ? "Fane_013" : "Fane_012", "Fane_014", "Fane_015", "Fane_016"];
        // Independent XYZ bytes from the nine use_bx_00_all scenery objects in
        // both byte-identical Fane.hmp files. Keep one reward chest per island;
        // the full run proves the two distinct faction anchors on island5.
        string[] sceneryCoordinates = [
            "D5F32D4300000000BDE518C3", "0180BE41000000007F3217C3",
            "4DA2E3C200000000FC1101C3", "52DC20C300000000DCED1742",
            camp == 0 ? "DE0E09C3000000005DE61A43" : "8998C5C200000000BBD83243", "1AE61A430000000083BE2F43",
            "F38330430000000007D78841", "501F8440000000002570B942"];
        Check.Equal(8, chests.Count, "all eight treasures retain separate native identities");
        var stream = PacketBuilder.NpcSpawns(chests);
        Check.Equal(8 * 108, stream.Length, "all chests use the same native NPC appearance constructor");
        for (var index = 0; index < chests.Count; index++)
        {
            var chest = chests[index];
            var packet = stream.AsSpan(index * 108, 108);
            var scenery = Convert.FromHexString(sceneryCoordinates[index]);
            var sceneryX = BinaryPrimitives.ReadSingleLittleEndian(scenery);
            var sceneryZ = BinaryPrimitives.ReadSingleLittleEndian(scenery.AsSpan(8));
            var dx = chest.X - sceneryX;
            var dz = chest.Z - sceneryZ;
            Check.True(dx * dx + dz * dz < 0.4f * 0.4f,
                $"island {index + 1}: clickable NPC overlays the large native scenery chest as on island 1");
            Check.True(chest.ObjectId == 5710u + index && chest.InteractionId == chest.ObjectId &&
                chest.NpcKey == expectedKeys[index] && chest.TemplateKey == expectedKeys[index] + "_Male15" &&
                WonderlandTreasureChestPolicy.TryGetIsland(chest, out var island) && island == index + 1,
                "appearance consistency cannot alias a treasure name, click identity or reward island");
            var header = index == 4 ? camp == 0 ? "6C0024271100CF00" : "6C0024271101CF00" : "6C0024271102CF00";
            Check.True(packet[..8].SequenceEqual(Convert.FromHexString(header)) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet[8..]) == chest.ObjectId &&
                BinaryPrimitives.ReadSingleLittleEndian(packet[28..]) == chest.X &&
                BinaryPrimitives.ReadSingleLittleEndian(packet[36..]) == chest.Z,
                "each chest keeps its own object and terrain position inside native Fane NPC framing");
            // Literal bytes from the three externally captured treasure spawns,
            // wire offset 40. Native actor+0x4D4 uses this as orientation, not scale.
            Check.True(packet.Slice(40, 4).SequenceEqual(Convert.FromHexString("9A994440")),
                "every chest uses the same externally observed first-three orientation");
            Check.Equal(expectedKeys[index] + "_Male15",
                Encoding.ASCII.GetString(packet[44..]).TrimEnd('\0'),
                "wire template retains the matching unique client label");
            Check.True(chest.Detail10077.Length == 0 && chest.Detail10080.Length == 0,
                "later chests have no alternate detail or equipment appearance path");
        }
    }

    private static void CheckAlliedCamp(MonsterRuntimeMode mode, byte camp)
    {
        var map = WonderlandMapChecks.Create(mode, camp);
        var now = WonderlandMapChecks.EnterIsland(map, 5);
        Check.True(map.TryGetWonderlandSnapshot(out var run), "fifth island exists");
        Check.Equal(camp, run.PartyCamp, "presentation uses the admitted party's fixed allegiance");
        var allies = run.ActiveSpawns.Where(policy => policy.IsAllied).ToArray();
        Check.Equal(7, allies.Length, "one marshal and six captured faction supporters are friendly");
        Check.Equal(7, run.ActiveSpawns.Count(policy => !policy.IsAllied),
            "the opposing marshal and supporters stay hostile");
        var friendlyHeader = Convert.FromHexString(camp == 0 ? "6C0024271200CF00" : "6C0024271201CF00");
        var enemyHeader = Convert.FromHexString("6C0024271202CF00");
        foreach (var policy in run.ActiveSpawns)
        {
            Check.True(map.TryGetMonsterSnapshot(policy.ObjectId, out var monster), "published faction actor exists");
            var expectedHeader = policy.IsAllied ? friendlyHeader : enemyHeader;
            Check.True(monster.Definition.Packet.AsSpan(0, 8).SequenceEqual(expectedHeader) &&
                PacketBuilder.CapturedMonsterAppearance(monster.Appearance).AsSpan(0, 8).SequenceEqual(expectedHeader),
                $"{mode}/{camp}: native camp survives initial spawn and current-vitals appearance projection");
            if (!policy.IsAllied) continue;
            Check.True(policy.FactionCamp == camp && !policy.RequiredForProgression,
                "friendly appearance cannot change the opposing-faction progression objective");
            Check.True(!map.TryApplyMonsterDamageGuarded(monster.ObjectId, 1, 101,
                monster.SpawnGeneration, monster.HealthRevision, now, out _) &&
                !map.TryApplyMonsterPeriodicDamageGuarded(monster.ObjectId, 1, 101,
                    monster.SpawnGeneration, monster.HealthRevision, now, out _),
                "friendly actors remain immune to direct and periodic player attacks");
            Check.True(map.TryGetMonsterSnapshot(monster.ObjectId, out var after) &&
                after.CurrentHealth == monster.CurrentHealth && after.HealthRevision == monster.HealthRevision,
                "rejected attacks cannot alter allied health or its revision");
        }
        var runtime = map.InitializeMonsters([], now);
        var friendlyIds = allies.Select(policy => policy.ObjectId).ToHashSet();
        var targets = allies.Select(policy => new MonsterCombatTarget(101, policy.Position.X,
            policy.Position.Z, true, WorldInstanceId: map.WorldInstanceId)).ToArray();
        for (var second = 0; second <= 5; second++)
        {
            var tick = runtime.Advance(now.AddSeconds(second), targets);
            Check.True(!tick.Updates.Any(update => friendlyIds.Contains(update.Monster.ObjectId) &&
                update.Kind == MonsterRuntimeUpdateKind.Attacked),
                "same-faction actors never generate a hostile player attack even at point-blank range");
        }
        Check.True(map.SnapshotMonsters().Where(monster => !run.ActiveSpawns.Any(policy =>
                policy.ObjectId == monster.ObjectId)).All(monster => monster.Definition.Packet[5] == 2),
            "earlier ordinary monsters retain their established hostile camp");
    }
}
