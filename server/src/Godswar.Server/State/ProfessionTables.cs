namespace Godswar.Server.State;

/// <summary>One character's profession row (public.profession_state).</summary>
internal readonly record struct ProfessionProgress(byte Profession, int Level, int Experience, int Ap);

/// <summary>What one finished craft did to the profession (PROF slice 1).</summary>
internal readonly record struct ProfessionCraftOutcome(
    bool Success,
    int SkillExperienceGained,
    int NewLevel,
    int NewExperience,
    int NewProficiency,
    bool LevelledUp);

/// <summary>
/// PROF slice 1 (2026-11-09): the profession rules on top of the client tables. Pure functions, so the
/// ProtocolChecks can exercise them without a database. Everything the client data does not settle is marked
/// GENERATED. Protocol and evidence: Development/professions/README.md.
/// </summary>
internal static partial class ProfessionTables
{
    /// <summary>The Profession Skillbook (ItemBaseAttribute 4268, NF_L0_LS301-308 "a cost of 1 Profession Skillbook").</summary>
    public const int ProfessionSkillbookItem = 4268;

    /// <summary>The proficiency the client draws as 100.0% (it prints proficiency / 100 with "%0.1f%%").</summary>
    public const int MaxProficiency = 10_000; // GENERATED cap: the client only shows the value; 100% is the natural top

    /// <summary>Lifing_AP.xml MaxSkill="1": one profession at a time (NF_L0_LS201, NF_L0_LS401).</summary>
    public const int MaxProfessions = 1;

    /// <summary>The blueprint each profession starts with: the four blueprints no recipe item teaches.</summary>
    // GENERATED inference: 10000/20000/30000/40000 are the only Lv 1 blueprints without a recipe item.
    public static int StarterBlueprint(byte profession) => profession is >= 1 and <= 4 ? profession * 10_000 : 0;

    public static (int MaxAp, int OnlineAdd, int OfflineAdd) ApFor(int characterLevel) =>
        ApByCharacterLevel[Math.Clamp(characterLevel, 1, ApByCharacterLevel.Length - 1)];

    /// <summary>Exp needed from <paramref name="level"/> to the next level; 0 at the top level.</summary>
    public static int UpExp(int level) => level is >= 1 and < MaxProfessionLevel ? LevelRows[level].UpExp : 0;

    /// <summary>Character level Lifing_Skill.xml lists beside a profession level (PlayLv).</summary>
    public static int CharacterLevelFor(int level) => LevelRows[Math.Clamp(level, 1, MaxProfessionLevel)].PlayLv;

    public static (int SkillExp, int BlueprintExp, int CooldownSeconds) BlueprintExtras(int blueprintId) =>
        Extras.TryGetValue(blueprintId, out var extras) ? extras : (0, 0, 0);

    /// <summary>
    /// Profession Mentor learn buttons (NpcFunLifeSkill.lua page 3: 302 Alchemy, 304 Smithing, 306 Artificer,
    /// 308 Scholar) to the Lifing_Skill.xml SkillID.
    /// </summary>
    public static byte ProfessionForMentorButton(int button) => button switch
    {
        302 => 1,
        304 => 2,
        306 => 3,
        308 => 4,
        _ => 0
    };

    /// <summary>
    /// AP after <paramref name="elapsed"/> since <paramref name="since"/>: one OnlineAdd/OfflineAdd per whole
    /// ApTime (300 s), capped at MaxAP for the character's level. Returns the new AP and the new anchor (advanced
    /// by whole periods only, so a partial period is never lost).
    /// </summary>
    public static (int Ap, DateTimeOffset Anchor, int Periods) Accrue(
        int ap, DateTimeOffset since, DateTimeOffset now, int characterLevel, bool online)
    {
        var (maxAp, onlineAdd, offlineAdd) = ApFor(characterLevel);
        if (now <= since)
        {
            return (Math.Min(ap, maxAp), since, 0);
        }

        var periods = (long)((now - since).TotalSeconds / ApTimeSeconds);
        if (periods <= 0)
        {
            return (Math.Min(ap, maxAp), since, 0);
        }

        // Online: only the first period is credited at the online rate. A 15 s ticker never sees more than one,
        // so a bigger gap is time the character spent logged out and earns the offline rate.
        var gained = online
            ? onlineAdd + (periods - 1) * offlineAdd
            : periods * offlineAdd;
        var next = (int)Math.Min(maxAp, Math.Max(ap, 0) + gained);
        return (Math.Max(next, Math.Min(ap, maxAp)), since.AddSeconds(periods * ApTimeSeconds), (int)Math.Min(periods, int.MaxValue));
    }

