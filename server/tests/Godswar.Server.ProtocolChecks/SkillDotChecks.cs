using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W2 skill statuses: Bleeding / Electrocuted / Burned from 16 player skills tick on monsters.</summary>
internal static class SkillDotChecks
{
    public const string CheckName = "W2 skill damage-over-time statuses (Bleeding, Electrocuted, Burned)";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        Check.Equal(16, SkillDamageOverTimeCatalog.Count, "16 skills");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SkillDots), "combat.skill_dots defaults to on");
        Check.Equal(50, Finalise2Rates.Amount(Finalise2RateKeys.SkillDotChance), "chance defaults to 50%");
        Check.Equal(100, Finalise2Rates.Amount(Finalise2RateKeys.SkillDotDamagePercent), "damage defaults to stock");

        Check.True(SkillDamageOverTimeCatalog.TryGet(63, out var rage4) &&
                   rage4 is { Family: SkillDamageOverTimeFamily.Bleeding, TickDamage: 800, IntervalMilliseconds: 2_000, TickCount: 3 },
            "Rage Claw 4 = Bleeding 800 every 2 s for 6 s (Status.ini 17)");
        Check.True(SkillDamageOverTimeCatalog.TryGet(60, out var rage1) && rage1.DurationMilliseconds == 6_000,
            "Rage Claw 1: status 14's Time=674 typo is read as 6 s");
        Check.True(SkillDamageOverTimeCatalog.TryGet(564, out var cloud5) &&
                   cloud5 is { Family: SkillDamageOverTimeFamily.Electrocuted, TickDamage: 600, TickCount: 10 },
            "Thundercloud 5 = Electrocuted 600 x10");
        Check.True(SkillDamageOverTimeCatalog.TryGet(544, out var lotus5) &&
                   lotus5 is { Family: SkillDamageOverTimeFamily.Burned, TickDamage: 800, TickCount: 8 },
            "Fire Lotus 5 = Burned 800 x8");
        Check.True(!SkillDamageOverTimeCatalog.TryGet(70, out _), "Stun 70 is not a damage-over-time skill");

        var ledger = new SkillDamageOverTimeLedger(5001);
        Check.True(ledger.TryApply(rage4, 4, 77, 1_000, 800), "Bleeding starts");
        Check.Equal(0, ledger.CollectDue(2_999).Count, "nothing before the first 2 s tick");
        var first = ledger.CollectDue(3_000);
        Check.True(first.Count == 1 && first[0] is { Damage: 800, TickOrdinal: 101, TargetCharacterId: 5001, SourceCharacterId: 4, SourceEventId: 77 } &&
                   first[0].Effect == ElementalEffectKind.Burn && first[0].Provenance == CombatEventProvenance.ElementalStatus,
            "first tick at +2 s, ordinal 101, routed as a monster Burn tick");
        var late = ledger.CollectDue(60_000);
        Check.True(late.Count == 2 && late.Sum(static i => i.Damage) == 1_600 && late[^1].TickOrdinal == 103,
            "overdue ticks all come out once: 3 ticks in total, then it ends");
        Check.Equal(0, ledger.ActiveCount, "finished status is removed");

        Check.True(ledger.TryApply(rage4, 4, 80, 0, 800), "Rage Claw 4 bleeding");
        Check.True(!ledger.TryApply(rage1, 4, 81, 100, 120), "a weaker Bleeding does not replace a stronger running one");
        Check.True(ledger.TryApply(cloud5, 4, 82, 100, 600), "a different family runs alongside");
        Check.Equal(2, ledger.ActiveCount, "two families active");
        Check.True(ledger.TryApply(rage4, 4, 83, 500, 800), "equal priority refreshes");
        var refreshed = ledger.CollectDue(2_400);
        Check.True(refreshed.All(static i => i.SourceEventId != 80), "the refreshed Bleeding ticks from the new hit only");
        Check.True(!ledger.TryApply(rage4, 0, 84, 0, 800) && !ledger.TryApply(rage4, 4, 0, 0, 800) &&
                   !ledger.TryApply(rage4, 4, 85, 0, 0), "invalid source, event or damage is refused");
        Check.Equal(6, SkillHitStunCatalog.Count, "6 stun skills (Dizzy, Concussed, Ice Cone 4/5 Frozen)");
        Check.Equal(7, SkillHitDebuffCatalog.Count, "7 debuff skills");
        Check.True(SkillHitDebuffCatalog.TryGet(822, out var weak3) &&
                   weak3 is { Kind: SkillHitDebuffKind.Weakness, Amount: 800, DurationMilliseconds: 10_000 },
            "Ice Cone 3 = Weakness +8 % damage taken for 10 s");
        Check.True(SkillHitDebuffCatalog.TryGet(844, out var blind) &&
                   blind is { Kind: SkillHitDebuffKind.Blinded, Amount: 200, DurationMilliseconds: 5_000 },
            "Holy Light 5 = Blinded, Hit Rating -200 for 5 s");
        var debuffs = new SkillDebuffLedger();
        SkillHitDebuffCatalog.TryGet(820, out var weak1);
        Check.True(debuffs.TryApply(weak3, 0) && debuffs.Amount(SkillHitDebuffKind.Weakness, 9_999) == 800 &&
                   debuffs.Amount(SkillHitDebuffKind.Weakness, 10_000) == 0, "Weakness lasts exactly 10 s");
        Check.True(debuffs.TryApply(weak3, 0) && !debuffs.TryApply(weak1, 1_000) &&
                   debuffs.TryApply(weak1, 10_000), "a weaker Weakness waits for the stronger one to expire");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SkillDebuffs), "combat.skill_debuffs defaults to on");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.PvpSkills),
            "pvp.skills defaults to on (instant single-target skills on enemy players; real-client victim test 2026-11-08)");
        Check.True(SkillHitStunCatalog.TryGet(44, out var dizzy, out var dizzyMs) && dizzy == 1447 && dizzyMs == 1_000,
            "Steel Rain 5 = Dizzy 1 s");
        Check.True(SkillHitStunCatalog.TryGet(583, out var conc, out var concMs) && conc == 1444 && concMs == 2_000,
            "Fire Blast 4 = Concussed 2 s");
        Check.True(Finalise2Rates.On(Finalise2RateKeys.SkillStuns), "combat.skill_stuns defaults to on");
        ledger.Clear();
        Check.Equal(0, ledger.ActiveCount, "clear");
        return Task.CompletedTask;
    }
}
