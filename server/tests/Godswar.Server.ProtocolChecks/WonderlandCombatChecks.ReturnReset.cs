using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string ReturnResetCheckName = "Wonderland monster return movement and full HP reset publication";

    public static async Task RunReturnResetAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        {
            await using var f = await Fixture.CreateAsync(1, mode, PlayerRuntimeMode.Ecs);
            var hit = f.DirectHit("alpha", 1000);
            var original = hit.Monster;
            await using (var view = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
                f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)) view!.Commit();
            f.Character.PositionX = original.HomeX + 15;
            f.Character.PositionZ = original.HomeZ;
            f.Registry.UpdateCharacter(f.Session, f.Character, advanceWorldRevision: false);
            for (var step = 0; step < 8; step++)
            {
                f.Now += MonsterMapRuntime.TickInterval;
                await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now, CancellationToken.None);
            }
            Check.True(f.Monster("alpha").X > original.HomeX, "reset fixture boss first chases away from home");
            f.MoveTo(original);
            f.Runtime.Map.ClearMonsterAggroForCharacter(f.Character.Id, f.Now);
            var packetStart = f.Transport.ReadLegacyPackets().Count;
            var continuations = 0;
            var returned = false;
            for (var step = 0; step < 64 && !returned; step++)
            {
                f.Now += MonsterMapRuntime.TickInterval;
                await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now, CancellationToken.None);
                var current = f.Monster("alpha");
                if (current.CombatPhase == MonsterCombatPhase.Returning)
                {
                    Check.Equal(original.CurrentHealth, current.CurrentHealth,
                        "resetting boss stays damaged until it reaches home");
                    if (step > 0) continuations++;
                }
                returned = current.CombatPhase == MonsterCombatPhase.AwaitingRetirement;
            }
            await f.FlushAsync();
            Check.True(returned && continuations > 1, "boss publishes a multi-step return to its original spawn");
            var reset = f.Monster("alpha");
            Check.True(reset.CurrentHealth == reset.MaximumHealth && !reset.IsMoving &&
                reset.X == reset.HomeX && reset.Z == reset.HomeZ &&
                reset.SpawnGeneration == original.SpawnGeneration && reset.HealthRevision == original.HealthRevision + 1,
                "reset restores full HP once at home without retiring the instance boss");
            var packets = f.Transport.ReadLegacyPackets().Skip(packetStart).ToArray();
            var bossPackets = packets.Where(p => p.Length >= 8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == original.ObjectId).ToArray();
            Check.True(bossPackets.Count(p => Op(p) == 10016) >= continuations,
                "native return includes continued positions rather than a single walking start");
            var hp = bossPackets.Single(p => Op(p) == 10097);
            Check.True(BinaryPrimitives.ReadUInt32LittleEndian(hp.AsSpan(8)) == reset.MaximumHealth &&
                Array.IndexOf(bossPackets, hp) > Array.FindLastIndex(bossPackets, p => Op(p) == 10017),
                "native absolute full HP update follows the exact-home movement end");
            Check.True(bossPackets.All(p => Op(p) is not (10020 or 10023 or 10024)),
                "normal return never removes and redraws the boss");

            f.Now += MonsterMapRuntime.TickInterval;
            await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now, CancellationToken.None);
            await CheckPostResetDamageAndImmediateReturnAsync(f);
        }

        static ushort Op(byte[] p) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
    }

    private static async Task CheckPostResetDamageAndImmediateReturnAsync(Fixture f)
    {
        var hit = f.DirectHit("alpha", 10);
        var before = f.Transport.ReadLegacyPackets().Count;
        await f.Registry.DeliverMonsterHealthPacketToViewerAsync(f.Session, 207, hit.ObjectId,
            PacketBuilder.PhysicalDamage(WorldObjectIds.ForPlayer(f.Character.Id),
                f.Character.PositionX, 0, f.Character.PositionZ, hit.ObjectId, 10, 1, 1),
            hit.HealthMutation!.Value, CancellationToken.None, "PostResetHit");
        await f.FlushAsync();
        var delivered = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
        Check.True(delivered.Any(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 10026) &&
            delivered.All(p => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) is not (10020 or 10024)),
            "next hit follows acknowledged reset HP without health-gap removal/reappearance");

        f.Runtime.Map.ClearMonsterAggroForCharacter(f.Character.Id, f.Now);
        before = f.Transport.ReadLegacyPackets().Count;
        f.Now += MonsterMapRuntime.TickInterval;
        await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        Check.True(f.Transport.ReadLegacyPackets().Skip(before).Any(p =>
            BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)) == 10097 &&
            BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == hit.ObjectId),
            "reset already at home still publishes its queued full HP revision");
    }
}
