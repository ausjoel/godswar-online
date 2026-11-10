using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckNativePlayerDeathAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        f.Character.CurrentHp = 1;
        f.Runtime.Map.TryGetWonderlandSnapshot(out var before);
        var packetCount = f.Transport.ReadLegacyPackets().Count;
        await f.IncomingAsync("tower");
        var emitted = f.Transport.ReadLegacyPackets().Skip(packetCount).ToArray();
        Check.True(f.Character.CurrentHp == 0 && !f.Session.IsDisconnected,
            $"a real lethal tower hit kills the player under {monsters}/{players}");
        var death = emitted.Single(packet => packet.Length == 116 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10027);
        Check.True(BinaryPrimitives.ReadUInt32LittleEndian(death.AsSpan(4)) == 0x1448 &&
            Enumerable.Range(0, 5).All(index =>
                BinaryPrimitives.ReadInt32LittleEndian(death.AsSpan(8 + index * 4)) == -1) &&
            death.AsSpan(28, 80).IndexOfAnyExcept((byte)0) == -1 &&
            BinaryPrimitives.ReadInt32LittleEndian(death.AsSpan(108)) == -1 &&
            BinaryPrimitives.ReadInt32LittleEndian(death.AsSpan(112)) == 0,
            "native MSG_DEAD opens local revival without granting or overwriting reward recipients");
        Check.True(emitted.All(packet => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != Opcodes.SceneChange),
            "lethal damage does not masquerade as a scene change or force a client reload");
        await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now.AddSeconds(35), CancellationToken.None);
        Check.True(f.Registry.TryGetSessionWorldInstanceId(f.Session, out var current) && current == f.Runtime.InstanceId &&
            f.Character.CurrentMap == 207 && f.Character.CurrentHp == 0 &&
            f.Runtime.Map.TryGetWonderlandSnapshot(out var after) && after.State == WonderlandRunState.Active &&
            before.StartedAt == after.StartedAt && before.Deadline == after.Deadline && after.CompletedIslands == 0,
            "remaining dead past the observed city-transfer delay cannot terminate the active run");
    }
}
