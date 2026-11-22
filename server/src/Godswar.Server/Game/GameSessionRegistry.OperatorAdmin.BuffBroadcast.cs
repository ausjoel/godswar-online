using System.Text.Json.Nodes;
using Godswar.Server.Application.Awards;
using Godswar.Server.Application.Operators;
using Godswar.Server.Infrastructure.EventsNpc;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public const string BuffBroadcastPath = "/admin/buff/broadcast";
    private const int BuffBroadcastListedCharacters = 200;
    private static readonly TimeSpan BuffBroadcastPerPlayerBudget = TimeSpan.FromSeconds(6);

    /// <summary>
    /// W4-B (the admin: "Apply buffs server wide for players online"): one status to every world-ready player, optionally
    /// one camp (1 Athens, 0 Sparta) and a level range, through the same per-session action as /admin/buff/apply
    /// (dead targets and stronger statuses are refused the same way). queueOffline also queues it for every active
    /// character that is not in the world and matches the filter: they get it at their next login.
    /// Body: {actor, statusId | skillId, durationSeconds?, camp?, minLevel?, maxLevel?, queueOffline?, allowHostile?}.
    /// </summary>
    private async Task<OperatorAdminResponse> HandleBuffBroadcastAsync(
        JsonObject body,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
        {
            return Error(400, "actor (1-64 characters) is required");
        }

        if (!TryReadOperatorBuff(body, out var definition, out var error))
        {
            return Error(400, error);
        }

        var allowHostile = body["allowHostile"] is JsonValue hostile && hostile.TryGetValue<bool>(out var h) && h;
        if (!definition.Beneficial && !allowHostile)
        {
            return Error(400, "this status is hostile; send allowHostile true to broadcast it");
        }

        if (definition.RequiresEquippedMount)
        {
            return Error(400, "mount statuses need each player's own mount; they cannot be broadcast");
        }

        var filter = new BuffBroadcastFilter(Int(body, "camp"), Int(body, "minLevel") ?? 1, Int(body, "maxLevel") ?? 200);
        if (!filter.IsValid)
        {
            return Error(400, "camp must be 0 (Sparta) or 1 (Athens), levels 1-200 with minLevel <= maxLevel");
        }

        var queueOffline = body["queueOffline"] is JsonValue queue && queue.TryGetValue<bool>(out var q) && q;
        var targets = SnapshotWorldReadySessions()
            .Where(context => filter.Matches(context.Character.Camp, context.Character.Level))
            .GroupBy(static context => context.CharacterId)
            .Select(static group => group.First())
            .ToArray();
        // In parallel: each target has its own 10 s budget, so a long list must not run one after another.
        var outcomes = await Task.WhenAll(targets.Select(async context =>
        {
            string result;
            // 6 s per player, under the management port's 10 s answer deadline.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(BuffBroadcastPerPlayerBudget);
            try
            {
                result = await RunOperatorActionAsync(context.Session,
                    new OperatorApplyBuffAction("web:" + actor, definition), budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = "that player's session is busy; try again";
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                result = "error: " + failure.GetType().Name;
            }

            bool? applied = null;
            if (result.StartsWith("{", StringComparison.Ordinal) && JsonNode.Parse(result) is JsonObject reply)
            {
                applied = reply["applied"]?.GetValue<bool>() == true;
            }

            return (context.CharacterId, context.CharacterName, Applied: applied, Result: result);
        }));

        var queued = 0L;
        if (queueOffline)
        {
            if (EventsNpcRuntime.Store is not { } store)
            {
                return Error(503, "the database store is unavailable; nothing was queued");
            }

            // Every active character not in the world now (world-ready players were handled above). Level is
            // character_base.fighter_job_lv (the snapshot reader's level column).
            var online = SnapshotWorldReadySessions().Select(static context => context.CharacterId).Distinct().ToArray();
            var inserted = await store.ExecuteScalarAsync("""
                WITH added AS (
                    INSERT INTO public.pending_character_buffs (character_id, status_id, duration_seconds, source, created_by)
                    SELECT c.id, @status, @duration, 'server-wide', @by
                    FROM public.character_base c
                    WHERE c.lifecycle_state = 'active'
                      AND (@camp < 0 OR c.camp = @camp)
                      AND c.fighter_job_lv BETWEEN @min AND @max
                      AND NOT (c.id = ANY(@online))
                    RETURNING 1)
                SELECT count(*) FROM added;
                """, cancellationToken,
                ("status", (int)definition.StatusId),
                ("duration", body.ContainsKey("durationSeconds")
                    ? (object)checked((long)Math.Ceiling(definition.Duration.TotalSeconds))
                    : DBNull.Value),
                ("by", "web:" + actor),
                ("camp", filter.Camp ?? -1),
                ("min", filter.MinLevel),
                ("max", filter.MaxLevel),
                ("online", online));
            queued = inserted is long count ? count : 0;
        }

        var applied = outcomes.Count(static outcome => outcome.Applied == true);
        var notApplied = outcomes.Count(static outcome => outcome.Applied == false);
        var busy = outcomes.Length - applied - notApplied;
        var listed = new JsonArray(outcomes.Take(BuffBroadcastListedCharacters).Select(static outcome => (JsonNode)new JsonObject
        {
            ["id"] = outcome.CharacterId,
            ["name"] = outcome.CharacterName,
            ["applied"] = outcome.Applied,
            ["note"] = outcome.Applied is null ? LoginAwardPolicy.Clip(outcome.Result, 80) : null
        }).ToArray());
        var summary = $"status={definition.StatusId} matched={outcomes.Length} applied={applied} not_applied={notApplied} " +
            $"busy={busy} queued_offline={queued} camp={(filter.Camp?.ToString() ?? "all")} levels={filter.MinLevel}-{filter.MaxLevel}";
        Console.WriteLine($"[admin] {BuffBroadcastPath} actor={actor} {summary}");
        if (OperatorControls.Store is { } logStore)
        {
            try { await logStore.LogAsync(0, "web:" + actor, BuffBroadcastPath.TrimStart('/'), "all", summary, cancellationToken); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            { Console.WriteLine($"[admin] log row failed: {failure.Message}"); }
        }

        return new(200, new JsonObject
        {
            ["ok"] = true,
            ["statusId"] = definition.StatusId,
            ["durationSeconds"] = checked((long)Math.Ceiling(definition.Duration.TotalSeconds)),
            ["matched"] = outcomes.Length,
            ["applied"] = applied,
            ["notApplied"] = notApplied,
            ["busy"] = busy,
            ["queuedOffline"] = queued,
            ["persistence"] = "session",
            ["characters"] = listed
        }.ToJsonString());
    }
}
