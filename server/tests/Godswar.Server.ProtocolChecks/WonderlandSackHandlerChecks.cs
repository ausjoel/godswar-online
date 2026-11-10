using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class WonderlandSackHandlerChecks
{
    public const string CheckName = "Wonderland sack bag activation refreshes current inventory without pet resets";
    private const int SourceSlot = 25;
    private const int RewardSlot = 26;

    public static async Task RunAsync()
    {
        await CheckRawActivationAsync(full: false);
        await CheckRawActivationAsync(full: true);
        await CheckSecureDuplicateUsesCurrentBagAsync();
    }

    private static async Task CheckRawActivationAsync(bool full)
    {
        var before = Character(sackStack: 2, rewardStack: 0);
        var current = full ? Character(sackStack: 2, rewardStack: 0) : Character(sackStack: 1, rewardStack: 25);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope => full
                ? PetDurableExecutionResult.Rejected(Receipt(envelope, full: true))
                : PetDurableExecutionResult.Committed(Receipt(envelope, full: false))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(before, current, [], executor,
            hasLocalDevelopmentCapability: true);
        await fixture.InvokeAsync(UsePacket());
        Check.Equal(1, executor.ActivateCount, "native opcode10051 sack click reaches durable activation once");
        Check.True(executor.ActivationEnvelope is { } envelope &&
            envelope.Command.KitBagSlot == SourceSlot && envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId == envelope.Connection.ConnectionId,
            "the raw sack request binds its source slot and connection-specific operation identity");
        AssertCurrentBag(fixture.ReadLegacyPackets(), current, full ? "full bag" : "opened sack");
        var acquisitions = fixture.ReadLegacyPackets().Where(packet => Opcode(packet) == 10185).ToArray();
        Check.True(acquisitions.Length == (full ? 0 : 1) && acquisitions.All(packet =>
                packet.Length == 80 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 10133 &&
                packet[34] == 1 && packet[35] == 25),
            "only a newly committed sack opening shows its exact bound reward through the daily item notification");
        var notes = fixture.ReadLegacyPackets().Where(packet => Opcode(packet) == Opcodes.ServerNote).ToArray();
        Check.True(full ? notes.Length == 1 && notes[0].SequenceEqual(PacketBuilder.ServerNote(
                "Make room in your bag for any possible sack reward, then try again. Your sack was not consumed."))
                : notes.Length == 0,
            "successful sack opening stays silent while a full bag retains its useful failure message");
    }

    private static async Task CheckSecureDuplicateUsesCurrentBagAsync()
    {
        // The old grant was 25 Dew, but later commands consumed all but seven.
        // Replaying its receipt must project seven, never reconstruct 25.
        var stale = Character(sackStack: 1, rewardStack: 25);
        var current = Character(sackStack: 0, rewardStack: 7);
        var operation = Guid.NewGuid();
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope => PetDurableExecutionResult.Duplicate(Receipt(envelope, full: false))
        };
        await using var fixture = PetDurableHandlerFixture.Create(stale, current, [], executor);
        await fixture.InvokeAsync(UsePacket(operation));
        Check.Equal(1, executor.ActivateCount, "secure duplicate is resolved through its durable operation");
        AssertCurrentBag(fixture.Transport.ReadLegacyPackets(), current, "duplicate sack receipt");
        Check.True(fixture.Transport.ReadLegacyPackets().All(packet => Opcode(packet) is not (Opcodes.ServerNote or 10185)) &&
            fixture.Transport.ReadLegacyPackets().Count(packet => packet.SequenceEqual(
                PacketBuilder.StorageItemKitBagDelete(SourceSlot))) == 1,
            "successful sack replay silently clears the consumed slot and refreshes current inventory");
        Check.True(fixture.Transport.CommandResults.Count == 1 &&
            fixture.Transport.CommandResults[0].OperationId == operation &&
            fixture.Transport.CommandResults[0].Disposition == SecureLegacyCommandDisposition.Replayed &&
            fixture.Transport.CommandResults[0].ResultCode == (uint)PetDurableReceiptStatus.WonderlandSackOpened,
            "secure duplicate terminates with the original sack result and operation identity");
    }

    private static void AssertCurrentBag(IReadOnlyList<byte[]> actual, GameCharacter current, string reason)
    {
        var expected = PacketBuilder.KitBagDetailPages(current)
            .Concat(PacketBuilder.KitBagSlotIndexes(current)).ToArray();
        foreach (var packet in expected)
            Check.True(actual.Count(candidate => candidate.AsSpan().SequenceEqual(packet)) == 1,
                $"{reason} sends each authoritative inventory frame exactly once");
        Check.True(actual.All(packet => Opcode(packet) is not (Opcodes.PythonNote or 10237 or Opcodes.PetExperience or
            Opcodes.PetOperationResult)),
            $"{reason} emits no pet list, pet EXP or presence result that could reset a merged companion");
    }

    private static PetDurableReceipt Receipt(CommandEnvelope<BagItemActivationCommand> envelope, bool full) =>
        new(CommandFamily.BagItemActivation,
            full ? PetDurableReceiptStatus.WonderlandSackBagFull : PetDurableReceiptStatus.WonderlandSackOpened,
            envelope.Subject.AccountId, envelope.Subject.CharacterId, envelope.Command.KitBagSlot,
            EquipmentSlot: -1, PetId: 0, PetLevel: 0, PetExperience: 0, PetRevision: 0,
            IsCarried: false, IsSummoned: false, PresenceOperation: 0, AggregateRevision: full ? 0 : 1,
            AuditReference: "wonderland-sack-handler-check", OutboxEventId: full ? null : Guid.NewGuid(),
            WonderlandSack: full ? null : new WonderlandSackOpenEvidence(123, 4450, SourceSlot,
                WonderlandSackRewardPolicy.Revision, new string('A', 64), 0, 82, 0, 10133, 25, 1));

    private static GameCharacter Character(short sackStack, short rewardStack)
    {
        var bag = GameDefaults.EmptyKitBag;
        if (sackStack > 0)
            bag = KitBagSlots.SetSlot(bag, SourceSlot,
                (CompactItemEntry.Empty with { Id = 4450, Stack = sackStack, Bound = 1 }).ToCompactString());
        if (rewardStack > 0)
            bag = KitBagSlots.SetSlot(bag, RewardSlot,
                (CompactItemEntry.Empty with { Id = 10133, Stack = rewardStack, Bound = 1 }).ToCompactString());
        return new GameCharacter { Id = 2, AccountId = 13, Name = "test2", KitBag = bag,
            Equipment = GameDefaults.DefaultEquipment(1) };
    }

    private static GamePacket UsePacket(Guid? operation = null)
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.BreakItem);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), SourceSlot / 24);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), SourceSlot % 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), uint.MaxValue);
        return new GamePacket(bytes, operation);
    }

    private static ushort Opcode(byte[] packet) => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
}
