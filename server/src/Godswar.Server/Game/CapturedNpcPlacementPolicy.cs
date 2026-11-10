using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server.Game;

/// <summary>
/// Rewrites an NPC with the placement a reference capture recorded for it.
/// </summary>
/// <remarks>
/// Sparta's city and newbie maps were published from the capture, so their NPCs
/// already carry the reference's own object id, appearance word, position and
/// facing. Every other map was written from the client's ini, and the difference
/// is visible in the frames the client receives: on Athens city the appearance
/// word is 0x11 where the reference sent 0x111, and the ids are shifted - the
/// npc at (75.9, -78.8) is the capture's 5212 with script key Athens_074 while
/// our row calls the same template 5213. The client resolves an NPC's dialog from
/// that identity, which is why clicking one of ours sends nothing at all.
/// <para>
/// The maps whose frames already come from the capture are excluded, so a frame
/// that is known to work is never rewritten. Everything the capture recorded is
/// applied together: id, interaction id, appearance word, position and facing all
/// belong to the same actor, and taking only some of them would mix two servers'
/// data for one object.
/// </para>
/// </remarks>
internal static class CapturedNpcPlacementPolicy
{
    /// <summary>
    /// Maps whose NPCs are published from the capture already and must not move.
    /// </summary>
    private static readonly short[] AlreadyPublishedFromCapture = [0, 4];

    public static NpcSpawnDefinition Apply(NpcSpawnDefinition npc)
    {
        if (AlreadyPublishedFromCapture.Contains(npc.MapId) ||
            !CapturedNpcPlacements.TryFind(npc.NpcKey, out var captured))
        {
            return npc;
        }

        return Place(npc, captured);
    }

    /// <summary>
    /// Applies the capture to a whole map and keeps every identity unique.
    /// </summary>
    /// <remarks>
    /// The capture only recorded 90 of Athens' 128 city NPCs, and our published
    /// ids for the rest are one higher than the reference's. Renumbering just the
    /// captured subset therefore landed one NPC on another's id - Athens_051 keeps
    /// its published 5190 while the captured Athens_052 also becomes 5190 - and the
    /// catalog installs NPCs into dictionaries keyed by object id and interaction
    /// id with <c>Add</c>, so the duplicate threw and the session ended with the
    /// client reporting a disconnect. Sparta never hit it because its city map is
    /// published from the capture and is excluded here.
    /// <para>
    /// The captured placements stay authoritative and are applied first. An NPC the
    /// capture did not record keeps its published id while that id is free on both
    /// axes, and otherwise moves above the map's highest id: the id is a runtime
    /// handle the client echoes back, so a fresh free one is harmless, where a
    /// duplicate ends the session.
    /// </para>
    /// </remarks>
    public static List<NpcSpawnDefinition> ApplyToMap(
        IReadOnlyList<NpcSpawnDefinition> npcs)
    {
        var candidates = DropAbsentFromCapture(npcs);
        var placed = new List<NpcSpawnDefinition>(candidates.Count);
        var pending = new List<NpcSpawnDefinition>(candidates.Count);
        var usedIds = new HashSet<uint>();
        foreach (var npc in candidates)
        {
            if (AlreadyPublishedFromCapture.Contains(npc.MapId) ||
                !CapturedNpcPlacements.TryFind(npc.NpcKey, out var captured))
            {
                pending.Add(npc);
                continue;
            }

            var effective = Place(npc, captured);
            if (!usedIds.Add(effective.ObjectId))
            {
                Console.WriteLine(
                    $"[npc] captured placement collides map={npc.MapId} " +
                    $"object={effective.ObjectId} key={npc.NpcKey}");
                pending.Add(npc);
                continue;
            }

            placed.Add(effective);
        }

        // Sorted so the ids a map ends up with do not depend on the order the
        // content reader happened to return its rows in.
        pending.Sort(static (left, right) =>
            string.CompareOrdinal(left.NpcKey, right.NpcKey));
        var nextFree = usedIds.Count == 0 ? 1u : usedIds.Max() + 1u;
        foreach (var npc in pending)
        {
            if (usedIds.Add(npc.ObjectId) &&
                (npc.InteractionId == npc.ObjectId ||
                 usedIds.Add(npc.InteractionId)))
            {
                placed.Add(npc);
                continue;
            }

            var renumbered = npc with
            {
                ObjectId = nextFree,
                InteractionId = nextFree
            };
            usedIds.Add(nextFree);
            Console.WriteLine(
                $"[npc] renumbered uncaptured npc map={npc.MapId} " +
                $"key={npc.NpcKey} from={npc.ObjectId} to={nextFree}");
            while (usedIds.Contains(nextFree))
            {
                nextFree++;
            }

            placed.Add(renumbered);
        }

        return placed;
    }

    /// <summary>
    /// Drops the NPCs a captured map does not have.
    /// </summary>
    /// <remarks>
    /// A map the capture covers is the reference server's own content, so a row
    /// the reference never placed there is ours, not its. Maps the capture does
    /// not cover are left exactly as published: "the reference does not have it"
    /// and "we never went there" are different statements, and only the first
    /// justifies removing a row.
    /// </remarks>
    private static List<NpcSpawnDefinition> DropAbsentFromCapture(
        IReadOnlyList<NpcSpawnDefinition> npcs)
    {
        var kept = new List<NpcSpawnDefinition>(npcs.Count);
        if (npcs.Count == 0)
        {
            return kept;
        }

        // The list is one map's content, so its first row names the map.
        var mapId = npcs[0].MapId;
        if (!CapturedNpcPlacements.HasCaptureFor(mapId))
        {
            kept.AddRange(npcs);
            return kept;
        }

        var capturedKeys = CapturedNpcPlacements.CapturedKeysFor(mapId);
        foreach (var npc in npcs)
        {
            if (capturedKeys.Contains(npc.NpcKey))
            {
                kept.Add(npc);
                continue;
            }

            Console.WriteLine(
                $"[npc] dropped not-in-capture map={npc.MapId} key={npc.NpcKey} " +
                $"object={npc.ObjectId}");
        }

        return kept;
    }

    private static NpcSpawnDefinition Place(
        NpcSpawnDefinition npc,
        CapturedNpcPlacements.Placement captured) =>
        npc with
        {
            ObjectId = captured.ObjectId,
            InteractionId = captured.ObjectId,
            AppearanceType = captured.AppearanceType,
            X = captured.X,
            Z = captured.Z,
            Facing = captured.Facing
        };
}
