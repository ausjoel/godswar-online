using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetDurableCommandContractChecks
{
    public static Task RunAsync()
    {
        CheckBagIdentity();
        CheckPetIdentities();
        CheckPetSkillUnlearnIdentity();
        CheckRawLocalIdentities();
        CheckReceiptRoundTrip();
        CheckAppearanceChangeContract();
        CheckPetBindContract();
        CheckGrowthPreviewReceiptRoundTrip();
        CheckRebirthGrowthReceiptRoundTrip();
        CheckHatchRankReceiptRoundTrip();
        CheckLegacyPetGrowthReceipt();
        CheckPetShedReceipts();
        CheckPresenceProjectionReconciliation();
        CheckInventoryProjectionRoundTrip();
        CheckMigration();
        return Task.CompletedTask;
    }

    private static void CheckPresenceProjectionReconciliation()
    {
        var historicalCallOut = new PetDurableReceipt(
            CommandFamily.PetPresenceTransition,
            PetDurableReceiptStatus.PresenceChanged,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: -1,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 20,
            PetExperience: 100,
            PetRevision: 5,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 2,
            AggregateRevision: 4,
            AuditReference: "presence-projection-check",
            OutboxEventId: Guid.NewGuid());
        Check.Equal(
            (int)PetOperationResultCode.CallOutSucceeded,
            (int)GameClientHandler.ResolveAuthoritativePresenceResult(
                historicalCallOut,
                currentPetExists: true,
                currentIsCarried: true,
                currentIsSummoned: true),
            "matching current presence preserves the original result");
        Check.Equal(
            (int)PetOperationResultCode.RecallSucceeded,
            (int)GameClientHandler.ResolveAuthoritativePresenceResult(
                historicalCallOut,
                currentPetExists: true,
                currentIsCarried: true,
                currentIsSummoned: false),
            "old CallOut retry presents a later authoritative Recall");
    }

    private static void CheckInventoryProjectionRoundTrip()
    {
        var receipt = new PetBagActivationInventoryReceipt(
            PetBagActivationInventoryPersistenceCodec
                .MaximumLedgerEntryCount,
            7,
            2,
            Guid.NewGuid());
        var decoded =
            PetBagActivationInventoryPersistenceCodec.Decode(
                PetBagActivationInventoryPersistenceCodec.Encode(
                    receipt));
        Check.Equal(
            receipt,
            decoded,
            "pet bag inventory event canonical round trip");
        Check.Throws<InvalidDataException>(
            () => PetBagActivationInventoryPersistenceCodec.Encode(
                receipt with
                {
                    LedgerEntryCount =
                        PetBagActivationInventoryPersistenceCodec
                            .MaximumLedgerEntryCount + 1
                }),
            "pet bag inventory event bounds its ledger count");
    }

    private static void CheckBagIdentity()
    {
        var operation = Guid.NewGuid();
        var command = new BagItemActivationCommand(
            PetCommandOperationIdentity.SecureClient(operation),
            31);
        var envelope = BagItemActivationCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            command);
        var retry = BagItemActivationCommandEnvelope.Create(
            Subject(),
            Correlation(),
            envelope.ReceivedAt.AddSeconds(1),
            command);
        var changedObservation =
            BagItemActivationCommandEnvelope.Create(
                Subject(),
                Correlation(),
                envelope.ReceivedAt.AddSeconds(2),
                command with
                {
                    ExecutionConstraint =
                        BagItemActivationExecutionConstraint
                            .RideRuntimeBlocked
                });
        var playerSkillBookOnlyObservation =
            BagItemActivationCommandEnvelope.Create(
                Subject(),
                Correlation(),
                envelope.ReceivedAt.AddSeconds(3),
                command with
                {
                    ExecutionConstraint =
                        BagItemActivationExecutionConstraint
                            .PlayerSkillBookOnly
                });
        Check.True(
            BagItemActivationCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "bag activation command validates");
        Span<byte> legacyOperationScope = stackalloc byte[16];
        Check.True(
            operation.TryWriteBytes(
                legacyOperationScope,
                bigEndian: true,
                out var written) &&
            written == legacyOperationScope.Length,
            "secure pet operation UUID has its legacy wire shape");
        Check.Equal(
            CommandEnvelopeContract.DeriveOperationId(
                CommandFamily.BagItemActivation,
                Subject(),
                legacyOperationScope),
            envelope.OperationId,
            "secure pet operation ID preserves the pre-raw identity digest");
        Check.Equal(
            envelope.OperationId,
            retry.OperationId,
            "bag activation UUID is stable across retries");
        Check.Equal(
            envelope.RequestHash,
            retry.RequestHash,
            "bag activation request does not depend on an item hint");
        Check.Equal(
            envelope.RequestHash,
            changedObservation.RequestHash,
            "transient Ride observation does not change retry identity");
        Check.Equal(
            envelope.RequestHash,
            playerSkillBookOnlyObservation.RequestHash,
            "server-classified player skill-book routing does not change " +
            "retry identity");
        Check.True(
            BagItemActivationCommandEnvelope.Validate(
                changedObservation) ==
                CommandEnvelopeValidation.Valid,
            "server-derived Ride observation validates");
        Check.True(
            BagItemActivationCommandEnvelope.Validate(
                playerSkillBookOnlyObservation) ==
                CommandEnvelopeValidation.Valid &&
            PetDurableCommandContract.CanonicalBagActivation(31).Length ==
                sizeof(ushort) * 2,
            "player skill-book-only routing validates without changing " +
            "the historical four-byte canonical command");
    }

    private static void CheckPetIdentities()
    {
        var operation = Guid.NewGuid();
        var level = PetLevelUpgradeCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            new PetLevelUpgradeCommand(
                PetCommandOperationIdentity.SecureClient(operation),
                7));
        var call = PetPresenceTransitionCommandEnvelope.Create(
            Subject(),
            Correlation(),
            DateTimeOffset.UtcNow,
            new PetPresenceTransitionCommand(
                PetCommandOperationIdentity.SecureClient(operation),
                7,
                PetPresenceCommandOperation.CallOut));
        Check.True(
            PetLevelUpgradeCommandEnvelope.Validate(level) ==
                CommandEnvelopeValidation.Valid,
            "pet level command validates");
        Check.True(
            PetPresenceTransitionCommandEnvelope.Validate(call) ==
                CommandEnvelopeValidation.Valid,
            "pet presence command validates");
        Check.True(
            level.OperationId != call.OperationId,
            "command families domain-separate one UUID");
        Check.Throws<ArgumentException>(
            () => PetLevelUpgradeCommandEnvelope.Create(
                Subject(),
                Correlation(),
                DateTimeOffset.UtcNow,
                new PetLevelUpgradeCommand(
                    PetCommandOperationIdentity.SecureClient(Guid.Empty),
                    7)),
            "empty pet operation UUID is rejected");
    }

    private static void CheckPetSkillUnlearnIdentity()
    {
        var operation = Guid.NewGuid();
        var subject = Subject();
        var correlation = Correlation();
        var command = new PetSkillUnlearnCommand(
            PetCommandOperationIdentity.SecureClient(operation),
            SkillSlot: 11);
        var envelope = PetSkillUnlearnCommandEnvelope.Create(
            subject,
            correlation,
            DateTimeOffset.UtcNow,
            command);
        var retry = PetSkillUnlearnCommandEnvelope.Create(
            subject,
            correlation,
            envelope.ReceivedAt.AddSeconds(1),
            command);

        Check.True(
            PetSkillUnlearnCommandEnvelope.Validate(envelope) ==
                CommandEnvelopeValidation.Valid,
            "pet skill-unlearn command validates at the twelfth native slot");
        Check.True(
            envelope.Family == CommandFamily.PetSkillUnlearn &&
            envelope.OperationId == retry.OperationId &&
            envelope.RequestHash == retry.RequestHash,
            "pet skill-unlearn retry identity is stable and family-scoped");
        Check.True(
            PetSkillUnlearnCommandEnvelope.Validate(
                envelope with
                {
                    Command = command with { SkillSlot = -1 }
                }) == CommandEnvelopeValidation.InvalidCommand &&
            PetSkillUnlearnCommandEnvelope.Validate(
                envelope with
                {
                    Command = command with { SkillSlot = 12 }
                }) == CommandEnvelopeValidation.InvalidCommand,
            "pet skill-unlearn accepts only native removal slots zero through eleven");

        var rawConnection = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.LegacyTcp);
        var rawIdentity = PetCommandOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            rawConnection.ConnectionId);
        var rawEnvelope = PetSkillUnlearnCommandEnvelope.CreateRawLocal(
            subject,
            rawConnection,
            DateTimeOffset.UtcNow,
            new PetSkillUnlearnCommand(rawIdentity, SkillSlot: 0));
        Check.True(
            PetSkillUnlearnCommandEnvelope.Validate(rawEnvelope) ==
                CommandEnvelopeValidation.Valid &&
            rawEnvelope.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId,
            "raw-local pet skill-unlearn remains bound to its connection");
    }

    private static void CheckRawLocalIdentities()
    {
        var operation = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var correlation = new CommandConnectionCorrelation(
            connectionId,
            CommandTransportKind.LegacyTcp);
        var identity = PetCommandOperationIdentity.RawLocalServer(
            operation,
            connectionId);
        var receivedAt = DateTimeOffset.UtcNow;

        var bag = BagItemActivationCommandEnvelope.CreateRawLocal(
            Subject(),
            correlation,
            receivedAt,
            new BagItemActivationCommand(identity, 31));
        var bagRetry =
            BagItemActivationCommandEnvelope.CreateRawLocal(
                Subject(),
                correlation,
                receivedAt.AddSeconds(1),
                new BagItemActivationCommand(identity, 31));
        var level = PetLevelUpgradeCommandEnvelope.CreateRawLocal(
            Subject(),
            correlation,
            receivedAt,
            new PetLevelUpgradeCommand(identity, 7));
        var presence =
            PetPresenceTransitionCommandEnvelope.CreateRawLocal(
                Subject(),
                correlation,
                receivedAt,
                new PetPresenceTransitionCommand(
                    identity,
                    7,
                    PetPresenceCommandOperation.CallOut));

        Check.True(
            BagItemActivationCommandEnvelope.Validate(bag) ==
                CommandEnvelopeValidation.Valid &&
            PetLevelUpgradeCommandEnvelope.Validate(level) ==
                CommandEnvelopeValidation.Valid &&
            PetPresenceTransitionCommandEnvelope.Validate(presence) ==
                CommandEnvelopeValidation.Valid,
            "all pet durable families accept bounded raw-local identity");
        Check.True(
            bag.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            level.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId &&
            presence.IdentityStrength ==
                CommandIdentityStrength.ServerOperationId,
            "raw-local pet envelopes retain server identity strength");
        Check.True(
            bag.Command.Identity == identity &&
            bag.Command.ClientOperationId == Guid.Empty,
            "raw-local identity is never mislabeled as a client UUID");
        Check.True(
            bag.OperationId == bagRetry.OperationId &&
            bag.RequestHash == bagRetry.RequestHash,
            "one raw-local identity replays deterministically");
        Check.True(
            bag.OperationId != level.OperationId &&
            level.OperationId != presence.OperationId,
            "raw-local pet families remain domain-separated");

        var wrongConnection = correlation with
        {
            ConnectionId = Guid.NewGuid()
        };
        Check.Throws<ArgumentException>(
            () => BagItemActivationCommandEnvelope.CreateRawLocal(
                Subject(),
                wrongConnection,
                receivedAt,
                new BagItemActivationCommand(identity, 31)),
            "raw-local identity cannot move to another legacy connection");
        Check.True(
            BagItemActivationCommandEnvelope.Validate(
                bag with { Connection = wrongConnection }) ==
                CommandEnvelopeValidation.InvalidCorrelation,
            "tampered raw-local envelope fails connection correlation");
        Check.Throws<ArgumentException>(
            () => BagItemActivationCommandEnvelope.Create(
                Subject(),
                correlation,
                receivedAt,
                new BagItemActivationCommand(identity, 31)),
            "raw-local identity cannot enter the secure constructor");
        Check.Throws<ArgumentException>(
            () => BagItemActivationCommandEnvelope.CreateRawLocal(
                Subject(),
                correlation,
                receivedAt,
                new BagItemActivationCommand(
                    PetCommandOperationIdentity.SecureClient(operation),
                    31)),
            "secure identity cannot enter the raw-local constructor");
    }

    private static CommandSubject Subject() => new(13, 2);

    private static CommandConnectionCorrelation Correlation() =>
        new(Guid.NewGuid(), CommandTransportKind.SecureTlsLegacy);
}
