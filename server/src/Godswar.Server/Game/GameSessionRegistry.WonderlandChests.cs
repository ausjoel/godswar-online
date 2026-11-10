using Godswar.Server.Application.Commands;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private IWonderlandChestClaimStore? _wonderlandChests;

    internal void ConfigureWonderlandChests(IWonderlandChestClaimStore? store)
    {
        if (store is null) return;
        var previous = Interlocked.CompareExchange(ref _wonderlandChests, store, null);
        if (previous is not null && !ReferenceEquals(previous, store))
            throw new InvalidOperationException("Wonderland chest persistence is already configured.");
    }

    /// <summary>
    /// The NPCs a Wonderland run owns, added to the map's published roster.
    /// </summary>
    /// <remarks>
    /// Nothing about these actors is map content: the eight island teleporters,
    /// the entrance blackmarket actor and the eight treasure chests exist only
    /// inside a run, and <c>npc_spawn_definitions</c> carries no row for map 207
    /// at all, so without this the party enters an island with no way to travel
    /// on and no chest to open. The chests depend on the party's camp, which is
    /// why the injection is resolved from the caller's own run.
    /// </remarks>
    internal IReadOnlyList<NpcSpawnDefinition> AddWonderlandInstanceNpcs(ClientSession session,
        IReadOnlyList<NpcSpawnDefinition> existing)
    {
        // Deliberately lock-free. This runs inside the destination map-entry
        // publication, which already holds the entering session's state gate,
        // while the world tick holds the registry gate and drives the very run
        // being read here. Taking the registry gate on this path deadlocks a
        // party entering map 207: the members' scene changes are queued, their
        // publication never finishes, so they are never announced in the
        // instance (invisible to each other, no skills or items, movement only).
        // The session table is concurrent and the run snapshot query already
        // reads the owning map without the registry gate.
        if (!_sessions.TryGetValue(session, out var actor) ||
            actor.Character.CurrentMap != WonderlandMapId ||
            !TryGetWonderlandEncounterSnapshot(actor.WorldInstanceId, out var run))
        {
            return existing;
        }

        return WonderlandTraversalPolicy.AddTeleporters(
            WonderlandTreasureChestPolicy.AddChests(existing, run.PartyCamp));
    }

    /// <summary>
    /// The treasure chests a Wonderland run owns, added to the map's published
    /// roster. Split from <see cref="AddWonderlandInstanceNpcs"/> so the chest
    /// contract is addressable without the traversal actors.
    /// </summary>
    internal IReadOnlyList<NpcSpawnDefinition> AddWonderlandTreasureChests(ClientSession session,
        IReadOnlyList<NpcSpawnDefinition> existing)
    {
        if (!_sessions.TryGetValue(session, out var actor) ||
            actor.Character.CurrentMap != WonderlandMapId ||
            !TryGetWonderlandEncounterSnapshot(actor.WorldInstanceId, out var run))
        {
            return existing;
        }

        return WonderlandTreasureChestPolicy.AddChests(existing, run.PartyCamp);
    }

    internal async Task<WonderlandChestClaimReceipt> ClaimWonderlandChestAsync(ClientSession session,
        NpcSpawnDefinition npc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var store = _wonderlandChests;
        if (store is null) return new(WonderlandChestClaimStatus.Unavailable, 0, []);
        WonderlandChestClaimRequest request;
        lock (_gate)
        {
            if (!WonderlandTreasureChestPolicy.TryGetIsland(npc, out var island) ||
                !_sessions.TryGetValue(session, out var actor) || actor.Character.CurrentHp <= 0 ||
                !IsCurrentWonderlandMember(actor, actor.WorldInstanceId) ||
                !_wonderlandAdmissions.TryGetValue(actor.WorldInstanceId, out var admission) ||
                !admission.Sealed || !admission.Entrants.Contains(actor.CharacterId) ||
                !TryGetWonderlandEncounterSnapshot(actor.WorldInstanceId, out var run) ||
                !WonderlandTreasureChestPolicy.IsUnlocked(run, island, now))
                return new(WonderlandChestClaimStatus.NotEligible, 0, []);
            var dx = (double)actor.Character.PositionX - npc.X;
            var dz = (double)actor.Character.PositionZ - npc.Z;
            if (!double.IsFinite(dx) || !double.IsFinite(dz) ||
                dx * dx + dz * dz > WonderlandTreasureChestPolicy.InteractionRadius * WonderlandTreasureChestPolicy.InteractionRadius)
                return new(WonderlandChestClaimStatus.NotEligible, 0, []);
            if (!_wonderlandTitleSettled.TryGetValue((actor.WorldInstanceId, island), out var hash))
                return new(WonderlandChestClaimStatus.Unavailable, 0, []);
            request = new(actor.WorldInstanceId, actor.RealmId, island, run.PartyCamp, hash,
                new CommandSubject(actor.AccountId, actor.CharacterId), actor.Ownership);
        }
        return await store.ClaimAsync(request, cancellationToken);
    }
}
