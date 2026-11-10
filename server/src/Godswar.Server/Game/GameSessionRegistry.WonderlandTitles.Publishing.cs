using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private async Task PublishWonderlandTitlesAsync(WonderlandTitleRequest request, WonderlandTitleReceipt receipt)
    {
        if (receipt.WorldInstanceId != request.WorldInstanceId || receipt.Award.TitleId != request.Award.TitleId ||
            receipt.Award.IslandNumber != request.IslandNumber || receipt.Members.Count != request.FrozenMembers.Count ||
            receipt.Members.Select(member => member.CharacterId).Distinct().Count() != receipt.Members.Count ||
            receipt.Members.Any(member => member.HonorPoints < 0 || member.RewardRevision < 0 ||
                !request.FrozenMembers.Any(expected => expected.CharacterId == member.CharacterId &&
                    expected.AccountId == member.AccountId)))
            throw new InvalidDataException("Wonderland title receipt does not match its frozen eligible members.");
        var writes = new List<(ClientSession Session, Task Write)>();
        lock (_gate)
        {
            foreach (var reward in receipt.Members)
            {
                var current = _sessions.Values.FirstOrDefault(context => context.AccountId == reward.AccountId &&
                    context.CharacterId == reward.CharacterId && context.RealmId == request.RealmId && context.WorldReady &&
                    IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership));
                if (current is null) continue;
                var character = current.Character;
                character.AddOwnedTitle(receipt.Award.TitleId);
                if (character.MedusaRewardRevision <= reward.RewardRevision)
                {
                    character.MedusaHonorPoints = reward.HonorPoints;
                    character.MedusaRewardRevision = reward.RewardRevision;
                }
                // Earning a title never changes the equipped title or combat stats.
                var packets = new List<ReadOnlyMemory<byte>>
                {
                    PacketBuilder.MedusaDesignationInfo(character.SelectedTitleId, character.OwnedTitleIds)
                };
                if (current.Session.TryAdmitExactBatch(packets, out var write)) writes.Add((current.Session, write));
                else current.Session.Disconnect();
            }
        }
        foreach (var entry in writes)
        {
            try { await entry.Write; }
            catch (Exception) { entry.Session.Disconnect(); }
        }
    }
}
