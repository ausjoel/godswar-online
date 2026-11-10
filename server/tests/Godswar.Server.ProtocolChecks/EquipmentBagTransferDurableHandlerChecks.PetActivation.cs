using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private const uint PlayerSkillBookItemId = 5_019;
    private static readonly CompactItemEntry PlayerSkillBookItem =
        CompactItemEntry.Empty with
        {
            Id = PlayerSkillBookItemId,
            Bound = 1,
            Stack = 1
        };
    private static readonly GameplayRuntimeCatalogs
        PlayerSkillBookGameplayCatalogs =
            GameplayRuntimeCatalogs.Empty with
            {
                Content = GameplayContentCatalog.Empty with
                {
                    SkillBooks =
                    [
                        new GameplaySkillBookDefinition(
                            checked((int)PlayerSkillBookItemId),
                            "skill_blood_claw_1_book",
                            "Blood Claw I",
                            SkillId: 50,
                            BaseName: "Blood Claw",
                            SkillLevel: 1,
                            ClassIds: [0],
                            MinLevel: 4,
                            MaxLevel: null,
                            PreviousSkillId: null,
                            StatsJson: "{}")
                    ]
                }
            };

    private static async Task
        CheckActiveRideBlocksDurableRightClickMountAsync()
    {
        var petExecutor = new PetActivationExecutor();
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            // Deliberately stale: the live bag projection contains a weapon,
            // while the durable executor represents the locked DB item as a
            // mount. Runtime policy must not classify from this cache.
            liveState: UnequipAfterState,
            persistedState: UnequipAfterState,
            equipmentSlot: EquipmentSlots.Mount,
            petDurableCommands: petExecutor);
        await ActivateRideRuntimeStatusAsync(fixture);

        await InvokePacketAsync(
            fixture.Handler,
            CreateBreakItemPacket(OperationId));

        Check.Equal(
            1,
            petExecutor.ExecuteCount,
            "Ride-active right-click decision reaches durable persistence");
        Check.True(
            petExecutor.ExecutedCommand?.ExecutionConstraint ==
                BagItemActivationExecutionConstraint
                    .RideRuntimeBlocked,
            "Ride observation is bound independently of stale cached item classification");
        Check.Equal(
            1,
            fixture.Transport.CommandResults.Count,
            "Ride-active right-click mount replacement terminates once");
        var result = fixture.Transport.CommandResults[0];
        Check.True(
            result.Disposition ==
                SecureLegacyCommandDisposition.Rejected &&
            result.CommandFamily ==
                (ushort)CommandFamily.BagItemActivation &&
            result.ResultCode ==
                (uint)PetDurableReceiptStatus.EquipmentRestricted &&
            result.AuthoritativeRevision == 0 &&
            result.OperationId == OperationId,
            "Ride-active right-click mount replacement returns a finite family-26 rejection");
    }

    private static async Task
        CheckPlayerSkillBookBagSlotPacketOnlyAcknowledgesAsync()
    {
        var petExecutor = new PetActivationExecutor();
        await using var fixture = CreateLegacyRawFixture(
            hasLocalLegacyAuthenticationAccess: true,
            petDurableCommands: petExecutor,
            activationState: new TransferSlotState(
                CompactItemEntry.Empty,
                PlayerSkillBookItem),
            gameplayCatalogs: PlayerSkillBookGameplayCatalogs);
        var request = CreateBagItemActionPacket();

        await InvokePacketAsync(fixture.Handler, request);

        Check.Equal(
            0,
            petExecutor.ExecuteCount,
            "opcode 10056 player-book slot projection never activates the book");
        Check.Equal(
            0,
            fixture.Store.EquipCount,
            "opcode 10056 player-book slot projection never downgrades to equip");
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Count == 1 &&
            packets[0].AsSpan().SequenceEqual(request.Buffer),
            "opcode 10056 player-book slot projection receives only its native echo acknowledgement");
        Check.True(
            !fixture.Transport.Disconnected,
            "opcode 10056 player-book slot projection keeps the session connected");
    }

    private static async Task
        CheckLocalRawPlayerSkillBookBindsConstraintAsync()
    {
        var petExecutor = new PetActivationExecutor();
        await using var fixture = CreateLegacyRawFixture(
            hasLocalLegacyAuthenticationAccess: true,
            petDurableCommands: petExecutor,
            activationState: new TransferSlotState(
                CompactItemEntry.Empty,
                PlayerSkillBookItem),
            gameplayCatalogs: PlayerSkillBookGameplayCatalogs);

        await InvokePacketAsync(
            fixture.Handler,
            CreateBreakItemPacket());

        Check.Equal(
            1,
            petExecutor.ExecuteCount,
            "opcode 10051 player skill book reaches durable persistence");
        Check.True(
            petExecutor.ExecutedCommand is
            {
                ExecutionConstraint:
                    BagItemActivationExecutionConstraint
                        .PlayerSkillBookOnly,
                KitBagSlot: KitBagSlot
            },
            "opcode 10051 player skill-book classification is bound as a fail-closed " +
            "execution constraint");
        Check.Equal(
            0,
            fixture.Store.EquipCount,
            "player skill-book activation cannot downgrade into compatibility equip");
        Check.True(
            !fixture.Transport.Disconnected,
            "validated local raw player skill-book activation keeps the session connected");
    }

    private static GamePacket CreateBagItemActionPacket()
    {
        const int packetLength = 40;
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            Opcodes.BagItemAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)),
            0x0000_1448);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8, sizeof(ushort)),
            checked((ushort)(KitBagSlot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10, sizeof(ushort)),
            checked((ushort)(KitBagSlot % 24)));
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16, sizeof(int)),
            KitBagSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, sizeof(uint)),
            PlayerSkillBookItemId);
        return new GamePacket(packet);
    }

    private sealed class PetActivationExecutor :
        IPetDurableCommandExecutor
    {
        public int ExecuteCount { get; private set; }
        public BagItemActivationCommand? ExecutedCommand
        { get; private set; }

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<BagItemActivationCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(
                PetDurableExecutionResult.Rejected(
                    new PetDurableReceipt(
                        CommandFamily.BagItemActivation,
                        PetDurableReceiptStatus.EquipmentRestricted,
                        envelope.Subject.AccountId,
                        envelope.Subject.CharacterId,
                        envelope.Command.KitBagSlot,
                        EquipmentSlots.Mount,
                        PetId: 0,
                        PetLevel: 0,
                        PetExperience: 0,
                        PetRevision: 0,
                        IsCarried: false,
                        IsSummoned: false,
                        PresenceOperation: 0,
                        AggregateRevision: 0,
                        AuditReference: "ride-runtime-check",
                        OutboxEventId: null)));
        }

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetLevelUpgradeCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot upgrade pets.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetPresenceTransitionCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot change pet presence.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetSkillUnlearnCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot unlearn pet skills.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetGrowthResetCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot reset pet growth.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetBasicSavvyResetCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot reset pet Basic Savvy.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetOwnerMergeToggleCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot toggle owner Merge.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetToPetMergeCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot merge pets.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetRebirthCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot rebirth pets.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetAppearanceChangeCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot change pet appearance.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetBindCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot bind pets.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetSoulContractCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot sign Soul Contracts.");

        public Task<PetDurableExecutionResult> ExecuteAsync(
            CommandEnvelope<PetManagerUtilityCommand> envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The right-click mount check cannot use Pet Manager utilities.");
    }

}
