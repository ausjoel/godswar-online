using System.Collections.Concurrent;
using System.Globalization;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Infrastructure.EventsNpc;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// PROF slice 1: online Activity Points. On the 15 s notice ticker, every world-ready character with a profession
/// row whose last AP period ended 300 s (Lifing_AP ApTime) ago gains OnlineAdd for its level, capped at MaxAP, and
/// gets a fresh 10339. Offline time is credited at login (GameClientHandler.Professions.cs). The update is relative
/// (ap + gain), so it never undoes AP a craft spent at the same moment.
/// </summary>
internal sealed partial class GameSessionRegistry
{
    // Rig 2026-11-09: a 10339 that reaches the client before its UI is up crashes GodsWar.exe inside the
    // _MSG_SYN_AP handler (eip 0x6d6854, read of address 4). A session is only sent AP after its EnterUiReady
    // (10357), and only while it is still in the world membership it was marked in: every map/instance/scene
    // transition and the revive reset unmark it, and a changed membership epoch also blocks it.
    private readonly ConcurrentDictionary<ClientSession, long> _professionUiReady = new();

    internal void MarkProfessionUiReady(ClientSession session)
    {
        if (_sessions.TryGetValue(session, out var context))
        {
            _professionUiReady[session] = context.WorldMembershipEpoch;
        }
    }

    internal void UnmarkProfessionUiReady(ClientSession session) => _professionUiReady.TryRemove(session, out _);

    /// <summary>PROF slice 2: whether this session may get profession / gathering frames now (see above).</summary>
    internal bool IsProfessionUiReady(ClientSession session) =>
        _sessions.TryGetValue(session, out var context) && ProfessionUiReady(context, _professionUiReady);

    internal static bool ProfessionUiReady(GameSessionContext context, IReadOnlyDictionary<ClientSession, long> ready) =>
        context.WorldReady && ready.TryGetValue(context.Session, out var epoch) && epoch == context.WorldMembershipEpoch;


    /// <summary>
    /// PROF slice 2: a Type 1 blueprint whose cooldown ended gets 10338 kind 5 = 0, so the window unlocks it without
    /// a relog (no client code was found that counts the stored cooldown word down by itself). Runs on the same 15 s
    /// ticker; the row is cleared in the same statement, so it is announced once.
    /// </summary>
    internal async Task ExpireProfessionCooldownsAsync(CancellationToken cancellationToken)
    {
        if (!Finalise2Rates.On(Finalise2RateKeys.Professions) || EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        // Same gate as the AP ticker: only sessions whose UI is up (EnterUiReady) in their current world membership.
        var sessions = SnapshotWorldReadySessions().Where(c => ProfessionUiReady(c, _professionUiReady)).ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        string? expired;
        try
        {
            expired = await store.ExecuteScalarAsync("""
                WITH done AS (
                    UPDATE public.profession_blueprints SET cooldown_until = NULL
                    WHERE character_id = ANY(@ids) AND cooldown_until IS NOT NULL AND cooldown_until <= now()
                    RETURNING character_id, blueprint_id)
                SELECT string_agg(character_id || ':' || blueprint_id, ',') FROM done;
                """, cancellationToken,
                ("ids", sessions.Select(static c => c.CharacterId).Distinct().ToArray())) as string;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[profession] cooldown expiry skipped: {error.GetType().Name}");
            return;
        }

        foreach (var entry in (expired ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = entry.Split(':');
            var characterId = int.Parse(bits[0], CultureInfo.InvariantCulture);
            var blueprintId = int.Parse(bits[1], CultureInfo.InvariantCulture);
            var context = sessions.FirstOrDefault(c => c.CharacterId == characterId);
            if (context is null || !ProfessionBlueprintCatalog.TryGet(blueprintId, out var blueprint))
            {
                continue;
            }

            await context.Session.SendAsync(PacketBuilder.LifeSkillAttribute(blueprint.Profession, blueprintId,
                PacketBuilder.BlueprintCooldownAttribute, 0), cancellationToken, "ProfessionCooldown");
            Console.WriteLine($"[profession] cooldown over character={characterId} blueprint={blueprintId}");
        }
    }

    /// <summary>
    /// PROF slice 2: relays a gather rabbit 10342 to the other players of the sender's world instance, but only to
    /// sessions whose UI is up (the same gate as the AP ticker), so no peer gets it during its world entry.
    /// </summary>
    internal async Task<int> RelayGatherPoseAsync(ClientSession sender, byte[] frame, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sender, out var origin))
        {
            return 0;
        }

        var sent = 0;
        foreach (var context in SnapshotWorldReadySessions())
        {
            if (ReferenceEquals(context.Session, sender) || context.WorldInstanceId != origin.WorldInstanceId ||
                !ProfessionUiReady(context, _professionUiReady))
            {
                continue;
            }

            try
            {
                await context.Session.SendAsync(frame, cancellationToken, "GatherPose");
                sent++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[gather] pose relay skipped: {error.GetType().Name}");
            }
        }

        return sent;
    }

