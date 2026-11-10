using System.Security.Cryptography;

namespace Godswar.Server.Game;

/// <summary>
/// Identity of one ground item inside a kill. The high half identifies the drop
/// and the low half the kill itself, which is the split the reference server
/// sent in the opcode-10029 record at +64/+68 and echoed back with its pickup
/// reply so the client can clear the matching ground item.
/// </summary>
internal static class MonsterLootGroundKey
{
    internal readonly record struct Key(uint High, uint Low);

    public static Key Resolve(Guid deathEventId, int lootIndex)
    {
        Span<byte> input = stackalloc byte[20];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        deathEventId.TryWriteBytes(input[..16]);

        BitConverter.TryWriteBytes(input[16..], lootIndex);
        SHA256.HashData(input, hash);
        var high = BitConverter.ToUInt32(hash);

        BitConverter.TryWriteBytes(input[16..], 0);
        SHA256.HashData(input, hash);
        var low = BitConverter.ToUInt32(hash);
        return new(high, low);
    }
}
