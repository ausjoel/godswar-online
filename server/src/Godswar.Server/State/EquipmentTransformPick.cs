namespace Godswar.Server.State;

/// <summary>
/// FORGEGEMS: lets the Trans tab honour the player's preview choice.
/// The stock 2.51 client sends C2S 10111 (preview pick) as source item id, 0,
/// 2, 3 (0x5612e0 called from 0x5eb02e): the clicked candidate is not on the
/// wire, so the server keeps its random pick. A patched client may send the
/// clicked candidate id in dword +4 (UNPROVEN proposal, see the forgegems
/// README); when that id is one of the published RltID targets it is used.
/// GODSWAR_AMETHYST_PICK=0 ignores dword +4 entirely.
/// </summary>
internal static class EquipmentTransformPick
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("GODSWAR_AMETHYST_PICK") == "1"; // RELEASE: opt-in

    /// <summary>Index of <paramref name="preferredTargetId"/> in the targets, or the random index.</summary>
    public static int ChooseIndex(
        IReadOnlyList<uint> publishedTargets,
        uint preferredTargetId,
        int randomIndex)
    {
        ArgumentNullException.ThrowIfNull(publishedTargets);
        if (preferredTargetId != 0)
        {
            for (var index = 0; index < publishedTargets.Count; index++)
            {
                if (publishedTargets[index] == preferredTargetId)
                {
                    return index;
                }
            }
        }

        return randomIndex;
    }
}
