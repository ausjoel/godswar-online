using System.Buffers.Binary;

namespace Godswar.Server.Protocol;

/// <summary>
/// Opt-in legacy-wire extension used only by the local network shim while the
/// secure transport is disabled. Stock requests keep their captured shape and
/// therefore never receive an extended response.
/// </summary>
internal static class RawFighterLevelSealProjectionProtocol
{
    public const int RequestBytes = 92;
    public const int StockResultBytes = 16;
    public const int OutcomeResultBytes = 24;
    public const int ProjectionResultBytes = 32;

    public const uint CapabilityTokenPrefix = 0xA100_0000;
    public const uint CapabilityTokenPrefixMask = 0xFF00_0000;
    public const uint CapabilityTokenNonceMask = 0x00FF_FFFF;
    public const uint ResultMarker = 0x3150_5852; // "RXP1" on the wire.

    private const int RequestNpcOffset = 4;
    private const int RequestDialogOffset = 8;
    private const int RequestCapabilityTokenOffset = 12;
    private const int RequestSubIdOffset = 16;
    private const int RequestArgumentsOffset = 20;
    private const int RequestArgumentCount = 18;

    private const uint SpartaLevelSealerNpcId = 5_139;
    private const uint AthensLevelSealerNpcId = 5_281;
    private const int LevelSealerDialogIndex = 116;
    private const int SealSubId = 102;
    private const int UnsealSubId = 103;

    public static bool IsCapabilityToken(uint token) =>
        (token & CapabilityTokenPrefixMask) == CapabilityTokenPrefix &&
        (token & CapabilityTokenNonceMask) != 0;

    public static bool TryReadCapabilityToken(
        GamePacket packet,
        out uint token)
    {
        ArgumentNullException.ThrowIfNull(packet);
        token = 0;
        if (packet.ClientOperationId.HasValue ||
            packet.Length != RequestBytes ||
            packet.Buffer.Length != RequestBytes ||
            packet.Opcode != Opcodes.NpcFunctionAction)
        {
            return false;
        }

        var bytes = packet.Buffer.AsSpan();
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.Slice(RequestNpcOffset, sizeof(uint)));
        var dialogIndex = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(RequestDialogOffset, sizeof(int)));
        var subId = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(RequestSubIdOffset, sizeof(int)));
        if (npcId is not (
                SpartaLevelSealerNpcId or AthensLevelSealerNpcId) ||
            dialogIndex != LevelSealerDialogIndex ||
            subId is not (SealSubId or UnsealSubId))
        {
            return false;
        }

        for (var index = 0; index < RequestArgumentCount; index++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(
                    bytes.Slice(
                        RequestArgumentsOffset + index * sizeof(int),
                        sizeof(int))) != -1)
            {
                return false;
            }
        }

        var candidate = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.Slice(RequestCapabilityTokenOffset, sizeof(uint)));
        if (!IsCapabilityToken(candidate))
        {
            return false;
        }

        token = candidate;
        return true;
    }
}
