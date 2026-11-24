using System.Reflection;
using System.Text.Json;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Limits;
using Godswar.Server.Application.Rates;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W16 (batch 2026-11-22, agent B26-S): the end-game master switch (d129 note), the mall's Purge Potion prices (d125),
// no Vigor potion in any compiled shop (d126) and the NPC buttons that used to answer nothing (d128).
internal static class B26W16Checks
{
    public const string EndgameCheckName = "W16: end-game master switch (endgame.enabled) opens and closes the whole path";
    public const string ShopCheckName = "W16: mall Purge Potion prices match Athens_087, no Vigor potion in any compiled shop";
    public const string NpcCheckName = "W16: silent NPC buttons answer with the client's own pages";

    private static ServerRatesSnapshot Rates(decimal? master, decimal originalGw, decimal godGems)
    {
        var values = new List<KeyValuePair<string, decimal>>
        {
            KeyValuePair.Create(GameLimitProfile.RateKey, originalGw),
            KeyValuePair.Create(Finalise2RateKeys.GodGemDrops, godGems),
            KeyValuePair.Create(Finalise2RateKeys.GodGemDropChance, 10_000m) // every listed boss rolls a gem
        };
        if (master is { } m) values.Add(KeyValuePair.Create(EndgameSwitch.RateKey, m));
        return ServerRatesSnapshot.Create(values, 1, DateTimeOffset.UtcNow, "check", "test");
    }

    public static Task RunEndgameAsync()
    {
        var def = ServerRateCatalog.Get(EndgameSwitch.RateKey);
        Check.Equal(2m, def.DefaultValue, "endgame.enabled defaults to 2 (separate switches): an upgrade changes nothing");
        Check.Equal((0m, 2m, 1m), (def.MinimumValue, def.MaximumValue, def.Step), "endgame.enabled is 0, 1 or 2");
        Check.True(def.Kind == ServerRateKind.Absolute, "endgame.enabled is an absolute setting");

        var death = Guid.Parse("6a0c1d0e-0000-4000-8000-000000000001");
        const string boss = "A_boss_boar_001";
        // (master, limits.original_gw, gem.god_gem_drops) -> (Original limits?, god gems?)
        var cases = new (decimal? Master, decimal Gw, decimal Gems, bool Original, bool Gems2, string What)[]
        {
            (null, 0m, 1m, false, true, "no row (default 2), live today (original_gw 0, gems 1): open"),
            (null, 1m, 0m, true, false, "no row (default 2), pre-switch live (original_gw 1, gems 0): closed"),
            (2m, 0m, 0m, false, false, "2 follows the old keys, also when they disagree"),
            (0m, 0m, 1m, true, false, "0 closes everything although the old keys say open"),
            (1m, 1m, 0m, false, true, "1 opens everything although the old keys say closed"),
            (1m, 0m, 1m, false, true, "1 with the old keys open"),
            (0m, 1m, 0m, true, false, "0 with the old keys closed")
        };
        foreach (var c in cases)
        {
            var rates = Rates(c.Master, c.Gw, c.Gems);
            Check.Equal(c.Original, EndgameSwitch.OriginalLimits(rates), c.What + ": limits");
            Check.Equal(c.Gems2, EndgameSwitch.GodGemDrops(rates), c.What + ": god gem switch");
            Check.Equal(c.Gems2, GodGemLootRule.TryRoll(boss, death, rates) is not null, c.What + ": a 100% boss roll");
            ServerRates.Install(rates);
            try
            {
                Check.Equal(c.Original, GameLimitProfile.Original, c.What + ": GameLimitProfile.Original");
                Check.Equal(!c.Original, GameLimitProfile.ClassSuitTierIVAllowed, c.What + ": Class Suit IV-VII");
                Check.Equal(c.Original ? (short)12 : (short)25, GameLimitProfile.MaximumGrade(25), c.What + ": grade cap");
                Check.Equal(c.Original ? (short)10 : (short)20, GameLimitProfile.MaximumQuality(20), c.What + ": quality cap");
            }
            finally
            {
                ServerRates.ResetToDefaults();
            }
        }

        Check.True(EndgameSwitch.Describe(Rates(1m, 1m, 0m)).Contains("master=on") &&
                   EndgameSwitch.Describe(Rates(1m, 1m, 0m)).Contains("path=OPEN"), "the log line names the mode and the path");
        Check.True(EndgameSwitch.Describe(Rates(null, 0m, 0m)).Contains("OPEN-WITHOUT-G25"),
            "the log line warns when the limits are open but no god gem can drop");
        return Task.CompletedTask;
    }

    private static List<(uint Item, uint Price)> Describe(IReadOnlyList<object> rows) =>
        JsonSerializer.Deserialize<List<JsonElement>>(JsonSerializer.Serialize(rows))!
            .Select(static e => (e.GetProperty("item").GetUInt32(), e.GetProperty("price").GetUInt32())).ToList();

