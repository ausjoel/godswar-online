using Godswar.Server.Application.World;

namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// NORTH-GIVERS-2026-11: checks that the north quest givers are loaded when the north
/// quests are on (see <see cref="NorthQuestGivers"/>).
/// </summary>
internal static class NorthQuestGiverPolicy
{
    /// <summary>
    /// The givers the enabled north quests name that have no enabled GM placement with
    /// that key on <see cref="NorthQuestGivers.MapId"/>. Empty while the north quests are off.
    /// </summary>
    public static IReadOnlyList<NorthQuestGivers.Giver> MissingGivers(GmNpcOverrideSnapshot snapshot) =>
        MissingGivers(snapshot, NorthQuests.EnabledSteps);

    internal static IReadOnlyList<NorthQuestGivers.Giver> MissingGivers(
        GmNpcOverrideSnapshot snapshot,
        IReadOnlyList<StarterQuestChain.Step> northSteps)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var named = northSteps
            .SelectMany(static step => new[] { step.GiverKey, step.ResponderKey })
            .ToHashSet(StringComparer.Ordinal);
        var placed = snapshot.SpawnsFor(NorthQuestGivers.MapId)
            .Select(static spawn => spawn.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        return NorthQuestGivers.All
            .Where(giver => named.Contains(giver.NpcKey) && !placed.Contains(giver.NpcKey))
            .ToList();
    }
}
