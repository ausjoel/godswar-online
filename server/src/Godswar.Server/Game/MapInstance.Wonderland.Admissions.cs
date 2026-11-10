using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    /// <summary>
    /// Adds a member who confirmed the party's window after the run was sealed.
    /// </summary>
    /// <remarks>
    /// Only the rosters grow: the run's participant list (island travel, allied
    /// combat presentation, area targeting) and the monster runtime's attacker
    /// whitelist. The published monsters and their plan - counts and health - are
    /// left exactly as they were generated.
    /// </remarks>
    internal bool TryAppendWonderlandParticipant(
        int characterId,
        int level,
        byte camp,
        out WonderlandSnapshot snapshot)
    {
        snapshot = null!;
        lock (_wonderlandGate)
        lock (_monsterRuntimeGate)
        {
            if (_wonderlandRun is null || _wonderlandFinalizedIds is null ||
                _wonderlandMonsters is null)
            {
                Console.WriteLine(
                    "[instance-invite] append refused: run=" +
                    $"{_wonderlandRun is not null} finalized=" +
                    $"{_wonderlandFinalizedIds is not null} monsters=" +
                    $"{_wonderlandMonsters is not null} map={MapId}");
                return false;
            }
            if (!_wonderlandRun.TryAddLateParticipant(
                    new(characterId, level, camp),
                    out snapshot))
            {
                _wonderlandRun.Advance(DateTimeOffset.UtcNow);
                var observed = _wonderlandRun.Snapshot();
                Console.WriteLine(
                    "[instance-invite] append refused by the run " +
                    $"state={observed.State} island={observed.CurrentIsland} " +
                    $"party={observed.Participants.Length} camp={observed.PartyCamp} " +
                    $"joining-camp={camp} character={characterId}");
                return false;
            }
            if (!_wonderlandMonsters.TryAddParticipant(characterId))
            {
                return false;
            }
            _wonderlandFinalizedIds =
                [.. _wonderlandFinalizedIds.Append(characterId).Order()];
            return true;
        }
    }

    /// <summary>Fix actual successful entrants before publication, retaining the leader's faction and clock.</summary>
    internal bool TryFinalizeWonderlandAdmissions(IReadOnlyCollection<int> actualEntrants)
    {
        ArgumentNullException.ThrowIfNull(actualEntrants);
        var ids = actualEntrants.Order().ToArray();
        if (ids.Length is < 1 or > 5 || ids.Distinct().Count() != ids.Length) return false;
        lock (_wonderlandGate)
        lock (_monsterRuntimeGate)
        {
            if (_wonderlandFinalizedIds is not null) return _wonderlandFinalizedIds.SequenceEqual(ids);
            if (_wonderlandRun is null || _wonderlandMonsters is null || _wonderlandMonsters.Count != 0) return false;
            var pending = _wonderlandRun.Snapshot();
            if (pending.State != WonderlandRunState.Active || pending.CurrentIsland != 1 || !pending.PublicationPending ||
                ids.Any(id => !pending.Participants.Any(p => p.CharacterId == id))) return false;
            var entrants = pending.Participants.Where(p => ids.Contains(p.CharacterId)).ToArray();
            var run = new WonderlandRunRuntime(Descriptor, entrants, pending.StartedAt, pending.PartyCamp);
            _ = run.Advance(pending.LastObservedAt);
            var policies = Enumerable.Range(1, 8).Select(island =>
                WonderlandMonsterPlan.Create(island, entrants.Length, pending.PartyCamp)).ToArray();
            var definitions = PrepareWonderlandDefinitions(_wonderlandContent!, policies);
            var monsters = new WonderlandMonsterRuntime(Descriptor.InstanceId, pending.Deadline, ids,
                policies.Sum(stage => stage.Length));
            // No actor exists yet. Only HP and the participant whitelist change;
            // fixed combat ratings/ranges and their existing profile pin are identical.
            _wonderlandRun = run;
            _wonderlandDefinitions = definitions;
            _wonderlandPolicies = policies.SelectMany(stage => stage).ToDictionary(p => p.ObjectId);
            _wonderlandMonsters = monsters;
            _monsterRuntime = monsters;
            _wonderlandFinalizedIds = ids;
            return true;
        }
    }
}
