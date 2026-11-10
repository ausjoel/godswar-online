using Godswar.Server.Application.Progression;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class DeveloperProgressionCommandChecks
{
    public const string CheckName =
        "Authorized durable developer progression commands";

    public static Task RunAsync()
    {
        CheckParsing();
        CheckMutationPolicy();
        CheckProjectionSafety();
        CheckExactPersistenceScope();
        return Task.CompletedTask;
    }

    private static void CheckParsing()
    {
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "Hero:/level set 200",
                out var setLevel,
                out _) &&
            setLevel is
            {
                Operation:
                    DeveloperProgressionOperation.SetFighterLevel,
                Value: 200
            },
            "level set accepts the upper fighter-level bound after a sender prefix");
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "/level add -17",
                out var lowerLevel,
                out _) &&
            lowerLevel is
            {
                Operation:
                    DeveloperProgressionOperation.AdjustFighterLevel,
                Value: -17
            },
            "level add accepts a bounded negative delta");
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "/level add +5",
                out var raiseLevel,
                out _) &&
            raiseLevel?.Value == 5,
            "level add accepts an explicit positive sign");
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "/talentpoints add 2147483647",
                out var talent,
                out _) &&
            talent is
            {
                Operation:
                    DeveloperProgressionOperation.AddTalentPoints,
                Value: int.MaxValue
            },
            "talent command accepts the storage-type input boundary");
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "Hero>/talent add 25",
                out var talentAlias,
                out _) &&
            talentAlias?.Operation ==
                DeveloperProgressionOperation.AddTalentPoints,
            "short talent alias is recognized at a sender boundary");
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "/zodiacenergy add 900",
                out var zodiac,
                out _) &&
            zodiac is
            {
                Operation:
                    DeveloperProgressionOperation.AddZodiacEnergy,
                Value: 900
            },
            "zodiac-energy command parses a positive whole-energy grant");
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                "/zenergy add 1",
                out var zodiacAlias,
                out _) &&
            zodiacAlias?.Operation ==
                DeveloperProgressionOperation.AddZodiacEnergy,
            "short Zodiac-energy alias is recognized");

        AssertConsumedButInvalid("/level set 0");
        AssertConsumedButInvalid("/level set 201");
        AssertConsumedButInvalid("/level add 0");
        AssertConsumedButInvalid("/level add -200");
        AssertConsumedButInvalid("/talentpoints add 0");
        AssertConsumedButInvalid("/zodiacenergy add -1");
        AssertConsumedButInvalid("/zenergy add 2147483648");
        AssertConsumedButInvalid("/level set 50 trailing");
        Check.True(
            !DeveloperProgressionChatCommand.TryParse(
                "ordinary map chat about leveling",
                out _,
                out _),
            "ordinary map chat is not consumed");
        Check.True(
            !DeveloperProgressionChatCommand.TryParse(
                "/leveling add 1",
                out _,
                out _),
            "a longer slash word does not collide with the level command");
    }

    private static void CheckMutationPolicy()
    {
        var before = new DeveloperProgressionProjection(
            FighterLevel: 80,
            FighterExperience: 4_000_000_000L,
            TalentPoints: 17,
            ZodiacLevel: 1,
            ZodiacEnergy: 900,
            ZodiacEnergyRemainderX100: 50,
            ProgressionRevision: 41);

        var setLevel = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.SetFighterLevel,
                120));
        Check.True(setLevel.Changed, "setting a different level commits");
        var levelAfter = RequireCurrent(setLevel);
        Check.Equal(120, levelAfter.FighterLevel, "set-level target");
        Check.Equal(0L, levelAfter.FighterExperience, "set-level resets fighter EXP");
        Check.Equal(before.TalentPoints, levelAfter.TalentPoints, "set-level preserves talent points");
        Check.Equal(before.ZodiacEnergy, levelAfter.ZodiacEnergy, "set-level preserves Zodiac energy");
        Check.Equal(before.ZodiacEnergyRemainderX100, levelAfter.ZodiacEnergyRemainderX100, "set-level preserves fractional Zodiac energy");
        Check.Equal(42L, levelAfter.ProgressionRevision, "set-level advances the progression revision exactly once");

        var sameLevelWithExperience = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.SetFighterLevel,
                before.FighterLevel));
        Check.True(
            sameLevelWithExperience.Changed &&
            RequireCurrent(sameLevelWithExperience)
                .FighterExperience == 0,
            "setting the current level still atomically clears nonzero EXP");
        var unchanged = DeveloperProgressionMutation.Apply(
            before with { FighterExperience = 0 },
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.SetFighterLevel,
                before.FighterLevel));
        Check.True(
            unchanged.Status ==
                DeveloperProgressionMutationStatus.Unchanged &&
            RequireCurrent(unchanged).ProgressionRevision ==
                before.ProgressionRevision,
            "already-matching level and zero EXP is a revision-neutral no-op");

        var adjusted = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AdjustFighterLevel,
                -79));
        Check.True(
            adjusted.Changed &&
            RequireCurrent(adjusted) is
            {
                FighterLevel: 1,
                FighterExperience: 0
            },
            "bounded downward level adjustment reaches level one and clears EXP");
        var rejectedLevel = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AdjustFighterLevel,
                -80));
        Check.True(
            rejectedLevel.Status ==
                DeveloperProgressionMutationStatus.TargetOutOfRange &&
            RequireCurrent(rejectedLevel) == before,
            "out-of-range derived level is rejected without mutation");

        var talent = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AddTalentPoints,
                25));
        var talentAfter = RequireCurrent(talent);
        Check.Equal(42, talentAfter.TalentPoints, "talent grant is additive");
        Check.Equal(before.FighterLevel, talentAfter.FighterLevel, "talent grant preserves fighter level");
        Check.Equal(before.FighterExperience, talentAfter.FighterExperience, "talent grant preserves fighter EXP");
        var talentOverflow = DeveloperProgressionMutation.Apply(
            before with { TalentPoints = int.MaxValue },
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AddTalentPoints,
                1));
        Check.True(
            talentOverflow.Status ==
                DeveloperProgressionMutationStatus.ArithmeticOverflow,
            "talent-point overflow is rejected before integer wraparound");

        var zodiac = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AddZodiacEnergy,
                99));
        Check.True(
            zodiac.Changed &&
            RequireCurrent(zodiac) is
            {
                ZodiacEnergy: 999,
                ZodiacEnergyRemainderX100: 50
            },
            "Zodiac grant adds whole energy while preserving the remainder");
        var zodiacOverflow = DeveloperProgressionMutation.Apply(
            before,
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AddZodiacEnergy,
                100));
        Check.True(
            zodiacOverflow.Status ==
                DeveloperProgressionMutationStatus
                    .ZodiacStorageLimitExceeded &&
            RequireCurrent(zodiacOverflow) == before,
            "Zodiac grant rejects total fixed-point energy above the level cap");

        var revisionExhausted = DeveloperProgressionMutation.Apply(
            before with { ProgressionRevision = long.MaxValue },
            new DeveloperProgressionCommand(
                DeveloperProgressionOperation.AddTalentPoints,
                1));
        Check.True(
            revisionExhausted.Status ==
                DeveloperProgressionMutationStatus.RevisionExhausted &&
            RequireCurrent(revisionExhausted).TalentPoints ==
                before.TalentPoints,
            "revision exhaustion rejects the mutation without partial state");
        Check.Throws<InvalidDataException>(
            () => DeveloperProgressionMutation.Apply(
                before with { FighterExperience = (long)uint.MaxValue + 1 },
                new DeveloperProgressionCommand(
                    DeveloperProgressionOperation.AddTalentPoints,
                    1)),
            "corrupt persisted fighter EXP is rejected before mutation");
    }

    private static void CheckProjectionSafety()
    {
        var stats = new CharacterStats
        {
            Level = 80,
            MaxHp = 250_000,
            MaxMp = 4_000
        };
        var character = new GameCharacter
        {
            Level = 80,
            Experience = 123_456,
            TalentPoints = 17,
            TalentExperience = 88,
            FighterLevelSealed = true,
            ZodiacLevel = 1,
            ZodiacEnergy = 900,
            ZodiacEnergyRemainderX100 = 50,
            MaxHp = 250_000,
            CurrentHp = 196_614,
            MaxMp = 4_000,
            CurrentMp = 2_559,
            VitalsRevision = 73,
            CalculatedStats = stats
        };
        var projection = new DeveloperProgressionProjection(
            FighterLevel: 120,
            FighterExperience: 0,
            TalentPoints: 42,
            ZodiacLevel: 1,
            ZodiacEnergy: 999,
            ZodiacEnergyRemainderX100: 50,
            ProgressionRevision: 74);

        GameSessionRegistry.ApplyDeveloperProgressionProjection(
            character,
            projection);
        Check.Equal(120, character.Level, "live projection installs fighter level");
        Check.Equal(0L, character.Experience, "live projection installs the intentional EXP reset");
        Check.Equal(42, character.TalentPoints, "live projection installs talent points");
        Check.Equal(88, character.TalentExperience, "live projection preserves talent experience");
        Check.True(character.FighterLevelSealed, "live projection preserves level-seal state");
        Check.Equal(196_614, character.CurrentHp, "live projection preserves current HP exactly");
        Check.Equal(2_559, character.CurrentMp, "live projection preserves current MP exactly");
        Check.Equal(250_000, character.MaxHp, "progression-only projection does not replace maximum HP");
        Check.Equal(4_000, character.MaxMp, "progression-only projection does not replace maximum MP");
        Check.Equal(73L, character.VitalsRevision, "progression-only projection does not forge a vitals revision");
        Check.True(
            ReferenceEquals(stats, character.CalculatedStats),
            "progression-only projection does not replace calculated stats");
    }

    private static void CheckExactPersistenceScope()
    {
        var levelSet = SetClause(
            PostgresDeveloperProgressionCommandExecutor
                .FighterLevelUpdateSql);
        Check.True(
            levelSet.Contains("fighter_job_lv", StringComparison.Ordinal) &&
            levelSet.Contains("fighter_job_exp", StringComparison.Ordinal),
            "level mutation atomically writes level and the EXP reset");
        Check.True(
            !levelSet.Contains("SkillPoint", StringComparison.Ordinal) &&
            !levelSet.Contains("zodiac_energy", StringComparison.Ordinal),
            "level mutation does not write talent or Zodiac balances");

        var talentSet = SetClause(
            PostgresDeveloperProgressionCommandExecutor
                .TalentPointsUpdateSql);
        Check.True(
            talentSet.Contains("SkillPoint", StringComparison.Ordinal) &&
            !talentSet.Contains("fighter_job_lv", StringComparison.Ordinal) &&
            !talentSet.Contains("fighter_job_exp", StringComparison.Ordinal) &&
            !talentSet.Contains("zodiac_energy", StringComparison.Ordinal),
            "talent mutation writes only talent points and revision");

        var zodiacSet = SetClause(
            PostgresDeveloperProgressionCommandExecutor
                .ZodiacEnergyUpdateSql);
        Check.True(
            zodiacSet.Contains("zodiac_energy", StringComparison.Ordinal) &&
            !zodiacSet.Contains("fighter_job_lv", StringComparison.Ordinal) &&
            !zodiacSet.Contains("fighter_job_exp", StringComparison.Ordinal) &&
            !zodiacSet.Contains("SkillPoint", StringComparison.Ordinal),
            "Zodiac mutation writes only whole/remainder energy and revision");

        foreach (var sql in new[]
                 {
                     levelSet,
                     talentSet,
                     zodiacSet
                 })
        {
            Check.True(
                sql.Contains(
                    "progression_reward_revision",
                    StringComparison.Ordinal),
                "every committed developer mutation advances progression revision");
            Check.True(
                !sql.Contains("curHP", StringComparison.Ordinal) &&
                !sql.Contains("curMP", StringComparison.Ordinal) &&
                !sql.Contains("MaxHP", StringComparison.Ordinal) &&
                !sql.Contains("MaxMP", StringComparison.Ordinal),
                "developer progression SQL never writes HP or MP columns");
        }
    }

    private static DeveloperProgressionProjection RequireCurrent(
        DeveloperProgressionMutationResult result) =>
        result.Current ?? throw new InvalidOperationException(
            "Expected an authoritative developer progression projection.");

    private static string SetClause(string sql)
    {
        var whereOffset = sql.IndexOf(
            "WHERE",
            StringComparison.Ordinal);
        Check.True(whereOffset > 0, "developer mutation SQL has a WHERE guard");
        return sql[..whereOffset];
    }

    private static void AssertConsumedButInvalid(string text)
    {
        Check.True(
            DeveloperProgressionChatCommand.TryParse(
                text,
                out var command,
                out var error) &&
            command is null &&
            !string.IsNullOrWhiteSpace(error),
            $"invalid developer progression text is consumed: {text}");
    }
}
