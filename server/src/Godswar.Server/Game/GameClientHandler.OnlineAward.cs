using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IOnlineAwardCommandExecutor? _onlineAwardCommands;
    private readonly OnlineAwardBalanceSnapshot? _onlineAwardBalance;

    private async Task HandleOnlineAwardAsync(
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

        if (!IsExactOnlineAwardRequest(
                packet,
                route,
                npcId,
                dialogIndex,
                subId,
                arguments))
        {
            await SendOnlineAwardRejectedAsync(
                npcId,
                OnlineAwardExecutionDisposition.InvalidIntent,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }
        if (_session.IsSecure && !packet.ClientOperationId.HasValue)
        {
            CommandMetrics.RecordUnsupportedLegacyIdentity(
                CommandFamily.OnlineAward);
            await SendOnlineAwardRejectedAsync(
                npcId,
                OnlineAwardExecutionDisposition.InvalidIntent,
                null,
                cancellationToken);
            return;
        }
        if (!_session.IsSecure &&
            !AllowLegacyPlayerMutationFallback("online_award"))
        {
            return;
        }
        if (!HasCurrentOnlineAwardRealmAuthority() ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }
        if (!TryGetOnlineAwardBalance(out var balance) ||
            _onlineAwardCommands is null)
        {
            await SendOnlineAwardRejectedAsync(
                npcId,
                OnlineAwardExecutionDisposition.ProviderUnavailable,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }

        var identity = _session.IsSecure
            ? OnlineAwardOperationIdentity.SecureClient(
                packet.ClientOperationId!.Value)
            : OnlineAwardOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        var receivedAt = DateTimeOffset.UtcNow;
        if (!OnlineAwardCommandEnvelope.TryCreate(
                identity,
                _processRealmId.Value,
                checked((int)npcId),
                dialogIndex,
                _realmCalendar.GetDay(receivedAt).DayNumber,
                out var command))
        {
            await SendOnlineAwardRejectedAsync(
                npcId,
                OnlineAwardExecutionDisposition.InvalidIntent,
                packet.ClientOperationId,
                cancellationToken);
            return;
        }

        var envelope = OnlineAwardCommandEnvelope.Create(
            new CommandSubject(_account.Id, _character.Id),
            new CommandConnectionCorrelation(
                _commandConnectionId,
                identity.IsSecureClient
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp),
            receivedAt,
            command) with { Ownership = ownership };
        var bagBefore = _character.KitBag;
        OnlineAwardExecutionResult execution;
        try
        {
            execution = await _onlineAwardCommands.ExecuteAsync(
                envelope,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "[online-award] durable command remains pending: " +
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        CommandMetrics.Record(
            CommandFamily.OnlineAward,
            identity.Strength,
            MapOnlineAwardOutcome(execution.Disposition));
        if (!execution.IsDurable)
        {
            await SendOnlineAwardRejectedAsync(
                npcId,
                execution.Disposition,
                identity.IsSecureClient ? identity.OperationId : null,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Online Award result has no receipt.");
        try
        {
            ValidateOnlineAwardReceipt(
                receipt,
                execution.Disposition ==
                    OnlineAwardExecutionDisposition.Committed
                    ? balance
                    : null);
            await ReloadOnlineAwardProjectionAsync(
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
                "[online-award] committed projection remains pending: " +
                exception.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        await SendOnlineAwardDurableResultAsync(
            npcId,
            identity,
            receipt,
            execution.Disposition,
            bagBefore,
            balance,
            cancellationToken);
    }

    private static bool IsExactOnlineAwardRequest(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments) =>
        packet.Length == 92 &&
        packet.Buffer.Length == 92 &&
        route.Behavior == NpcDialogueBehavior.OnlineAward &&
        route.DialogIndex == OnlineAwardProtocol.DialogIndex &&
        OnlineAwardProtocol.IsEndpoint(route.NpcKey, npcId) &&
        dialogIndex == OnlineAwardProtocol.DialogIndex &&
        BinaryPrimitives.ReadInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(int))) == dialogIndex &&
        subId == OnlineAwardProtocol.InitialRequestSubId &&
        // The native client leaves these unused words as stale scratch state.
        // Their count is wire structure; their values are not claim intent.
        arguments.Count == 18;

    private bool HasCurrentOnlineAwardRealmAuthority() =>
        _account is not null &&
        _character is not null &&
        _character.AccountId == _account.Id &&
        _character.RealmId == _processRealmId &&
        _processRealmId.IsValid;

    private bool TryGetOnlineAwardBalance(
        out OnlineAwardBalanceSnapshot balance)
    {
        balance = _onlineAwardBalance!;
        try
        {
            balance?.Validate();
            return balance is not null;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(
                "[online-award] invalid balance snapshot: " +
                exception.Message);
            return false;
        }
    }

    private async Task SendOnlineAwardRejectedAsync(
        uint npcId,
        OnlineAwardExecutionDisposition disposition,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        var nativeResult = disposition switch
        {
            OnlineAwardExecutionDisposition.AlreadyClaimed =>
                OnlineAwardProtocol.AlreadyClaimedSubId,
            OnlineAwardExecutionDisposition.BagFull =>
                OnlineAwardProtocol.BagFullSubId,
            _ => OnlineAwardProtocol.UnavailableSubId
        };
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                OnlineAwardProtocol.DialogIndex,
                nativeResult),
            cancellationToken,
            "OnlineAwardRejected");
        if (_session.IsSecure && clientOperationId.HasValue)
        {
            await SendSecureGearMentorResultAsync(
                clientOperationId.Value,
                CommandFamily.OnlineAward,
                nativeResult,
                disposition ==
                    OnlineAwardExecutionDisposition.RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected,
                Math.Max(0, _character?.OnlineAwardRevision ?? 0),
                cancellationToken);
        }
    }

    private static CommandOutcome MapOnlineAwardOutcome(
        OnlineAwardExecutionDisposition disposition) => disposition switch
        {
            OnlineAwardExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            OnlineAwardExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            OnlineAwardExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            OnlineAwardExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            OnlineAwardExecutionDisposition.ProviderUnavailable =>
                CommandOutcome.ProviderUnavailable,
            _ => CommandOutcome.PreconditionFailed
        };
}
