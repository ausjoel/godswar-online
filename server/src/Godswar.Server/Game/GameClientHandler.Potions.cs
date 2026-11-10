using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PetDurableReceipt?> HandleDurablePotionAsync(PetCommandOperationIdentity identity,
        int slot, BagConsumableEffect effect, CancellationToken token)
    {
        if (_character is not { } character || _petDurableCommands is null || _characterCheckpoints is null ||
            !TryCreatePetSubject(identity, out var subject))
        {
            RecordPetProviderUnavailable(CommandFamily.BagItemActivation, identity,
                "Potion use requires the owned checkpoint coordinator.");
            return null;
        }
        var command = new BagItemActivationCommand(identity, slot);
        var correlation = PetCorrelation(identity);
        var unowned = identity.IsSecureClient
            ? BagItemActivationCommandEnvelope.Create(subject, correlation, DateTimeOffset.UtcNow, command)
            : identity.IsRawLocalServer
                ? BagItemActivationCommandEnvelope.CreateRawLocal(subject, correlation, DateTimeOffset.UtcNow, command)
                : BagItemActivationCommandEnvelope.CreateServerSessionLifecycle(subject, correlation, DateTimeOffset.UtcNow, command);
        if (!TryBindCurrentPlayerOwnership(unowned, out var envelope, out var ownership)) return null;
        var item = KitBagSlots.GetItemId(character.KitBag, slot);
        if (!PendingPotionVitals.TryPrepare(character, item, effect, ownership, out var pending, out var context))
            return null;
        envelope = envelope with { Command = envelope.Command with { PotionContext = context } };
        return await ExecuteAndCompletePetCommandAsync(identity, CommandFamily.BagItemActivation, ownership,
            async () =>
            {
                var submitted = false;
                try
                {
                    if (pending is not null)
                    {
                        var before = pending.Plan.Before;
                        var checkpoint = new CharacterVitalsCheckpoint(subject.AccountId, subject.CharacterId,
                            ownership, before.Hp, before.Mp, before.Revision);
                        var flushed = await _characterCheckpoints.FlushThroughAsync(checkpoint, token);
                        if (!flushed.Satisfies(checkpoint.Revision))
                            throw new InvalidOperationException("Potion before-vitals checkpoint was not satisfied.");
                    }
                    submitted = true;
                    var execution = await ExecutePotionWithRecoveryAsync(envelope, token);
                    if (pending is not null)
                    {
                        var receipt = execution.Receipt;
                        var committed = receipt?.Status == PetDurableReceiptStatus.PotionUsed &&
                            receipt.Potion is { } proof && proof.Matches(pending.Plan, item, ownership);
                        if (execution.Disposition == PetDurableExecutionDisposition.Committed && !committed ||
                            !pending.TryResolve(committed, committed ? receipt!.Potion : null))
                            throw new InvalidDataException("Potion result did not resolve its exact pending reservation.");
                        // Persist current live health even when the originating socket was cancelled.
                        // Incoming damage may have advanced the revision during the transaction.
                        using var flushDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        if (!await PersistVitalsCheckpointAsync(character, force: true, flushDeadline.Token))
                            throw new InvalidOperationException("Potion current-vitals checkpoint was not satisfied.");
                    }
                    return execution;
                }
                catch
                {
                    if (pending is { HoldsCheckpoints: true })
                    {
                        if (submitted) { pending.AbandonUnresolved(); _session.Disconnect(); }
                        else if (!pending.TryResolve(committed: false)) pending.AbandonUnresolved();
                    }
                    throw;
                }
            }, token);
    }

    private async Task<PetDurableExecutionResult> ExecutePotionWithRecoveryAsync(
        CommandEnvelope<BagItemActivationCommand> envelope, CancellationToken token)
    {
        try { return await _petDurableCommands!.ExecuteAsync(envelope, token); }
        catch (PlayerOwnershipValidationException) { throw; }
        catch (Exception error)
        {
            // An interrupted await may have committed. Resolve the SAME durable operation;
            // the executor either replays it or performs the still-uncommitted transaction once.
            Console.Error.WriteLine($"[potion-recovery] character-id={envelope.Subject.CharacterId} reason={error.GetType().Name}");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _petDurableCommands!.ExecuteAsync(envelope, deadline.Token);
        }
    }

    private async Task<bool> ReloadPotionProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, CancellationToken token)
    {
        if (_character is not { } character || _account is null ||
            !TryCaptureCurrentPlayerOwnership(out var owner)) return false;
        var previousBag = character.KitBag;
        try
        {
            var snapshot = await _characterSnapshots.ReadAsync(_account.Id, _processRealmId, token);
            CharacterSnapshotContract.Validate(snapshot);
            if (snapshot.RealmId != _processRealmId || snapshot.Character is not { } loaded ||
                loaded.Identity.CharacterId != character.Id || loaded.Identity.AccountId != _account.Id)
                throw new InvalidDataException("Potion inventory snapshot identity mismatch.");
            if (!ReferenceEquals(_character, character) || !RevalidateCurrentPlayerOwnership(owner)) return false;
            // Inventory-only refresh keeps the exact live vitals object used by combat.
            // Replacing it from a full snapshot creates a copy/install window that can lose
            // damage received between copying HP and changing the registry character pointer.
            character.KitBag = loaded.Loadout.KitBag;
            return await SendPotionProjectionAsync(receipt, disposition, previousBag, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[potion-projection] character-id={character.Id} reason={error.GetType().Name}");
            _session.Disconnect();
            return false;
        }
    }

    private bool IsPotionProjection(PetDurableReceipt receipt) =>
        receipt.Status is PetDurableReceiptStatus.PotionUsed or PetDurableReceiptStatus.PotionNotNeeded or
            PetDurableReceiptStatus.PotionContextMismatch ||
        receipt.Status == PetDurableReceiptStatus.ConsumableCooldownActive && _character is not null &&
        receipt.KitBagSlot is >= 0 and < 96 && PotionUsePolicy.TryResolve(RequireItemContent().Templates,
            KitBagSlots.GetItemId(_character.KitBag, receipt.KitBagSlot), out _);

    private async Task<bool> SendPotionProjectionAsync(PetDurableReceipt receipt,
        PetDurableExecutionDisposition disposition, string previousKitBag, CancellationToken token)
    {
        if (_character is null) return false;
        if (receipt.Status == PetDurableReceiptStatus.PotionUsed)
        {
            if (receipt.Potion is not { IsValid: true } evidence) return false;
            if (disposition == PetDurableExecutionDisposition.Committed &&
                KitBagSlots.GetItemId(previousKitBag, receipt.KitBagSlot) == evidence.ItemTemplateId)
                await _session.SendAsync(PacketBuilder.BagConsumableActivation(previousKitBag,
                    receipt.KitBagSlot, LocalPlayerObjectId), token, "PotionActivation");
            if (KitBagSlots.GetItem(_character.KitBag, receipt.KitBagSlot).IsEmpty)
                await _session.SendAsync(PacketBuilder.StorageItemKitBagDelete(receipt.KitBagSlot), token, "PotionConsumedSlotClear");
        }
        await SendKitBagRefreshAsync(token);
        await _session.SendAsync(PacketBuilder.PlayerVitalsUpdate(LocalPlayerObjectId,
            _character.CurrentHp, _character.CurrentMp), token, "PotionCurrentVitals");
        // A refused potion (cooling down, already full, state changed) stays silent: clicking one must not interrupt play.
        return true;
    }
}
