using Godswar.Server.Application.Combat;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

// B21 (batch 2026-11-21, agent S2): Wishing Well book binding, board message times, per-class area skill balance.
internal static class B21ServerSettingsChecks
{
    public const string CheckName =
        "B21: Wishing Well paid book unbound, board message time, area skill balance per class";

    public static Task RunAsync()
    {
        // Bug b1794901326354: the free wish gives a bound book, a wish paid with Gold an unbound one.
        Check.Equal(1, PostgresGameStore.WishingPoolBookBound(0), "free wish: bound book");
        Check.Equal(0, PostgresGameStore.WishingPoolBookBound(230), "paid wish (230 Gold): unbound book");

        // Item 19: HH:MM realm time in front of a board message, before it is wrapped.
        var defs = ServerRateCatalog.Definitions.ToDictionary(static d => d.Key);
        Check.Equal(1m, defs[Finalise2RateKeys.BoardMessageTime].DefaultValue, "board.message_time defaults on");
        var brisbane = RealmCalendar.CreateForTesting(new RealmId(1), "Australia/Brisbane");
        var at = new DateTimeOffset(2026, 11, 21, 9, 5, 0, TimeSpan.Zero); // 19:05 in Brisbane (UTC+10)
        Check.Equal("19:05 Event transporters now open", BoardClock.Stamp("Event transporters now open", at, brisbane, true),
            "the message gets the realm time");
        Check.Equal("Hello", BoardClock.Stamp("Hello", at, brisbane, false), "switch off: no time");
        Check.Equal("Hello", BoardClock.Stamp("Hello", at, null, true), "no calendar yet: no time");
        var stamped = BoardText.Wrap(BoardClock.Stamp(new string('a', 44), at, brisbane, true));
        Check.True(stamped.All(static l => l.Length <= BoardText.Width) && stamped[0].StartsWith("19:05", StringComparison.Ordinal),
            "the time counts towards the 46-character board width (wrapped, not cut by Board.lua)");

        // Item 18: per-class area skill damage, default x1.
        foreach (var key in new[] { AreaSkillBalance.WarriorKey, AreaSkillBalance.ChampionKey, AreaSkillBalance.PriestKey,
                     AreaSkillBalance.MageKey })
        {
            Check.Equal(1m, defs[key].DefaultValue, key + " defaults to x1 (today's damage)");
            Check.True(defs[key].Kind == ServerRateKind.Multiplier, key + " is a multiplier slider");
        }
        Check.Equal(AreaSkillBalance.MageKey, AreaSkillBalance.KeyFor(3), "profession 3 is Mage");
        Check.Equal(AreaSkillBalance.WarriorKey, AreaSkillBalance.KeyFor(0), "profession 0 is Warrior");
        Check.True(AreaSkillBalance.KeyFor(9) is null, "an unknown profession is never scaled");
        var hit = new CombatResolution(5, 1, 0, CombatDamageChannel.Magic, CombatHitOutcome.Normal, 22_142,
            new(9_000, 100, 0, CombatRollEvidence.NotRolled), default);
        Check.Equal(22_142u, AreaSkillBalance.Apply(1m, hit).Damage, "x1 leaves the damage unchanged");
        Check.Equal(13_285u, AreaSkillBalance.Apply(0.6m, hit).Damage, "x0.6 scales and rounds");
        Check.Equal(1u, AreaSkillBalance.Apply(0.1m, hit with { Damage = 3 }).Damage, "a landed hit never drops below 1");
        var miss = hit with { Outcome = CombatHitOutcome.Miss, Damage = 0 };
        Check.Equal(miss, AreaSkillBalance.Apply(0.5m, miss), "a miss stays a miss");
        Check.Equal(22_142u, AreaSkillBalance.Apply((byte)3, hit).Damage, "live default (no row): Mage area damage unchanged");

        // Item 15: Crier Gold / B-Gold choices priced per X like the silver choice, at 1,500 silver per Gold.
        var top = new FactionCrierBalanceTier(131, 200, 250_000, 38, 140_000, 400_000);
        var low = new FactionCrierBalanceTier(20, 39, 23_500, 5, 15_000, 40_000);
        var x9 = new FactionCrierBalanceOption(112, FactionCrierCurrency.BoundGold, 312, 9, FactionCrierRewardKind.Experience);
        var x24 = new FactionCrierBalanceOption(134, FactionCrierCurrency.Gold, 1_377, 24,
            FactionCrierRewardKind.ExperienceAndTalentPoints);
        var silver = new FactionCrierBalanceOption(110, FactionCrierCurrency.Silver, 0, 6, FactionCrierRewardKind.Experience);
        Check.Equal(140, FactionCrierRewardPolicy.GoldPriceByMultiplier(x9, top, false, 1500) ?? -1, "Lv131+ 9X = 140 Gold");
        Check.Equal(533, FactionCrierRewardPolicy.GoldPriceByMultiplier(x24, top, true, 1500) ?? -1, "Lv131+ 24X = 533 Gold");
        Check.Equal(15, FactionCrierRewardPolicy.GoldPriceByMultiplier(x9, low, false, 1500) ?? -1, "Lv20-39 9X = 15 B-Gold");
        Check.True(FactionCrierRewardPolicy.GoldPriceByMultiplier(x9, top, false, 0) is null, "0 = the fixed table price");
        Check.True(FactionCrierRewardPolicy.GoldPriceByMultiplier(silver, top, false, 1500) is null, "silver keeps its price");
        Check.Equal(0m, defs[FactionCrierRewardPolicy.SilverPerGoldKey].DefaultValue, "crier.silver_per_gold defaults to 0 (B22 W11: fixed Gold prices)");

        // Item 17: the switches live runs ON now default ON too (limits.original_gw is left for the admin).
        foreach (var key in new[] { "pet.wild_capture", "pet.treasure_land_capture", "event.spy_invasion", "event.board",
                     "battlefield.awarder", "battlefield.scoring", "battlefield.show_paid_amounts", "event.weekend_exp_claim",
                     "event.festival_gifts", "npc.village_leader_books", "guild.building_rules", "guild.altar_curve",
                     "guild.lord_rules", "guild.double_claims", "guild.quest_rules", "status.account_bonus_icons",
                     "npc.marriage", "board.event_open_notices", "npc.class_change", "npc.title_expert",
                     "npc.fortune_teller", "trade.player", "trade.stalls" })
            Check.Equal(1m, defs[key].DefaultValue, key + " defaults on");
        Check.Equal(0m, defs["limits.original_gw"].DefaultValue, "limits.original_gw default untouched (the admin decides)");
        return Task.CompletedTask;
    }
}
