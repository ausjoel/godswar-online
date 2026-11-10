using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private ICharacterTitleSelectionStore? _characterTitleSelections;

    internal void ConfigureCharacterTitleSelections(ICharacterTitleSelectionStore? store)
    {
        if (store is null) return;
        var previous = Interlocked.CompareExchange(ref _characterTitleSelections, store, null);
        if (previous is not null && !ReferenceEquals(previous, store))
            throw new InvalidOperationException("Character title selections are already configured.");
    }

    internal async Task<bool> SelectCharacterTitleAsync(ClientSession session, uint titleId,
        CancellationToken cancellationToken)
    {
        GameSessionContext expected;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out expected!) || !expected.WorldReady ||
                !IsCurrentTitleSelectionActor(expected, out _)) return false;
        }

        CharacterTitleSelectionReceipt? receipt = null;
        var store = _characterTitleSelections;
        if (store is not null)
        {
            var request = new CharacterTitleSelectionRequest(
                new CommandSubject(expected.AccountId, expected.CharacterId), expected.RealmId,
                expected.Ownership, titleId);
            try
            {
                receipt = await store.SelectAsync(request, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[title] selection deferred character={expected.CharacterId} reason={error.GetType().Name}");
            }
        }

        if (receipt?.Status == CharacterTitleSelectionStatus.OwnershipLost)
        {
            lock (_gate)
            {
                if (IsCurrentTitleSelectionActor(expected, out _)) session.Disconnect();
            }
            return false;
        }
        var writes = new List<(ClientSession Session, Task Completion)>();
        var applied = false;
        lock (_gate)
        {
            if (!IsCurrentTitleSelectionActor(expected, out var current)) return false;
            var character = current.Character;
            if (receipt?.Succeeded == true)
            {
                if (receipt.SelectedTitleId != titleId || receipt.HonorPoints < 0 || receipt.RewardRevision < 0 ||
                    receipt.OwnedTitleIds.Count > CharacterSnapshotLimits.OwnedTitleCount ||
                    receipt.OwnedTitleIds.Any(id => id == 0) ||
                    receipt.SelectedTitleId != 0 && !receipt.OwnedTitleIds.Contains(receipt.SelectedTitleId))
                    throw new InvalidDataException("Character title selection returned invalid ownership evidence.");

                foreach (var ownedTitle in receipt.OwnedTitleIds) character.AddOwnedTitle(ownedTitle);
                if (character.MedusaRewardRevision <= receipt.RewardRevision)
                {
                    character.SelectedTitleId = receipt.SelectedTitleId;
                    character.MedusaHonorPoints = receipt.HonorPoints;
                    character.MedusaRewardRevision = receipt.RewardRevision;
                    applied = true;
                }
            }
            // Hide is optimistic in Origin. A refusal or stale receipt must
            // restore the current authoritative title to the requesting client.
            if (current.WorldReady)
            {
                AdmitTitleSelectionPackets(current, applied, writes);
            }
        }
        foreach (var write in writes)
        {
            try { await write.Completion; }
            catch (Exception) { write.Session.Disconnect(); }
        }
        return applied;
    }

    // Earning a title never changes the equipped title, as with Wonderland titles.
    internal async Task PublishItemTitleAsync(ClientSession session, uint titleId, long rewardRevision,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> packet;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var current) || !current.WorldReady ||
                !IsCurrentAccountSession(current.AccountId, current.Session, current.Ownership)) return;
            var character = current.Character;
            character.AddOwnedTitle(titleId);
            if (character.MedusaRewardRevision <= rewardRevision) character.MedusaRewardRevision = rewardRevision;
            packet = PacketBuilder.MedusaDesignationInfo(character.SelectedTitleId, character.OwnedTitleIds);
        }
        await session.SendAsync(packet, cancellationToken, "ItemTitleOwnership");
    }

    private bool IsCurrentTitleSelectionActor(GameSessionContext expected, out GameSessionContext current) =>
        _sessions.TryGetValue(expected.Session, out current!) && !current.Session.IsDisconnected &&
        current.AccountId == expected.AccountId && current.CharacterId == expected.CharacterId &&
        current.RealmId == expected.RealmId && current.Ownership.IsValid && current.Ownership == expected.Ownership &&
        ReferenceEquals(current.Character, expected.Character) &&
        IsCurrentAccountSession(current.AccountId, current.Session, current.Ownership);

    private void AdmitTitleSelectionPackets(GameSessionContext current, bool publishToObservers,
        List<(ClientSession Session, Task Completion)> writes)
    {
        var title = PacketBuilder.PlayerTitleInfo(current.Character, current.ObjectId);
        var self = new ReadOnlyMemory<byte>[]
        {
            PacketBuilder.MedusaDesignationInfo(current.Character.SelectedTitleId, current.Character.OwnedTitleIds),
            title
        };
        // Stage 4 audit: 2 packets, state-bearing (the client must show the committed title). They always fit an empty
        // 128-packet queue, so a rejection means the client is already ~128 packets behind: disconnecting is the
        // designed overload policy and is deliberately kept. Retrying later would run outside this routing fence
        // and could reorder against a newer title change.
        if (!current.Session.TryAdmitExactBatch(self, out var selfWrite)) current.Session.Disconnect();
        writes.Add((current.Session, selfWrite));
        if (!publishToObservers) return;
        foreach (var viewer in _sessions.Values)
        {
            if (ReferenceEquals(viewer.Session, current.Session) || !viewer.WorldReady ||
                viewer.WorldInstanceId != current.WorldInstanceId || viewer.RealmId != current.RealmId ||
                !IsCurrentAccountSession(viewer.AccountId, viewer.Session, viewer.Ownership)) continue;
            // Stage 4: the observer copy is purely cosmetic. A bystander whose queue is full skips it instead of being
            // disconnected over someone else's title change (its next full world refresh shows the title).
            if (!viewer.Session.TryAdmitExact(title, out var write)) continue;
            writes.Add((viewer.Session, write));
        }
    }
}
