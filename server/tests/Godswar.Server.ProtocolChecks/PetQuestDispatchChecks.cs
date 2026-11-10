using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W3 Magic Bell (F-136): Send Pet duration, quest eligibility, the 10254/10259 frames and the bell listing.</summary>
internal static class PetQuestDispatchChecks
{
    public const string CheckName = "W3 Magic Bell pet quest dispatch (F-136)";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        try
        {
            Check.True(Finalise2Rates.On(Finalise2RateKeys.MagicBell), "pet.magic_bell_dispatch defaults to on");
            Check.Equal(50_000, Finalise2Rates.Amount(Finalise2RateKeys.MagicBellPrice), "bell price defaults to 50,000");

            // the admin: 10-70 minutes by pet stats, rank 75 gets the lower bound.
            Check.Equal(70, PetQuestDispatchPolicy.DispatchMinutes(0m), "rank 0 = 70 minutes");
            Check.Equal(10, PetQuestDispatchPolicy.DispatchMinutes(75m), "rank 75 = 10 minutes");
            Check.Equal(10, PetQuestDispatchPolicy.DispatchMinutes(100m), "rank 100 = 10 minutes (floor)");
            Check.Equal(40, PetQuestDispatchPolicy.DispatchMinutes(37.5m), "rank 37.5 = 40 minutes (linear)");
            Check.Equal(64, PetQuestDispatchPolicy.DispatchMinutes(8.12m), "rank 8.12 = 63.5 -> 64 minutes (rounded up)");
            Check.Equal(70, PetQuestDispatchPolicy.DispatchMinutes(-5m), "a negative rank counts as 0");
            Check.True(Enumerable.Range(0, 7600).Select(r => PetQuestDispatchPolicy.DispatchMinutes(r / 100m))
                    .All(static m => m is >= 10 and <= 70), "every rank 0-75.99 stays within 10-70 minutes");
            Check.Equal(30, PetQuestDispatchPolicy.DispatchMinutes(10m, 30, 50, 75), "a fastest above the slowest is clamped");

            var dispatchable = StarterQuestChain.Steps.Where(PetQuestDispatchPolicy.IsDispatchable).ToArray();
            Check.True(dispatchable.Length is > 0 and <= 160,
                $"dispatchable quests are a subset of the client's 160 pet=\"5\" quests: {dispatchable.Length}");
            Check.True(dispatchable.All(static q => q.UIQuestSort == StarterQuestChain.SortDaily && q.MinLevel is >= 1 and <= 140),
                "only daily (UIQuestSort 3) quests of level 1-140");
            Check.True(!StarterQuestChain.Steps.Where(static q => q.UIQuestSort != StarterQuestChain.SortDaily)
                .Any(PetQuestDispatchPolicy.IsDispatchable), "no story, repeat, city or guild quest");

            var result = PetQuestDispatchPolicy.ResultPacket(1146, PetQuestDispatchPolicy.Result.Accepted);
            Check.True(result.Length == 9 && BitConverter.ToUInt16(result, 0) == 9 &&
                       BitConverter.ToUInt16(result, 2) == Opcodes.PetQuestDispatchResult && Opcodes.PetQuestDispatchResult == 10259 &&
                       BitConverter.ToUInt32(result, 4) == 1146 && result[8] == 5,
                "10259 = [9][10259][u32 quest @4][u8 code @8] (client 0x69ec50)");
            var request = new GamePacket([8, 0, 0x0e, 0x28, 0x7a, 0x04, 0, 0]);
            Check.True(request.Opcode == Opcodes.PetQuestDispatchRequest &&
                       PetQuestDispatchPolicy.TryReadRequest(request.Payload, out var questId) && questId == 1146,
                "10254 [8][0x280e][u32 quest] (client 0x69da80) reads quest 1146");
            Check.True(!PetQuestDispatchPolicy.TryReadRequest(new byte[2], out _), "a short 10254 is ignored");

            // F-143: the silver listing is off by default; the bell is the Item Mall's (MagicBellSourceChecks).
            Check.True(!Finalise2Rates.On(Finalise2RateKeys.MagicBellMerchant), "pet.magic_bell_merchant defaults to off");
            Check.Equal(-1, BellIndex(CapitalNpcServiceKind.PetMerchant), "default: no bell at the Athens Pet Merchant");
            Check.Equal(-1, BellIndex(CapitalNpcServiceKind.SpartaPetMerchant), "default: no bell at the Sparta Pet Merchant");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Finalise2RateKeys.MagicBellMerchant, 1m)], 1, DateTimeOffset.UtcNow, "check", "test"));
            foreach (var service in new[] { CapitalNpcServiceKind.PetMerchant, CapitalNpcServiceKind.SpartaPetMerchant })
            {
                var index = BellIndex(service);
                Check.True(index >= 0, $"{service} lists the Magic Bell on tab 1");
                Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(service, 0, index, 4113, out var bell) &&
                           bell.UnitPrice == 50_000 && bell.Currency == CapitalNpcShopCurrency.Silver,
                    $"{service} sells the Magic Bell for 50,000 silver");
                Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(service, 0, 0, 10000, out var first) && first.UnitPrice == 600,
                    $"{service} tab 1 #0 is unchanged");
            }

            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Finalise2RateKeys.MagicBellMerchant, 1m), KeyValuePair.Create(Finalise2RateKeys.MagicBellPrice, 1234m)],
                1, DateTimeOffset.UtcNow, "check", "test"));
            var repriced = BellIndex(CapitalNpcServiceKind.PetMerchant);
            Check.True(PacketBuilder.TryResolveCapitalNpcShopOffer(CapitalNpcServiceKind.PetMerchant, 0, repriced, 4113, out var cheap) &&
                       cheap.UnitPrice == 1234, "the editor price applies at once");
            ServerRates.Install(ServerRatesSnapshot.Create(
                [KeyValuePair.Create(Finalise2RateKeys.MagicBellMerchant, 1m), KeyValuePair.Create(Finalise2RateKeys.MagicBell, 0m)],
                2, DateTimeOffset.UtcNow, "check", "test"));
            Check.Equal(-1, BellIndex(CapitalNpcServiceKind.PetMerchant), "switch 0: no bell on the shelf");
        }
        finally
        {
            ServerRates.ResetToDefaults();
        }

        return Task.CompletedTask;
    }

    private static int BellIndex(CapitalNpcServiceKind service)
    {
        for (var index = 0; index < 64; index++)
        {
            if (PacketBuilder.TryResolveCapitalNpcShopOffer(service, 0, index, 4113, out _)) return index;
        }

        return -1;
    }
}