    internal async Task AccrueProfessionApAsync(CancellationToken cancellationToken)
    {
        foreach (var gone in _professionUiReady.Keys.Where(static s => s.IsDisconnected).ToArray())
        {
            _professionUiReady.TryRemove(gone, out _);
        }

        if (!Finalise2Rates.On(Finalise2RateKeys.Professions) || EventsNpcRuntime.Store is not { } store)
        {
            return;
        }

        var sessions = SnapshotWorldReadySessions().Where(c => ProfessionUiReady(c, _professionUiReady)).ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        string? due;
        try
        {
            due = await store.ExecuteScalarAsync(
                "SELECT string_agg(character_id || ':' || ap || ':' || " +
                "floor(extract(epoch FROM (now() - ap_updated_at)) / @t)::bigint, ',') " +
                "FROM public.profession_state WHERE character_id = ANY(@ids) " +
                "AND ap_updated_at <= now() - make_interval(secs => @t);",
                cancellationToken, ("t", (double)ProfessionTables.ApTimeSeconds),
                ("ids", sessions.Select(static c => c.CharacterId).Distinct().ToArray())) as string;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[profession] AP accrual skipped: {error.GetType().Name}");
            return;
        }

        foreach (var entry in (due ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = entry.Split(':');
            var characterId = int.Parse(bits[0], CultureInfo.InvariantCulture);
            var periods = long.Parse(bits[2], CultureInfo.InvariantCulture);
            var context = sessions.FirstOrDefault(c => c.CharacterId == characterId);
            if (context is null || periods <= 0)
            {
                continue;
            }

            var (maxAp, onlineAdd, offlineAdd) = ProfessionTables.ApFor(context.Character.Level);
            // The first period is online time; any more is time the character was logged out (see Accrue).
            var gain = (int)Math.Min(int.MaxValue, onlineAdd + (periods - 1) * offlineAdd);
            try
            {
                var ap = await store.ExecuteScalarAsync("""
                    UPDATE public.profession_state
                    SET ap = GREATEST(ap, LEAST(@max, ap + @gain)),
                        ap_updated_at = ap_updated_at + make_interval(secs => @span), updated_at = now()
                    WHERE character_id = @c AND ap_updated_at + make_interval(secs => @span) <= now()
                    RETURNING ap;
                    """, cancellationToken, ("c", characterId), ("max", maxAp), ("gain", gain),
                    ("span", (double)periods * ProfessionTables.ApTimeSeconds));
                // Re-checked: the update may have raced a map change.
                if (ap is int current && _sessions.TryGetValue(context.Session, out var now) &&
                    ProfessionUiReady(now, _professionUiReady))
                {
                    await context.Session.SendAsync(PacketBuilder.SyncActivityPoints(current, maxAp), cancellationToken,
                        "ProfessionAp");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Console.WriteLine($"[profession] AP accrual skipped character={characterId}: {error.GetType().Name}");
            }
        }
    }
}
