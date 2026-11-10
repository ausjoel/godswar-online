using System.Buffers.Binary;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class AtlantisPetSpawnChecks
{
    public const string CheckName = "Atlantis published pets, capture isolation, and canonical compatibility";
    private static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static Task RunAsync()
    {
        foreach (var mode in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs }) CheckRuntime(mode);
        CheckCanonical();
        return Task.CompletedTask;
    }

    private static MapInstance CreateMap(MonsterRuntimeMode mode)
    {
        var map = new MapInstance(WorldInstanceDescriptor.Create(RealmId.Tempest, WorldInstanceId.New(),
            new(205), InstanceKind.Dungeon, 5, Start), mode);
        Check.True(map.TryConfigureAtlantisWaves(AtlantisLiveWaveChecks.Content(), [(101, 90)], Start) &&
            map.TryStartAtlantisEncounter(Start, out _) && map.TrySpawnPendingAtlantisWave(Start, out _),
            "pet fixture starts a real published Atlantis group");
        return map;
    }

    private static void CheckRuntime(MonsterRuntimeMode mode)
    {
        var map = CreateMap(mode);
        Check.True(map.TryConfigureAtlantisWaves(AtlantisLiveWaveChecks.Content(), [(101, 90)], Start),
            "pet setup is idempotent");
        Check.Equal(14, map.InitializeMonsters([], Start).Count, "readiness preserves 12 scored monsters plus two pets");
        foreach (var pet in AtlantisPetSpawnPolicy.Spawns)
        {
            var monster = map.SnapshotMonsters().Single(value => value.ObjectId == pet.ObjectId);
            Check.True(monster.Definition.TemplateKey == pet.Placement.TemplateKey &&
                monster.X == pet.Placement.X && monster.Z == pet.Placement.Z &&
                monster.Definition.Tier == 1 && monster.RespawnAt is null &&
                monster.CurrentHealth == (pet.ObjectId == AtlantisPetSpawnPolicy.MermaidObjectId ? 90_000u : 10u) &&
                BinaryPrimitives.ReadUInt16LittleEndian(monster.Definition.Packet.AsSpan(6)) == 205,
                $"{mode}: exact published pet appearance and requested position pass the native map gate");
            Check.True(map.CanCaptureAtlantisPet(101, pet.ObjectId) && !map.CanCaptureAtlantisPet(102, pet.ObjectId) &&
                !map.CanCaptureAtlantisPet(101, 42000), "only admitted characters and ambient pet IDs are capture eligible");
            var other = CreateMap(mode).SnapshotMonsters().Single(value => value.ObjectId == pet.ObjectId);
            Check.True(!map.TryCaptureMonster(other, Start, out _), "equal object IDs from another run cannot capture this pet");
            Check.True(!map.TryCaptureMonster(monster with { HealthRevision = monster.HealthRevision + 1 }, Start, out _),
                "stale health evidence cannot claim the pet");
            Check.True(map.TryCaptureMonster(monster, Start.AddSeconds(1), out var death) && death.Killed &&
                !map.TryCaptureMonster(monster, Start.AddSeconds(1), out _), "each pet has one exact capture claim");
            var score = AtlantisMonsterKillScoring.RecordCommitted(map, AtlantisLiveWaveChecks.Content(),
                map.WorldInstanceId, death, Start.AddSeconds(1));
            Check.True(score.PointsAwarded == 0, "even a replayed committed pet death is outside the score roster");
        }
        map.TryGetAtlantisRunSnapshot(out var run);
        map.TryGetAtlantisWaveSnapshot(out var wave);
        Check.True(run.TeamPoints == 0 && wave.RemainingMonsterCount == 12 && wave.CommittedKillCount == 0 &&
            !map.TrySpawnPendingAtlantisWave(Start.AddSeconds(2), out _), "capturing both pets cannot score or clear a wave");
        map.AdvanceMonsters(Start.AddMinutes(5));
        map.AdvanceMonsters(Start.AddMinutes(5).AddSeconds(1));
        Check.True(map.SnapshotMonsters().Where(value => value.ObjectId >= AtlantisPetSpawnPolicy.MermaidObjectId)
            .All(value => !value.IsAlive && !value.IsSpawned && value.RespawnAt is null), "captured pets never respawn");

        var passive = CreateMap(mode);
        var runtime = passive.InitializeMonsters([], Start);
        for (var tick = 0; tick < 30; tick++)
        {
            var update = runtime.Advance(Start.AddSeconds(tick), [new(101, -59, 88, true, 101)]);
            Check.True(update.Updates.All(value => value.Kind != MonsterRuntimeUpdateKind.Attacked ||
                value.Monster.ObjectId < AtlantisPetSpawnPolicy.MermaidObjectId), "unprovoked tier-one pets stay passive");
            foreach (var pet in AtlantisPetSpawnPolicy.Spawns)
            {
                var actual = runtime.Snapshot().Single(value => value.ObjectId == pet.ObjectId);
                var dx = actual.X - pet.Placement.X;
                var dz = actual.Z - pet.Placement.Z;
                Check.True(dx * dx + dz * dz <= 4.001f, "two-unit pet roaming stays within verified wall clearance");
            }
        }
        passive.TryCancelAtlantisEncounter(Start.AddMinutes(1), out _);
        var remainingPet = passive.SnapshotMonsters().Single(value => value.ObjectId == AtlantisPetSpawnPolicy.MermaidObjectId);
        Check.True(!passive.CanCaptureAtlantisPet(101, remainingPet.ObjectId) &&
            !passive.TryCaptureMonster(remainingPet, Start.AddMinutes(1), out _), "cancelled runs reject capture completion");
    }

    private static void CheckCanonical()
    {
        // F-128: a Mermaid kill never drops Morning Dew 1-5; other items, other maps and the Siren are untouched.
        for (uint dewItem = 10_130; dewItem <= 10_134; dewItem++)
            Check.True(AtlantisPetSpawnPolicy.BlocksMermaidKillDrop(205, AtlantisPetSpawnPolicy.MermaidTemplateKey, dewItem),
                "a Mermaid kill withholds every Morning Dew");
        Check.True(!AtlantisPetSpawnPolicy.BlocksMermaidKillDrop(205, AtlantisPetSpawnPolicy.MermaidTemplateKey, 10_135) &&
            !AtlantisPetSpawnPolicy.BlocksMermaidKillDrop(205, AtlantisPetSpawnPolicy.SirenTemplateKey, 10_132) &&
            !AtlantisPetSpawnPolicy.BlocksMermaidKillDrop(11, AtlantisPetSpawnPolicy.MermaidTemplateKey, 10_132),
            "only the Mermaid's Morning Dew on Atlantis is withheld");
        var medusa = new PetCaptureIntent(40073, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            7, 11, 10150, MedusaEncounterDifficulty.Enhanced);
        Check.Equal("0001000F010200009C8900112233445566778899AABBCCDDEEFF00000007000000000000000B000027A6",
            Convert.ToHexString(PetDurableCommandContract.CanonicalBagActivation(15, medusa)),
            "historical Medusa capture canonical bytes remain exact");
        var atlantis = medusa with { TargetObjectId = AtlantisPetSpawnPolicy.MermaidObjectId,
            EggItemId = 10158, Difficulty = MedusaEncounterDifficulty.Normal, Context = PetCaptureContext.AtlantisMerman };
        Check.True(atlantis.IsValid && PetDurableCommandContract.CanonicalBagActivation(15, atlantis)[4] == 2 &&
            !(atlantis with { EggItemId = 10150 }).IsValid &&
            !(atlantis with { Difficulty = MedusaEncounterDifficulty.Mythic }).IsValid &&
            !(atlantis with { Context = PetCaptureContext.Medusa }).IsValid,
            "explicit Atlantis capture cannot masquerade as a Medusa difficulty or egg");
        // F-122: the Mermaid's net gives a Morning Dew under its own canonical marker; no egg id passes.
        var dew = atlantis with { EggItemId = 10132, Context = PetCaptureContext.AtlantisMermaidDew };
        Check.True(dew.IsValid && PetDurableCommandContract.CanonicalBagActivation(15, dew)[4] == 5 &&
            !(dew with { EggItemId = 10158 }).IsValid && !(dew with { EggItemId = 10135 }).IsValid &&
            AtlantisPetSpawnPolicy.MermaidGivesDew && AtlantisPetSpawnPolicy.MermaidDewItemId == 10132 &&
            !CapturablePetCombatPolicy.IsCaptureTarget(205, AtlantisPetSpawnPolicy.MermaidTemplateKey,
                AtlantisPetSpawnPolicy.MermaidObjectId) &&
            CapturablePetCombatPolicy.IsCaptureTarget(205, AtlantisPetSpawnPolicy.SirenTemplateKey,
                AtlantisPetSpawnPolicy.SirenObjectId),
            "F-122 Mermaid: Morning Dew context, no ten-hit rule; the Siren keeps it");
        var egg = CompactItemEntry.Empty with { Id = 10158, Stack = 1, Grade = 1, Quality = 1 };
        var bag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 4, egg.ToCompactString());
        Check.True(GameClientHandler.TryResolvePetCaptureAcquisition(GameDefaults.EmptyKitBag, bag,
            out var slot, out var acquired, 10158) && slot == 4 && acquired == egg,
            "native acquisition projection recognizes the newly committed Merman egg");
    }
}
