using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// petcapture: wild baby-pet capture (species table, switch, durable contract) and the First Pet quest rows.
internal static class WildPetCaptureChecks
{
    public const string CheckName = "Wild baby pet capture and First Pet quest";

    public static Task RunAsync()
    {
        try
        {
            CheckSpeciesTableAgainstClientTemplates();
            CheckContract();
            CheckChancesAndAptitudes();
            CheckSwitch();
            CheckFirstPetQuest();
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }
        return Task.CompletedTask;
    }

    private static void Install(decimal value) =>
        ServerRates.Install(ServerRatesSnapshot.Create(
            [KeyValuePair.Create(ServerRateKeys.WildPetCapture, value)],
            1, DateTimeOffset.UtcNow, "check", "test"));

    private static void CheckSpeciesTableAgainstClientTemplates()
    {
        foreach (var (mapId, key) in WildPetCapturePolicy.Templates)
        {
            var seed = MonsterTemplateSeeds.Monsters.Where(t => t.SourceMapId == mapId && t.TemplateKey == key).ToArray();
            Check.True(seed.Length == 1 && seed[0].IsPet, $"map {mapId} {key} is one published client pet template");
            Check.True(WildPetCapturePolicy.TryGetEgg(mapId, key, out var egg), $"map {mapId} {key} has an egg");
            var model = seed.Length == 1 ? seed[0].ModelFile : "";
            var expected = model.StartsWith("flower_", StringComparison.Ordinal) ? 10151u
                : model.StartsWith("cow_", StringComparison.Ordinal) ? 10152u : 0u;
            Check.Equal(expected, egg, $"map {mapId} {key} ({model}) yields the egg of its client model");
        }
        // every client pet template on the wild maps is covered, and no other map is wild
        foreach (var template in MonsterTemplateSeeds.Monsters.Where(t => t.IsPet && t.SourceMapId is { } m &&
                     WildPetCapturePolicy.IsWildMap(m)))
        {
            Check.True(WildPetCapturePolicy.IsWildPetTemplate(template.SourceMapId!.Value, template.TemplateKey),
                $"client pet template {template.SourceMapId} {template.TemplateKey} is capturable");
        }
        foreach (var map in new[] { 0, 1, 42, 200, 205, 207 })
        {
            Check.True(!WildPetCapturePolicy.IsWildMap(map), $"map {map} keeps its own capture rule (not wild)");
        }
        Check.True(!WildPetCapturePolicy.IsWildPetTemplate(200, "A_normal_male_001") &&
            !WildPetCapturePolicy.IsWildPetTemplate(42, "A_normalA_male_001") &&
            !WildPetCapturePolicy.IsWildPetTemplate(2, "B_normalA_wraith_006"), "Medusa elf, farm dog and a wraith are not wild pets");
        Check.True(WildPetCapturePolicy.TryGetEgg(11, "A_normal_male_001", out var marathon) && marathon == 10151 &&
            WildPetCapturePolicy.TryGetEgg(18, "A_normal_male_001", out var megara) && megara == 10152,
            "the same template key is a Pixie on Marathon and a Minotaur on Megara");
    }

