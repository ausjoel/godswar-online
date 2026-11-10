using System.Collections.Concurrent;
using Godswar.Server.Application.Operators;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rates;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

// ROUND2: what the operator (GM) commands need from the session registry:
// online lookups by name, a per-session sink so an action on another player
// runs on that player's own handler (under his character-state gate), and the
// GM's own powers layered onto the one status aggregate that combat, movement
// and the 10166 status frame all read.

/// <summary>An action one session asks another session's handler to run.</summary>
internal abstract record OperatorSessionAction(string ActorName);

internal sealed record OperatorTeleportAction(
    string ActorName,
    byte MapId,
    float X,
    float Z) : OperatorSessionAction(ActorName);

/// <summary>Reload the character from the database and refresh the client.</summary>
internal sealed record OperatorReloadAction(
    string ActorName,
    string Reason) : OperatorSessionAction(ActorName);

/// <summary>Run a progression command (level, Talent, zodiac) on this character.</summary>
internal sealed record OperatorProgressionAction(
    string ActorName,
    GamePacket CommandPacket,
    DeveloperProgressionCommand Command) : OperatorSessionAction(ActorName);

/// <summary>Send this character its status again, after its speed changed.</summary>
internal sealed record OperatorStatusRefreshAction(
    string ActorName) : OperatorSessionAction(ActorName);

/// <summary>Apply one runtime status (buff) to this character.</summary>
internal sealed record OperatorStatusAction(
    string ActorName,
    SkillStatusEffectDefinition Definition) : OperatorSessionAction(ActorName);

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<ClientSession, Func<
        OperatorSessionAction,
        CancellationToken,
        Task<string>>> _operatorSinks = [];

    internal void RegisterOperatorSink(
        ClientSession session,
        Func<OperatorSessionAction, CancellationToken, Task<string>> sink)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sink);
        _operatorSinks[session] = sink;
    }

    internal void UnregisterOperatorSink(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _operatorSinks.TryRemove(session, out _);
    }

    /// <summary>
    /// Runs an action on another session's handler. The caller's own gate is
    /// held while this waits, so it is bounded: a target that does not answer
    /// within 10 s is reported as busy rather than stalling the GM's session.
    /// </summary>
    internal async Task<string> RunOperatorActionAsync(
        ClientSession target,
        OperatorSessionAction action,
        CancellationToken cancellationToken)
    {
        if (!_operatorSinks.TryGetValue(target, out var sink))
        {
            return "that player is not in the world";
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await sink(action, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "that player's session is busy; try again";
        }
    }

    /// <summary>A world-ready session of this realm by character name.</summary>
    internal GameSessionContext? FindOnlineCharacter(string name)
    {
        lock (_gate)
        {
            GameSessionContext? found = null;
            foreach (var candidate in _sessions.Values)
            {
                if (!candidate.WorldReady ||
                    candidate.Session.IsDisconnected ||
                    !string.Equals(
                        candidate.CharacterName,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found is not null)
                {
                    return null;
                }

                found = candidate;
            }

            return found;
        }
    }

    /// <summary>
    /// Any registered session of this character, world-ready or not (logging
    /// in, changing maps, or disconnected with its logout save pending). Used so /admin never reports such a player as offline.
    /// </summary>
    internal bool IsCharacterConnected(string name)
    {
        lock (_gate)
        {
            // A disconnected session still registered here may have its
            // logout save pending, so it counts as connected too.
            return _sessions.Values.Any(candidate =>
                string.Equals(
                    candidate.CharacterName,
                    name,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    internal GameSessionContext? FindOnlineAccount(int accountId)
    {
        lock (_gate)
        {
            return _sessions.Values.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                !candidate.Session.IsDisconnected);
        }
    }

    internal GameSessionContext[] SnapshotWorldReadySessions()
    {
        lock (_gate)
        {
            return _sessions.Values
                .Where(static candidate =>
                    candidate.WorldReady &&
                    !candidate.Session.IsDisconnected &&
                    candidate.CharacterId > 0)
                .ToArray();
        }
    }

    /// <summary>The GM's own speed / attack / defence powers, if any.</summary>
    private ClientStatusAggregate ApplyOperatorModifiers(
        ClientSession session,
        ClientStatusAggregate aggregate)
    {
        int characterId;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context))
            {
                return aggregate;
            }

            characterId = context.CharacterId;
        }

        var powers = OperatorControls.PowersOf(characterId);
        var serverSpeed = (double)ServerRates.Current.Get(ServerRateKeys.MovementSpeed);
        if (powers.IsNone)
        {
            return serverSpeed == 1d
                ? aggregate
                : aggregate with
                {
                    MovementSpeedMultiplier = (float)Math.Clamp(
                        (double)aggregate.MovementSpeedMultiplier * serverSpeed,
                        0.1,
                        OperatorControls.UsesCharacterGmFlags ? float.MaxValue / 1024d : 20d)
                };
        }

        return aggregate with
        {
            MovementSpeedMultiplier = (float)Math.Clamp(
                (double)aggregate.MovementSpeedMultiplier * powers.SpeedFactor * serverSpeed,
                0.1,
                OperatorControls.UsesCharacterGmFlags ? float.MaxValue / 1024d : 20d),
            AttackMultiplier = Math.Max(
                Math.Max(1, aggregate.AttackMultiplier),
                powers.AttackMultiplier),
            PhysicalDefense = OperatorSaturatingAdd(aggregate.PhysicalDefense, powers.DefenseBonus),
            MagicDefense = OperatorSaturatingAdd(aggregate.MagicDefense, powers.DefenseBonus)
        };
    }

    private static int OperatorSaturatingAdd(int left, int right) =>
        (int)Math.Clamp((long)left + right, int.MinValue, int.MaxValue);
}
