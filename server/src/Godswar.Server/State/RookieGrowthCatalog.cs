using System.Buffers.Binary;

namespace Godswar.Server.State;

/// <summary>
/// One reward item: base id, appended attributes (no -1), base level, append level, count, binding. GENERATED mapping:
/// base level -> Quality (wire byte 24) and append level -> Grade (byte 25), the order of the file's column comment
/// ("bl, al") and of the 2008 ItemData (Base, BaseLV, Append, AppendLV); only rows 132-141 have the two differ.
/// </summary>
internal readonly record struct RookieGrowthItem(int ItemId, int[] Appends, short Quality, short Grade, short Count, bool Bound)
{
    /// <summary>A plain item (no appends, level 1/1) can go through the shared stacking grant.</summary>
    public bool IsPlain => Appends.Length == 0 && Quality == 1 && Grade == 1;
}

/// <summary>One RookieQuestReward.xml row, in the client's field order (CRookieQuestUI reward struct).</summary>
internal sealed record RookieGrowthReward(
    int Id,
    int Group,
    long Experience,
    int SkillPoint,
    int Money,
    int Stone,
    int UnionStone,
    int UnionMoney,
    int UnionContribute,
    int BindingGold,
    int MasterVal,
    int ConsEnergy,
    int Reputation,
    IReadOnlyList<RookieGrowthItem> Items);

/// <summary>What finishes an entry.</summary>
internal enum RookieGrowthCondition
{
    /// <summary>A pure UI action the stock exe reports itself (sub 3); the server cannot see it.</summary>
    ClientReported,
    HoldsOrDoneQuest,
    CompletedQuest,
    ZodiacLevel,
    ZodiacGridLevel,
    Reputation,
    WeaponRank,
    ArmorRank,
    Level,
}

internal readonly record struct RookieGrowthRule(RookieGrowthCondition Condition, int Threshold = 0);

/// <summary>
/// the admin F-138 newbie growth rewards: the Rookie Guide window (minimap RookieBtn -> RookieGuideWin, RookieGuideProc.lua,
/// texts RookieGuideCofig.lua, rewards Settings/Sys/RookieQuestReward.xml). Wire, from GodsWar.exe 2013 (client 1.4.4):
///   10308 client -> server, 24 bytes (send helper 0x4bfae0, static buffer 0x15ae618): +8 u16 sub, +0xc u32 entry id.
///     sub 3 = "done" reported by the exe itself (ids 3,4,5,6 via 0x4bfb30; 7 inline at 0x5627a7; 8 at 0x63705c and
///     Lua CompleteRookie(8); 19 at 0x692fd7); sub 4 = delete (Lua DelRookie); sub 5 = the window's Complete button
///     (Lua RequestCompleteRookie). Every sender first tests the entry's bit in the local player's mask (+0x768).
///   10308 server -> client (handler 0x4e9b3a -> 0x4bf8c0), only in world: +4 u32 must equal the local player's object
///     id (+0x248, set by EnterMain) or it is dropped; +8 u16 sub 1 = SYN (the 256-bit mask at +0x18, every new bit
///     calls Lua RookieGuideMsg(1,id) silently), 2 = ADD (one id, announced), 3 = COM (clears the bit, prints the
///     reward line, then CRookieQuestUI 0x5f12c0 applies the XML reward to its local copy and writes +0x10 into the
///     player's current EXP +0x2b4), 4 = DEL. The packet is 56 bytes for every sub (GENERATED: the exe reads up to +0x38).
/// The client applies COM rewards to its own bag/wallet model, so the server pays, sends COM with the new EXP, then
/// sends the authoritative bag pages and status so the server's numbers win.
/// </summary>
internal static partial class RookieGrowthCatalog
{
    public const ushort Opcode = 10308;
    public const int ClientPacketLength = 24;
    public const int ServerPacketLength = 56;
    public const ushort SubSync = 1;
    public const ushort SubAdd = 2;
    public const ushort SubComplete = 3;
    public const ushort SubDelete = 4;
    public const ushort SubRequestComplete = 5;
    public const short StateClaimed = 1;
    public const short StateDeleted = 2;

