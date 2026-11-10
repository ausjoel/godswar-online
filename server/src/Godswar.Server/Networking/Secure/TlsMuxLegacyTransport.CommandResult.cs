using System.Buffers;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransport
{
    public async ValueTask SendLegacyCommandResultAsync(
        SecureLegacyCommandResult result,
        CancellationToken cancellationToken)
    {
        if (_secureRole != SecureEndpointRole.Game ||
            _boundGamePrincipal is null)
        {
            throw new InvalidOperationException(
                "A legacy command result can be sent only by a bound secure game channel.");
        }
        if (!_authenticated.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "A legacy command result cannot be sent before game authentication.");
        }

        var payload = ArrayPool<byte>.Shared.Rent(
            SecureProtocolConstants.MaximumLegacyCommandResultBytes);
        var bytesWritten = 0;
        try
        {
            if (!SecureLegacyCommandResultCodec.TryEncode(
                    result,
                    payload,
                    out bytesWritten) ||
                bytesWritten is not (
                    SecureProtocolConstants.LegacyCommandResultBytes or
                    SecureProtocolConstants.LegacyCommandResultV2Bytes))
            {
                throw new ArgumentException(
                    "The bounded legacy command result is invalid.",
                    nameof(result));
            }

            await WriteControlFrameAsync(
                SecureFrameType.LegacyCommandResult,
                payload.AsMemory(0, bytesWritten),
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                payload.AsSpan(
                    0,
                    SecureProtocolConstants.MaximumLegacyCommandResultBytes));
            ArrayPool<byte>.Shared.Return(payload);
        }
    }
}
