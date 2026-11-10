using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.FactionCrier;

internal static class FactionCrierCommandEnvelope
{
    public const int DialogIndex = 15;
    public const int AthensNpcId = 5194;
    public const int CaptureAthensNpcId = 5193;
    public const int PublishedSpartaNpcId = 5052;
    public const int SourceSpartaNpcId = 5054;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumStateUtf8Bytes = 512;
    public const ushort CanonicalVersion = 1;

    private const int OperationScopeBytes = 33;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + sizeof(byte) +
        (sizeof(int) * 7) + SHA256.HashSizeInBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        FactionCrierOperationIdentity identity,
        int realmId,
        int npcId,
        int dialogIndex,
        int subId,
        int realmPeriodDayNumber,
        FactionCrierNameplateSelection? renewalSource,
        out FactionCrierCommand command)
    {
        command = new(
            identity,
            ResolveOperation(subId),
            realmId,
            npcId,
            dialogIndex,
            subId,
            realmPeriodDayNumber,
            renewalSource);
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<FactionCrierCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        FactionCrierCommand command)
    {
        if (!IsValidCommand(command) ||
            !HasMatchingProvenance(command.Identity, connection))
        {
            throw new ArgumentException(
                "The Faction Crier command or transport is invalid.",
                nameof(command));
        }

        return CommandEnvelopeContract.Create(
            CommandFamily.FactionCrier,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            CreateOperationScope(command.Identity),
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<FactionCrierCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.FactionCrier,
            envelope.Command.Identity.Strength,
            CreateOperationScope(envelope.Command.Identity),
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        FactionCrierOperationIdentity identity) =>
        CommandEnvelopeContract.DeriveOperationId(
            CommandFamily.FactionCrier,
            subject,
            CreateOperationScope(identity));

    public static FactionCrierOperation ResolveOperation(int subId) =>
        subId switch
        {
            1 => FactionCrierOperation.DailyClaim,
            >= 31 and <= 36 => FactionCrierOperation.WeeklyReclaim,
            >= 41 and <= 46 => FactionCrierOperation.RenewNameplate,
            >= 101 and <= 106 or >= 110 and <= 134 =>
                FactionCrierOperation.TurnIn,
            _ => 0
        };

    public static bool IsMutationSubId(int subId) =>
        ResolveOperation(subId) != 0;

    private static bool IsValidCommand(FactionCrierCommand command)
    {
        var expectedOperation = ResolveOperation(command.SubId);
        if (expectedOperation == 0 ||
            command.Operation != expectedOperation ||
            command.RealmId <= 0 ||
            command.DialogIndex != DialogIndex ||
            // NPCFIX (2026-11-07): the Athens crier reaches the client as the capture's id 5193
            // (FactionCrierProtocol.AthensNpcId); only 5194 was accepted here, so every Athens claim was refused.
            command.NpcId is not (
                AthensNpcId or CaptureAthensNpcId or PublishedSpartaNpcId or SourceSpartaNpcId) ||
            !IsValidIdentity(command.Identity))
        {
            return false;
        }

        if (command.Operation == FactionCrierOperation.RenewNameplate)
        {
            return command.RealmPeriodDayNumber == 0 &&
                command.RenewalSource is { } selection &&
                IsValidSelection(selection);
        }

        if (command.RenewalSource.HasValue)
        {
            return false;
        }

        return command.Operation is
                FactionCrierOperation.DailyClaim or
                FactionCrierOperation.WeeklyReclaim
            ? command.RealmPeriodDayNumber > 0
            : command.RealmPeriodDayNumber == 0;
    }

    private static bool IsValidSelection(
        FactionCrierNameplateSelection selection)
    {
        if (selection.KitBagSlot is < MinimumKitBagSlot or > MaximumKitBagSlot ||
            selection.ItemId is < FactionCrierRewardPolicy.FirstNameplateItemId or
                > FactionCrierRewardPolicy.LastNameplateItemId ||
            string.IsNullOrWhiteSpace(selection.ExpectedCompactItemState) ||
            selection.ExpectedCompactItemState.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(selection.ExpectedCompactItemState) <=
                MaximumStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsValidIdentity(
        FactionCrierOperationIdentity identity) =>
        identity.IsSecureClient || identity.IsRawLocalServer;

    private static bool HasMatchingProvenance(
        FactionCrierOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient
            ? connection.Transport is
                CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand
            : identity.IsRawLocalServer &&
                connection.Transport == CommandTransportKind.LegacyTcp &&
                identity.RawLocalConnectionId == connection.ConnectionId;

    private static byte[] CreateOperationScope(
        FactionCrierOperationIdentity identity)
    {
        var bytes = new byte[OperationScopeBytes];
        bytes[0] = (byte)identity.Strength;
        WriteGuid(identity.OperationId, bytes.AsSpan(1, 16));
        WriteGuid(identity.RawLocalConnectionId, bytes.AsSpan(17, 16));
        return bytes;
    }

    private static byte[] CreateCanonicalRequest(
        FactionCrierCommand command)
    {
        var bytes = new byte[CanonicalRequestBytes];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[2] = (byte)command.Operation;
        var offset = 3;
        WriteInt(command.RealmId);
        WriteInt(command.NpcId);
        WriteInt(command.DialogIndex);
        WriteInt(command.SubId);
        WriteInt(command.RealmPeriodDayNumber);
        WriteInt(command.RenewalSource?.KitBagSlot ?? -1);
        WriteInt(command.RenewalSource?.ItemId ?? 0);
        if (command.RenewalSource is { } selection)
        {
            var stateBytes = StrictUtf8.GetBytes(
                selection.ExpectedCompactItemState);
            SHA256.HashData(stateBytes, bytes.AsSpan(offset));
        }

        return bytes;

        void WriteInt(int value)
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
            throw new ArgumentException("The UUID could not be encoded.");
        }
    }
}
