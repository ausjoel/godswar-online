using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// PROF slice 1 (2026-11-09): the life-skill message layouts read from Origin.exe 0x6cfe70, the Lifing_AP accrual,
// the craft exp / level / proficiency rules and the crafting area. Development/professions/README.md.
internal static class ProfessionSliceChecks
{
    public const string PacketCheckName = "PROF slice 1 life-skill messages 10334-10341 have the client's layouts";
    public const string RulesCheckName = "PROF slice 1 AP accrual, craft exp, level-up and area follow the client tables";
    public const string TimingCheckName = "PROF slice 1 profession messages wait for EnterUiReady";

    private static string ServerSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Godswar.Server");
            if (Directory.Exists(Path.Combine(candidate, "Game")))
            {
                return candidate;
            }

            candidate = Path.Combine(directory.FullName, "server", "src", "Godswar.Server");
            if (Directory.Exists(Path.Combine(candidate, "Game")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("server source not found");
    }

    /// <summary>
    /// Rig 2026-11-09: a 10339 sent during world entry (before ClientReady) crashed GodsWar.exe in _MSG_SYN_AP
    /// (eip 0x6d6854). The sync must run from the EnterUiReady case, never from world entry, and the AP ticker must
    /// be gated on the per-session UI mark that every transition clears.
    /// </summary>
    public static Task RunTimingAsync()
    {
        var game = Path.Combine(ServerSource(), "Game");
        string Read(string file) => File.ReadAllText(Path.Combine(game, file));
        var login = Read("GameClientHandler.LoginWorldEntry.cs");
        // ("Profession" alone is also the character class field there.)
        Check.True(new[] { "SendProfession", "LearnLifeSkill", "UnlearnLifeSkill", "LearnBlueprint", "LifeSkillAttribute",
                "SyncActivityPoints", "MakeBlueprint" }.All(name => !login.Contains(name, StringComparison.Ordinal)),
            "world entry sends nothing profession-related");
        var dispatch = Read("GameClientHandler.Dispatch.cs");
        var uiReady = dispatch.IndexOf("case Opcodes.EnterUiReady:", StringComparison.Ordinal);
        var nextCase = dispatch.IndexOf("case Opcodes.GameServerReady:", uiReady, StringComparison.Ordinal);
        var sync = dispatch.IndexOf("SendProfessionStateAfterUiReadyAsync", StringComparison.Ordinal);
        Check.True(uiReady > 0 && sync > uiReady && sync < nextCase, "the profession sync runs in the EnterUiReady case");
        Check.Equal(1, dispatch.Split("SendProfessionStateAfterUiReadyAsync").Length - 1, "and nowhere else in dispatch");
        var professions = Read("GameClientHandler.Professions.cs");
        Check.True(professions.Contains("if (!_enterUiReadyReceived || IsMapTransitionPending", StringComparison.Ordinal),
            "the sync refuses before EnterUiReady and during a map transition");
        var ticker = Read("GameSessionRegistry.ProfessionAp.cs");
        Check.True(ticker.Contains("Where(c => ProfessionUiReady(c, _professionUiReady))", StringComparison.Ordinal),
            "the online AP ticker only sends to sessions marked UI-ready");
        foreach (var file in new[] { "GameClientHandler.MapTransitions.cs", "GameClientHandler.InstanceTransitions.cs",
                     "GameClientHandler.SameMapSceneTransitions.cs", "GameClientHandler.MovementCombat.cs" })
        {
            Check.True(Read(file).Contains("_registry.UnmarkProfessionUiReady(_session);", StringComparison.Ordinal),
                $"{file} clears the UI-ready mark");
        }

        return Task.CompletedTask;
    }

    private static int I32(byte[] packet, int offset) => BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset));

    private static void Header(byte[] packet, ushort opcode, int length, string name)
    {
        Check.Equal(length, packet.Length, $"{name} length");
        Check.Equal((ushort)length, BinaryPrimitives.ReadUInt16LittleEndian(packet), $"{name} size word");
        Check.Equal(opcode, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)), $"{name} opcode");
    }

    public static Task RunPacketsAsync()
    {
        var learn = PacketBuilder.LearnLifeSkill(2, 7, 1234);
        Header(learn, 10334, 16, "10334 learn profession");
        Check.True(I32(learn, 4) == 2 && I32(learn, 8) == 7 && I32(learn, 12) == 1234, "10334 skill, level, exp");

        var unlearn = PacketBuilder.UnlearnLifeSkill(3);
        Header(unlearn, 10335, 8, "10335 unlearn profession");
        Check.Equal(3, I32(unlearn, 4), "10335 skill");

        var blueprint = PacketBuilder.LearnBlueprint(1, 10100, 250, 0, fresh: true);
        Header(blueprint, 10336, 24, "10336 learn blueprint");
        Check.True(I32(blueprint, 4) == 1 && I32(blueprint, 8) == 10100 && I32(blueprint, 12) == 250 &&
            I32(blueprint, 16) == 0 && blueprint[20] == 1, "10336 skill, blueprint, proficiency, cooldown, fresh");
        Check.Equal((byte)0, PacketBuilder.LearnBlueprint(1, 10100, 0, 0, fresh: false)[20], "10336 login sync is not fresh");

        var forget = PacketBuilder.UnlearnBlueprint(4, 40000);
        Header(forget, 10337, 12, "10337 unlearn blueprint");
        Check.True(I32(forget, 4) == 4 && I32(forget, 8) == 40000, "10337 skill, blueprint");

        var attribute = PacketBuilder.LifeSkillAttribute(1, 10000, PacketBuilder.BlueprintProficiencyAttribute, 52);
        Header(attribute, 10338, 20, "10338 attribute");
        Check.True(I32(attribute, 4) == 1 && I32(attribute, 8) == 10000 && attribute[12] == 4 && I32(attribute, 16) == 52,
            "10338 skill, blueprint, kind, value");

        var ap = PacketBuilder.SyncActivityPoints(1500, 2000);
        Header(ap, 10339, 12, "10339 AP (12 bytes, as captured on Origin)");
        Check.True(I32(ap, 4) == 1500 && I32(ap, 8) == 2000, "10339 current, max");

        var refusal = PacketBuilder.MakeBlueprintError(1, 10000, PacketBuilder.CraftErrorActivityPoints);
        Header(refusal, 10340, 16, "10340 refusal");
        Check.Equal(0x502, I32(refusal, 12), "10340 error ERROR_0502 at +12");

        var made = PacketBuilder.MakeBlueprintReturn(1, 10000, 1, 30, 4002, 5, bound: false);
        Header(made, 10341, 96, "10341 make return");
        Check.True(I32(made, 4) == 1 && I32(made, 8) == 10000 && I32(made, 12) == 1 && I32(made, 16) == 30,
            "10341 skill, blueprint, result, exp");
        Check.True(I32(made, 24) == 4002 && I32(made, 28) == -1 && I32(made, 44) == -1 && made[48] == 1 &&
            made[49] == 1 && made[50] == 0 && made[51] == 5, "10341 item record: id, empty attributes, quality, grade, unbound, stack");
        var failed = PacketBuilder.MakeBlueprintReturn(1, 10000, 0, 0, 4002, 5, bound: false);
        Check.True(failed.AsSpan(24).IndexOfAnyExcept((byte)0) < 0, "10341 failure carries no item");
        Check.Equal((byte)99, PacketBuilder.MakeBlueprintReturn(1, 1, 1, 0, 1, 500, false)[51], "10341 stack never above 99");
        return Task.CompletedTask;
    }

    public static Task RunRulesAsync()
    {
        // Switch: registered, default on.
        var definition = ServerRateCatalog.Definitions.Single(static d => d.Key == Finalise2RateKeys.Professions);
        Check.Equal(1m, definition.DefaultValue, "profession.crafting defaults to 1");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.Professions), "with no rate row the switch reads on");

        // Lifing_AP.xml rows.
        Check.Equal((2000, 30, 15), ProfessionTables.ApFor(1), "Lifing_AP level 1");
        Check.Equal((14487, 66, 33), ProfessionTables.ApFor(200), "Lifing_AP level 200");
        Check.Equal(ProfessionTables.ApFor(200), ProfessionTables.ApFor(250), "levels past 200 use the last row");
        Check.Equal(300, ProfessionTables.ApTimeSeconds, "ApTime");

        var t0 = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        Check.Equal((100, t0, 0), ProfessionTables.Accrue(100, t0, t0.AddSeconds(299), 1, online: true), "under one period: nothing");
        Check.Equal((130, t0.AddSeconds(300), 1), ProfessionTables.Accrue(100, t0, t0.AddSeconds(310), 1, online: true),
            "one online period: +OnlineAdd, anchor moves 300 s");
        Check.Equal((100 + 15 * 12, t0.AddHours(1), 12), ProfessionTables.Accrue(100, t0, t0.AddHours(1).AddSeconds(5), 1, online: false),
            "one hour offline: 12 x OfflineAdd");
        Check.Equal(2000, ProfessionTables.Accrue(1990, t0, t0.AddDays(3), 1, online: false).Ap, "capped at MaxAP");
        Check.Equal(2000, ProfessionTables.Accrue(2500, t0, t0.AddDays(3), 1, online: false).Ap,
            "AP above the level's MaxAP is clamped to it");

        // Lifing_Skill.xml UpExp / PlayLv.
        Check.Equal(960, ProfessionTables.UpExp(1), "UpExp level 1");
        Check.Equal(621557, ProfessionTables.UpExp(29), "UpExp level 29");
        Check.Equal(0, ProfessionTables.UpExp(30), "level 30 is the top");
        Check.Equal(70, ProfessionTables.CharacterLevelFor(10), "PlayLv of level 10");

        // Blueprint extras (SkillExp / BlueprintExp / Time).
        Check.Equal((30, 26, 0), ProfessionTables.BlueprintExtras(10000), "blueprint 10000 SkillExp 30, BlueprintExp 26");
        Check.Equal(3600, ProfessionTables.BlueprintExtras(13000).CooldownSeconds, "blueprint 13000 Time 0,0,0,1,0,0 = 1 hour");

        Check.True(ProfessionBlueprintCatalog.TryGet(10000, out var potion), "blueprint 10000");
        var outcome = ProfessionTables.ApplyCraft(potion, 1, 0, 0, 50, success: true);
        Check.True(outcome is { Success: true, SkillExperienceGained: 30, NewLevel: 1, NewExperience: 30, NewProficiency: 26, LevelledUp: false },
            "a success earns SkillExp and BlueprintExp");
        var levelUp = ProfessionTables.ApplyCraft(potion, 1, 950, 0, 50, success: true);
        Check.True(levelUp is { NewLevel: 2, NewExperience: 20, LevelledUp: true }, "960 exp levels 1 -> 2, remainder kept");
        Check.True(ProfessionBlueprintCatalog.TryGet(10600, out var levelSix) && levelSix.ExperienceUntilLevel == 9 &&
            ProfessionTables.BlueprintExtras(10600).SkillExp > 10, "blueprint 10600 earns exp up to level 9");
        var gated = ProfessionTables.ApplyCraft(levelSix, 6, ProfessionTables.UpExp(6) - 10, 0, 54, success: true);
        Check.True(gated is { NewLevel: 6, LevelledUp: false } && gated.NewExperience == ProfessionTables.UpExp(6),
            "level 7 needs character level 55: exp stops at UpExp");
        var tooHigh = ProfessionTables.ApplyCraft(potion, 5, 0, 0, 200, success: true);
        Check.Equal(0, tooHigh.SkillExperienceGained, "no SkillExp above the blueprint's SkillLv (4)");
        var fail = ProfessionTables.ApplyCraft(potion, 1, 10, 7, 50, success: false);
        Check.True(fail is { Success: false, SkillExperienceGained: 0, NewExperience: 10, NewProficiency: 7 }, "a failure earns nothing");
        Check.Equal(ProfessionTables.MaxProficiency,
            ProfessionTables.ApplyCraft(potion, 1, 0, ProfessionTables.MaxProficiency - 1, 50, true).NewProficiency, "proficiency caps at 100%");

        Check.Equal(95, ProfessionTables.SuccessPercent(potion, 0), "odds without proficiency");
        Check.Equal(100, ProfessionTables.SuccessPercent(potion, 10_000), "odds + proficiency/500, capped at 100");
        Check.True(ProfessionBlueprintCatalog.TryGet(30000, out var cloak) && ProfessionTables.SuccessPercent(cloak, 2500) == 65,
            "60% + 2500/500");

        Check.Equal((byte)1, ProfessionTables.ProfessionForMentorButton(302), "mentor 302 = Alchemy");
        Check.Equal((byte)4, ProfessionTables.ProfessionForMentorButton(308), "mentor 308 = Scholar");
        Check.Equal((byte)0, ProfessionTables.ProfessionForMentorButton(207), "207 is the unlearn confirmation, not a profession");
        Check.True(Enumerable.Range(1, 4).All(static p => ProfessionBlueprintCatalog.TryGet(
            ProfessionTables.StarterBlueprint((byte)p), out var b) && b.Profession == p && b.RecipeItemId == 0 && b.Type == 0),
            "each starter blueprint exists, belongs to its profession and has no recipe item");

        Check.True(ProfessionTables.InCraftingArea(GameDefaults.SpartaCapitalMap, 149, -187), "the square in Sparta");
        Check.True(ProfessionTables.InCraftingArea(GameDefaults.AthensCapitalMap, 149, -187), "the square in Athens");
        Check.True(!ProfessionTables.InCraftingArea(GameDefaults.AthensCapitalMap, 165, -97), "the capital spawn point is outside");
        Check.True(!ProfessionTables.InCraftingArea(7, 149, -187), "another map is outside");

        // Every product stacks at most 99 in the client (Overlap 99 or 1); the craft path also caps the grant at 99.
        Check.True(ProfessionBlueprintCatalog.All.All(static b => b.ProductCount <= 99), "no blueprint makes more than 99 at once");
        return Task.CompletedTask;
    }
}
