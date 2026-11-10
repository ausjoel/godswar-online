using System.Globalization;
using System.Text.Json.Nodes;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct EditorBuffToken(Guid Epoch, int Kind, uint StatusId, long Revision);
internal sealed record OperatorInspectBuffAction(string ActorName) : OperatorSessionAction(ActorName);
internal sealed record OperatorRemoveBuffAction(string ActorName, EditorBuffToken Token) : OperatorSessionAction(ActorName);

internal sealed partial class GameSessionRegistry
{
    internal static bool TryReadEditorBuffToken(JsonObject body, out EditorBuffToken token)
    {
        token = default;
        if (!Guid.TryParseExact(Text(body, "epoch"), "N", out var epoch) || epoch == Guid.Empty ||
            Int(body, "kind") is not { } kind || kind < 1 ||
            Int(body, "statusId") is not { } status || status < 1 ||
            !long.TryParse(Text(body, "revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 1)
            return false;
        token = new(epoch, kind, (uint)status, revision);
        return true;
    }

    private static bool IsEditorRemovableBuff(ActiveRuntimeStatus status) =>
        // ROUND3: a GM-applied finite status may be removed, including a stat penalty.
        // Native mounts and other source-owned effects retain their own lifecycle.
        status.ExpiresAt != DateTimeOffset.MaxValue && (status.OperatorApplied ||
            status.Beneficial && OperatorBuffCatalog.All.Any(entry => entry.Definition.Beneficial &&
                entry.Definition.Kind == status.Kind && entry.Definition.StatusId == status.StatusId));

    internal async Task<JsonObject> InspectRuntimeBuffsForEditorAsync(
        ClientSession session, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!TryGetOrCreatePlayerStatusState(session, out var state))
            return new JsonObject { ["ok"] = false, ["error"] = "offline" };
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var rows = new JsonArray();
            foreach (var status in state.RuntimeStatuses.Values.Where(value => value.ExpiresAt > now).OrderBy(value => value.Kind))
                rows.Add(new JsonObject
                {
                    ["kind"] = status.Kind, ["statusId"] = status.StatusId,
                    // Revisions are strings to preserve exact Int64 values in JavaScript.
                    ["revision"] = status.Revision.ToString(CultureInfo.InvariantCulture),
                    ["remainingSeconds"] = status.RemainingSeconds(now),
                    ["permanent"] = status.ExpiresAt == DateTimeOffset.MaxValue,
                    ["priority"] = status.Priority, ["beneficial"] = status.Beneficial,
                    ["removable"] = IsEditorRemovableBuff(status)
                });
            return new JsonObject { ["ok"] = true, ["epoch"] = state.EditorEpoch.ToString("N"), ["buffs"] = rows };
        }
        finally { state.Gate.Release(); }
    }

    internal async Task<JsonObject> RemoveRuntimeBuffForEditorAsync(
        ClientSession session, EditorBuffToken expected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state))
            return new JsonObject { ["ok"] = false, ["error"] = "buff_changed" };
        var claims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.EditorEpoch != expected.Epoch ||
                !state.RuntimeStatuses.TryGetValue(expected.Kind, out var current) ||
                current.StatusId != expected.StatusId || current.Revision != expected.Revision || current.ExpiresAt <= now)
                return new JsonObject { ["ok"] = false, ["error"] = "buff_changed" };
            // ROUND3: finite operator rides are removable; native persistent rides and durable sources remain protected.
            if (!IsEditorRemovableBuff(current))
                return new JsonObject { ["ok"] = false, ["error"] = "buff_not_removable" };
            var revision = checked(state.Revision + 1);
            state.RuntimeStatuses.Remove(expected.Kind);
            state.Revision = revision;
            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(session, state, now, "editor-buff-removed",
                force: true, broadcast: true, cancellationToken, claimedDisconnects: claims);
            return new JsonObject { ["ok"] = true, ["removed"] = true, ["statusId"] = expected.StatusId };
        }
        finally { state.Gate.Release(); claims.CompleteAll(this); }
    }
}
