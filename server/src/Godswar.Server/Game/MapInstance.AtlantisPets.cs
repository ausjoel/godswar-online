using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private static CapturedMonsterSpawn[] PrepareAtlantisPetDefinitions(GameplayContentCatalog content,
        IReadOnlyList<(int CharacterId, int Level)> party) =>
        AtlantisPetSpawnPolicy.Spawns.Select(pet =>
        {
            var templates = content.MonsterTemplates.Where(template => template.SourceMapId == 205 &&
                template.TemplateKey == pet.Placement.TemplateKey).ToArray();
            if (templates.Length != 1 || !templates[0].IsPet || templates[0].IsBoss || templates[0].IsElite ||
                !AtlantisEncounterPolicy.TryParseMonsterRank(templates[0].Rank, out var rank) ||
                rank != AtlantisMonsterRank.Normal)
            {
                throw new InvalidDataException($"Atlantis requires its published pet '{pet.Placement.TemplateKey}'.");
            }
            // Tier one follows the existing passive, capturable ambient-pet
            // convention. These identities are never bound to a scored wave.
            // F-122: with atlantis.mermaid_dew on, the Mermaid keeps tier one (passive) but gets stage-5 elite health
            // for the party (GENERATED), so netting it is far easier than killing it.
            var stats = pet.ObjectId == AtlantisPetSpawnPolicy.MermaidObjectId && AtlantisPetSpawnPolicy.MermaidGivesDew
                ? new AtlantisSpawnStats(1, AtlantisSpawnBalancePolicy.Resolve(party, 5, AtlantisMonsterRank.Elite).MaximumHealth)
                : new AtlantisSpawnStats(1, 10);
            var spawn = CreateAtlantisSpawn(pet.ObjectId, templates[0], pet.Placement, stats);
            spawn.Validate(205);
            return spawn;
        }).ToArray();

    internal bool CanCaptureAtlantisPet(int characterId, uint objectId)
    {
        lock (_atlantisEncounterGate)
        {
            if (_atlantisRun?.Snapshot().State != AtlantisRunState.Active ||
                _atlantisAdmittedParty is null ||
                !_atlantisAdmittedParty.Any(member => member.CharacterId == characterId))
            {
                return false;
            }
            lock (_monsterRuntimeGate)
            {
                return _atlantisMonsters is not null &&
                    _atlantisMonsters.TryGetSnapshot(objectId, out var monster) &&
                    AtlantisPetSpawnPolicy.Matches(objectId, monster.Definition.TemplateKey) &&
                    monster.IsAlive && monster.IsSpawned;
            }
        }
    }

    private bool TryCaptureAtlantisPet(MonsterRuntimeSnapshot expected, DateTimeOffset now,
        out MonsterDamageResult result)
    {
        lock (_atlantisEncounterGate)
        {
            if (_atlantisRun?.Advance(now).State != AtlantisRunState.Active ||
                !AtlantisPetSpawnPolicy.Matches(expected.ObjectId, expected.Definition.TemplateKey))
            {
                result = default!;
                return false;
            }
            lock (_monsterRuntimeGate)
            {
                return TryCaptureMonsterCore(expected, now, out result);
            }
        }
    }
}
