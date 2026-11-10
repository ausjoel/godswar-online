using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>F-138 newbie growth rewards: the RookieQuestReward table, the 10308 wire and the server-side checks.</summary>
internal static class RookieGrowthChecks
{
    public const string CheckName = "F-138 newbie growth rewards (Rookie Guide 10308)";

    public static Task RunAsync()
    {
        ServerRates.ResetToDefaults();
        Check.True(Finalise2Rates.On(Finalise2RateKeys.RookieGrowth), "prize.rookie_growth defaults to on");

        // The table is the client's own file.
        Check.Equal(112, RookieGrowthCatalog.Rewards.Count, "112 RookieQuestReward rows");
        var r124 = RookieGrowthCatalog.Rewards[124];
        Check.True(r124.Items.Count == 3 && r124.Items[2] is { ItemId: 4222, Count: 3, Bound: true },
            "ID124 item3 '3.' is read as 3");
        var r130 = RookieGrowthCatalog.Rewards[130];
        Check.True(r130 is { Experience: 2000, SkillPoint: 10, Money: 1000, BindingGold: 333 } &&
                   r130.Items[0] is { ItemId: 2101, Quality: 3, Grade: 3, Count: 1 } &&
                   r130.Items[0].Appends.SequenceEqual([12, 101]) && !r130.Items[0].IsPlain,
            "To Level 20: 2000 EXP, 10 TP, 1000 silver, 333 B-Gold, weapon 2101 with appends 12/101 at 3/3");
        Check.True(RookieGrowthCatalog.Rewards[1].Items[0] is { ItemId: 4001, Count: 10, IsPlain: true },
            "Accept Quest: 10 x 4001");
        Check.True(RookieGrowthCatalog.Rewards.Values.SelectMany(r => r.Items).All(i => i.Count is > 0 and <= 99),
            "no reward stack above 99");

        // Only checkable, fully payable entries are offered.
        Check.Equal(37, RookieGrowthCatalog.Rules.Count, "37 entries offered");
        foreach (var id in RookieGrowthCatalog.Rules.Keys)
        {
            Check.True(id is > 0 and < 256 && RookieGrowthCatalog.Rewards.TryGetValue(id, out var reward) &&
                       RookieGrowthCatalog.IsPayable(reward), $"offered entry {id} has a payable reward");
        }

        Check.True(!RookieGrowthCatalog.Rules.ContainsKey(61) && !RookieGrowthCatalog.Rules.ContainsKey(45) &&
                   !RookieGrowthCatalog.Rules.ContainsKey(49), "guild contribution / Masters Ethics entries are not offered");
        Check.True(!RookieGrowthCatalog.IsPayable(RookieGrowthCatalog.Rewards[61]), "guild contribution is not payable");
        var clientReported = RookieGrowthCatalog.Rules
            .Where(p => p.Value.Condition == RookieGrowthCondition.ClientReported).Select(p => p.Key).Order().ToArray();
        Check.True(clientReported.SequenceEqual([3, 4, 5, 6, 7, 8, 19]),
            "sub 3 is believed only for the ids the exe itself reports (3,4,5,6,7,8,19)");

        // Server-side checks.
        var fresh = new GameCharacter { Level = 19 };
        Check.True(RookieGrowthCatalog.IsMet(8, RookieGrowthCatalog.SubComplete, fresh), "map settings: the exe's report counts");
        Check.True(!RookieGrowthCatalog.IsMet(130, RookieGrowthCatalog.SubRequestComplete, fresh), "level 19 cannot claim To Level 20");
        var twenty = new GameCharacter { Level = 120 };
        Check.True(RookieGrowthCatalog.IsMet(130, RookieGrowthCatalog.SubRequestComplete, twenty), "level 120 claims To Level 20");
        Check.True(RookieGrowthCatalog.IsMet(141, RookieGrowthCatalog.SubRequestComplete, twenty), "level 120 claims To Level 120");
        Check.True(!RookieGrowthCatalog.IsMet(141, RookieGrowthCatalog.SubComplete, twenty),
            "a forged sub 3 cannot claim a server-checked entry");
        Check.True(!RookieGrowthCatalog.IsMet(61, RookieGrowthCatalog.SubRequestComplete, twenty), "an entry not offered never passes");
        Check.True(!RookieGrowthCatalog.IsMet(8, RookieGrowthCatalog.SubDelete, fresh), "delete is not a claim");
        Check.True(!RookieGrowthCatalog.IsMet(2, RookieGrowthCatalog.SubRequestComplete, fresh) &&
                   RookieGrowthCatalog.IsMet(2, RookieGrowthCatalog.SubRequestComplete, new GameCharacter { QuestCompletedIds = [101] }),
            "Submit needs a completed quest");
        Check.True(RookieGrowthCatalog.IsMet(116, RookieGrowthCatalog.SubRequestComplete, new GameCharacter { WeaponRank = 2 }) &&
                   !RookieGrowthCatalog.IsMet(117, RookieGrowthCatalog.SubRequestComplete, new GameCharacter { WeaponRank = 2 }),
            "weapon rank 2 claims Weapon Rank 2 only");
        Check.True(RookieGrowthCatalog.IsMet(20, RookieGrowthCatalog.SubRequestComplete, new GameCharacter { ZodiacLevel = 2 }) &&
                   !RookieGrowthCatalog.IsMet(24, RookieGrowthCatalog.SubRequestComplete, new GameCharacter { ZodiacLevel = 3 }),
            "zodiac level thresholds");

        var offered = RookieGrowthCatalog.Offered(new HashSet<int> { 1, 130 });
        Check.True(!offered.Contains(1) && !offered.Contains(130) && offered.Contains(2) && offered.Count ==
            RookieGrowthCatalog.Rules.Count - 2, "claimed or deleted entries are not offered again");

        // Client request: 24 bytes, +8 u16 sub, +0xc u32 id.
        var request = new byte[RookieGrowthCatalog.ClientPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(request, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), RookieGrowthCatalog.Opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(8), RookieGrowthCatalog.SubRequestComplete);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(0xc), 130);
        Check.True(RookieGrowthCatalog.ParseRequest(request) == (RookieGrowthCatalog.SubRequestComplete, 130), "request parses");
        Check.True(RookieGrowthCatalog.ParseRequest(request.AsSpan(0, 12)) is null, "a short request is refused");

