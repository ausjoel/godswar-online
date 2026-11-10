using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// FORGE: rolls every forge step many times through the real planner
/// (ForgePersistencePlanner.TryCreate with the production random source) and
/// prints the empirical success rate beside the rate the client window shows
/// (client FUN_00423e00: table value + gem bonus + crystal bonus x count,
/// clamped 0..100). Run: ProtocolChecks --forge-rolls [rollsPerRow] [forge.success]
/// Exit code 1 if any row is outside 4 standard deviations of the rolled
/// chance, or if a parity rule fails.
/// </summary>
internal static class ForgeRollTableChecks
{
    private const int Silver = 50_000_000;
    private const uint Sword = 1000; // Short Sword; all 611 rules share the same vectors.
    private const int L4Crystal = 4233; // +12 each

    public static int Run(string[] args)
    {
        var rolls = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 10_000;
        var rate = args.Length > 2 ? decimal.Parse(args[2], CultureInfo.InvariantCulture) : 1m;
        ServerRates.Install(ServerRatesSnapshot.Create(
            [new KeyValuePair<string, decimal>(ServerRateKeys.ForgeSuccess, rate)],
            1, DateTimeOffset.UtcNow, "forge-rolls", "test"));
        var failures = 0;
        Console.WriteLine($"forge.success = {rate}; {rolls} rolls per row; equipment {Sword} (Short Sword)");
        Console.WriteLine("op       step      gem   crystals  client%  rolled%  empirical%  successes   ok");

        // Sapphire: quality Q -> Q+1, the lowest gem level whose Round allows Q.
        for (var q = 1; q <= 19; q++)
        {
            foreach (var gem in SapphiresFor(q))
            {
                foreach (var crystals in new[] { 0, 10, 25 })
                {
                    failures += Row("Sapphire", $"Q{q}->Q{q + 1}", Equip(q, 1), gem, crystals, rolls);
                }
            }
        }

        // Emerald: grade G -> G+1.
        for (var g = 1; g <= 24; g++)
        {
            foreach (var gem in EmeraldsFor(g))
            {
                foreach (var crystals in new[] { 0, 10, 25 })
                {
                    failures += Row("Emerald", $"G{g}->G{g + 1}", Equip(1, g), gem, crystals, rolls);
                }
            }
        }

        // Ruby: item level, per weapon in the Short Sword line.
        foreach (var id in new uint[] { 1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008 })
        {
            foreach (var gem in new[] { 4200, 4201, 4202 })
            {
                failures += Row("Ruby", $"{id}->next", Equip(1, 1) with { Id = id }, gem, 0, rolls);
            }
        }

        failures += ParityChecks();
        ServerRates.ResetToDefaults();
        Console.WriteLine(failures == 0 ? "ALL ROWS OK" : $"FAILED ROWS: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<int> SapphiresFor(int q)
    {
        if (q <= 7) { yield return 4210; yield return 4212; }
        else if (q <= 12) { yield return 4213; yield return 4215; }
        else { yield return 4215; }
    }

    private static IEnumerable<int> EmeraldsFor(int g)
    {
        if (g <= 9) { yield return 4220; yield return 4222; }
        else if (g <= 17) { yield return 4223; yield return 4225; }
        else { yield return 4225; }
    }

    private static CompactItemEntry Equip(int quality, int grade) =>
        CompactItemEntry.Parse(
            $"[{Sword},0,,,,,{quality},{grade},1,1,0,0,1]");

    /// <summary>Independent copy of the client's formula, for the comparison column.</summary>
    private static int ClientShows(CompactItemEntry equipment, int gemId, int crystals)
    {
        EquipmentForgeCatalog.TryGet(equipment.Id, out var rule);
        ForgingMaterialRuleCatalog.TryGet((uint)gemId, out var gem);
        ForgingMaterialRuleCatalog.TryGet(L4Crystal, out var crystal);
        var q = Math.Max(1, (int)equipment.Quality);
        var g = Math.Max(1, (int)equipment.Grade);
        long table = gem.MaterialType switch
        {
            1 => rule.Probability ?? 0,
            2 => rule.BaseProyAdd[q - 1],
            _ => rule.AppendProyAdd[g - 1]
        };
        return (int)Math.Clamp(table + (gem.MaterialProyAdd ?? 0) + (long)crystals * (crystal.MaterialProyAdd ?? 0), 0, 100);
    }

    private static int Row(string op, string step, CompactItemEntry equipment, int gemId, int crystals, int rolls)
    {
        var shows = ClientShows(equipment, gemId, crystals);
        var kitBag = GameDefaults.EmptyKitBag;
        kitBag = KitBagSlots.SetSlot(kitBag, 0, equipment.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, CompactItemEntry.Parse($"[{gemId},,,,,,1,1,1,99,0,0]").ToCompactString());
        ForgeSlotSelection? odds = null;
        if (crystals > 0)
        {
            kitBag = KitBagSlots.SetSlot(kitBag, 2, CompactItemEntry.Parse($"[{L4Crystal},,,,,,1,1,1,99,0,0]").ToCompactString());
            odds = ForgeSlotSelection.Capture(kitBag, 2, crystals);
        }

        var request = new ForgeTransactionRequest(
            ForgeSlotSelection.Capture(kitBag, 0),
            ForgeSlotSelection.Capture(kitBag, 1),
            odds);

        var successes = 0;
        var rolled = -1;
        string? rejection = null;
        for (var i = 0; i < rolls; i++)
        {
            if (!ForgePersistencePlanner.TryCreate(kitBag, Silver, request, RandomNumberGenerator.GetInt32(100),
                    out var plan, out _, out var reason))
            {
                rejection = reason;
                break;
            }

            rolled = plan!.Calculation.SuccessProbability;
            if (plan.Calculation.DisplayedProbability != shows)
            {
                Console.WriteLine($"  MISMATCH server displayed {plan.Calculation.DisplayedProbability} vs client formula {shows}");
                return 1;
            }

            if (plan.Succeeded)
            {
                successes++;
            }
        }

        var gem = $"{gemId}";
        if (rejection is not null)
        {
            // The client disables Start below 5% (or for a gem outside its Round),
            // so the server must refuse the same selections.
            var ok = shows < EquipmentForgeCalculator.MinimumClientStartProbability ||
                     rejection.Contains("MaterialRoundNotAllowed", StringComparison.Ordinal);
            Console.WriteLine($"{op,-8} {step,-9} {gem,5} {crystals,9} {shows,8} {"-",8} {"refused",11} {rejection,-24} {(ok ? "ok" : "FAIL")}");
            return ok ? 0 : 1;
        }

        var p = rolled / 100.0;
        var empirical = (double)successes / rolls;
        var sigma = Math.Sqrt(p * (1 - p) / rolls);
        var within = Math.Abs(empirical - p) <= Math.Max(4 * sigma, 1e-9);
        Console.WriteLine($"{op,-8} {step,-9} {gem,5} {crystals,9} {shows,8} {rolled,8} {empirical * 100,11:F2} {successes,10}   {(within ? "ok" : "FAIL")}");
        return within ? 0 : 1;
    }

    private static int ParityChecks()
    {
        var failures = 0;
        void Expect(bool condition, string text)
        {
            Console.WriteLine($"{(condition ? "ok  " : "FAIL")} {text}");
            if (!condition) failures++;
        }

        ServerRates.ResetToDefaults();
        // A stored grade/quality of 0 is shown as 1 by the client and the
        // kit-bag projection; it must forge from 1 (live row 81 was refused).
        var stored = CompactItemEntry.Parse($"[{Sword},150,,,,,10,0,1,1,0,0,25]");
        var staged = stored with { Grade = 1 };
        var kitBag = GameDefaults.EmptyKitBag;
        kitBag = KitBagSlots.SetSlot(kitBag, 0, stored.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, CompactItemEntry.Parse("[4222,,,,,,1,1,1,5,0,0]").ToCompactString());
        var request = new ForgeTransactionRequest(
            new ForgeSlotSelection(0, staged, 1),
            ForgeSlotSelection.Capture(kitBag, 1),
            null);
        var okSuccess = ForgePersistencePlanner.TryCreate(kitBag, Silver, request, 0, out var win, out _, out var why1);
        Expect(okSuccess && win!.Succeeded && win.Calculation.SuccessEquipment.Grade == 2,
            $"grade-0 item staged as G1 forges G1->G2 (was StaleSelection) {why1}");
        var okFail = ForgePersistencePlanner.TryCreate(kitBag, Silver, request, 99, out var lose, out _, out var why2);
        Expect(okFail && !lose!.Succeeded && lose.Mutations[0].After == stored,
            $"a failed roll leaves the stored row unchanged {why2}");

        // The window's 100% always succeeds, even with a slider below 1.
        ServerRates.Install(ServerRatesSnapshot.Create(
            [new KeyValuePair<string, decimal>(ServerRateKeys.ForgeSuccess, 0.5m)],
            2, DateTimeOffset.UtcNow, "forge-rolls", "test"));
        var hundred = Equip(1, 1);
        kitBag = GameDefaults.EmptyKitBag;
        kitBag = KitBagSlots.SetSlot(kitBag, 0, hundred.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, CompactItemEntry.Parse("[4202,,,,,,1,1,1,5,0,0]").ToCompactString());
        request = new ForgeTransactionRequest(ForgeSlotSelection.Capture(kitBag, 0), ForgeSlotSelection.Capture(kitBag, 1), null);
        var okRuby = ForgePersistencePlanner.TryCreate(kitBag, Silver, request, 99, out var ruby, out _, out _);
        Expect(okRuby && ruby!.Succeeded && ruby.Calculation.SuccessProbability == 100,
            "shown 100% (Level 3 Ruby) rolls at 100 with forge.success 0.5");
        Expect(okRuby && ruby!.Calculation.SuccessEquipment.Quality == 1 && ruby.Calculation.SuccessEquipment.Grade == 1,
            "Ruby success keeps quality and grade (client 0x424821)");

        // Round: the client reads only "min,max".
        ForgingMaterialRuleCatalog.TryGet(4213, out var l4);
        var listRule = l4 with { Round = [8, 9, 10, 11, 12] };
        Expect(listRule.AllowsRound(9) && !listRule.AllowsRound(10),
            "Round \"8,9,10,11,12\" is read as 8..9, like the client's sscanf(\"%d,%d\")");
        return failures;
    }
}