    /// <summary>
    /// The success chance of a craft in percent: the blueprint's Odds plus the bonus the client prints beside it,
    /// "(+N%)" with N = proficiency / 500 (0x6d68e0 divides the record's proficiency word by 500).
    /// GENERATED: that the displayed bonus is added to the roll is inferred from the display alone.
    /// </summary>
    public static int SuccessPercent(ProfessionBlueprint blueprint, int proficiency) =>
        Math.Min(100, blueprint.OddsPercent + Math.Clamp(proficiency, 0, MaxProficiency) / 500);

    /// <summary>
    /// Applies one finished craft. SkillExp is only earned while the profession level is at most the blueprint's
    /// SkillLv (client text MAKE_LIFESKILL_MSG11/12: "If profession level is under X, Y can be obtained").
    /// </summary>
    public static ProfessionCraftOutcome ApplyCraft(
        ProfessionBlueprint blueprint, int level, int experience, int proficiency, int characterLevel, bool success)
    {
        if (!success)
        {
            // GENERATED: a failed roll earns nothing (the client prints only "Manufacturing failed!").
            return new(false, 0, level, experience, proficiency, false);
        }

        var (skillExp, blueprintExp, _) = BlueprintExtras(blueprint.Id);
        var gained = level <= blueprint.ExperienceUntilLevel && level < MaxProfessionLevel ? skillExp : 0;
        var newExperience = checked(experience + gained);
        var newLevel = level;
        var levelled = false;
        // GENERATED: a level is gained when the exp reaches UpExp and the character has the next level's PlayLv;
        // the remainder carries over. Below that character level the exp stops at UpExp.
        while (newLevel < MaxProfessionLevel && UpExp(newLevel) > 0 && newExperience >= UpExp(newLevel))
        {
            if (characterLevel < CharacterLevelFor(newLevel + 1))
            {
                newExperience = UpExp(newLevel);
                break;
            }

            newExperience -= UpExp(newLevel);
            newLevel++;
            levelled = true;
        }

        if (newLevel >= MaxProfessionLevel)
        {
            newExperience = 0;
        }

        // Proficiency (the record word the client prints as "%0.1f%%" after / 100) grows by BlueprintExp.
        var newProficiency = Math.Min(MaxProficiency, proficiency + Math.Max(0, blueprintExp));
        return new(true, gained, newLevel, newExperience, newProficiency, levelled);
    }

    /// <summary>
    /// PROF slice 2: the cooldown a make starts, in seconds. Only a successful make of a Type 1 blueprint
    /// ("Perpetual (periodic)", IGG news 1251) starts its Lifing_Blueprint Time (1 h, 20 h or 1 day). The one Type 0
    /// row with a Time (blueprint 32002, 1 h) gets none: the Type decides. GENERATED: a failed roll starts none.
    /// </summary>
    public static int CooldownAfterMake(ProfessionBlueprint blueprint, bool success) =>
        success && blueprint.Type == 1 ? BlueprintExtras(blueprint.Id).CooldownSeconds : 0;

    /// <summary>The seconds left of a cooldown that ends at <paramref name="until"/> (rounded up; 0 when over).</summary>
    public static int CooldownRemaining(DateTimeOffset? until, DateTimeOffset now) =>
        until is { } end && end > now ? (int)Math.Min(int.MaxValue, Math.Ceiling((end - now).TotalSeconds)) : 0;

    /// <summary>
    /// Lifing_Skill.xml Area: both capitals (server maps 0 Sparta and 1 Athens, client terrain ids 050 / 000)
    /// share the rectangle x 86..182, z -205..-125 around the square at (149,-187). The client checks it before
    /// casting; the server repeats it with a small tolerance for movement lag (GENERATED 4 units).
    /// </summary>
    public static bool InCraftingArea(int map, float x, float z, float tolerance = 4f) =>
        map is GameDefaults.SpartaCapitalMap or GameDefaults.AthensCapitalMap &&
        x >= AreaMinX - tolerance && x <= AreaMaxX + tolerance &&
        z >= AreaMinZ - tolerance && z <= AreaMaxZ + tolerance;
}
