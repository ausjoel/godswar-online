using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerExperiencePillHandlerChecks
{
    public const string CheckName =
        "EXP Pill native activation projects current character EXP without replay gains or pet resets";
    private const int SourceSlot = 25;
    private const ushort ExperienceOpcode = 10031;
    private const ushort LevelUpOpcode = 10030;

    public static async Task RunAsync()
    {
        await CheckRawCommitAsync(10, finalPill: true);
        await CheckRawCommitAsync(120, finalPill: false);
        await CheckSecureReplayAsync(reusedSlot: false);
        await CheckSecureReplayAsync(reusedSlot: true);
        await CheckRejectionAsync(PetDurableReceiptStatus.PlayerExperienceMaximumReached);
        await CheckRejectionAsync(PetDurableReceiptStatus.ConsumableCooldownActive);
    }

    private static async Task CheckRawCommitAsync(int previousLevel, bool finalPill)
    {
        var evidence = Evidence(previousLevel);
        var before = Character(previousLevel, evidence.PreviousExperience, finalPill ? (short)1 : (short)2);
        var current = Character(evidence.NewLevel, evidence.NewExperience, finalPill ? (short)0 : (short)1);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope => PetDurableExecutionResult.Committed(Receipt(envelope, evidence))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(before, current, [], executor,
            hasLocalDevelopmentCapability: true, persistedStats: Stats(current),
            persistedProgression: Progression(current));
        await fixture.InvokeAsync(UsePacket());
        Check.Equal(1, executor.ActivateCount, "native EXP Pill click reaches durable activation once");
        Check.True(executor.ActivationEnvelope is { } envelope &&
            envelope.Command.KitBagSlot == SourceSlot && envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.OperationId != Guid.Empty &&
            envelope.Command.Identity.RawLocalConnectionId == envelope.Connection.ConnectionId,
            "opcode 10051 uses its authoritative bag slot and connection operation identity");

        var actual = fixture.ReadLegacyPackets();
        AssertBagAndVitals(actual, fixture.Handler, current, clearSlot: finalPill);
        AssertOnlyFrame(actual, PacketBuilder.ExperienceGain(1_000_000, current.Experience),
            "a first commit reports exactly one million gained EXP with the current EXP bar");
        Check.Equal(1, actual.Count(packet => Opcode(packet) == ExperienceOpcode),
            "only one character EXP result is sent per first commit");
        var levels = actual.Where(packet => Opcode(packet) == LevelUpOpcode).ToArray();
        Check.Equal(evidence.PreviousLevel == evidence.NewLevel ? 0 : 1, levels.Length,
            "only a first commit that crosses a level emits a level-up packet");
        if (levels.Length == 1)
        {
            var packet = levels[0];
            Check.True(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == current.Level &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16)) == current.Experience &&
                BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(20)) == current.MaxHp &&
                BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(24)) == current.CurrentHp &&
                BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(28)) == current.MaxMp &&
                BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(32)) == current.CurrentMp,
                "the level-up packet retains authoritative EXP and damaged HP/MP instead of healing");
        }
        AssertOnlyFrame(actual, PacketBuilder.ServerNote("EXP Pill granted 1,000,000 character EXP."),
            "a successful activation identifies the character EXP reward");
    }

    private static async Task CheckSecureReplayAsync(bool reusedSlot)
    {
        var oldEvidence = Evidence(10);
        var stale = Character(oldEvidence.NewLevel, oldEvidence.NewExperience, 0);
        // Later progression and source-slot reuse must survive a replay of this old receipt.
        var current = Character(150, 7_777_777, 0);
        if (reusedSlot)
            current.KitBag = KitBagSlots.SetSlot(current.KitBag, SourceSlot,
                (CompactItemEntry.Empty with { Id = 10133, Stack = 7, Bound = 1 }).ToCompactString());
        var operation = Guid.NewGuid();
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope => PetDurableExecutionResult.Duplicate(Receipt(envelope, oldEvidence))
        };
        await using var fixture = PetDurableHandlerFixture.Create(stale, current, [], executor,
            persistedStats: Stats(current), persistedProgression: Progression(current));
        await fixture.InvokeAsync(UsePacket(operation));
        var actual = fixture.Transport.ReadLegacyPackets();
        AssertBagAndVitals(actual, fixture.Handler, current, clearSlot: !reusedSlot);
        AssertOnlyFrame(actual, PacketBuilder.ExperienceGain(0, current.Experience),
            "a replay refreshes the latest EXP while reporting zero additional gain");
        Check.True(actual.Count(packet => Opcode(packet) == ExperienceOpcode) == 1 &&
            actual.All(packet => Opcode(packet) != LevelUpOpcode),
            "historical level changes cannot produce a second level-up or EXP reward");
        Check.True(fixture.Transport.CommandResults.Count == 1 &&
            fixture.Transport.CommandResults[0].OperationId == operation &&
            fixture.Transport.CommandResults[0].Disposition == SecureLegacyCommandDisposition.Replayed &&
            fixture.Transport.CommandResults[0].ResultCode == (uint)PetDurableReceiptStatus.PlayerExperienceAdded,
            "secure replay returns the original pill disposition and operation identity");
        Check.True(fixture.SavedVitals is null, "a replay does not persist invented healing or vitals changes");
    }

    private static async Task CheckRejectionAsync(PetDurableReceiptStatus status)
    {
        var level = status == PetDurableReceiptStatus.PlayerExperienceMaximumReached ? 200 : 120;
        var before = Character(level, 1234, 2);
        var current = Character(level, 1234, 2);
        var executor = new DelegatingPetDurableCommandExecutor
        {
            Activate = envelope => PetDurableExecutionResult.Rejected(Receipt(envelope, null, status))
        };
        await using var fixture = PetDurableRawHandlerFixture.Create(before, current, [], executor,
            hasLocalDevelopmentCapability: true, persistedStats: Stats(current),
            persistedProgression: Progression(current));
        await fixture.InvokeAsync(UsePacket());
        Check.Equal(1, executor.ActivateCount, "a rejected pill still uses the authoritative durable path");
        var actual = fixture.ReadLegacyPackets();
        AssertBagAndVitals(actual, fixture.Handler, current, clearSlot: false);
        Check.True(actual.All(packet => Opcode(packet) is not (ExperienceOpcode or LevelUpOpcode)),
            "cap and shared cooldown rejections emit no EXP or level-up success");
        var expectedNote = status == PetDurableReceiptStatus.ConsumableCooldownActive
            ? "The EXP Pill is cooling down. Please wait a moment."
            : "You cannot receive the full EXP reward at your current limit. Your EXP Pill was not consumed.";
        AssertOnlyFrame(actual, PacketBuilder.ServerNote(expectedNote),
            "shared cooldown status 96 is projected as an EXP Pill rejection for current item 4174");
    }

    private static void AssertBagAndVitals(IReadOnlyList<byte[]> packets, GameClientHandler handler,
        GameCharacter current, bool clearSlot)
    {
        foreach (var frame in PacketBuilder.KitBagDetailPages(current)
                     .Concat(PacketBuilder.KitBagSlotIndexes(current)))
            AssertOnlyFrame(packets, frame, "every inventory refresh frame matches the current bag exactly once");
        var deleted = PacketBuilder.StorageItemKitBagDelete(SourceSlot);
        Check.Equal(clearSlot ? 1 : 0, packets.Count(packet => packet.AsSpan().SequenceEqual(deleted)),
            "the consumed source slot is cleared only while the current snapshot still has it empty");
        Check.True(packets.All(packet => Opcode(packet) is not (10237 or Opcodes.PetExperience or
            Opcodes.PetOperationResult)), "a pill refresh never resets a companion through pet-list or pet EXP packets");
        var refreshed = typeof(GameClientHandler).GetField("_character",
            BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(handler) as GameCharacter;
        Check.True(refreshed is not null && refreshed.Level == current.Level &&
            refreshed.Experience == current.Experience && refreshed.CurrentHp == current.CurrentHp &&
            refreshed.CurrentMp == current.CurrentMp && refreshed.VitalsRevision == current.VitalsRevision,
            "the refreshed session keeps current progression, damaged vitals and the existing vitals revision");
    }

    private static void AssertOnlyFrame(IReadOnlyList<byte[]> packets, byte[] expected, string reason) =>
        Check.Equal(1, packets.Count(packet => packet.AsSpan().SequenceEqual(expected)), reason);

    private static PlayerExperienceItemEvidence Evidence(int level)
    {
        Check.True(PlayerExperienceItemPolicy.TryApply(level, 123, false, out var result),
            "the test pill grants its full million character EXP");
        return new(123, SourceSlot, 4174, 1_000_000, level, 123, false,
            result.Level, result.Experience, 7, 8);
    }

    private static PetDurableReceipt Receipt(CommandEnvelope<BagItemActivationCommand> envelope,
        PlayerExperienceItemEvidence? evidence,
        PetDurableReceiptStatus status = PetDurableReceiptStatus.PlayerExperienceAdded) =>
        new(CommandFamily.BagItemActivation, status, envelope.Subject.AccountId,
            envelope.Subject.CharacterId, envelope.Command.KitBagSlot, EquipmentSlot: -1,
            PetId: 0, PetLevel: 0, PetExperience: 0, PetRevision: 0, IsCarried: false,
            IsSummoned: false, PresenceOperation: 0, AggregateRevision: evidence is null ? 0 : 1,
            AuditReference: "experience-pill-handler-check", OutboxEventId: evidence is null ? null : Guid.NewGuid(),
            PlayerExperience: evidence);

    private static GameCharacter Character(int level, long experience, short stack) => new()
    {
        Id = 2, AccountId = 13, Name = "test2", Level = level, Experience = experience,
        Equipment = GameDefaults.DefaultEquipment(1), MaxHp = 9000, CurrentHp = 5432,
        MaxMp = 5000, CurrentMp = 2345, VitalsRevision = 17,
        KitBag = stack == 0 ? GameDefaults.EmptyKitBag : KitBagSlots.SetSlot(GameDefaults.EmptyKitBag,
            SourceSlot, (CompactItemEntry.Empty with { Id = 4174, Stack = stack, Bound = 1 }).ToCompactString())
    };

    private static CharacterCalculatedStatsSnapshot Stats(GameCharacter character) =>
        CharacterSnapshotContractChecks.CreateValidSnapshot().Character!.CalculatedStats with
        {
            Level = character.Level, MaxHp = character.MaxHp, CurrentHp = character.CurrentHp,
            MaxMp = character.MaxMp, CurrentMp = character.CurrentMp
        };

    private static CharacterProgressionSnapshot Progression(GameCharacter character) =>
        new(character.Level, character.Experience, 0, 0, 0, character.FighterLevelSealed, 8);

    private static GamePacket UsePacket(Guid? operation = null)
    {
        var packet = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 92);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Opcodes.BreakItem);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), SourceSlot / 24);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), SourceSlot % 24);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(72), uint.MaxValue);
        return new(packet, operation);
    }

    private static ushort Opcode(byte[] packet) => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
}