    /// <summary>
    /// The entries this server offers and how each one is checked. Only entries whose condition the server can check
    /// AND whose every reward field it can pay are listed. Left out (reported to the admin): guild contribution / guild
    /// silver (GuildForword 61-86, Guild Chat 49), Masters Ethics (45), and the conditions the server does not track
    /// (skills, Talent, pets, fashion, stall, friends, team, chat, kills, forging, nameplates, wishes).
    /// Thresholds come from the window's Aim texts (RookieGuideCofig.lua).
    /// </summary>
    public static IReadOnlyDictionary<int, RookieGrowthRule> Rules { get; } = new Dictionary<int, RookieGrowthRule>
    {
        [1] = new(RookieGrowthCondition.HoldsOrDoneQuest), // GENERATED reading of "Accept a quest.": holds or finished one
        [2] = new(RookieGrowthCondition.CompletedQuest), // GENERATED reading of "Submit a quest.": finished one
        [3] = new(RookieGrowthCondition.ClientReported), // Quest search
        [4] = new(RookieGrowthCondition.ClientReported), // Auto-track
        [5] = new(RookieGrowthCondition.ClientReported), // Quest auto-track
        [6] = new(RookieGrowthCondition.ClientReported), // Map auto-track
        [7] = new(RookieGrowthCondition.ClientReported), // Open a map
        [8] = new(RookieGrowthCondition.ClientReported), // Map settings
        [19] = new(RookieGrowthCondition.ClientReported), // AFK feature
        [20] = new(RookieGrowthCondition.ZodiacLevel, 2),
        [22] = new(RookieGrowthCondition.ZodiacGridLevel, 2), // GENERATED: "zodiac effect" = any zodiac skill grid at level 2
        [24] = new(RookieGrowthCondition.ZodiacLevel, 4),
        [25] = new(RookieGrowthCondition.ZodiacLevel, 6),
        [52] = new(RookieGrowthCondition.Reputation, 100), // GENERATED: Reputation = the current honour balance
        [53] = new(RookieGrowthCondition.Reputation, 500),
        [54] = new(RookieGrowthCondition.Reputation, 1200),
        [116] = new(RookieGrowthCondition.WeaponRank, 2),
        [117] = new(RookieGrowthCondition.WeaponRank, 3),
        [118] = new(RookieGrowthCondition.WeaponRank, 4),
        [119] = new(RookieGrowthCondition.WeaponRank, 5),
        [120] = new(RookieGrowthCondition.ArmorRank, 2),
        [121] = new(RookieGrowthCondition.ArmorRank, 3),
        [122] = new(RookieGrowthCondition.ArmorRank, 4),
        [123] = new(RookieGrowthCondition.ArmorRank, 5),
        [124] = new(RookieGrowthCondition.ArmorRank, 6),
        [130] = new(RookieGrowthCondition.Level, 20),
        [131] = new(RookieGrowthCondition.Level, 30),
        [132] = new(RookieGrowthCondition.Level, 40),
        [133] = new(RookieGrowthCondition.Level, 50),
        [134] = new(RookieGrowthCondition.Level, 60),
        [135] = new(RookieGrowthCondition.Level, 70),
        [136] = new(RookieGrowthCondition.Level, 80),
        [137] = new(RookieGrowthCondition.Level, 90),
        [138] = new(RookieGrowthCondition.Level, 100),
        [139] = new(RookieGrowthCondition.Level, 110),
        [140] = new(RookieGrowthCondition.Level, 115),
        [141] = new(RookieGrowthCondition.Level, 120),
    };

    /// <summary>True when the server pays every field of this reward (the rule list must only hold such entries).</summary>
    public static bool IsPayable(RookieGrowthReward reward) =>
        reward is { Stone: 0, UnionStone: 0, UnionMoney: 0, UnionContribute: 0, MasterVal: 0 };

    /// <summary>
    /// Offered entries, minus the ones this character claimed or deleted. GENERATED: every supported entry is offered
    /// in the one SYN at world entry (the stock server's own unlock order is unknown), and a delete is permanent.
    /// </summary>
    public static IReadOnlyList<int> Offered(IReadOnlyCollection<int> closed) =>
        Rules.Keys.Where(id => !closed.Contains(id)).Order().ToArray();

