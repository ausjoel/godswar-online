using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class FactionCrierProjectionChecks
{
    public const string CheckName =
        "Faction Crier authoritative runtime projection";

    public static Task RunAsync()
    {
        var live = CreateCharacter(
            level: 80,
            experience: 1_000,
            talentPoints: 9,
            silver: 10_000,
            gold: 500,
            bindingGold: 700,
            kitBag: "[3820,,,,,,1,1,0,1]#[]#",
            factionCrierRevision: 3,
            maxHp: 2_000,
            maxMp: 800,
            currentHp: 1_500,
            currentMp: 400);
        var persisted = CreateCharacter(
            level: 81,
            experience: 2_500,
            talentPoints: 47,
            silver: 9_500,
            gold: 270,
            bindingGold: 388,
            kitBag: "[]#[3825,,,,,,1,1,0,1]#",
            factionCrierRevision: 4,
            maxHp: 2_400,
            maxMp: 900,
            currentHp: 100,
            currentMp: 50);

        GameClientHandler.ApplyFactionCrierProjection(live, persisted);

        Check.True(
            live.Level == 81 &&
            live.Experience == 2_500 &&
            live.TalentPoints == 47 &&
            live.Silver == 9_500 &&
            live.Gold == 270 &&
            live.BindingGold == 388 &&
            live.KitBag == persisted.KitBag &&
            live.FactionCrierRevision == 4,
            "Faction Crier imports progression, all wallets, bag, and revision");
        Check.True(
            live.MaxHp == 2_400 &&
            live.MaxMp == 900 &&
            live.CurrentHp == 1_500 &&
            live.CurrentMp == 400 &&
            live.CalculatedStats?.Level == 81,
            "Faction Crier imports level-derived maxima without restoring stale vitals");

        var sameLevelLive = CreateCharacter(
            level: 160,
            experience: 0,
            talentPoints: 9,
            silver: 10_000,
            gold: 500,
            bindingGold: 700,
            kitBag: "[3820,,,,,,1,1,0,1]#[]#",
            factionCrierRevision: 3,
            maxHp: 42_000,
            maxMp: 9_000,
            currentHp: 18_210,
            currentMp: 843);
        var liveStats = sameLevelLive.CalculatedStats;
        var vitalsRevision = sameLevelLive.VitalsRevision;
        var sameLevelPersisted = CreateCharacter(
            level: 160,
            experience: 250_000,
            talentPoints: 9,
            silver: 10_000,
            gold: 500,
            bindingGold: 700,
            kitBag: "[]#[]#",
            factionCrierRevision: 4,
            maxHp: 1_500,
            maxMp: 177,
            currentHp: 1,
            currentMp: 1);

        GameClientHandler.ApplyFactionCrierProjection(
            sameLevelLive,
            sameLevelPersisted);

        Check.True(
            sameLevelLive.Experience == 250_000 &&
            sameLevelLive.FactionCrierRevision == 4 &&
            ReferenceEquals(sameLevelLive.CalculatedStats, liveStats) &&
            sameLevelLive.MaxHp == 42_000 &&
            sameLevelLive.MaxMp == 9_000 &&
            sameLevelLive.CurrentHp == 18_210 &&
            sameLevelLive.CurrentMp == 843 &&
            sameLevelLive.VitalsRevision == vitalsRevision,
            "same-level Faction Crier EXP preserves all live stats and vitals");

        var wrongCharacter = CreateCharacter(
            level: 81,
            experience: 2_500,
            talentPoints: 47,
            silver: 9_500,
            gold: 270,
            bindingGold: 388,
            kitBag: persisted.KitBag,
            factionCrierRevision: 4,
            maxHp: 2_400,
            maxMp: 900,
            currentHp: 100,
            currentMp: 50);
        wrongCharacter.Id++;
        Check.Throws<InvalidDataException>(
            () => GameClientHandler.ApplyFactionCrierProjection(
                live,
                wrongCharacter),
            "Faction Crier projection rejects identity substitution");
        return Task.CompletedTask;
    }

    private static GameCharacter CreateCharacter(
        int level,
        long experience,
        int talentPoints,
        int silver,
        int gold,
        int bindingGold,
        string kitBag,
        long factionCrierRevision,
        int maxHp,
        int maxMp,
        int currentHp,
        int currentMp)
    {
        var character = new GameCharacter
        {
            Id = 41,
            AccountId = 7,
            RealmId = RealmId.Tempest,
            Name = "FactionProjection",
            Camp = GameDefaults.SpartaCamp,
            Profession = 0,
            Level = level,
            Experience = experience,
            TalentPoints = talentPoints,
            TalentExperience = 5,
            Silver = silver,
            Gold = gold,
            BindingGold = bindingGold,
            Equipment = string.Empty,
            KitBag = kitBag,
            FactionCrierRevision = factionCrierRevision,
            MaxHp = maxHp,
            MaxMp = maxMp,
            CurrentHp = currentHp,
            CurrentMp = currentMp
        };
        character.CalculatedStats = new CharacterStats
        {
            CharacterId = character.Id,
            AccountId = character.AccountId,
            Name = character.Name,
            Profession = character.Profession,
            Level = level,
            MaxHp = maxHp,
            MaxMp = maxMp,
            CurrentHp = currentHp,
            CurrentMp = currentMp,
            PhysicalAttack = level * 10,
            PhysicalDefense = level * 5,
            BasicAttackIntervalMilliseconds = 1_500,
            BasicAttackRange = 1.7f
        };
        return character;
    }
}
