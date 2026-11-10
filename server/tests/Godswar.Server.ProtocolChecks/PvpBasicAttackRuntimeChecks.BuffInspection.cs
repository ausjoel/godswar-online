using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string BuffInspectionCheckName = "Runtime buff inspection and removal fence recasts sessions expiry and derived effects";
    public static async Task RunBuffInspectionAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var socket = await RuntimePolicySessionSocket.CreateAsync();
            await using var other = await RuntimePolicySessionSocket.CreateAsync();
            var registry = Registry(mode);
            Join(registry, socket, Player(1400, GameDefaults.SpartaCamp));
            Join(registry, other, Player(1401, GameDefaults.SpartaCamp));
            var now = DateTimeOffset.UtcNow;
            Check.True(SkillStatusEffectCatalog.TryGet(774, out var gaia), "authored Gaia exists");
            gaia = gaia with { Duration = TimeSpan.FromMinutes(10) };
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, gaia, now, "fixture", CancellationToken.None), "first buff applies");
            var snapshot = await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now, CancellationToken.None);
            var token = ReadToken(snapshot, gaia.StatusId);
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, gaia, now.AddSeconds(1), "recast", CancellationToken.None), "same-rank recast applies");
            var refused = await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now.AddSeconds(2), CancellationToken.None);
            Check.True(refused["ok"]!.GetValue<bool>() == false && registry.IsRuntimeStatusActive(socket.Session, gaia.Kind, now.AddSeconds(2)), "stale revision cannot remove recast buff");
            snapshot = await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now.AddSeconds(2), CancellationToken.None);
            token = ReadToken(snapshot, gaia.StatusId);
            await registry.ApplyRuntimeStatusAndPublishAsync(other.Session, gaia, now, "other", CancellationToken.None);
            await registry.ApplyRuntimeStatusAndPublishAsync(other.Session, gaia, now.AddSeconds(1), "other-recast", CancellationToken.None);
            var otherToken = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(other.Session, now.AddSeconds(2), CancellationToken.None), gaia.StatusId);
            Check.True(otherToken.Revision == token.Revision && otherToken.Epoch != token.Epoch,
                "cross-session test aligns kind/status/revision so lifetime is the deciding fence");
            var wrongSession = await registry.RemoveRuntimeBuffForEditorAsync(other.Session, token, now.AddSeconds(2), CancellationToken.None);
            Check.True(wrongSession["ok"]!.GetValue<bool>() == false && registry.IsRuntimeStatusActive(other.Session, gaia.Kind, now.AddSeconds(2)), "another status lifetime cannot use this token even for the same kind/status");
            var removed = await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now.AddSeconds(2), CancellationToken.None);
            Check.True(removed["removed"]!.GetValue<bool>() && !registry.IsRuntimeStatusActive(socket.Session, gaia.Kind, now.AddSeconds(2)), "fresh token removes the intended buff in both runtime modes");
            var repeated = await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now.AddSeconds(2), CancellationToken.None);
            Check.True(repeated["ok"]!.GetValue<bool>() == false, "repeated remove does not act on a later effect");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, gaia, now, "expiry", CancellationToken.None);
            token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now, CancellationToken.None), gaia.StatusId);
            Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now.AddMinutes(11), CancellationToken.None))["ok"]!.GetValue<bool>() == false, "expired snapshot cannot remove anything");
            // A permanent mount/source-owned status must remain under its own authoritative operation.
            await registry.SetPersistentRuntimeStatusAndPublishAsync(socket.Session, 80000, 60000, 1, true, 0, true, now, "derived-fixture", CancellationToken.None);
            var derived = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now, CancellationToken.None), 60000);
            Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, derived, now, CancellationToken.None))["ok"]!.GetValue<bool>() == false && registry.IsRuntimeStatusActive(socket.Session, 80000, now), "unknown source-owned status is not silently deleted as a class buff");
            await registry.SetPersistentRuntimeStatusAndPublishAsync(socket.Session, gaia.Kind, gaia.StatusId, 5, true, 0, true, now, "persistent-known-id", CancellationToken.None);
            var persistent = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now, CancellationToken.None), gaia.StatusId);
            Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, persistent, now, CancellationToken.None))["ok"]!.GetValue<bool>() == false,
                "permanent source-owned effect remains protected even if it shares a class-buff identity");
            var body = new JsonObject { ["epoch"] = token.Epoch.ToString("N"), ["kind"] = token.Kind, ["statusId"] = (int)token.StatusId, ["revision"] = long.MaxValue.ToString() };
            Check.True(GameSessionRegistry.TryReadEditorBuffToken(body, out var parsed) && parsed.Revision == long.MaxValue, "exact string revision survives beyond JavaScript integer precision");
            body["revision"] = 1.5;
            Check.True(!GameSessionRegistry.TryReadEditorBuffToken(body, out _), "fractional revision rejected");
        }
    }
    private static EditorBuffToken ReadToken(JsonObject snapshot, uint statusId)
    {
        var row = snapshot["buffs"]!.AsArray().Select(value => value!.AsObject()).Single(value => value["statusId"]!.GetValue<uint>() == statusId);
        var request = new JsonObject { ["epoch"] = snapshot["epoch"]!.GetValue<string>(), ["kind"] = row["kind"]!.GetValue<int>(), ["statusId"] = (int)statusId, ["revision"] = row["revision"]!.GetValue<string>() };
        Check.True(GameSessionRegistry.TryReadEditorBuffToken(request, out var token), "inspect response yields exact validated removal token");
        return token;
    }
}
