namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// ARENA build: admission to Parnassus (map 68) from the capital Battlefield
/// Transporters.
/// </summary>
/// <remarks>
/// Retail rules, IGG "Parnassus" (igg-acid=371): "Accessible Periods 12:00pm-6:00pm,
/// 7:00pm-11:55pm (Server Time). Level Requirement Level 70-89. To enter
/// Parnassus, turn to the Battlefield Transporter (173, -89) in your faction's
/// main city."
/// <para>
/// The stock client already has every line of it in the Battlefield Transporter's
/// script (<c>UI/XML/NpcFun/NpcFunTranmit.lua</c>, <c>UI/Base/LuaText.lua</c>):
/// Index 1 sub 1002 draws the button <c>NF_L0_1002</c> "*Parnassus"; Index 2 sub
/// 1003 prints <c>NF_L0_1003</c> "The battlefield can only be accessed from
/// 12:00pm-6:00pm and 7:00pm-11:55pm." and sub 1004 prints <c>NF_L0_1004</c> (the
/// Lv70-89 refusal). Sub 1001 next to it is the Duel Arena button this server
/// already serves, so 1002 is the retail Parnassus button of the same menu. No
/// capture of a 1002 click exists: the click layout is assumed to be the Duel
/// Arena's (dialog 1, sub id, all 18 path arguments -1).
/// </para>
/// <para>
/// Arrival: each faction's "Main City Teleporter" named point of the client's own
/// <c>Monster/Parnassus/Address.ini</c> (Sparta [0] 220,-211; Athens [1]
/// -212,223), beside that faction's Frontline Commander (the quest giver). Both
/// points are walkable and clear in <c>Map/Parnassus.hmp</c>. This is a client
/// named point, not a captured server arrival.
/// </para>
/// </remarks>
internal static class ParnassusProtocol
{
    public const byte MapId = 68;
    public const int EntrySubId = 1002;
    public const int ResultDialogIndex = 2;
    public const int ClosedResultSubId = 1003;
    public const int LevelResultSubId = 1004;
    public const int MinimumLevel = 70;
    public const int MaximumLevel = 89;

    public static (float X, float Z) Arrival(byte camp) =>
        camp == 0 ? (220f, -211f) : (-212f, 223f);

    /// <summary>NPC_FLAG_SYS_TRANMIT (NpcFun.lua): the transporter script.</summary>
    public const int ReturnFunction = 1;
    public const string AthensReturnNpcKey = "Parnassus_003";
    public const string SpartaReturnNpcKey = "Parnassus_004";

    public static bool IsReturnTransporter(int mapId, string npcKey) =>
        mapId == MapId && npcKey is AthensReturnNpcKey or SpartaReturnNpcKey;

    /// <summary>
    /// <c>NpcFunTranmit.lua</c>: 1005 "前线战场回雅典" (back to Athens), 1006
    /// "前线战场回斯巴达" (back to Sparta). Which key stands on which side is
    /// GENERATED: by analogy with the commanders (Parnassus_001 Athens,
    /// Parnassus_002 Sparta), 003 is the Athenian and 004 the Spartan transporter.
    /// </summary>
    public static int ReturnButton(string npcKey) =>
        npcKey == AthensReturnNpcKey ? 1005 : 1006;

    /// <summary>The menu the transporter shows, with the Parnassus button appended once.</summary>
    public static int[] WithEntryButton(IReadOnlyList<int> menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        return menu.Contains(EntrySubId)
            ? menu.ToArray()
            : menu.Append(EntrySubId).ToArray();
    }

    /// <summary>
    /// IGG accessible periods, server (realm) time: [12:00, 18:00) and [19:00, 23:55).
    /// </summary>
    public static bool IsOpen(TimeOnly realmTime) =>
        (realmTime >= new TimeOnly(12, 0) && realmTime < new TimeOnly(18, 0)) ||
        (realmTime >= new TimeOnly(19, 0) && realmTime < new TimeOnly(23, 55));

    public static bool IsEntryClick(
        int dialogIndex,
        int subId,
        IReadOnlyList<int> arguments) =>
        dialogIndex == BattlefieldTransporterProtocol.DialogIndex &&
        subId == EntrySubId &&
        arguments.Count == BattlefieldTransporterProtocol.FunctionArgumentCount &&
        arguments.All(static value => value == -1);
}
