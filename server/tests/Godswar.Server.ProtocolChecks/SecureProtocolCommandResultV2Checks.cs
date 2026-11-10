using System.Buffers.Binary;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureProtocolCodecChecks
{
    private static void CheckFighterLevelSealResultV2(Guid operationId)
    {
        var sealedResult = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Applied,
            SecureProtocolConstants.FighterLevelSealCommandFamily,
            SecureProtocolConstants.FighterLevelSealedResultCode,
            authoritativeRevision: 0x0102030405060708,
            operationId,
            new SecureFighterExperienceProjection(
                currentExperience: 0xFEDCBA98,
                maximumExperience: uint.MaxValue));
        var encoded = new byte[
            SecureProtocolConstants.LegacyCommandResultV2Bytes];
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                sealedResult,
                encoded,
                out var written) &&
            written == encoded.Length,
            "sealed fighter command result encodes as v2");
        var expected = Convert.FromHexString(
            "0201003C0000006A0102030405060708" +
            "00112233445566778899AABBCCDDEEFF" +
            "FEDCBA98FFFFFFFF");
        Check.True(
            encoded.SequenceEqual(expected),
            "fighter EXP result v2 has canonical network byte order");
        Check.True(
            SecureLegacyCommandResultCodec.TryDecode(
                encoded,
                out var decoded) &&
            decoded == sealedResult,
            "sealed fighter EXP projection round trips");

        const uint normalLevelMaximum = 117_174_640;
        var unsealedResult = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Replayed,
            SecureProtocolConstants.FighterLevelSealCommandFamily,
            SecureProtocolConstants.FighterLevelUnsealedResultCode,
            authoritativeRevision: 12,
            operationId,
            new SecureFighterExperienceProjection(
                currentExperience: uint.MaxValue,
                maximumExperience: normalLevelMaximum));
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                unsealedResult,
                encoded,
                out written) &&
            written == encoded.Length &&
            SecureLegacyCommandResultCodec.TryDecode(
                encoded,
                out decoded) &&
            decoded == unsealedResult &&
            decoded.FighterExperienceProjection is
            {
                CurrentExperience: uint.MaxValue,
                MaximumExperience: normalLevelMaximum
            },
            "unseal v2 preserves legitimate over-threshold current EXP");
        var v2Canonical = (byte[])encoded.Clone();

        Check.True(
            !SecureLegacyCommandResultCodec.TryEncode(
                sealedResult,
                new byte[
                    SecureProtocolConstants.LegacyCommandResultV2Bytes - 1],
                out written) &&
            written == 0,
            "fighter EXP result rejects a short destination");

        var legacySealedResult = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Applied,
            SecureProtocolConstants.FighterLevelSealCommandFamily,
            SecureProtocolConstants.FighterLevelSealedResultCode,
            authoritativeRevision: 1,
            operationId);
        Check.True(
            SecureLegacyCommandResultCodec.TryEncode(
                legacySealedResult,
                encoded,
                out written) &&
            written == SecureProtocolConstants.LegacyCommandResultBytes &&
            SecureLegacyCommandResultCodec.TryDecode(
                encoded.AsSpan(0, written),
                out decoded) &&
            decoded == legacySealedResult &&
            decoded.FighterExperienceProjection is null,
            "pre-extension Level Sealer v1 results remain decodable");

        CheckFighterLevelSealResultV2Rejections(v2Canonical, operationId);
        CheckFighterLevelSealResultV2FrameContext();
    }

    private static void CheckFighterLevelSealResultV2Rejections(
        byte[] canonical,
        Guid operationId)
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new SecureFighterExperienceProjection(1, 0),
            "fighter EXP projection rejects a zero maximum");
        Check.Throws<ArgumentException>(
            () => _ = new SecureLegacyCommandResult(
                SecureLegacyCommandDisposition.Applied,
                commandFamily: 59,
                SecureProtocolConstants.FighterLevelSealedResultCode,
                authoritativeRevision: 1,
                operationId,
                new SecureFighterExperienceProjection(1, 2)),
            "other command families cannot carry fighter EXP");
        Check.Throws<ArgumentException>(
            () => _ = new SecureLegacyCommandResult(
                SecureLegacyCommandDisposition.Rejected,
                SecureProtocolConstants.FighterLevelSealCommandFamily,
                SecureProtocolConstants.FighterLevelSealedResultCode,
                authoritativeRevision: 1,
                operationId,
                new SecureFighterExperienceProjection(1, 2)),
            "rejected Level Sealer outcomes cannot carry fighter EXP");
        Check.Throws<ArgumentException>(
            () => _ = new SecureLegacyCommandResult(
                SecureLegacyCommandDisposition.Replayed,
                SecureProtocolConstants.FighterLevelSealCommandFamily,
                resultCode: 109,
                authoritativeRevision: 1,
                operationId,
                new SecureFighterExperienceProjection(1, 2)),
            "Level Sealer no-op results cannot carry fighter EXP");

        var malformedCases = new List<(string Name, Action<byte[]> Mutate)>
        {
            ("wrong version", bytes => bytes[0] = 1),
            ("rejected disposition", bytes => bytes[1] = 3),
            ("wrong family", bytes =>
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 59)),
            ("no-op result", bytes =>
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 109)),
            ("zero revision", bytes => bytes.AsSpan(8, 8).Clear()),
            ("zero maximum", bytes => bytes.AsSpan(36, 4).Clear())
        };
        foreach (var malformedCase in malformedCases)
        {
            var malformed = (byte[])canonical.Clone();
            malformedCase.Mutate(malformed);
            Check.True(
                !SecureLegacyCommandResultCodec.TryDecode(
                    malformed,
                    out _),
                $"fighter EXP v2 {malformedCase.Name} rejects");
        }

        Check.True(
            !SecureLegacyCommandResultCodec.TryDecode(
                canonical.AsSpan(0, canonical.Length - 1),
                out _),
            "fighter EXP v2 requires its exact bounded length");
    }

    private static void CheckFighterLevelSealResultV2FrameContext()
    {
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultV2Bytes,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: true);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultV2Bytes,
            SecureEndpointRole.Game,
            SecureFrameDirection.ClientToServer,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultV2Bytes - 1,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: false);
        CheckHeaderContext(
            SecureFrameType.LegacyCommandResult,
            SecureProtocolConstants.LegacyCommandResultV2Bytes + 1,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expected: false);
    }
}
