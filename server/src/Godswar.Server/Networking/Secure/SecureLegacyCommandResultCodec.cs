using System.Buffers.Binary;

namespace Godswar.Server.Networking.Secure;

internal static class SecureLegacyCommandResultCodec
{
    public static bool TryEncode(
        in SecureLegacyCommandResult result,
        Span<byte> destination,
        out int bytesWritten)
    {
        bytesWritten = 0;
        var outputLength = result.FighterExperienceProjection.HasValue
            ? SecureProtocolConstants.LegacyCommandResultV2Bytes
            : SecureProtocolConstants.LegacyCommandResultBytes;
        if (!IsValid(result) ||
            destination.Length < outputLength)
        {
            return false;
        }

        var output = destination[..outputLength];
        output.Clear();
        output[0] = result.FighterExperienceProjection.HasValue
            ? SecureProtocolConstants.LegacyCommandResultV2Version
            : SecureProtocolConstants.LegacyCommandResultVersion;
        output[1] = (byte)result.Disposition;
        BinaryPrimitives.WriteUInt16BigEndian(
            output[2..],
            result.CommandFamily);
        BinaryPrimitives.WriteUInt32BigEndian(
            output[4..],
            result.ResultCode);
        BinaryPrimitives.WriteUInt64BigEndian(
            output[8..],
            result.AuthoritativeRevision);
        if (!result.OperationId.TryWriteBytes(
                output[16..],
                bigEndian: true,
                out var guidBytesWritten) ||
            guidBytesWritten != 16)
        {
            output.Clear();
            return false;
        }
        if (result.FighterExperienceProjection is { } projection)
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                output[32..],
                projection.CurrentExperience);
            BinaryPrimitives.WriteUInt32BigEndian(
                output[36..],
                projection.MaximumExperience);
        }

        bytesWritten = output.Length;
        return true;
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out SecureLegacyCommandResult result)
    {
        result = default;
        var isVersion1 =
            source.Length ==
                SecureProtocolConstants.LegacyCommandResultBytes &&
            source[0] ==
                SecureProtocolConstants.LegacyCommandResultVersion;
        var isVersion2 =
            source.Length ==
                SecureProtocolConstants.LegacyCommandResultV2Bytes &&
            source[0] ==
                SecureProtocolConstants.LegacyCommandResultV2Version;
        if (!isVersion1 && !isVersion2)
        {
            return false;
        }

        var disposition =
            (SecureLegacyCommandDisposition)source[1];
        var commandFamily =
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]);
        var authoritativeRevision =
            BinaryPrimitives.ReadUInt64BigEndian(source[8..]);
        var operationId = new Guid(source[16..32], bigEndian: true);
        if (!SecureProtocolValidation.IsLegacyCommandDisposition(
                disposition) ||
            commandFamily == 0 ||
            operationId == Guid.Empty ||
            disposition == SecureLegacyCommandDisposition.Applied &&
                authoritativeRevision == 0)
        {
            return false;
        }

        var resultCode =
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        if (isVersion2)
        {
            var currentExperience =
                BinaryPrimitives.ReadUInt32BigEndian(source[32..]);
            var maximumExperience =
                BinaryPrimitives.ReadUInt32BigEndian(source[36..]);
            if (maximumExperience == 0 ||
                !SecureLegacyCommandResult
                    .CanCarryFighterExperienceProjection(
                        disposition,
                        commandFamily,
                        resultCode,
                        authoritativeRevision))
            {
                return false;
            }

            result = new SecureLegacyCommandResult(
                disposition,
                commandFamily,
                resultCode,
                authoritativeRevision,
                operationId,
                new SecureFighterExperienceProjection(
                    currentExperience,
                    maximumExperience));
        }
        else
        {
            result = new SecureLegacyCommandResult(
                disposition,
                commandFamily,
                resultCode,
                authoritativeRevision,
                operationId);
        }
        return true;
    }

    private static bool IsValid(in SecureLegacyCommandResult result)
    {
        return SecureProtocolValidation.IsLegacyCommandDisposition(
                result.Disposition) &&
            result.CommandFamily != 0 &&
            result.OperationId != Guid.Empty &&
            (result.Disposition !=
                SecureLegacyCommandDisposition.Applied ||
                result.AuthoritativeRevision != 0) &&
            (result.FighterExperienceProjection is not { } projection ||
                projection.MaximumExperience != 0 &&
                SecureLegacyCommandResult
                    .CanCarryFighterExperienceProjection(
                        result.Disposition,
                        result.CommandFamily,
                        result.ResultCode,
                        result.AuthoritativeRevision));
    }
}
