using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Application.Pets;

internal readonly record struct BagItemActivationCommand(
    PetCommandOperationIdentity Identity,
    int KitBagSlot,
    BagItemActivationExecutionConstraint ExecutionConstraint =
        BagItemActivationExecutionConstraint.None,
    PetCaptureIntent? Capture = null)
{
    // Transient session observations never change the logical client request digest.
    public PotionUseContext? PotionContext { get; init; }

    public BagItemActivationCommand(
        Guid clientOperationId,
        int kitBagSlot,
        BagItemActivationExecutionConstraint executionConstraint =
            BagItemActivationExecutionConstraint.None)
        : this(
            PetCommandOperationIdentity.SecureClient(clientOperationId),
            kitBagSlot,
            executionConstraint)
    {
    }

    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal readonly record struct PetCaptureIntent(
    uint TargetObjectId,
    Guid TargetRuntimeInstanceId,
    uint TargetSpawnGeneration,
    ulong TargetHealthRevision,
    uint EggItemId,
    MedusaEncounterDifficulty Difficulty,
    PetCaptureContext Context = PetCaptureContext.Medusa)
{
    public bool IsValid =>
        TargetObjectId != 0 &&
        TargetRuntimeInstanceId != Guid.Empty &&
        TargetSpawnGeneration != 0 &&
        EggItemId != 0 &&
        (Context switch
        {
            PetCaptureContext.Medusa => Difficulty is MedusaEncounterDifficulty.Enhanced or
                MedusaEncounterDifficulty.Mythic,
            PetCaptureContext.AtlantisMerman => EggItemId == 10158 &&
                Difficulty == MedusaEncounterDifficulty.Normal,
            // Lelantine Farm dogs: Difficulty carries the dog tier (Normal/Enhanced/Mythic).
            PetCaptureContext.LelantineDog => EggItemId == 10159 &&
                Difficulty is MedusaEncounterDifficulty.Normal or MedusaEncounterDifficulty.Enhanced or
                    MedusaEncounterDifficulty.Mythic,
            // Wild baby pets (petcapture): Flower Pixie or Minotaur egg, no difficulty.
            PetCaptureContext.WildBaby => WildPetCapturePolicy.SpeciesForEgg(EggItemId) != 0 &&
                Difficulty == MedusaEncounterDifficulty.Normal,
            PetCaptureContext.AtlantisMermaidDew => AtlantisPetSpawnPolicy.IsMermaidDewItem(EggItemId) &&
                Difficulty == MedusaEncounterDifficulty.Normal,
            _ => false
        });
}

internal enum PetCaptureContext : byte
{
    Medusa = 0,
    AtlantisMerman = 1,
    LelantineDog = 2,
    WildBaby = 3,
    AtlantisMermaidDew = 4 // F-122: the "egg" item is a Morning Dew 10130-10134, no pet
}

/// <summary>
/// Server-observed execution state. This is deliberately excluded from the
/// client request hash: a retry must replay the first durable decision even
/// when the transient runtime observation has since changed.
/// </summary>
internal enum BagItemActivationExecutionConstraint : byte
{
    None = 0,
    RideRuntimeBlocked = 1,
    PlayerSkillBookOnly = 2,
    BatchUse = 3 // W2 Personal Helper (function 107): repeated uses skip the 1-second consumable cooldown
}

internal readonly record struct PetLevelUpgradeCommand(
    PetCommandOperationIdentity Identity,
    long PetId)
{
    public PetLevelUpgradeCommand(Guid clientOperationId, long petId)
        : this(
            PetCommandOperationIdentity.SecureClient(clientOperationId),
            petId)
    {
    }

    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal enum PetPresenceCommandOperation : byte
{
    Take = 0,
    CallOut = 1,
    Recall = 2,

    /// <summary>
    /// Permanently destroys the pet (native opcode 10238).
    /// </summary>
    /// <remarks>
    /// Appended rather than inserted: the enum's numeric value is persisted as
    /// the receipt's <c>PresenceOperation</c> (always value + 1), so the first
    /// three members must keep the ids earlier receipts already carry.
    /// </remarks>
    Delete = 3
}

internal readonly record struct PetPresenceTransitionCommand(
    PetCommandOperationIdentity Identity,
    long PetId,
    PetPresenceCommandOperation Operation)
{
    public PetPresenceTransitionCommand(
        Guid clientOperationId,
        long petId,
        PetPresenceCommandOperation operation)
        : this(
            PetCommandOperationIdentity.SecureClient(clientOperationId),
            petId,
            operation)
    {
    }

    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal readonly record struct PetSkillUnlearnCommand(
    PetCommandOperationIdentity Identity,
    int SkillSlot)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal enum PetGrowthResetOperation : byte
{
    Preview = 1,
    Accept = 2
}

internal readonly record struct PetGrowthResetCommand(
    PetCommandOperationIdentity Identity,
    PetGrowthResetOperation Operation = PetGrowthResetOperation.Preview,
    Guid PreviewOperationId = default)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal readonly record struct PetOwnerMergeToggleCommand(
    PetCommandOperationIdentity Identity)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal static class PetDurableCommandContract
{
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const long MaximumPetId = uint.MaxValue;
    private const ushort CanonicalVersion = 1;

    public static byte[] OperationScope(
        PetCommandOperationIdentity identity)
    {
        if (!identity.IsSecureClient &&
            !identity.IsRawLocalServer &&
            !identity.IsServerSessionLifecycle)
        {
            throw new ArgumentException(
                "A valid pet command operation identity is required.",
                nameof(identity));
        }

        if (identity.IsSecureClient)
        {
            var secure = new byte[16];
            WriteGuid(identity.OperationId, secure);
            return secure;
        }

        var raw = new byte[
            identity.IsServerSessionLifecycle ? 34 : 33];
        raw[0] = (byte)identity.Strength;
        var operationOffset = 1;
        if (identity.IsServerSessionLifecycle)
        {
            // Keep server-owned lifecycle identities domain-separated from
            // raw client compatibility identities at the durable inbox.
            raw[1] = 1;
            operationOffset = 2;
        }
        WriteGuid(
            identity.OperationId,
            raw.AsSpan(operationOffset, 16));
        WriteGuid(
            identity.ConnectionId,
            raw.AsSpan(operationOffset + 16, 16));
        return raw;
    }

    public static byte[] CanonicalBagActivation(
        int kitBagSlot,
        PetCaptureIntent? capture = null)
    {
        if (capture is { } captured)
        {
            var captureBytes = new byte[
                (sizeof(ushort) * 2) + 2 + sizeof(uint) + 16 +
                sizeof(uint) + sizeof(ulong) + sizeof(uint)];
            BinaryPrimitives.WriteUInt16BigEndian(
                captureBytes,
                CanonicalVersion);
            BinaryPrimitives.WriteUInt16BigEndian(
                captureBytes.AsSpan(sizeof(ushort)),
                checked((ushort)kitBagSlot));
            // Preserve every existing Medusa byte; new contexts receive an
            // explicit domain marker rather than overloading its difficulty.
            captureBytes[sizeof(ushort) * 2] = captured.Context switch
            {
                PetCaptureContext.Medusa => 1,
                PetCaptureContext.AtlantisMerman => 2,
                PetCaptureContext.LelantineDog => 3,
                PetCaptureContext.WildBaby => 4,
                PetCaptureContext.AtlantisMermaidDew => 5,
                _ => throw new ArgumentOutOfRangeException(nameof(capture))
            };
            captureBytes[(sizeof(ushort) * 2) + 1] =
                checked((byte)captured.Difficulty);
            var offset = (sizeof(ushort) * 2) + 2;
            BinaryPrimitives.WriteUInt32BigEndian(
                captureBytes.AsSpan(offset),
                captured.TargetObjectId);
            offset += sizeof(uint);
            WriteGuid(
                captured.TargetRuntimeInstanceId,
                captureBytes.AsSpan(offset, 16));
            offset += 16;
            BinaryPrimitives.WriteUInt32BigEndian(
                captureBytes.AsSpan(offset),
                captured.TargetSpawnGeneration);
            offset += sizeof(uint);
            BinaryPrimitives.WriteUInt64BigEndian(
                captureBytes.AsSpan(offset),
                captured.TargetHealthRevision);
            offset += sizeof(ulong);
            BinaryPrimitives.WriteUInt32BigEndian(
                captureBytes.AsSpan(offset),
                captured.EggItemId);
            return captureBytes;
        }

        var bytes = new byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            checked((ushort)kitBagSlot));
        return bytes;
    }

    public static byte[] CanonicalPet(long petId, byte operation)
    {
        var bytes = new byte[sizeof(ushort) + sizeof(long) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            petId);
        bytes[^1] = operation;
        return bytes;
    }

    public static byte[] CanonicalSkillSlot(int skillSlot)
    {
        var bytes = new byte[sizeof(ushort) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes.AsSpan(sizeof(ushort)),
            checked((ushort)skillSlot));
        return bytes;
    }

    public static byte[] CanonicalPetGrowthReset(
        PetGrowthResetOperation operation,
        Guid previewOperationId)
    {
        var bytes = new byte[sizeof(ushort) + 1 + 16];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[sizeof(ushort)] = checked((byte)operation);
        WriteGuid(
            previewOperationId,
            bytes.AsSpan(sizeof(ushort) + 1, 16));
        return bytes;
    }

    public static byte[] CanonicalPetBasicSavvyReset(
        PetBasicSavvyResetOperation operation,
        Guid previewOperationId)
    {
        var bytes = new byte[sizeof(ushort) + 1 + 16];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[sizeof(ushort)] = checked((byte)operation);
        WriteGuid(
            previewOperationId,
            bytes.AsSpan(sizeof(ushort) + 1, 16));
        return bytes;
    }

    public static byte[] CanonicalPetOwnerMergeToggle()
    {
        var bytes = new byte[sizeof(ushort) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[^1] = 1;
        return bytes;
    }

    public static byte[] CanonicalPetBind()
    {
        var bytes = new byte[sizeof(ushort) + 1];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, CanonicalVersion);
        bytes[^1] = 1;
        return bytes;
    }

    public static bool HasMatchingProvenance(
        PetCommandOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient
            ? connection.Transport is
                CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand
            : identity.IsRawLocalServer &&
              connection.Transport == CommandTransportKind.LegacyTcp &&
              identity.ConnectionId == connection.ConnectionId ||
              identity.IsServerSessionLifecycle &&
              connection.Transport is
                  CommandTransportKind.LegacyTcp or
                  CommandTransportKind.SecureTlsLegacy &&
              identity.ConnectionId == connection.ConnectionId;

    public static bool IsValidIdentity(
        PetCommandOperationIdentity identity) =>
        identity.IsSecureClient ||
        identity.IsRawLocalServer ||
        identity.IsServerSessionLifecycle;

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var written) ||
            written != destination.Length)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(value));
        }
    }
}