        // Server SYN: read back the way 0x4bf8c0 does (u64 word id>>6, bit id&63 at +0x18).
        var sync = RookieGrowthCatalog.Sync(0x1448, [1, 8, 130, 255, 256, 0]);
        Check.Equal(56, sync.Length, "SYN is 56 bytes");
        Check.Equal((ushort)56, BinaryPrimitives.ReadUInt16LittleEndian(sync), "size field");
        Check.Equal((ushort)10308, BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(2)), "opcode 10308");
        Check.Equal(0x1448u, BinaryPrimitives.ReadUInt32LittleEndian(sync.AsSpan(4)), "local player object id at +4");
        Check.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(sync.AsSpan(8)), "sub 1 = SYN");
        var set = Enumerable.Range(0, 256).Where(id =>
            (BinaryPrimitives.ReadUInt64LittleEndian(sync.AsSpan(0x18 + 8 * (id >> 6))) >> (id & 63) & 1) == 1).ToArray();
        Check.True(set.SequenceEqual([1, 8, 130, 255]), "mask holds exactly the offered ids (0 and 256 dropped)");

        var complete = RookieGrowthCatalog.Entry(0x1448, RookieGrowthCatalog.SubComplete, 130, 12345);
        Check.True(complete.Length == 56 && BinaryPrimitives.ReadUInt16LittleEndian(complete.AsSpan(8)) == 3 &&
                   BinaryPrimitives.ReadInt32LittleEndian(complete.AsSpan(0xc)) == 130 &&
                   BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(0x10)) == 12345 &&
                   complete.AsSpan(0x18).IndexOfAnyExcept((byte)0) < 0,
            "COM: sub 3, id at +0xc, new current EXP at +0x10, empty mask");

        // The server's mirror of the client mask: each open id closes once, never-synced ids never.
        var mask = new RookieGrowthMask();
        mask.Reset([1, 8, 130, 0, 300]);
        Check.True(mask.Contains(8) && !mask.Contains(0) && !mask.Contains(300), "mirror holds the synced ids only");
        Check.True(mask.TryClose(8) && !mask.TryClose(8), "an entry closes once (no second COM/DEL to underflow the mask)");
        Check.True(!mask.TryClose(2), "an entry that was never synced cannot be closed");
        mask.Reset([2]);
        Check.True(mask.Contains(2) && !mask.Contains(1), "a SYN replaces the whole mirror");
        return Task.CompletedTask;
    }
}
