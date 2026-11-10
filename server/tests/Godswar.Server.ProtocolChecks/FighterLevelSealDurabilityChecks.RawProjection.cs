using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class FighterLevelSealDurabilityChecks
{
    private const uint RawProjectionToken = 0xA112_3456;

    private static void CheckRawFighterExperienceProjection(
        string handler)
    {
        var request = BuildRawProjectionRequest(RawProjectionToken);
        Check.True(
            RawFighterLevelSealProjectionProtocol.TryReadCapabilityToken(
                request,
                out var decodedToken) &&
            decodedToken == RawProjectionToken,
            "exact raw Level Sealer request opts into EXP projection");

        var stockRequest = BuildRawProjectionRequest(116);
        Check.True(
            !RawFighterLevelSealProjectionProtocol.TryReadCapabilityToken(
                stockRequest,
                out _),
            "stock raw Level Sealer request does not opt into an extension");
        Check.True(
            !RawFighterLevelSealProjectionProtocol.TryReadCapabilityToken(
                BuildRawProjectionRequest(0xA100_0000),
                out _) &&
            !RawFighterLevelSealProjectionProtocol.TryReadCapabilityToken(
                BuildRawProjectionRequest(0xE712_3456),
                out _),
            "raw projection token requires A1 and a nonzero 24-bit nonce");

        var changedArgument = BuildRawProjectionRequest(RawProjectionToken);
        BinaryPrimitives.WriteInt32LittleEndian(
            changedArgument.Buffer.AsSpan(88),
            0);
        Check.True(
            !RawFighterLevelSealProjectionProtocol.TryReadCapabilityToken(
                changedArgument,
                out _),
            "raw projection capability rejects changed Level Sealer arguments");
        var identifiedRequest = new GamePacket(
            request.Buffer.ToArray(),
            Guid.Parse("56b8dc2e-a94f-4849-9f26-1dcba1c49326"));
        Check.True(
            !RawFighterLevelSealProjectionProtocol.TryReadCapabilityToken(
                identifiedRequest,
                out _),
            "secure operation identity cannot enter the raw projection path");

        const uint currentExperience = 4_000_000_000;
        var sealedResult =
            PacketBuilder.RawFighterLevelSealProjectionResult(
                npcId: 5_139,
                resultSubId: 106,
                RawProjectionToken,
                currentExperience,
                uint.MaxValue);
        CheckRawProjectionResult(
            sealedResult,
            expectedBytes:
                RawFighterLevelSealProjectionProtocol.ProjectionResultBytes,
            resultSubId: 106,
            currentExperience,
            uint.MaxValue,
            "raw seal");

        var unsealedResult =
            PacketBuilder.RawFighterLevelSealProjectionResult(
                npcId: 5_281,
                resultSubId: 107,
                RawProjectionToken,
                currentExperience: uint.MaxValue,
                maximumExperience: 117_174_640);
        CheckRawProjectionResult(
            unsealedResult,
            expectedBytes:
                RawFighterLevelSealProjectionProtocol.ProjectionResultBytes,
            resultSubId: 107,
            currentExperience: uint.MaxValue,
            maximumExperience: 117_174_640,
            "raw unseal");

        var rejectedResult =
            PacketBuilder.RawFighterLevelSealProjectionResult(
                npcId: 5_139,
                resultSubId: 109,
                RawProjectionToken);
        Check.True(
            rejectedResult.Length ==
                RawFighterLevelSealProjectionProtocol.OutcomeResultBytes &&
            ReadUInt16(rejectedResult, 0) == rejectedResult.Length &&
            ReadUInt32(rejectedResult, 16) ==
                RawFighterLevelSealProjectionProtocol.ResultMarker &&
            ReadUInt32(rejectedResult, 20) == RawProjectionToken,
            "raw no-op echoes correlation without an EXP projection");

        var stockResult = PacketBuilder.NpcFunctionActionResponse(
            5_139,
            116,
            106);
        Check.True(
            stockResult.Length ==
                RawFighterLevelSealProjectionProtocol.StockResultBytes,
            "stock raw client keeps the captured 16-byte result");
        Check.True(
            handler.Contains(
                "if (!_session.IsSecure &&",
                StringComparison.Ordinal) &&
            handler.Contains(
                "TryReadCapabilityToken(packet, out var capabilityToken)",
                StringComparison.Ordinal) &&
            handler.Contains(
                "RawFighterLevelSealProjectionResult(",
                StringComparison.Ordinal),
            "handler gates the legacy extension to opted-in raw sessions");
    }

    private static GamePacket BuildRawProjectionRequest(uint token)
    {
        var packet = new byte[
            RawFighterLevelSealProjectionProtocol.RequestBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 5_139);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), 116);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), token);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16), 102);
        for (var offset = 20; offset < packet.Length; offset += sizeof(int))
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(offset),
                -1);
        }
        return new GamePacket(packet);
    }

    private static void CheckRawProjectionResult(
        byte[] packet,
        int expectedBytes,
        int resultSubId,
        uint currentExperience,
        uint maximumExperience,
        string context)
    {
        Check.True(
            packet.Length == expectedBytes &&
            ReadUInt16(packet, 0) == expectedBytes &&
            ReadUInt16(packet, 2) == Opcodes.NpcFunctionActionResponse &&
            ReadUInt32(packet, 12) == checked((uint)resultSubId) &&
            ReadUInt32(packet, 16) ==
                RawFighterLevelSealProjectionProtocol.ResultMarker &&
            ReadUInt32(packet, 20) == RawProjectionToken &&
            ReadUInt32(packet, 24) == currentExperience &&
            ReadUInt32(packet, 28) == maximumExperience,
            $"{context} carries correlated authoritative Fighter EXP");
    }

    private static ushort ReadUInt16(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(offset));

    private static uint ReadUInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset));
}
