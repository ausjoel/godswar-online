using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MonsterPlayerDamageEcsLiveAdapterChecks
{
    private static long MonsterAttackEgressHighWaterBytes(
        RuntimePolicySessionSocket socket)
    {
        var egress = (BoundedReliableEgress)typeof(ClientSession)
            .GetField("_egress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(socket.Session)!;
        return egress.Snapshot.HighWaterBytes;
    }

    private static async Task<byte[]> ReadMonsterImpactPrefixAsync(
        RuntimePolicySessionSocket socket,
        PlayerRuntimeMode mode,
        uint monsterObjectId)
    {
        var count = mode == PlayerRuntimeMode.Ecs ? 2 : 1;
        byte[] firstImpact = [];
        for (var index = 0; index < count; index++)
        {
            var impact = await socket.ReadPacketAsync(24);
            Check.True(
                BinaryPrimitives.ReadUInt16LittleEndian(impact.AsSpan(2)) == 10046 &&
                BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(4)) == monsterObjectId &&
                BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(8)) == 0x1448 &&
                BinaryPrimitives.ReadUInt32LittleEndian(impact.AsSpan(12)) == 2000,
                $"{mode} native impact {index + 1} precedes damage to the local player");
            if (index == 0)
            {
                firstImpact = impact;
            }
            else
            {
                Check.True(
                    firstImpact.SequenceEqual(impact),
                    "ordinary ECS attack retains the paired basic impact");
            }
        }
        return firstImpact;
    }
}