    public static Task RunShopsAsync()
    {
        foreach (var (camp, name) in new[] { (GameDefaults.AthensCamp, "Athens"), (GameDefaults.SpartaCamp, "Sparta") })
        {
            var mall = Describe(PacketBuilder.DescribeCompiledMall(camp));
            var weak = mall.Where(static r => r.Item == 10102).ToList();
            var strong = mall.Where(static r => r.Item == 10101).ToList();
            Check.True(weak.Count == 1 && weak[0].Price == 11, name + " mall: Weak Purge Potion (10102) 11 Gold");
            Check.True(strong.Count == 1 && strong[0].Price == 110, name + " mall: Strong Purge Potion (10101) 110 Gold");
            Check.True(mall.All(static r => r.Item is < 4080 or > 4083), name + " mall: no Vigor potion");
            // the purchase path charges what the window shows: resolve each potion by its category and index
            var rows = JsonSerializer.Deserialize<List<JsonElement>>(JsonSerializer.Serialize(PacketBuilder.DescribeCompiledMall(camp)))!;
            foreach (var (item, price) in new[] { (10102u, 11), (10101u, 110) })
            {
                var at = rows.FindIndex(r => r.GetProperty("item").GetUInt32() == item);
                var category = rows[at].GetProperty("category").GetInt32();
                var index = rows.Take(at).Count(r => r.GetProperty("category").GetInt32() == category);
                Check.True(PacketBuilder.TryResolveMallCatalogOffer(camp, category, index, item, out var offer) &&
                           offer.UnitPrice == price, $"{name} mall purchase of {item} charges {price} Gold");
            }
        }

        var checkedShops = 0;
        foreach (var service in Enum.GetValues<CapitalNpcServiceKind>().Where(CapitalNpcServiceProtocol.IsShop))
        {
            List<(uint Item, uint Price)> rows;
            try
            {
                rows = Describe(PacketBuilder.DescribeCompiledCapitalShop(service));
            }
            catch (ArgumentException)
            {
                Console.WriteLine($"[w16-check] {service}: no captured shop stream (built in code or empty), not read");
                continue; // no captured stream for this service (the editor treats it the same way)
            }
            checkedShops++;
            Check.True(rows.All(static r => r.Item is < 4080 or > 4083), service + ": no Vigor potion (4080-4083)");
        }
        Console.WriteLine($"[w16-check] compiled shops read: {checkedShops}");
        Check.True(checkedShops >= 19, $"every compiled capital shop was read ({checkedShops})");
        return Task.CompletedTask;
    }

    private static (IReadOnlyList<int> Opening, IReadOnlyDictionary<int, int[]> Steps,
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, int[]>>? Pages) Dialogue(string field)
    {
        var value = typeof(GameClientHandler).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var type = value.GetType();
        return ((IReadOnlyList<int>)type.GetProperty("OpeningMenu")!.GetValue(value)!,
            (IReadOnlyDictionary<int, int[]>)type.GetProperty("Steps")!.GetValue(value)!,
            (IReadOnlyDictionary<int, IReadOnlyDictionary<int, int[]>>?)type.GetProperty("PageSelections")!.GetValue(value));
    }

    public static Task RunNpcAsync()
    {
        var shifter = Dialogue("ClassShifterDialogue");
        Check.True(shifter.Steps.TryGetValue(102, out var armour) && armour.SequenceEqual([123]),
            "Class Shifter 102 -> 123 ('Armor shifting is not available yet.')");

        var feeder = Dialogue("MountFeederDialogue");
        Check.True(feeder.Steps.TryGetValue(106, out var bind) && bind.SequenceEqual([206]),
            "Mount Feeder 106 -> 206 (the bind mount gear form)");

        var crier = Dialogue("BattlefieldCrierDialogue");
        var expected = new Dictionary<int, int[]>
        {
            [2] = [301], [3] = [401, 402, 403, 404], [4] = [501], [5] = [601], [6] = [701],
            [401] = [450], [402] = [452], [403] = [453], [404] = [454]
        };
        foreach (var (click, reply) in expected)
            Check.True(crier.Steps.TryGetValue(click, out var got) && got.SequenceEqual(reply),
                $"Battlefield crier {click} -> [{string.Join(',', reply)}]");
        Check.True(!crier.Steps.ContainsKey(1), "Battlefield crier 1 (Pindus) is left to its handlers");
        Check.True(crier.Pages is { } pages && pages.TryGetValue(3, out var lelantine) && lelantine.Count == 4 &&
                   lelantine[402].SequenceEqual([452]), "the Lelantine questions also answer as page selections");

        var variant = typeof(GameClientHandler).GetMethod("CampVariantReply", BindingFlags.NonPublic | BindingFlags.Static)!;
        int[] Camp(int fn, int[] reply, byte camp) => (int[])variant.Invoke(null, [fn, reply, camp])!;
        Check.True(Camp(27, [450], GameDefaults.AthensCamp).SequenceEqual([450]), "Lelantine story: Athens hears 450");
        Check.True(Camp(27, [450], GameDefaults.SpartaCamp).SequenceEqual([451]), "Lelantine story: Sparta hears 451");
        Check.True(Camp(21, [450], GameDefaults.SpartaCamp).SequenceEqual([450]), "only the crier has camp pages");
        return Task.CompletedTask;
    }
}
