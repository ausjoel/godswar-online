using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Awards;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>W4-B: login-window award rules, the Prize Chest award-tab frames and the broadcast filter.</summary>
internal static class LoginAwardChecks
{
    public const string CheckName = "W4-B login-window awards, Prize Chest frames, buff broadcast filter";

    public static Task RunAsync()
    {
        CheckWindowAndClaimKey();
        CheckParsing();
        CheckDescriptions();
        CheckBroadcastFilter();
        CheckPrizeChestFrames();
        CheckSwitchesAndCapabilities();
        return Task.CompletedTask;
    }

    private static LoginAward Award(bool perAccount = false, bool toChest = false, int minLevel = 1) => new(
        7, "Launch week", new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), perAccount, toChest,
        [new LoginAwardItem(4102, 5, 1, 1, true)], 1000, 0, 250, [new LoginAwardBuff(1000, 3600)], 1000, minLevel);

    private static void CheckWindowAndClaimKey()
    {
        var award = Award();
        Check.True(LoginAwardPolicy.IsOpen(award, award.StartsAt), "the start instant is inside the window");
        Check.True(LoginAwardPolicy.IsOpen(award, award.EndsAt.AddTicks(-1)), "just before the end is inside");
        Check.True(!LoginAwardPolicy.IsOpen(award, award.EndsAt), "the end instant is outside (end exclusive)");
        Check.True(!LoginAwardPolicy.IsOpen(award, award.StartsAt.AddTicks(-1)), "before the start is outside");
        Check.True(!LoginAwardPolicy.IsEligible(Award(minLevel: 30), award.StartsAt, 29), "below min_level gets nothing");
        Check.True(LoginAwardPolicy.IsEligible(Award(minLevel: 30), award.StartsAt, 30), "min_level itself qualifies");
        Check.Equal("c:42", LoginAwardPolicy.ClaimKey(award, 9, 42), "per-character awards key on the character");
        Check.Equal("a:9", LoginAwardPolicy.ClaimKey(Award(perAccount: true), 9, 42), "per-account awards key on the account");
        Check.Throws<ArgumentOutOfRangeException>(() => LoginAwardPolicy.ClaimKey(award, 0, 42), "account id must be positive");
    }

    private static void CheckParsing()
    {
        Check.True(LoginAwardPolicy.TryParseItems("""[{"item_id":4102,"quantity":99}]""", out var items, out _) &&
            items.Single() == new LoginAwardItem(4102, 99, 1, 1, true),
            "defaults: quality 1, grade 1, bound");
        Check.True(!LoginAwardPolicy.TryParseItems("""[{"item_id":4102,"quantity":100}]""", out _, out _),
            "a stack above 99 is refused (the admin's rule)");
        Check.True(!LoginAwardPolicy.TryParseItems("""[{"item_id":0,"quantity":1}]""", out _, out _), "item id 0 is refused");
        Check.True(!LoginAwardPolicy.TryParseItems("""{"item_id":1}""", out _, out _), "items must be a list");
        Check.True(!LoginAwardPolicy.TryParseItems("not json", out _, out _), "bad JSON is refused, never guessed");
        Check.True(LoginAwardPolicy.TryParseItems("""[{"item_id":2001,"quality":8,"grade":3,"bound":0}]""", out var gear, out _) &&
            gear.Single() == new LoginAwardItem(2001, 1, 8, 3, false), "explicit quality, grade, unbound");
        var seventeen = "[" + string.Join(",", Enumerable.Repeat("""{"item_id":1}""", 17)) + "]";
        Check.True(!LoginAwardPolicy.TryParseItems(seventeen, out _, out _), "at most 16 item rows");
        Check.True(LoginAwardPolicy.TryParseBuffs("""[{"status_id":1000},{"status_id":5,"duration_seconds":60}]""",
                out var buffs, out _) && buffs.Count == 2 && buffs[0].DurationSeconds is null && buffs[1].DurationSeconds == 60,
            "buffs: authored duration when absent");
        Check.True(!LoginAwardPolicy.TryParseBuffs("""[{"status_id":5,"duration_seconds":0}]""", out _, out _),
            "a zero duration is refused");
    }

    private static void CheckDescriptions()
    {
        var bag = LoginAwardPolicy.Describe(new LoginAwardGrant(Award(), false, false, [(4102, 5)], true, 1));
        Check.True(bag.Contains("5 items") && bag.Contains("1,000 Silver") && bag.Contains("250 B-Gold") &&
            bag.Contains("a title") && bag.Contains("a buff") && !bag.Contains("Prize Chest"), "bag award line: " + bag);
        var full = LoginAwardPolicy.Describe(new LoginAwardGrant(Award(), true, true, [], false, 0));
        Check.True(full.Contains("bag was full") && full.Contains("Prize Chest"), "a full bag points at the chest: " + full);
        Check.True(LoginAwardPolicy.Describe(new LoginAwardGrant(Award() with { Name = new string('x', 300) }, false,
            false, [], false, 0)).Length <= 200, "the chat line is clipped to 200 characters");
        var draw = PrizeChestPolicy.Describe(new PrizeChestDraw([(4102, 3)], 0, 0, 500, 2));
        Check.True(draw.Contains("3 items") && draw.Contains("500 B-Gold") && draw.Contains("2 prize(s) still wait"),
            "draw line: " + draw);
        Check.Equal("The Prize Chest has nothing for you.", PrizeChestPolicy.Describe(new PrizeChestDraw([], 0, 0, 0, 0)),
            "empty chest line");
    }

    private static void CheckBroadcastFilter()
    {
        Check.True(BuffBroadcastFilter.Everyone.Matches(0, 1) && BuffBroadcastFilter.Everyone.Matches(1, 200),
            "no filter reaches both camps and every level");
        var athens = new BuffBroadcastFilter(1, 50, 100);
        Check.True(athens.Matches(1, 50) && athens.Matches(1, 100) && !athens.Matches(0, 60) && !athens.Matches(1, 49) &&
            !athens.Matches(1, 101), "camp 1 Athens, inclusive level range");
        Check.True(!new BuffBroadcastFilter(2, 1, 200).IsValid && !new BuffBroadcastFilter(null, 90, 10).IsValid &&
            !new BuffBroadcastFilter(null, 0, 10).IsValid, "bad camp or level ranges are refused");
    }

    private static void CheckPrizeChestFrames()
    {
        var entries = Enumerable.Range(1, 13).Select(i => new PrizeChestEntry(i, PrizeChestPolicy.KindItem, 4100 + i, i,
            1, 1, i % 2 == 0)).ToList();
        var frames = PacketBuilder.PrizeChestSnapshotPackets(entries, 750);
        Check.Equal(4, frames.Length, "40 award cells = four 12-cell MSG_STORAGE chunks");
        for (var chunk = 0; chunk < frames.Length; chunk++)
        {
            var frame = frames[chunk];
            Check.Equal((ushort)frame.Length, BinaryPrimitives.ReadUInt16LittleEndian(frame), "size word");
            Check.Equal((ushort)10034, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)), "opcode 10034");
            Check.Equal(750, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)), "+4 B-Gold box");
            Check.Equal((ushort)40, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(12)), "+12 capacity 40");
            Check.Equal((byte)(chunk * 2), frame[14], "+14 chunk selector");
            Check.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(18)), "+18 storage type 1 (award)");
        }

        Check.Equal((ushort)0x0FFF, BinaryPrimitives.ReadUInt16LittleEndian(frames[0].AsSpan(16)), "first chunk: 12 cells");
        Check.Equal((ushort)0x0001, BinaryPrimitives.ReadUInt16LittleEndian(frames[1].AsSpan(16)), "second chunk: cell 12");
        Check.Equal(24 + (12 * 72), frames[0].Length, "12 records of 72 bytes");
        Check.Equal(24, frames[2].Length, "an empty chunk is header only");
        Check.Equal(4113u, BinaryPrimitives.ReadUInt32LittleEndian(frames[1].AsSpan(24)), "record starts with the item id");
        Check.Throws<ArgumentOutOfRangeException>(() => PacketBuilder.PrizeChestSnapshotPackets(
            Enumerable.Range(1, 41).Select(i => new PrizeChestEntry(i, "item", 1, 1, 1, 1, true)).ToList(), 0),
            "the award tab has 40 cells");

        var visible = PrizeChestPolicy.VisibleItems(Enumerable.Range(1, 45)
            .Select(i => new PrizeChestEntry(46 - i, i % 9 == 0 ? PrizeChestPolicy.KindBindingGold : PrizeChestPolicy.KindItem,
                1, 10, 1, 1, true)));
        Check.True(visible.Count == 40 && visible.All(static e => e.Kind == PrizeChestPolicy.KindItem) &&
            visible.Select(static e => e.Id).SequenceEqual(visible.Select(static e => e.Id).Order()),
            "the oldest 40 item rows are shown, in id order");
        Check.Equal(30, PrizeChestPolicy.VisibleBindingGold([
            new PrizeChestEntry(1, PrizeChestPolicy.KindBindingGold, 0, 10, 1, 1, true),
            new PrizeChestEntry(2, PrizeChestPolicy.KindBindingGold, 0, 20, 1, 1, true),
            new PrizeChestEntry(3, PrizeChestPolicy.KindSilver, 0, 999, 1, 1, true)]), "B-Gold box sums B-Gold rows only");

        // The Draw button frame as GodsWar.exe 0x621558 builds it.
        var draw = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(draw, 20);
        BinaryPrimitives.WriteUInt16LittleEndian(draw.AsSpan(2), 10059);
        BinaryPrimitives.WriteInt16LittleEndian(draw.AsSpan(4), -1);
        BinaryPrimitives.WriteInt16LittleEndian(draw.AsSpan(6), -1);
        BinaryPrimitives.WriteInt16LittleEndian(draw.AsSpan(8), -1);
        BinaryPrimitives.WriteInt32LittleEndian(draw.AsSpan(12), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(draw.AsSpan(18), 1);
        Check.True(PacketBuilder.IsPrizeChestTransfer(draw), "the Draw frame is routed to the Prize Chest");
        Check.Equal(PrizeChestRequest.DrawAll, PacketBuilder.ParsePrizeChestTransfer(draw), "Draw takes everything");
        draw[18] = 0;
        Check.True(!PacketBuilder.IsPrizeChestTransfer(draw), "storage type 0 stays a normal warehouse transfer");
        Check.True(PacketBuilder.ParsePrizeChestTransfer(draw).Kind == PrizeChestRequestKind.Refused,
            "a normal warehouse frame is never a Prize Chest request");

        // W5-F: the other award-storage senders of GodsWar.exe (0x621adf, 0x574d54 / 0x57bb8d, 0x6219bf).
        static byte[] Award(short slot, short page, short cell, int money, byte operation)
        {
            var frame = new byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, 20);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 10059);
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(4), slot);
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(6), page);
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(8), cell);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), money);
            frame[16] = operation;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(18), 1);
            return frame;
        }

        Check.Equal(new PrizeChestRequest(PrizeChestRequestKind.Cell, 7),
            PacketBuilder.ParsePrizeChestTransfer(Award(7, -1, -1, 0, 0)), "a double-clicked award cell takes that cell");
        Check.Equal(new PrizeChestRequest(PrizeChestRequestKind.Cell, 39),
            PacketBuilder.ParsePrizeChestTransfer(Award(39, 2, 17, 0, 0)), "an award cell dragged to a bag cell takes that cell");
        Check.Equal(new PrizeChestRequest(PrizeChestRequestKind.Cell, 0),
            PacketBuilder.ParsePrizeChestTransfer(Award(0, 0, 0, 0, 0)), "award cell 0 to bag page 0 cell 0");
        foreach (var (frame, why) in new[]
                 {
                     (Award(5, 3, -1, 0, 0), "a warehouse item dropped ON an award cell (+6 = award cell, +8 = -1)"),
                     (Award(40, -1, -1, 0, 0), "a cell past the 40 the tab has"),
                     (Award(-2, -1, -1, 0, 0), "a negative cell"),
                     (Award(3, 4, 0, 0, 0), "a bag page past the four the client has"),
                     (Award(3, -1, -1, 0, 1), "a deposit (operation 1)"),
                     (Award(-1, -1, -1, -1, 1), "a Draw frame marked as a deposit"),
                     (Award(-1, -1, -1, 0, 0), "an all-minus-one frame without Draw's money word"),
                     (Award(-1, -1, -1, 500, 0), "a money withdrawal with an amount"),
                     (Award(3, -1, -1, 9, 0), "a cell request that also names money")
                 })
        {
            Check.True(PacketBuilder.ParsePrizeChestTransfer(frame).Kind == PrizeChestRequestKind.Refused, "refused: " + why);
        }
    }

    private static void CheckSwitchesAndCapabilities()
    {
        var keys = ServerRateCatalog.Definitions.ToDictionary(static d => d.Key);
        Check.True(keys.TryGetValue(Finalise2RateKeys.LoginAwards, out var login) && login.DefaultValue == 1m,
            "prize.login_awards is registered and on by default");
        Check.True(keys.TryGetValue(Finalise2RateKeys.QueuedBuffs, out var queued) && queued.DefaultValue == 1m,
            "prize.queued_buffs is on by default");
        Check.True(keys.TryGetValue(Finalise2RateKeys.PrizeChest, out var chest) && chest.DefaultValue == 1m,
            "prize.prize_chest is on by default");
        Check.True(keys.TryGetValue(Finalise2RateKeys.PrizeChestWindow, out var window) && window.DefaultValue == 1m,
            "prize.prize_chest_window is on by default (W5-F: seen working in the real client)");
        var caps = JsonNode.Parse(GameSessionRegistry.OperatorAdminCapabilitiesJson)!["endpoints"]!.AsArray();
        Check.True(caps.Any(static e => e!["path"]!.GetValue<string>() == GameSessionRegistry.BuffBroadcastPath),
            "/admin/buff/broadcast is advertised");
        Check.True(caps.Any(static e => e!["path"]!.GetValue<string>() == GameSessionRegistry.PrizeChestNotifyPath),
            "/admin/prize-chest/notify is advertised (W5-F online delivery)");
    }
}
