using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] RawFighterLevelSealProjectionResult(
        uint npcId,
        int resultSubId,
        uint capabilityToken,
        uint? currentExperience = null,
        uint? maximumExperience = null)
    {
        if (!RawFighterLevelSealProjectionProtocol.IsCapabilityToken(
                capabilityToken))
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilityToken),
                capabilityToken,
                "The raw Fighter EXP capability token is invalid.");
        }
        if (currentExperience.HasValue != maximumExperience.HasValue)
        {
            throw new ArgumentException(
                "Current and maximum Fighter EXP must be supplied together.");
        }
        if (maximumExperience == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumExperience),
                maximumExperience,
                "Maximum Fighter EXP must be nonzero.");
        }

        var hasProjection = currentExperience.HasValue;
        var packet = new byte[
            hasProjection
                ? RawFighterLevelSealProjectionProtocol.
                    ProjectionResultBytes
                : RawFighterLevelSealProjectionProtocol.
                    OutcomeResultBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            NpcFunctionActionResponseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(8),
            CapitalNpcServiceProtocol.LevelSealerDialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12),
            resultSubId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            RawFighterLevelSealProjectionProtocol.ResultMarker);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            capabilityToken);
        if (hasProjection)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(24),
                currentExperience!.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(28),
                maximumExperience!.Value);
        }

        return packet;
    }
}
