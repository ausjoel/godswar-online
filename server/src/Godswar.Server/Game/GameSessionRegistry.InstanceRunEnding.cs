using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// The one end-of-run flow 飘渺幻境, 亚特兰蒂斯, 港湾遇袭 and 美杜莎之岛 share,
/// and the one per-run leader identity they all keep.
/// </summary>
/// <remarks>
/// A run that reached any of its three terminal states - completed, ended by its
/// leader, or timed out - enters this same flow:
/// <list type="number">
/// <item>every member still inside receives the same four native frames: the
/// current progress (<c>10229</c>), the completed panel (<c>10227</c> state
/// <c>3</c>), the completed run state (<c>10227</c>... the native completion
/// state) and the thirty-second leave countdown (<c>10231</c>);</item>
/// <item>a member who presses the native leave control is the only one who
/// leaves, carried home to his faction capital immediately, while everyone else
/// keeps his own countdown;</item>
/// <item>a member who does not press it is carried home when the countdown
/// expires;</item>
/// <item>the run's native instance list is cleared with the stock teardown
/// (<c>10231</c> with zero seconds), which is the one clear this server sends -
/// an empty <c>10218</c> roster is never published, because the stock handler at
/// <c>0x004B5000</c> reads a first member even when the count is zero.</item>
/// </list>
/// Each dungeon keeps its own entry conditions, maps and client scene ids, time
/// limit, scoring, reward table and the extra values its panel shows; it only
/// hands this flow the run's own numbers and the delegate that settles its
/// rewards. Reward settlement is attempted from this same terminal event, is
/// retried by later ticks and never holds the leave flow hostage.
/// <para>
/// The leader identity is one mutable character id per run, kept in
/// <see cref="_instanceRunLeaders"/>. The four dungeons own their own records and
/// read the value through that one store, so the end control reads only the
/// character id and never the session object that happened to register it.
/// </para>
/// </remarks>
internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// The one leave countdown every terminal run shows, in seconds.
    /// </summary>
    internal const int InstanceRunEndSeconds = 30;

    private static readonly TimeSpan InstanceRunEndDelay =
        TimeSpan.FromSeconds(InstanceRunEndSeconds);

    /// <summary>
    /// The one per-run leader identity: 飘渺幻境, 亚特兰蒂斯, 港湾遇袭 and
    /// 美杜莎之岛 all keep their run's leader here, and every one of them
    /// transfers it to the earliest still-present member when the registered
    /// leader leaves.
    /// </summary>
    private readonly ConcurrentDictionary<WorldInstanceId, int>
        _instanceRunLeaders = [];

    /// <summary>
    /// The runs that reached a terminal state, with the values the shared flow
    /// publishes. Written once per run, when its terminal state is first
    /// observed, so the countdown is anchored to that moment.
    /// </summary>
    private readonly ConcurrentDictionary<WorldInstanceId, InstanceRunEnding>
        _instanceRunEndings = [];

    /// <summary>
    /// Which members a run's end has already been published to. The native
    /// countdown runs locally from the published value, so it is sent once per
    /// member; a member who reconnects or arrives late is published on the tick
    /// that sees him.
    /// </summary>
    private readonly ConcurrentDictionary<(WorldInstanceId, ClientSession), byte>
        _instanceRunEndPublished = [];

    /// <summary>
    /// One terminal run, as the shared flow publishes it.
    /// </summary>
    /// <param name="InstanceId">The run's exact world instance.</param>
    /// <param name="Kind">Which dungeon's roster the run's members come from.</param>
    /// <param name="Dungeon">The dungeon's name, for the shared log lines.</param>
    /// <param name="SourceMapId">The dungeon map the members are inside.</param>
    /// <param name="ClientSceneId">The native scene the run's panel belongs to.</param>
    /// <param name="DailyEntryLimit">The run's own daily entry limit, republished with the state.</param>
    /// <param name="PanelValue">The run's own progress number: its score, or its cleared islands.</param>
    /// <param name="EndedAt">The run's terminal timestamp; the countdown is anchored to it.</param>
    /// <param name="Window">
    /// The run's own leave window. Every dungeon's ended or timed-out run uses
    /// the shared thirty seconds; 飘渺幻境's completed run keeps its own
    /// five-minute treasure window, which is the run's own time limit and not
    /// this flow's to change.
    /// </param>
    /// <param name="CanLeave">
    /// The run's own extra rule for allowing a member out during the countdown,
    /// or <c>null</c> when the run has none. 飘渺幻境 holds its members inside
    /// while its title settlement is still pending; the other three dungeons
    /// admit the leave unconditionally.
    /// </param>
    private sealed record InstanceRunEnding(
        WorldInstanceId InstanceId,
        InstanceRunKind Kind,
        string Dungeon,
        byte SourceMapId,
        ushort ClientSceneId,
        ushort DailyEntryLimit,
        int PanelValue,
        DateTimeOffset EndedAt,
        TimeSpan Window,
        Func<bool>? CanLeave);

    /// <summary>
    /// Enters the shared end-of-run flow for a run that reached a terminal state.
    /// </summary>
    /// <remarks>
    /// The first terminal observation wins: a later tick reuses the same record,
    /// so every member counts down from the run's own terminal moment instead of
    /// from whenever his panel happened to be rebuilt.
    /// </remarks>
    private InstanceRunEnding BeginInstanceRunEnd(
        WorldInstanceId instanceId,
        InstanceRunKind kind,
        string dungeon,
        byte sourceMapId,
        ushort clientSceneId,
        ushort dailyEntryLimit,
        int panelValue,
        DateTimeOffset endedAt,
        TimeSpan? window = null,
        Func<bool>? canLeave = null)
    {
        var ending = new InstanceRunEnding(instanceId, kind, dungeon, sourceMapId,
            clientSceneId, dailyEntryLimit, panelValue, endedAt.ToUniversalTime(),
            window ?? InstanceRunEndDelay, canLeave);
        return _instanceRunEndings.GetOrAdd(instanceId, ending);
    }

    /// <summary>
    /// One member a terminal run still has to publish to and carry home. Each
    /// dungeon's own member record projects into this, so the shared flow reads
    /// one shape for all four.
    /// </summary>
    private readonly record struct InstanceRunEndMember(
        ClientSession Session,
        int CharacterId,
        PlayerOwnershipFence Ownership,
        byte Camp);

    private bool TryGetInstanceRunEnding(WorldInstanceId instanceId,
        out InstanceRunEnding ending) =>
        _instanceRunEndings.TryGetValue(instanceId, out ending!);

    private static int InstanceRunEndRemainingSeconds(InstanceRunEnding ending,
        DateTimeOffset now) =>
        checked((int)Math.Clamp(
            Math.Ceiling((ending.EndedAt + ending.Window - now).TotalSeconds),
            0d, ending.Window.TotalSeconds));

    private static bool IsInstanceRunEndWindowOpen(InstanceRunEnding ending,
        DateTimeOffset now) =>
        now < ending.EndedAt + ending.Window;

    /// <summary>
    /// Publishes a terminal run to every member still inside it: the same four
    /// frames, the same thirty seconds, whichever of the three terminal states
    /// the run reached and whichever dungeon it belongs to.
    /// </summary>
    /// <remarks>
    /// The caller's settlement runs first and is retried by later ticks; a failed
    /// or pending durable write is reported and never stops the frames, the
    /// countdown or the exits. Membership is validated under the registry gate
    /// and the physical write completes after it is released.
    /// </remarks>
    private async Task PublishInstanceRunEndAsync(
        InstanceRunEnding ending,
        IReadOnlyList<InstanceRunEndMember> members,
        DateTimeOffset now,
        Func<CancellationToken, Task>? settle,
        CancellationToken cancellationToken)
    {
        if (settle is not null)
        {
            try
            {
                await settle(cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[instance] {ending.Dungeon} settlement will retry " +
                    $"instance={ending.InstanceId}: {error.Message}");
            }
        }

        var remaining = InstanceRunEndRemainingSeconds(ending, now);
        var roster = SnapshotInstanceRoster(ending.InstanceId, ending.Kind, now);
        foreach (var member in members)
        {
            Task? write = null;
            lock (_gate)
            {
                if (!IsInstanceRunEndMember(ending, member) ||
                    _instanceRunEndPublished.ContainsKey(
                        (ending.InstanceId, member.Session)))
                {
                    continue;
                }
                var packets = new List<ReadOnlyMemory<byte>>();
                // The member's panel may not exist yet - a reconnect, or an
                // arrival after the run ended - so the active run state is
                // reasserted before the terminal frames. The native countdown
                // only opens from that state.
                packets.Add(PacketBuilder.RepetitionSync(ending.ClientSceneId, 0, 0,
                    5, ending.DailyEntryLimit));
                if (roster.Length != 0)
                {
                    packets.Add(PacketBuilder.RepetitionInstanceMembers(roster));
                }
                packets.Add(PacketBuilder.RepetitionFightInfo(remaining,
                    ending.PanelValue));
                packets.Add(PacketBuilder.RepetitionPanelCompletion());
                packets.Add(PacketBuilder.RepetitionCompletionState(
                    ending.ClientSceneId, true));
                packets.Add(PacketBuilder.RepetitionCountdown(remaining));
                if (member.Session.TryAdmitExactBatch(packets, out var admitted))
                {
                    _instanceRunEndPublished[(ending.InstanceId, member.Session)] = 0;
                }
                write = admitted;
            }
            if (write is null)
            {
                continue;
            }
            try
            {
                await write;
                Console.WriteLine(
                    $"[instance] {ending.Dungeon} end countdown published " +
                    $"instance={ending.InstanceId} character={member.CharacterId} " +
                    $"seconds={remaining}");
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                member.Session.Disconnect();
            }
        }
    }

    /// <summary>
    /// The one membership rule the shared flow uses: the member's live session is
    /// still inside this exact run, on its map, under the ownership fence the run
    /// recorded for him.
    /// </summary>
    private bool IsInstanceRunEndMember(InstanceRunEnding ending,
        InstanceRunEndMember member) =>
        _sessions.TryGetValue(member.Session, out var current) && current.WorldReady &&
        !member.Session.IsDisconnected && current.CharacterId == member.CharacterId &&
        current.Ownership == member.Ownership &&
        current.WorldInstanceId == ending.InstanceId &&
        current.MapId == ending.SourceMapId &&
        current.Character.CurrentMap == current.MapId &&
        IsCurrentAccountSession(current.AccountId, member.Session, member.Ownership);

    /// <summary>
    /// The one implementation of the native leave control during a terminal run's
    /// countdown: only the member who pressed it leaves, and he leaves at once.
    /// </summary>
    /// <remarks>
    /// The run is already terminal, so the member is carried to his own faction
    /// capital immediately instead of waiting for the countdown. Everyone else
    /// keeps his own countdown and his own leave control, exactly as the other
    /// three dungeons behave.
    /// </remarks>
    internal bool TryResolveInstanceRunEndLeave(ClientSession session,
        int? repetitionId, int repetitionIndex, DateTimeOffset now,
        out AuthoritativeInstanceTransitionCommand command)
    {
        command = default;
        ArgumentNullException.ThrowIfNull(session);
        if (repetitionIndex != 0)
        {
            return false;
        }
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var actor) ||
                !actor.WorldReady || actor.Session.IsDisconnected ||
                actor.Character.CurrentMap != actor.MapId ||
                !actor.Ownership.IsValid ||
                !IsCurrentAccountSession(actor.AccountId, actor.Session,
                    actor.Ownership) ||
                !TryGetInstanceRunEnding(actor.WorldInstanceId, out var ending) ||
                actor.MapId != ending.SourceMapId ||
                // Zero names the run the sender is already inside, which is the
                // shape 美杜莎之岛's own end request carries; a foreign scene is
                // still refused.
                repetitionId is { } requested and not 0 &&
                    requested != ending.ClientSceneId ||
                ending.CanLeave is { } canLeave && !canLeave() ||
                !IsInstanceRunEndWindowOpen(ending, now))
            {
                return false;
            }

            command = BuildInstanceRunHomeCommand(ToInstanceRunEndMember(actor),
                ending);
            return true;
        }
    }

    /// <summary>
    /// The one automatic exit: nobody pressed leave, so every remaining member is
    /// carried home when the thirty seconds expire.
    /// </summary>
    /// <remarks>
    /// The physical transfer is the only part a dungeon supplies, because each
    /// one already owns the sink that moves its members. A failed transfer keeps
    /// the member inside and is retried by the next tick's pass.
    /// </remarks>
    private async Task<bool> EgressInstanceRunEndAsync(
        InstanceRunEnding ending,
        IReadOnlyList<InstanceRunEndMember> members,
        Func<ClientSession, AuthoritativeInstanceTransitionCommand,
            CancellationToken, Task<bool>> transfer,
        CancellationToken cancellationToken)
    {
        var allTransferred = true;
        foreach (var member in members)
        {
            lock (_gate)
            {
                if (!IsInstanceRunEndMember(ending, member))
                {
                    continue;
                }
            }

            try
            {
                if (!await transfer(member.Session,
                        BuildInstanceRunHomeCommand(member, ending),
                        cancellationToken))
                {
                    allTransferred = false;
                    Console.WriteLine(
                        $"[instance] {ending.Dungeon} end egress will retry " +
                        $"character={member.CharacterId} instance={ending.InstanceId}");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                allTransferred = false;
                Console.WriteLine(
                    $"[instance] {ending.Dungeon} end egress failed " +
                    $"character={member.CharacterId}: {error.Message}");
            }
        }
        return allTransferred;
    }

    private AuthoritativeInstanceTransitionCommand BuildInstanceRunHomeCommand(
        InstanceRunEndMember member,
        InstanceRunEnding ending)
    {
        var targetMap = member.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        var target = GetOrCreateDefaultWorldInstance(targetMap);
        return new AuthoritativeInstanceTransitionCommand(member.CharacterId,
            ending.InstanceId, ending.SourceMapId, member.Ownership,
            target.InstanceId, targetMap, GameDefaults.StartingPositionX,
            GameDefaults.StartingPositionZ);
    }

    /// <summary>
    /// Projects a live session context into the shared flow's one member shape.
    /// </summary>
    private static InstanceRunEndMember ToInstanceRunEndMember(
        GameSessionContext context) =>
        new(context.Session, context.CharacterId, context.Ownership,
            context.Character.Camp);

    /// <summary>
    /// The one place a run's native instance list is cleared: the stock teardown,
    /// <c>10231</c> with zero seconds, enqueued in the same membership fence that
    /// commits the departure.
    /// </summary>
    /// <remarks>
    /// This replaces the per-dungeon clears 飘渺幻境 and 亚特兰蒂斯 each kept, and
    /// gives 美杜莎之岛 the one it was missing - its list stayed populated after
    /// the run ended. Nothing here publishes an empty <c>10218</c> roster: the
    /// stock handler reads a first member when the count is zero.
    /// <para>
    /// The clear is sent when a member actually leaves the instance, never when
    /// the run merely entered its terminal state: a zero-second <c>10231</c>
    /// wipes the countdown the member still needs.
    /// </para>
    /// </remarks>
    private Task? RecordCommittedInstanceRunDepartureLocked(
        GameSessionContext? previous)
    {
        if (previous is null || !IsInstancePanelMap(previous.MapId))
        {
            return null;
        }
        _sessions.TryGetValue(previous.Session, out var current);
        if (current?.WorldInstanceId == previous.WorldInstanceId)
        {
            return null;
        }

        _instanceRunEndPublished.TryRemove((previous.WorldInstanceId,
            previous.Session), out _);
        if (current is null || previous.Session.IsDisconnected)
        {
            return null;
        }

        previous.Session.TryAdmitExactBatch([PacketBuilder.RepetitionReset()],
            out var clear);
        return clear;
    }

    /// <summary>
    /// Whether a map belongs to one of the four runs whose native instance list
    /// this flow owns.
    /// </summary>
    private static bool IsInstancePanelMap(byte mapId) =>
        mapId == DynamicDungeonContentMapPolicy.WonderlandMapId ||
        mapId == DynamicDungeonContentMapPolicy.AtlantisPortalMapId ||
        DynamicDungeonContentMapPolicy.IsHarborAttackMap(mapId) ||
        mapId == DynamicDungeonContentMapPolicy.TrainingGroundMapId || // TRAINING
        DynamicDungeonContentMapPolicy.IsMedusaMap(mapId);

    /// <summary>
    /// Drops a finished run's shared records with the run's own.
    /// </summary>
    private void ForgetInstanceRunEnd(WorldInstanceId instanceId)
    {
        _instanceRunEndings.TryRemove(instanceId, out _);
        _instanceRunLeaders.TryRemove(instanceId, out _);
    }

    /// <summary>
    /// The run's leader character id. The end control reads this and nothing
    /// else: it never compares the session object that registered the run, so a
    /// leader who dropped and relogged keeps the run's own end control.
    /// </summary>
    internal int InstanceRunLeaderCharacterId(WorldInstanceId instanceId) =>
        _instanceRunLeaders.GetValueOrDefault(instanceId);

    private bool TryGetInstanceRunLeader(WorldInstanceId instanceId,
        out int characterId) =>
        _instanceRunLeaders.TryGetValue(instanceId, out characterId);

    /// <summary>
    /// Opens a run's leader identity. The first registration wins; every later
    /// one is a reconnect of the same run.
    /// </summary>
    private void BeginInstanceRunLeader(WorldInstanceId instanceId,
        int characterId)
    {
        if (instanceId.IsValid && characterId > 0)
        {
            _instanceRunLeaders.TryAdd(instanceId, characterId);
        }
    }

    /// <summary>
    /// The one implementation of "the registered leader left, the run's end
    /// control moves to the earliest still-present member".
    /// </summary>
    /// <remarks>
    /// Called from every dungeon's own tick with the presence list it already
    /// captured, under the registry gate, and it never awaits: it only decides
    /// who takes over and writes the run's one leader identity. 飘渺幻境,
    /// 亚特兰蒂斯 and 港湾遇袭 used to keep three copies of these five lines;
    /// 美杜莎之岛 had none, so its end control became unreachable when its
    /// leader dropped.
    /// </remarks>
    private void MaintainInstanceRunLeader(WorldInstanceId instanceId,
        string dungeon, IReadOnlyList<int> entryOrder,
        IReadOnlyList<int> presentCharacterIds)
    {
        lock (_gate)
        {
            if (!TryGetInstanceRunLeader(instanceId, out var leaderId))
            {
                return;
            }
            var present = OrderInstanceMembers(entryOrder, presentCharacterIds);
            if (TryResolveInstanceLeaderSuccessor(instanceId, dungeon, leaderId,
                    present, out var successor))
            {
                _instanceRunLeaders[instanceId] = successor;
            }
        }
    }
}
