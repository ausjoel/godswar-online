using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Pets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

/// <summary>PETWORK (F-137): Pet_Job.xml prices and rewards, the stop share and the work window's frames.</summary>
internal static class PetWorkChecks
{
    public const string CheckName = "PETWORK pet work: Pet_Job.xml prices, rewards and frames";

    public static Task RunAsync()
    {
        Check.Equal(140, PetWorkTable.RowCount, "140 Pet_Job.xml rows");
        Check.True(PetWorkTable.Row(1) == new PetWorkRow(1, 224, 135, 0, 10, 1059, 1), "row 1 as in the XML");
        Check.True(PetWorkTable.Row(140) == new PetWorkRow(140, 15575, 55360, 6, 119, 138400, 10), "row 140");
        Check.True(PetWorkTable.Row(200).Level == 140 && PetWorkTable.Row(0).Level == 1,
            "levels above 140 use row 140 (GENERATED), below 1 row 1");

        Quote(1, PetWorkPayType.Silver, 1, cost: 224, exp: 135, talent: 0, seconds: 3600);
        Quote(1, PetWorkPayType.Gold, 1, cost: 10, exp: 1059, talent: 1, seconds: 3600);
        // 3.5x rounds half up like the client (base x GainBase + 0.5, truncated): 472.5 -> 473, 3.5 -> 4.
        Quote(1, PetWorkPayType.Silver, 3, cost: 672, exp: 473, talent: 0, seconds: 10800);
        Quote(1, PetWorkPayType.Gold, 3, cost: 30, exp: 3707, talent: 4, seconds: 10800);
        Quote(30, PetWorkPayType.Gold, 8, cost: 96, exp: 22920, talent: 20, seconds: 28800);
        Quote(30, PetWorkPayType.Silver, 24, cost: 15936, exp: 24640, talent: 0, seconds: 86400);
        Quote(140, PetWorkPayType.Silver, 24, cost: 373800, exp: 1771520, talent: 192, seconds: 86400);
        Quote(140, PetWorkPayType.Gold, 24, cost: 2856, exp: 4428800, talent: 320, seconds: 86400);
        Check.True(!PetWorkTable.TryQuote(10, 1, 2, out _) && !PetWorkTable.TryQuote(10, 3, 1, out _) &&
                   !PetWorkTable.TryQuote(10, 0, 24, out _), "only Silver/Gold and 1/3/8/24 hours");

        Check.True(PetWorkTable.StopShare(4428800, 320) == (885760L, 64), "early stop pays 20%");
        Check.True(PetWorkTable.StopShare(473, 4) == (94L, 0), "20% rounds down (GENERATED)");

        Check.Equal(10265, (int)Opcodes.PetWorkStartRequest, "start = 0x2819 (builder 0x6a09c0)");
        Check.Equal(10290, (int)Opcodes.PetWorkStatusRequest, "query = 0x2832");
        Check.Equal(10293, (int)Opcodes.PetWorkStopRequest, "stop = 0x2835 (builder 0x6a1ea0)");
        Check.True(PacketBuilder.PetWorkCodeReturn(PetWorkResultCode.Started)
            .SequenceEqual(new byte[] { 5, 0, 0x09, 0x28, 78 }), "code 78 on 10249, 5 bytes");
        Check.True(PacketBuilder.PetWorkCodeReturn(PetWorkResultCode.NoWorkTalent)[4] == 82, "code 82");
        Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.PetWorkCodeReturn((PetWorkResultCode)11),
            "unknown codes are refused");
        Check.True(PacketBuilder.PetWorkReward(Opcodes.PetWorkFinished, 1771520, 192).SequenceEqual(
                new byte[] { 12, 0, 0x1A, 0x28, 0x00, 0x08, 0x1B, 0x00, 192, 0, 0, 0 }),
            "10266: u32 EXP +4, u16 Talent Points +8");
        Check.True(PacketBuilder.PetWorkReward(Opcodes.PetWorkStopped, 94, 0)[2] == 0x36, "10294 = 0x2836");
        Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.PetWorkReward(Opcodes.PetWorkStatus, 1, 1),
            "reward frames are 10266 / 10294 only");
        var status = PacketBuilder.PetWorkStatus(1771520, 192, 1439, 373800, 1, 24);
        Check.True(status.SequenceEqual(new byte[]
            {
                16, 0, 0x33, 0x28, 0x00, 0x08, 0x1B, 0x00, 192, 0, 0x9F, 0x05, 0xFF, 0xFF, 1, 24
            }), "10291: EXP, talent, minutes left, cost (clamped to u16), pay type, hours");

        PetWorkRegistry.Mark(987654321);
        Check.True(PetWorkRegistry.IsWorking(987654321), "a marked pet is working");
        PetWorkRegistry.Clear(987654321);
        Check.True(!PetWorkRegistry.IsWorking(987654321), "cleared when the work ends");

        Check.True(Godswar.Server.Infrastructure.EventsNpc.PostgresEventsNpcStore.SchemaSql
            .Contains("CREATE TABLE IF NOT EXISTS public.pet_work_state"), "runtime DDL creates pet_work_state");
        var definition = Finalise2Rates.Definitions.Single(static d => d.Key == Finalise2RateKeys.PetWork);
        Check.Equal(1m, definition.DefaultValue, "the pet.work switch is on by default");
        return Task.CompletedTask;
    }

    private static void Quote(int level, PetWorkPayType pay, byte hours, int cost, long exp, int talent, int seconds)
    {
        Check.True(PetWorkTable.TryQuote(level, (byte)pay, hours, out var quote), $"Lv{level} {pay} {hours}h quotes");
        Check.True(quote.Cost == cost && quote.Experience == exp && quote.TalentPoints == talent &&
                   quote.Duration == TimeSpan.FromSeconds(seconds) && quote.Hours == hours,
            $"Lv{level} {pay} {hours}h = cost {cost}, {exp} EXP, {talent} Talent Points " +
            $"(got {quote.Cost}, {quote.Experience}, {quote.TalentPoints})");
    }
}
