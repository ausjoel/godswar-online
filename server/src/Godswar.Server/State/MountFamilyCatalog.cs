namespace Godswar.Server.State;

/// <summary>W2 Mount Feeder (the admin F-52): mount families (Lv40..120, one item per level) and their Mount Shifters.</summary>
internal sealed record MountFamily(string Name, uint[] Levels, uint ShifterId, IReadOnlyDictionary<uint, int> Extra)
{
    public uint ItemAt(int level) => Levels[(level - 40) / 10];
}

internal static partial class MountFamilyCatalog
{
    public const uint SoulStoneBaseItemId = 14201; // Soul Stone (Lvl 50) .. (Lvl 120) = 14201 .. 14208

    public static IReadOnlyList<MountFamily> Families => Generated;

    public static uint SoulStoneFor(int level) => SoulStoneBaseItemId + (uint)((level - 50) / 10);

    /// <summary>The family and level of a mount item (main ids and the special/timed/legacy variants).</summary>
    public static bool TryFind(uint itemId, out MountFamily family, out int level)
    {
        foreach (var candidate in Generated)
        {
            var index = Array.IndexOf(candidate.Levels, itemId);
            if (index >= 0)
            {
                (family, level) = (candidate, 40 + index * 10);
                return true;
            }
            if (candidate.Extra.TryGetValue(itemId, out level))
            {
                family = candidate;
                return true;
            }
        }
        (family, level) = (null!, 0);
        return false;
    }

    public static MountFamily? ByShifter(uint shifterId) => Generated.FirstOrDefault(f => f.ShifterId == shifterId);

    /// <summary>Mount gear: the five Lv40-120 gear lines (Coronet/Armor/Soul/Saddle/Tassels).</summary>
    public static bool IsMountGear(uint itemId) => itemId is >= 14500 and <= 14508 or >= 14600 and <= 14608 or
        >= 14700 and <= 14708 or >= 14800 and <= 14808 or >= 14900 and <= 14908;
}
