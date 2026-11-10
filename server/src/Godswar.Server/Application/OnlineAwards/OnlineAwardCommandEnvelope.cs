using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Application.OnlineAwards;

internal static class OnlineAwardCommandEnvelope
{
    public const ushort CanonicalVersion = 1;
    private const int ScopeBytes = 33;

    public static bool TryCreate(
        OnlineAwardOperationIdentity identity,
        int realmId,
        int npcId,
        int dialogIndex,
        int claimDayNumber,
        out OnlineAwardCommand command)
    {
        command = new(
            identity,
            realmId,
            npcId,
            dialogIndex,
            claimDayNumber);
        if (IsValid(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<OnlineAwardCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        OnlineAwardCommand command)
    {
        if (!IsValid(command) || !HasMatchingProvenance(command, connection))
        {
            throw new ArgumentException(
                "The Online Award command is invalid.",
                nameof(command));
        }

        return CommandEnvelopeContract.Create(
            CommandFamily.OnlineAward,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            CreateScope(command.Identity),
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<OnlineAwardCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValid(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!HasMatchingProvenance(envelope.Command, envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.OnlineAward,
            envelope.Command.Identity.Strength,
            CreateScope(envelope.Command.Identity),
            CreateCanonicalRequest(envelope.Command));
    }

    private static bool IsValid(OnlineAwardCommand command) =>
        command.RealmId > 0 &&
        OnlineAwardProtocol.IsEndpoint(
            checked((uint)command.NpcId),
            command.DialogIndex) &&
        command.ClaimDayNumber > 0 &&
        (command.Identity.IsSecureClient ||
         command.Identity.IsRawLocalServer);

    private static bool HasMatchingProvenance(
        OnlineAwardCommand command,
        CommandConnectionCorrelation connection) =>
        command.Identity.IsSecureClient
            ? connection.Transport is CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand
            : command.Identity.IsRawLocalServer &&
                connection.Transport == CommandTransportKind.LegacyTcp &&
                command.Identity.RawLocalConnectionId == connection.ConnectionId;

    private static byte[] CreateScope(OnlineAwardOperationIdentity identity)
    {
        var bytes = new byte[ScopeBytes];
        bytes[0] = (byte)identity.Strength;
        WriteGuid(identity.OperationId, bytes.AsSpan(1, 16));
        WriteGuid(identity.RawLocalConnectionId, bytes.AsSpan(17, 16));
        return bytes;
    }

    private static byte[] CreateCanonicalRequest(OnlineAwardCommand command)
    {
        var bytes = new byte[sizeof(ushort) + sizeof(int) * 4];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        var offset = sizeof(ushort);
        Write(command.RealmId);
        // The secure shim deliberately retains one character-scoped UUID
        // across the equivalent Athens and Sparta endpoints. Endpoint
        // validity is checked before hashing; normalize the city identity so
        // a lost-result retry after a map transfer canonically replays.
        Write(0);
        Write(command.DialogIndex);
        Write(command.ClaimDayNumber);
        return bytes;

        void Write(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                bytes.AsSpan(offset, sizeof(int)),
                value);
            offset += sizeof(int);
        }
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out var written) ||
            written != 16)
        {
            throw new ArgumentException("The operation identity is invalid.");
        }
    }
}
