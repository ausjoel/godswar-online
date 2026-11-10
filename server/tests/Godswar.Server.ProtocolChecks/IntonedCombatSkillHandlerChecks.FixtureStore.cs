using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class IntonedCombatSkillHandlerChecks
{
    private sealed class CombatStore : GameStoreTestStub
    {
        private readonly TaskCompletionSource<bool> _vitalsWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondVitalsWritten =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int VitalsWrites { get; private set; }
        public int PositionWrites { get; private set; }
        public IReadOnlyList<SkillState> Skills { get; set; } =
            [new() { SkillId = checked((int)ThunderSkillId), Level = 1 }];

        public Task WaitForVitalsWriteAsync() =>
            _vitalsWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));
        public Task WaitForSecondVitalsWriteAsync() =>
            _secondVitalsWritten.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public override Task<IReadOnlyList<SkillState>> GetSkillStatesAsync(
            int accountId, int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Skills);

        public override Task SaveCharacterVitalsAsync(int accountId, int characterId,
            int currentHp, int currentMp, long vitalsRevision, CancellationToken cancellationToken = default)
        {
            Check.True(accountId == AccountId && characterId == CharacterId,
                "skill fixture persists the active character vitals");
            VitalsWrites++;
            _vitalsWritten.TrySetResult(true);
            if (VitalsWrites >= 2) _secondVitalsWritten.TrySetResult(true);
            return Task.CompletedTask;
        }

        public override Task SaveCharacterPositionAsync(int accountId, int characterId,
            byte currentMap, float positionX, float positionZ, CancellationToken cancellationToken = default)
        {
            Check.True(accountId == AccountId && characterId == CharacterId && currentMap == 0,
                "skill fixture persists movement for its exact active character");
            PositionWrites++;
            return Task.CompletedTask;
        }
    }
}
