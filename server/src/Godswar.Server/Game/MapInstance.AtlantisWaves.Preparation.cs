using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private const uint FirstAtlantisMonsterObjectId = 42_000;

    private static CapturedMonsterSpawn[][] PrepareAtlantisWaveDefinitions(
        GameplayContentCatalog content, IReadOnlyList<(int CharacterId, int Level)> party)
    {
        var nextObjectId = FirstAtlantisMonsterObjectId;
        return AtlantisWavePlan.Waves.Select(wave => wave.Slots.Select(slot =>
        {
            var identity = wave.IsBossWave
                ? AtlantisMonsterTemplatePolicy.ResolveBoss(wave.StageIndex)
                : AtlantisMonsterTemplatePolicy.ResolveGroupMember(wave.StageIndex, slot.SlotIndex);
            var candidates = content.MonsterTemplates.Where(template =>
                template.SourceMapId == 205 && string.Equals(template.TemplateKey,
                    identity.TemplateKey, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1 || identity.Rank != slot.Rank ||
                !AtlantisEncounterPolicy.TryParseMonsterRank(candidates[0].Rank, out var rank) ||
                rank != slot.Rank || candidates[0].IsPet)
            {
                throw new InvalidDataException($"Atlantis requires one published map-205 {slot.Rank} template '{identity.TemplateKey}'.");
            }
            var template = candidates[0];
            var stats = AtlantisSpawnBalancePolicy.Resolve(party, wave.StageIndex + 1, slot.Rank);
            var definition = CreateAtlantisSpawn(nextObjectId++, template, identity, stats);
            definition.Validate(205);
            return definition;
        }).ToArray()).ToArray();
    }

    private static CapturedMonsterSpawn CreateAtlantisSpawn(uint objectId,
        GameplayMonsterTemplateDefinition template, AtlantisMonsterSpawnIdentity placement,
        AtlantisSpawnStats stats)
    {
        var packet = new byte[108];
        var templateBytes = Encoding.ASCII.GetBytes(template.TemplateKey);
        if (templateBytes.Length >= packet.Length - 44)
        {
            throw new InvalidDataException("Atlantis monster template does not fit its appearance packet.");
        }
        BinaryPrimitives.WriteUInt16LittleEndian(packet, checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), 10020);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 0x212);
        // Origin's 10020 handler checks the packed high-word map before
        // creating an object. Leaving it zero makes Atlantis mobs invisible.
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 205);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), stats.Level);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20), stats.MaximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), stats.MaximumHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28), placement.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32), placement.Y);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36), placement.Z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40), 1f);
        templateBytes.CopyTo(packet.AsSpan(44));
        return new(205, AtlantisMonsterTemplatePolicy.SceneKey, template.TemplateKey,
            template.DisplayName, objectId, placement.X, placement.Z, packet);
    }
}
