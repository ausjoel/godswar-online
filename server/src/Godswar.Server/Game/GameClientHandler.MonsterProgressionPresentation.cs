using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendMonsterDeathProgressionAsync(
        uint monsterObjectId,
        uint monsterSpawnGeneration,
        long currentExperience,
        int currentTalentExperience,
        int currentTalentPoints,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (_character.CurrentMap == 207)
        {
            // Native 10027 clears monster loot, even when used only to carry EXP.
            // Attribute refreshes preserve the durable reward projection without
            // racing the independent retained-corpse loot presentation.
            foreach (var packet in PacketBuilder.WonderlandMonsterProgression(LocalPlayerObjectId,
                         currentExperience, currentTalentExperience, currentTalentPoints))
                await _session.SendAsync(packet, cancellationToken, "WonderlandKillProgressionRefresh");
            foreach (var packet in PacketBuilder.WonderlandMonsterProgression(CurrentPlayerObjectId,
                         currentExperience, currentTalentExperience, currentTalentPoints))
                await _registry.BroadcastToMonsterViewersAsync(_character.CurrentMap, monsterObjectId,
                    packet, cancellationToken, _session, "WonderlandKillProgressionRefreshWorld",
                    expectedSpawnGeneration: monsterSpawnGeneration);
            return;
        }

        await _registry.DeliverMonsterPacketToViewerAsync(
            _session,
            _character.CurrentMap,
            monsterObjectId,
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                LocalPlayerObjectId,
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            monsterSpawnGeneration,
            cancellationToken,
            "MonsterKillProgressionRefresh");

        await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            monsterObjectId,
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                CurrentPlayerObjectId,
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            cancellationToken,
            _session,
            "MonsterKillProgressionRefreshWorld",
            expectedSpawnGeneration: monsterSpawnGeneration);
    }

}