    private static void CheckContract()
    {
        var wild = new PetCaptureIntent(11095, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), 7, 11,
            10151, MedusaEncounterDifficulty.Normal, PetCaptureContext.WildBaby);
        Check.True(wild.IsValid && (wild with { EggItemId = 10152 }).IsValid, "wild intent accepts the Pixie and Minotaur eggs");
        Check.True(!(wild with { EggItemId = 10159 }).IsValid && !(wild with { EggItemId = 10150 }).IsValid &&
            !(wild with { Difficulty = MedusaEncounterDifficulty.Enhanced }).IsValid &&
            !(wild with { Context = PetCaptureContext.LelantineDog }).IsValid,
            "wild intent cannot masquerade as a farm dog, Medusa elf or another difficulty");
        var bytes = PetDurableCommandContract.CanonicalBagActivation(15, wild);
        Check.Equal((byte)4, bytes[4], "wild capture has its own canonical marker byte 4");
        var medusa = new PetCaptureIntent(40073, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            7, 11, 10150, MedusaEncounterDifficulty.Enhanced);
        Check.Equal("0001000F010200009C8900112233445566778899AABBCCDDEEFF00000007000000000000000B000027A6",
            Convert.ToHexString(PetDurableCommandContract.CanonicalBagActivation(15, medusa)),
            "historical Medusa capture canonical bytes are unchanged");
        var dog = medusa with { EggItemId = 10159, Difficulty = MedusaEncounterDifficulty.Normal, Context = PetCaptureContext.LelantineDog };
        Check.True(dog.IsValid && PetDurableCommandContract.CanonicalBagActivation(15, dog)[4] == 3, "farm dog marker stays 3");
    }

    private static void CheckChancesAndAptitudes()
    {
        var chances = Enumerable.Range(10080, 5).Select(WildPetCapturePolicy.CaptureChance).ToArray();
        Check.True(chances.Zip(chances.Skip(1)).All(pair => pair.First < pair.Second) && chances[^1] <= 1.0 && chances[0] > 0,
            "tool chances rise Wooden < Iron < Silver < Gold < Tuck Net");
        Check.Equal(0.0, WildPetCapturePolicy.CaptureChance(10085), "a non-tool item never captures");
        Check.Equal("1,1,1,1,1", string.Join(",", WildPetCapturePolicy.ParseChances("100,100,100,100,100")), "override parses percent");
        Check.Equal("0.3,0.4,0.55,0.7,0.95", string.Join(",", WildPetCapturePolicy.ParseChances("1,2,3")), "bad override keeps the defaults");
        Check.Equal("0.3,0.4,0.55,0.7,0.95", string.Join(",", WildPetCapturePolicy.ParseChances("10,20,x,40,50")), "unparsable override keeps the defaults");
        var weights = WildPetCapturePolicy.AptitudeWeights;
        Check.Equal(10_000, weights.Sum(w => w.BasisPoints), "aptitude weights total 10,000 basis points");
        Check.True(weights.Select(w => w.Aptitude).Distinct().Count() == weights.Count &&
            weights.All(w => w.Aptitude is 1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 10 or 12 or 14 && w.BasisPoints > 0),
            "aptitude weights use the native capture aptitudes once each");
    }

    private static void CheckSwitch()
    {
        ServerRates.ResetToDefaults();
        Check.True(!WildPetCapturePolicy.Enabled, "wild capture is off by default");
        Check.True(!WildPetCapturePolicy.AdmitsAuthoredSpawn(11, "A_normal_male_001"), "switch off: generated pet rows are not admitted");
        Check.True(!WildPetCapturePolicy.TryGetRespawnInterval(2, "A_normal_male_001", out _), "switch off: respawn unchanged");
        var catalog = WorldBossCatalog.Empty;
        Check.Equal(TimeSpan.FromSeconds(10), MonsterRespawnPolicyRules.ResolveRespawnInterval(catalog, 2, "A_normal_male_001",
            TimeSpan.FromSeconds(10)), "switch off: Athens suburb pets keep the ordinary respawn");
        Install(1);
        Check.True(WildPetCapturePolicy.Enabled && WildPetCapturePolicy.AdmitsAuthoredSpawn(11, "A_normal_male_001") &&
            !WildPetCapturePolicy.AdmitsAuthoredSpawn(11, "A_normal_spider_006"), "switch on: only the pet templates are admitted");
        Check.Equal(WildPetCapturePolicy.RespawnInterval, MonsterRespawnPolicyRules.ResolveRespawnInterval(catalog, 2,
            "A_normal_male_001", TimeSpan.FromSeconds(10)), "switch on: wild pets wait the generated interval");
        Check.Equal(TimeSpan.FromSeconds(10), MonsterRespawnPolicyRules.ResolveRespawnInterval(catalog, 2,
            "B_normalA_wraith_006", TimeSpan.FromSeconds(10)), "switch on: other monsters keep the ordinary respawn");
        Check.Equal(TimeSpan.FromSeconds(60), MonsterRespawnPolicyRules.ResolveRespawnInterval(catalog, 42,
            "A_normalA_male_001", TimeSpan.FromSeconds(10)), "switch on: farm dogs keep their 60 s");
        Check.True(MonsterAggroPolicy.IsAggressive(30, "A_normal_male_001"), "aggro rules are untouched");
        ServerRates.ResetToDefaults();
    }

    private static void CheckFirstPetQuest()
    {
        var steps = FirstPetQuest.Steps;
        Check.Equal(2, steps.Count, "two First Pet rows (Sparta 302, Athens 1302)");
        Check.True(steps.All(s => s.MinLevel == 10 && s.IsOneTimeUtility && !s.IsDaily && s.MaxCompletionsPerDay == 0 &&
            s.TalentPoints == 2 && s.ResponderKey.EndsWith("_110", StringComparison.Ordinal) &&
            s.GiverKey.EndsWith("_094", StringComparison.Ordinal)),
            "level 10, one time, TP 2, Newbie Guide *_094 to Pet Master *_110");
        Check.True(GameClientHandler.IsWithinAcceptBand(10, steps[0]) && !GameClientHandler.IsWithinAcceptBand(9, steps[0]) &&
            GameClientHandler.IsWithinAcceptBand(150, steps[0]), "accept band: level 10 and up");
        if (Environment.GetEnvironmentVariable("GODSWAR_FIRST_PET_QUEST") != "1")
        {
            Check.True(StarterQuestChain.Find(302) is null && StarterQuestChain.Find(1302) is null,
                "switch off: the quest chain does not carry 302 / 1302");
        }
    }
}
