using System.Text.Json.Nodes;
using Godswar.Server.Application.Awards;
using Godswar.Server.Application.Operators;

namespace Godswar.Server.Game;

/// <summary>W5-F: tells one online player that prizes were put into their Prize Chest (see the handler).</summary>
internal sealed record PrizeChestDeliveryAction(string ActorName) : OperatorSessionAction(ActorName);

internal sealed partial class GameSessionRegistry
{
    public const string PrizeChestNotifyPath = "/admin/prize-chest/notify";
    private const int PrizeChestNotifyMaximumCharacters = 500;
    private static readonly TimeSpan PrizeChestNotifyPerPlayerBudget = TimeSpan.FromSeconds(6);

    /// <summary>
    /// W5-F (the admin: "drop any items into the prize chest while players are logged in too"). The editor inserts the
    /// prize_chest_entries rows itself (that is safe at any time: the server only reads them when the chest is
    /// opened or drawn), then posts the character ids here. Each one that is in the world gets one chat line and,
    /// if the award tab is open, a repaint. Offline ids are simply reported; they get the login reminder.
    /// Body: {actor, characterIds: [int, ...]}. Changes nothing in the database.
    /// </summary>
    private async Task<OperatorAdminResponse> HandlePrizeChestNotifyAsync(
        JsonObject body,
        string actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
        {
            return Error(400, "actor (1-64 characters) is required");
        }

        if (body["characterIds"] is not JsonArray array || array.Count is 0 or > PrizeChestNotifyMaximumCharacters)
        {
            return Error(400, $"characterIds must list 1-{PrizeChestNotifyMaximumCharacters} character ids");
        }

        var ids = new HashSet<int>();
        foreach (var node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<int>(out var id) || id <= 0)
            {
                return Error(400, "characterIds must hold positive whole numbers");
            }

            ids.Add(id);
        }

        var targets = SnapshotWorldReadySessions()
            .Where(context => ids.Contains(context.CharacterId))
            .GroupBy(static context => context.CharacterId)
            .Select(static group => group.First())
            .ToArray();
        var outcomes = await Task.WhenAll(targets.Select(async context =>
        {
            string result;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(PrizeChestNotifyPerPlayerBudget);
            try
            {
                result = await RunOperatorActionAsync(context.Session,
                    new PrizeChestDeliveryAction("web:" + actor), budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = "that player's session is busy; they will see the prizes at the chest";
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                result = "error: " + failure.GetType().Name;
            }

            return (context.CharacterId, context.CharacterName, Result: result);
        }));

        var notified = outcomes.Count(static outcome => outcome.Result.StartsWith("notified", StringComparison.Ordinal));
        var summary = $"requested={ids.Count} online={outcomes.Length} notified={notified}";
        Console.WriteLine($"[admin] {PrizeChestNotifyPath} actor={actor} {summary}");
        if (OperatorControls.Store is { } logStore)
        {
            try { await logStore.LogAsync(0, "web:" + actor, PrizeChestNotifyPath.TrimStart('/'), "prize chest", summary, cancellationToken); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            { Console.WriteLine($"[admin] log row failed: {failure.Message}"); }
        }

        return new(200, new JsonObject
        {
            ["ok"] = true,
            ["requested"] = ids.Count,
            ["online"] = outcomes.Length,
            ["notified"] = notified,
            ["characters"] = new JsonArray(outcomes.Select(static outcome => (JsonNode)new JsonObject
            {
                ["id"] = outcome.CharacterId,
                ["name"] = outcome.CharacterName,
                ["result"] = LoginAwardPolicy.Clip(outcome.Result, 120)
            }).ToArray())
        }.ToJsonString());
    }
}
