using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleWonderlandBlackmarketActionAsync(NpcSpawnDefinition npc, int dialogIndex,
        WorldInstanceId instanceId, PlayerOwnershipFence ownership, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            !_registry.TryGetPlayerLifeRevision(_session, out var expectedLife)) return;
        if (!_registry.TryResolveWonderlandBlackmarket(_session, instanceId, out var targetIsland, out var target, out var reservationId))
        {
            await SendWonderlandBlackmarketResultAsync(npc, 142, cancellationToken);
            return;
        }
        var store = _registry.WonderlandBlackmarket;
        if (store is null || _pendingMapTransition is not null || !_registered || !_worldPresenceAnnounced)
        {
            await _session.SendAsync(PacketBuilder.ServerNote("Teleportation is not ready. Please try again shortly."),
                cancellationToken, "WonderlandBlackmarketUnavailable");
            return;
        }
        var request = new WonderlandBlackmarketRequest(Guid.NewGuid(), new CommandSubject(_account.Id, _character.Id),
            ownership, _processRealmId, instanceId, targetIsland, dialogIndex, reservationId);
        var receipt = await ChargeWonderlandBlackmarketAsync(store, request);
        Console.WriteLine($"[wonderland-blackmarket] character={_character.Name} function={dialogIndex} " +
            $"island={targetIsland} cost={request.Cost} status={receipt.Status}");
        if (!receipt.Succeeded)
        {
            if (RevalidateCurrentPlayerOwnership(ownership))
            {
                if (receipt.Status == WonderlandBlackmarketStatus.InsufficientSilver)
                    await SendWonderlandBlackmarketResultAsync(npc, 141, cancellationToken);
                else
                    await _session.SendAsync(PacketBuilder.ServerNote("Teleportation is not ready. Please try again shortly."),
                        cancellationToken, "WonderlandBlackmarketUnavailable");
            }
            return;
        }

        var outcome = SceneTransitionOutcome.RejectedWithoutRelocation;
        var originalHp = _character.CurrentHp;
        var originalMp = _character.CurrentMp;
        var healed = false;
        long healedRevision = -1;
        try
        {
            // The original NPC page was consumed before debit. Complete this
            // accepted service independently of a canceled client read loop.
            if (!IsServiceCurrent()) return;
            await ReloadWonderlandBlackmarketWalletAsync(ownership, CancellationToken.None);
            if (!IsServiceCurrent()) return;
            lock (_character.VitalsSync)
            {
                if (_character.CurrentHp <= 0) return;
                originalHp = _character.CurrentHp;
                originalMp = _character.CurrentMp;
                if (dialogIndex is 62 or 63)
                {
                    _character.CurrentHp = _character.MaxHp;
                    if (dialogIndex == 63) _character.CurrentMp = _character.MaxMp;
                    _character.MarkVitalsChanged();
                    healedRevision = _character.VitalsRevision;
                    healed = true;
                }
            }
            outcome = await TryBeginSameMapSceneTransitionAsync(target.X, target.Z,
                $"wonderland-blackmarket:{targetIsland}",
                IsServiceCurrent,
                CancellationToken.None, publishRevivalVitals: healed);
            if (outcome != SceneTransitionOutcome.RejectedWithoutRelocation)
            {
                _registry.ClearWonderlandPlayerEffects(_session);
                if (!_session.IsDisconnected)
                    await _session.SendAsync(BuildLocalPlayerStatusUpdate(), CancellationToken.None,
                        "WonderlandBlackmarketWallet");
            }
        }
        catch
        {
            // A send failure after relocation still leaves a durable destination.
            if (_pendingMapTransition is not null && _character.PositionX == target.X && _character.PositionZ == target.Z)
                outcome = SceneTransitionOutcome.CommittedRequiresReconnect;
            throw;
        }
        finally
        {
            if (outcome == SceneTransitionOutcome.RejectedWithoutRelocation)
            {
                if (healed && _registry.TryGetPlayerLifeRevision(_session, out var currentLife) && currentLife == expectedLife)
                    lock (_character.VitalsSync)
                    {
                        if (_character.CurrentHp > 0 && _character.VitalsRevision == healedRevision)
                        {
                            _character.CurrentHp = originalHp;
                            _character.CurrentMp = originalMp;
                            _character.MarkVitalsChanged();
                        }
                    }
                var refund = await store.RefundAsync(request, CancellationToken.None);
                if (!refund.Succeeded) throw new InvalidDataException("Rejected Blackmarket relocation could not refund its debit.");
                if (RevalidateCurrentPlayerOwnership(ownership) && !_session.IsDisconnected)
                {
                    await ReloadWonderlandBlackmarketWalletAsync(ownership, CancellationToken.None);
                    await _session.SendAsync(BuildLocalPlayerStatusUpdate(), CancellationToken.None,
                        "WonderlandBlackmarketRefund");
                    await _session.SendAsync(PacketBuilder.ServerNote("Teleportation could not complete; your silver was refunded."),
                        CancellationToken.None, "WonderlandBlackmarketRejected");
                }
            }
        }

        bool IsServiceCurrent() => RevalidateCurrentPlayerOwnership(ownership) && CanInteractWithWonderlandTransport(npc) &&
            _registry.TryGetPlayerLifeRevision(_session, out var life) && life == expectedLife &&
            _registry.IsWonderlandTravelCurrent(_session, instanceId, targetIsland - 1);
    }

    private async Task ReloadWonderlandBlackmarketWalletAsync(PlayerOwnershipFence ownership, CancellationToken token)
    {
        var snapshot = await _characterSnapshots.ReadAsync(_account!.Id, _processRealmId, token);
        if (!RevalidateCurrentPlayerOwnership(ownership) || snapshot.Character is not { } character ||
            character.Identity.CharacterId != _character!.Id)
            throw new InvalidDataException("Blackmarket wallet projection lost its character ownership.");
        _character.Silver = character.Wallet.Silver;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
    }

    private static async Task<WonderlandBlackmarketReceipt> ChargeWonderlandBlackmarketAsync(
        IWonderlandBlackmarketStore store, WonderlandBlackmarketRequest request)
    {
        try { return await store.ChargeAsync(request, CancellationToken.None); }
        catch
        {
            // Resolve an ambiguous commit by replaying exactly the same durable
            // operation, never by issuing a new debit. If recovery also fails,
            // compensate that operation before propagating the failure.
            try
            {
                var recovered = await store.ChargeAsync(request, CancellationToken.None);
                if (!recovered.Succeeded)
                {
                    var refund = await store.RefundAsync(request, CancellationToken.None);
                    if (!refund.Succeeded && refund.Status != WonderlandBlackmarketStatus.NotEligible)
                        throw new InvalidDataException("Ambiguous Blackmarket debit could not be compensated.");
                }
                return recovered;
            }
            catch
            {
                var refund = await store.RefundAsync(request, CancellationToken.None);
                if (!refund.Succeeded && refund.Status != WonderlandBlackmarketStatus.NotEligible)
                    throw new InvalidDataException("Ambiguous Blackmarket debit could not be compensated.");
                throw;
            }
        }
    }

    private Task SendWonderlandBlackmarketResultAsync(NpcSpawnDefinition npc, int result,
        CancellationToken token) => _session.SendAsync(PacketBuilder.CapturedNpcFunctionActionResponse(
            npc.InteractionId, WonderlandTransportProtocol.BlackmarketResultDialogIndex, [0, result]), token, "WonderlandBlackmarketResult");
}
