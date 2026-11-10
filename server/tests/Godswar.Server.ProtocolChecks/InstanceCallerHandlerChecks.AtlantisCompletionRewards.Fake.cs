using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private sealed class ScriptedAtlantisCompletionRewards(IEnumerable<GameCharacter> characters)
        : IAtlantisCompletionRewardStore
    {
        private readonly Dictionary<int, GameCharacter> _characters =
            characters.ToDictionary(character => character.Id);
        private readonly Dictionary<WorldInstanceId, AtlantisCompletionRewardReceipt> _committed = [];
        public List<AtlantisCompletionRewardRequest> Requests { get; } = [];
        public bool LoseFirstCommitAcknowledgement { get; set; }
        public int RejectAttemptsRemaining { get; set; }
        public int CommitCount { get; private set; }
        public int DuplicateReceiptCount { get; private set; }

        public Task<AtlantisCompletionRewardReceipt> SettleAsync(AtlantisCompletionRewardRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (RejectAttemptsRemaining > 0)
            {
                RejectAttemptsRemaining--;
                return Task.FromResult(new AtlantisCompletionRewardReceipt(
                    AtlantisCompletionRewardStatus.CharacterUnavailable, request.WorldInstanceId,
                    request.Award, []));
            }
            if (_committed.TryGetValue(request.WorldInstanceId, out var existing))
            {
                DuplicateReceiptCount++;
                return Task.FromResult(existing with { Status = AtlantisCompletionRewardStatus.Duplicate });
            }
            var members = request.FrozenMembers.Select(member =>
            {
                var character = _characters[member.CharacterId];
                return new AtlantisCompletionRewardMember(member.AccountId, member.CharacterId,
                    character.Camp, character.MedusaHonorPoints,
                    checked(character.MedusaHonorPoints + request.Award.HardPoints),
                    checked(character.MedusaRewardRevision + 1), request.Award.TitleId);
            }).ToArray();
            var receipt = new AtlantisCompletionRewardReceipt(AtlantisCompletionRewardStatus.Applied,
                request.WorldInstanceId, request.Award, members);
            _committed.Add(request.WorldInstanceId, receipt);
            CommitCount++;
            if (LoseFirstCommitAcknowledgement)
            {
                LoseFirstCommitAcknowledgement = false;
                return Task.FromException<AtlantisCompletionRewardReceipt>(
                    new IOException("completion committed but acknowledgement was lost"));
            }
            return Task.FromResult(receipt);
        }

        public AtlantisCompletionRewardReceipt Committed(WorldInstanceId instanceId) => _committed[instanceId];
    }
}