    /// <summary>
    /// Whether a request may finish <paramref name="id"/>. Sub 3 is only believed for client-reported entries.
    /// GENERATED: the Complete button (sub 5) on a client-reported entry is believed too (the server sees neither).
    /// </summary>
    public static bool IsMet(int id, ushort sub, GameCharacter character)
    {
        if (!Rules.TryGetValue(id, out var rule) || sub is not (SubComplete or SubRequestComplete))
        {
            return false;
        }

        if (sub == SubComplete && rule.Condition != RookieGrowthCondition.ClientReported)
        {
            return false;
        }

        return rule.Condition switch
        {
            RookieGrowthCondition.ClientReported => true,
            RookieGrowthCondition.HoldsOrDoneQuest => character.Quests.Count > 0 || character.QuestCompletedIds.Length > 0,
            RookieGrowthCondition.CompletedQuest => character.QuestCompletedIds.Length > 0,
            RookieGrowthCondition.ZodiacLevel => character.ZodiacLevel >= rule.Threshold,
            RookieGrowthCondition.ZodiacGridLevel => character.ZodiacSkillGridLevels.Any(level => level >= rule.Threshold),
            RookieGrowthCondition.Reputation => character.MedusaHonorPoints >= rule.Threshold,
            RookieGrowthCondition.WeaponRank => character.WeaponRank >= rule.Threshold,
            RookieGrowthCondition.ArmorRank => character.ArmorRank >= rule.Threshold,
            RookieGrowthCondition.Level => character.Level >= rule.Threshold,
            _ => false,
        };
    }

    /// <summary>Reads a client 10308: (sub, entry id), or null when it is too short.</summary>
    public static (ushort Sub, int Id)? ParseRequest(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 0x10)
        {
            return null;
        }

        return (BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..]), BinaryPrimitives.ReadInt32LittleEndian(buffer[0xc..]));
    }

    /// <summary>A server 10308 for one entry (ADD / COM / DEL). <paramref name="value"/> = the new current EXP on COM.</summary>
    public static byte[] Entry(uint playerObjectId, ushort sub, int id, uint value = 0)
    {
        var packet = Header(playerObjectId, sub);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0xc), id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x10), value);
        return packet;
    }

    /// <summary>The SYN: every offered entry as one bit of the 256-bit mask at +0x18 (bit id&amp;63 of u64 id&gt;&gt;6).</summary>
    public static byte[] Sync(uint playerObjectId, IEnumerable<int> ids)
    {
        var packet = Header(playerObjectId, SubSync);
        foreach (var id in ids)
        {
            if (id is < 1 or > 255)
            {
                continue;
            }

            packet[0x18 + (id >> 3)] |= (byte)(1 << (id & 7));
        }

        return packet;
    }

    private static byte[] Header(uint playerObjectId, ushort sub)
    {
        var packet = new byte[ServerPacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, ServerPacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), playerObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), sub);
        return packet;
    }
}

/// <summary>
/// The server's copy of the client's entry mask (player+0x768). The exe sets and clears bits with plain add / subtract
/// (0x4bf995 add/adc, 0x4bf9d3 and 0x4bfa55 sub/sbb) without testing them, so a COM or DEL for a bit that is already
/// clear borrows into the neighbouring entries. Only ids present here may be closed, and each only once.
/// </summary>
internal sealed class RookieGrowthMask
{
    private readonly object _gate = new();
    private readonly HashSet<int> _open = [];

    /// <summary>A SYN replaces the client's whole mask (it copies all 32 bytes).</summary>
    public void Reset(IEnumerable<int> ids)
    {
        lock (_gate)
        {
            _open.Clear();
            _open.UnionWith(ids.Where(static id => id is > 0 and < 256));
        }
    }

    public bool Contains(int id)
    {
        lock (_gate) return _open.Contains(id);
    }

    /// <summary>True once per open id: the caller may then send the COM or DEL that clears it.</summary>
    public bool TryClose(int id)
    {
        lock (_gate) return _open.Remove(id);
    }
}
