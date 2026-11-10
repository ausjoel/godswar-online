using Godswar.Server.Application.Characters;
using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IFactionCrierCommandExecutor? _factionCrierCommands;
    private readonly FactionCrierBalanceSnapshot? _factionCrierBalance;

    private async Task HandleFactionCrierAsync(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (packet.Length != FactionCrierProtocol.ActionPacketBytes ||
            packet.Buffer.Length != FactionCrierProtocol.ActionPacketBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.Payload.Slice(8, sizeof(int))) != dialogIndex)
        {
            Console.Error.WriteLine(
                "[faction-crier] rejected non-canonical packet " +
                $"length={packet.Length}");
            return;
        }

        if (!IsExactFactionCrierEndpoint(route, npcId, dialogIndex))
        {
            Console.Error.WriteLine(
                "[faction-crier] rejected mismatched NPC route " +
                $"npc={npcId} dialog={dialogIndex}");
            return;
        }

        if (FactionCrierProtocol.TryGetNavigationPage(
                dialogIndex,
                subId,
                arguments,
                out var responseSubIds))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    responseSubIds),
                cancellationToken,
                "FactionCrierNavigationPage");
            return;
        }

        if (!FactionCrierProtocol.TryResolveMutation(
                dialogIndex,
                subId,
                arguments,
                out var wireIntent) ||
            !TryMapFactionCrierOperation(
                wireIntent.Operation,
                out var operation))
        {
            if (TryResolveFactionCrierRootOperation(
                    subId,
                    out var rejectedOperation))
            {
                CommandMetrics.Record(
                    CommandFamily.FactionCrier,
                    _session.IsSecure
                        ? CommandIdentityStrength.ClientOperationId
                        : CommandIdentityStrength.ServerOperationId,
                    CommandOutcome.Malformed);
                await SendFactionCrierRejectedAsync(
                    npcId,
                    rejectedOperation,
                    FactionCrierExecutionDisposition.InvalidIntent,
                    packet.ClientOperationId,
                    cancellationToken);
            }
            return;
        }

        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.FactionCrier);
            await SendFactionCrierRejectedAsync(
                npcId,
                operation,
                FactionCrierExecutionDisposition.InvalidIntent,
                clientOperationId: null,
                cancellationToken);
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("faction_crier"))
        {
            return;
        }
        if (!HasCurrentFactionCrierRealmAuthority())
        {
            await SendFactionCrierRejectedAsync(
                npcId,
                operation,
                FactionCrierExecutionDisposition.PreconditionFailed,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }
        if (_factionCrierCommands is null ||
            !TryGetFactionCrierBalance(out var balance))
        {
            CommandMetrics.Record(
                CommandFamily.FactionCrier,
                _session.IsSecure
                    ? CommandIdentityStrength.ClientOperationId
                    : CommandIdentityStrength.ServerOperationId,
                CommandOutcome.ProviderUnavailable);
            await SendFactionCrierRejectedAsync(
                npcId,
                operation,
                FactionCrierExecutionDisposition.ProviderUnavailable,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }

        var identity = _session.IsSecure
            ? FactionCrierOperationIdentity.SecureClient(
                packet.ClientOperationId!.Value)
            : FactionCrierOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        var subject = new CommandSubject(_account.Id, _character.Id);
        var replayIntent = new FactionCrierReplayIntent(
            _processRealmId.Value,
            wireIntent.ActionSubId);
        var receivedAt = DateTimeOffset.UtcNow;
        var bagBefore = _character.KitBag;

        FactionCrierExecutionResult execution;
        try
        {
            execution = identity.IsSecureClient
                ? await _factionCrierCommands.TryReplayAsync(
                    subject,
                    ownership,
                    replayIntent,
                    identity,
                    cancellationToken)
                : FactionCrierExecutionResult.ReplayNotFound();
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return;
            }

            if (execution.Disposition ==
                FactionCrierExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteFactionCrierAsync(
                    subject,
                    ownership,
                    identity,
                    wireIntent,
                    operation,
                    npcId,
                    dialogIndex,
                    receivedAt,
                    balance,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.FactionCrier,
                identity.Strength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            CommandMetrics.Record(
                CommandFamily.FactionCrier,
                identity.Strength,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[faction-crier] durable command remains pending: " +
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        CommandMetrics.Record(
            CommandFamily.FactionCrier,
            identity.Strength,
            MapFactionCrierOutcome(execution.Disposition));
        if (!execution.IsDurable)
        {
            await SendFactionCrierRejectedAsync(
                npcId,
                operation,
                execution.Disposition,
                identity.IsSecureClient ? identity.OperationId : null,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Faction Crier result has no receipt.");
        try
        {
            ValidateFactionCrierReceipt(
                operation,
                wireIntent.ActionSubId,
                receipt);
            await ReloadFactionCrierProjectionAsync(
                ownership,
                receipt,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "[faction-crier] committed projection remains pending: " +
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await SendFactionCrierDurableResultAsync(
            npcId,
            identity,
            receipt,
            execution.Disposition,
            bagBefore,
            cancellationToken);
        Console.WriteLine(
            $"[faction-crier] operation={operation} " +
            $"outcome={execution.Disposition} " +
            $"revision={receipt.FactionCrierRevision}");
    }

    private async Task<FactionCrierExecutionResult>
        ExecuteFactionCrierAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        FactionCrierOperationIdentity identity,
        FactionCrierWireIntent wireIntent,
        FactionCrierOperation operation,
        uint npcId,
        int dialogIndex,
        DateTimeOffset receivedAt,
        FactionCrierBalanceSnapshot balance,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.PreconditionFailed);
        }
        var availability = EvaluateFactionCrierAvailability(
            character.Level,
            operation,
            receivedAt,
            balance,
            _realmCalendar,
            out var realmDay);
        if (availability.HasValue)
        {
            return FactionCrierExecutionResult.Terminal(
                availability.Value);
        }

        FactionCrierNameplateSelection? renewalSource = null;
        if (operation == FactionCrierOperation.RenewNameplate)
        {
            var source = KitBagSlots.GetItem(
                character.KitBag,
                wireIntent.SourceKitBagSlot);
            if (source.IsEmpty ||
                source.Id is <
                    FactionCrierRewardPolicy.FirstNameplateItemId or
                    > FactionCrierRewardPolicy.LastNameplateItemId)
            {
                return FactionCrierExecutionResult.Terminal(
                    FactionCrierExecutionDisposition.MissingNameplate);
            }

            renewalSource = new FactionCrierNameplateSelection(
                wireIntent.SourceKitBagSlot,
                checked((int)source.Id),
                source.ToCompactString());
        }

        var periodDayNumber = operation switch
        {
            FactionCrierOperation.DailyClaim => realmDay.DayNumber,
            FactionCrierOperation.WeeklyReclaim =>
                RealmCalendar.GetWeekStart(realmDay).DayNumber,
            _ => 0
        };
        if (!FactionCrierCommandEnvelope.TryCreateCommand(
                identity,
                _processRealmId.Value,
                checked((int)npcId),
                dialogIndex,
                wireIntent.ActionSubId,
                periodDayNumber,
                renewalSource,
                out var command) ||
            command.Operation != operation)
        {
            return FactionCrierExecutionResult.Terminal(
                FactionCrierExecutionDisposition.InvalidIntent);
        }

        var envelope = FactionCrierCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                identity.IsSecureClient
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp),
            receivedAt,
            command) with
        {
            Ownership = ownership
        };
        return await _factionCrierCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private bool HasCurrentFactionCrierRealmAuthority() =>
        _account is not null &&
        _character is not null &&
        _character.AccountId == _account.Id &&
        _character.RealmId == _processRealmId &&
        _processRealmId.IsValid;

    private bool TryGetFactionCrierBalance(
        out FactionCrierBalanceSnapshot balance)
    {
        balance = _factionCrierBalance!;
        if (balance is null)
        {
            return false;
        }

        try
        {
            balance.Validate();
            return true;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(
                "[faction-crier] invalid balance snapshot: " +
                exception.Message);
            return false;
        }
    }

    private static bool IsExactFactionCrierEndpoint(
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex) =>
        route.Behavior == NpcDialogueBehavior.FactionCrier &&
        route.DialogIndex == dialogIndex &&
        dialogIndex == FactionCrierProtocol.DialogIndex &&
        string.Equals(
            route.NpcKey,
            route.ClientScriptKey,
            StringComparison.Ordinal) &&
        route.InitialMenuSubIds.SequenceEqual(
            FactionCrierProtocol.InitialMenuSubIds) &&
        FactionCrierProtocol.IsEndpoint(route.NpcKey, npcId);

    private static bool TryMapFactionCrierOperation(
        FactionCrierWireOperation wire,
        out FactionCrierOperation operation)
    {
        operation = wire switch
        {
            FactionCrierWireOperation.DailyClaim =>
                FactionCrierOperation.DailyClaim,
            FactionCrierWireOperation.WeeklyReclaim =>
                FactionCrierOperation.WeeklyReclaim,
            FactionCrierWireOperation.RenewNameplate =>
                FactionCrierOperation.RenewNameplate,
            FactionCrierWireOperation.TurnInNameplates =>
                FactionCrierOperation.TurnIn,
            _ => 0
        };
        return operation != 0;
    }

    private static bool TryResolveFactionCrierRootOperation(
        int subId,
        out FactionCrierOperation operation)
    {
        operation = subId switch
        {
            1 => FactionCrierOperation.DailyClaim,
            2 => FactionCrierOperation.TurnIn,
            3 => FactionCrierOperation.WeeklyReclaim,
            4 => FactionCrierOperation.RenewNameplate,
            _ => 0
        };
        return operation != 0;
    }

    private async Task SendFactionCrierRejectedAsync(
        uint npcId,
        FactionCrierOperation operation,
        FactionCrierExecutionDisposition disposition,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        var nativeResult = FactionCrierRewardPolicy.NativeFailureSubId(
            operation,
            disposition);
        // NPCFIX (2026-11-07): every refusal names its reason (it was silent; the admin saw only "no nameplate").
        Console.WriteLine(
            $"[faction-crier] refused character={_character?.Name ?? "<none>"} level={_character?.Level} " +
            $"operation={operation} disposition={disposition} reply={nativeResult} " +
            $"realm={_character?.RealmId}/{_processRealmId} secure={_session.IsSecure}");
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                FactionCrierProtocol.DialogIndex,
                nativeResult),
            cancellationToken,
            "FactionCrierRejected");
        if (_session.IsSecure && clientOperationId.HasValue)
        {
            await SendSecureGearMentorResultAsync(
                clientOperationId.Value,
                CommandFamily.FactionCrier,
                nativeResult,
                disposition ==
                    FactionCrierExecutionDisposition.RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected,
                Math.Max(0, _character?.FactionCrierRevision ?? 0),
                cancellationToken);
        }
    }

    private static CommandOutcome MapFactionCrierOutcome(
        FactionCrierExecutionDisposition disposition) =>
        disposition switch
        {
            FactionCrierExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            FactionCrierExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            FactionCrierExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            FactionCrierExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            FactionCrierExecutionDisposition.ProviderUnavailable or
                FactionCrierExecutionDisposition.ReplayNotFound =>
                CommandOutcome.ProviderUnavailable,
            _ => CommandOutcome.PreconditionFailed
        };
}
