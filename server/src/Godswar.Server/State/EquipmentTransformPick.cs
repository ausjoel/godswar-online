namespace Godswar.Server.State;

/// <summary>
/// Lets the Trans tab make exactly the preview entry the player clicked.
/// </summary>
/// <remarks>
/// W6-D (2026-11-16), from GodsWar.exe 1.5.0 (md5 88a7b7e9...) and the live log:
/// the preview window's click handler (0x5eacc0) loops edi = 0..23 over its 24
/// buttons (ReplacementUI.xml Item0..Item23); for the clicked button it pushes
/// edi and the source item id and calls 0x5612e0, which sends C2S 10111 as
/// source id, <b>clicked button index</b>, 2, 3. (Earlier notes said "0": that
/// is only the first button.) The buttons are filled by 0x5ea8f0 in RltID order
/// with the source id skipped, which is exactly
/// <see cref="EquipmentTransformCatalog.GetPreviewList"/>. So the stock client
/// already tells the server what was clicked; no exe patch is needed.
/// <para>
/// Wire value below 24 = button index into the unfiltered preview list. A value
/// of 1000 or more is accepted as an item id (a possible patched client); item
/// ids and indices cannot collide. GODSWAR_AMETHYST_PICK=0 (or unset) ignores the
/// value and keeps the old random pick.
/// </para>
/// </remarks>
internal static class EquipmentTransformPick
{
    public const int PreviewButtonCount = 24;

    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("GODSWAR_AMETHYST_PICK") == "1"; // RELEASE: opt-in

    /// <summary>
    /// The target item the player clicked, or 0 when the value names no
    /// preview entry of <paramref name="sourceItemId"/>.
    /// </summary>
    public static uint ResolvePick(uint sourceItemId, uint wireValue)
    {
        var preview = EquipmentTransformCatalog.GetPreviewList(sourceItemId);
        if (wireValue < PreviewButtonCount)
        {
            return wireValue < preview.Count ? preview[(int)wireValue] : 0;
        }

        return preview.Contains(wireValue) ? wireValue : 0;
    }

    /// <summary>
    /// Index of <paramref name="preferredTargetId"/> in the published targets;
    /// the random index when nothing was picked (0); -1 when the picked target
    /// is not published (the transform is then refused, never re-rolled).
    /// </summary>
    public static int ChooseIndex(
        IReadOnlyList<uint> publishedTargets,
        uint preferredTargetId,
        int randomIndex)
    {
        ArgumentNullException.ThrowIfNull(publishedTargets);
        if (preferredTargetId == 0)
        {
            return randomIndex;
        }

        for (var index = 0; index < publishedTargets.Count; index++)
        {
            if (publishedTargets[index] == preferredTargetId)
            {
                return index;
            }
        }

        return -1;
    }
}
