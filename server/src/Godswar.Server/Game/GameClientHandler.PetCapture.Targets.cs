using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly record struct PetCaptureKind(uint EggItemId,
        MedusaEncounterDifficulty Difficulty, PetCaptureContext Context = PetCaptureContext.Medusa);

    private bool TryResolvePetCaptureTarget(PetCaptureRequest request,
        out MonsterRuntimeSnapshot target, out PetCaptureKind kind)
    {
        target = default!;
        kind = default;
        var character = _character;
        if (character is null || !_registry.TryGetMonsterSnapshot(_session,
                character.CurrentMap, request.TargetObjectId, out var candidate) ||
            !candidate.IsAlive || !candidate.IsSpawned ||
            !_registry.IsMonsterVisibleTo(_session, candidate.ObjectId, candidate.SpawnGeneration) ||
            !IsWithinPetCaptureRange(character, candidate))
        {
            return false;
        }

        if (character.CurrentMap == 200 &&
            _registry.TryGetActiveMedusaCaptureDifficulty(_session, out var difficulty) &&
            WorldObjectIds.IsMedusaBabyRockElf(request.TargetObjectId) &&
            candidate.Definition.TemplateKey == MedusaIslandAmbientSpawnPolicy.BabyRockElfTemplateKey)
        {
            kind = new(RockElfEggItemId, difficulty);
        }
        else if (character.CurrentMap == 205 &&
            AtlantisPetSpawnPolicy.Matches(candidate.ObjectId, candidate.Definition.TemplateKey) &&
            _registry.CanCaptureAtlantisPet(_session, candidate.ObjectId))
        {
            kind = candidate.ObjectId == AtlantisPetSpawnPolicy.MermaidObjectId && AtlantisPetSpawnPolicy.MermaidGivesDew
                ? new(AtlantisPetSpawnPolicy.MermaidDewItemId,
                    MedusaEncounterDifficulty.Normal, PetCaptureContext.AtlantisMermaidDew)
                : new(AtlantisPetSpawnPolicy.MermanEggItemId,
                    MedusaEncounterDifficulty.Normal, PetCaptureContext.AtlantisMerman);
        }
        else if (character.CurrentMap == LelantineFarmProtocol.MapId &&
            LelantineFarmDogPolicy.TryGetTier(candidate.Definition.TemplateKey, out var dogTier))
        {
            kind = new(LelantineFarmDogPolicy.EggItemId, dogTier, PetCaptureContext.LelantineDog);
        }
        else if (WildPetCapturePolicy.Enabled &&
            WildPetCapturePolicy.TryGetEgg(character.CurrentMap, candidate.Definition.TemplateKey, out var wildEgg))
        {
            kind = new(wildEgg, MedusaEncounterDifficulty.Normal, PetCaptureContext.WildBaby);
        }
        else
        {
            return false;
        }
        target = candidate;
        return true;
    }

    private bool IsPetCaptureCompletionValid(PetCaptureRequest request,
        MonsterRuntimeSnapshot expected, PetCaptureKind expectedKind)
    {
        var reason = PetCaptureCompletionInvalidReason(request, expected, expectedKind);
        if (reason is not null)
        {
            Console.WriteLine($"[pet-capture] completion invalid reason={reason} " +
                $"character={_character?.Name ?? "<none>"} target={request.TargetObjectId}");
        }
        return reason is null;
    }

    private string? PetCaptureCompletionInvalidReason(PetCaptureRequest request,
        MonsterRuntimeSnapshot expected, PetCaptureKind expectedKind)
    {
        if (_character is not { } character) return "no-character";
        if (!HasPetCaptureInventoryCapacity(character, request.KitBagSlot)) return "net-or-bag";
        if (!TryResolvePetCaptureTarget(request, out var current, out var currentKind)) return "target-unresolved";
        if (currentKind != expectedKind) return "kind-changed";
        if (current.RuntimeInstanceId != expected.RuntimeInstanceId ||
            current.SpawnGeneration != expected.SpawnGeneration) return "instance-changed";
        if (current.HealthRevision != expected.HealthRevision) return "health-revision-changed";
        return null;
    }
}
