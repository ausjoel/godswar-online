using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Godswar.Server.Operations;
using System.Text.Json.Nodes;
using Godswar.Server.Application.Operators;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class BuffAdminChecks
{
    public const string CheckName = "Editor buff catalogue and native duration validation preserve authored gameplay";
    // ROUND3 (merged): applicable audited rows outside the operator catalogue (116 mounts + EXP layers + controls).
    private const int AuditedOnlyRowCount = 167;
    public static async Task RunAsync()
    {
        var registry = new GameSessionRegistry();
        var response = await registry.HandleOperatorAdminAsync(
            new OperatorAdminRequest("GET", "/admin/buffs/catalog", []), null!, CancellationToken.None);
        Check.Equal(200, response.StatusCode, "catalogue GET needs no character or write store");
        // Contract fixture for the editor's tests: GODSWAR_DUMP_BUFF_CATALOG=<file> saves the real response.
        if (Environment.GetEnvironmentVariable("GODSWAR_DUMP_BUFF_CATALOG") is { Length: > 0 } dumpPath)
            await File.WriteAllTextAsync(dumpPath, response.Json);
        using var doc = JsonDocument.Parse(response.Json);
        Check.Equal("session", doc.RootElement.GetProperty("persistence").GetString()!, "runtime buffs do not imply offline persistence");
        var rows = doc.RootElement.GetProperty("buffs").EnumerateArray().ToArray();
        // Operator catalogue = 25 class-skill buffs + 28 item buffs + 9 Status.ini data rows (scan: Development/buffs-sort-2026-11-05)
        // FINALISE2 (F-102): + 26 more helpful rows (scan groups G and C) + 94 hostile rows (group E, kind-110 mount row left out).
        // ROUND3 (merged): + applicable audited rows the operator catalogue lacks (native mounts, EXP layers, two controls).
        var operatorRows = rows.Where(row => row.GetProperty("source").GetString() is not ("status" or "mount")).ToArray();
        Check.Equal(182, operatorRows.Length, "class-skill, item, status-data, extra and hostile buffs exposed");
        Check.Equal(116, rows.Count(row => row.GetProperty("source").GetString() == "mount"), "ROUND3: conditional native mounts exposed");
        Check.Equal(AuditedOnlyRowCount, rows.Length - operatorRows.Length, "ROUND3: applicable audited rows join the buffs list");
        Check.Equal(357, doc.RootElement.GetProperty("statuses").GetArrayLength(), "ROUND3: every audited status has an applicability explanation");
        // ROUND3 (merged): listed rows that apply their audited contract; values both catalogues carry must agree.
        Check.Equal(97, OperatorBuffCatalog.All.Count(entry => GameSessionRegistry.TryResolveAuditedOperatorStatus(entry.Definition.StatusId, out _)),
            "ROUND3: listed rows overridden by the audited contract");
        Check.True(GameSessionRegistry.TryResolveAuditedOperatorStatus(10, out var bleedPulse) && bleedPulse.HpDamagePerPulse == 50 &&
            bleedPulse.DamageIntervalSeconds == 2, "ROUND3: listed Bleeding applies its audited HP loss");
        Check.True(!GameSessionRegistry.TryResolveAuditedOperatorStatus(235, out _), "235 keeps the operator catalogue's unconfirmed-unit row");
        Check.Equal(26, rows.Count(row => row.GetProperty("source").GetString() == "status-extra"), "FINALISE2: extra helpful rows exposed");
        Check.Equal(94, rows.Count(row => row.GetProperty("source").GetString() == "hostile"), "FINALISE2: hostile rows exposed");
        // ROUND3: a native mount row is not a buff slot (beneficial false) and is not hostile either.
        Check.True(rows.All(row => row.GetProperty("hostile").GetBoolean() ==
            (row.GetProperty("source").GetString() != "mount" && !row.GetProperty("beneficial").GetBoolean())), "FINALISE2: hostile flag = not beneficial");
        Check.True(rows.All(row => row.GetProperty("note").GetString()!.StartsWith("Applies: ", StringComparison.Ordinal)), "FINALISE2: every row says what it applies");
        Check.True(rows.All(row => row.GetProperty("name").GetString()!.Length > 0 && !row.GetProperty("name").GetString()!.StartsWith("status ", StringComparison.Ordinal)), "FINALISE2: every row has its Status.ini name");
        Check.True(OperatorBuffCatalog.All.All(entry => entry.Definition.Kind != MountCatalog.RuntimeStatusKind), "FINALISE2: no row uses the mount kind");
        Check.True(OperatorBuffCatalog.TryGetByStatus(400, out var caged) && caged.Note.Contains("Server enforces: no skill casts (stunned)", StringComparison.Ordinal) &&
            caged.Note.Contains("cannot move", StringComparison.Ordinal), "FINALISE2: Caged says the server stuns and roots");
        Check.True(OperatorBuffCatalog.TryGetByStatus(10, out var bleed) && bleed.Note.Contains("not applied: damage over time", StringComparison.Ordinal), "FINALISE2: damage over time is labelled icon only");
        Check.True(OperatorBuffCatalog.TryGetByStatus(620, out var rage) && rage.Definition.Duration == TimeSpan.FromHours(24) && rage.Note.Contains("Permanent", StringComparison.Ordinal), "FINALISE2: permanent rows last 24 h");
        Check.Equal(response.Json.Length < new ManagementOptions().MaximumResponseBytes, true, "catalogue fits the management response limit (ROUND3: 256 KiB)");
        Check.Equal(rows.Length, rows.Select(row => row.GetProperty("statusId").GetUInt32()).Distinct().Count(), "catalogue identities (status ids) unique");
        Check.Equal(25, rows.Count(row => row.GetProperty("source").GetString() == "class-skill"), "all five authored buff families and ranks exposed");
        Check.Equal(28, rows.Count(row => row.GetProperty("source").GetString() == "item"), "item buffs exposed");
        Check.Equal(9, rows.Count(row => row.GetProperty("source").GetString() == "status-data"), "status-data buffs exposed");
        Check.Equal(25, SkillStatusEffectCatalog.All.Count(), "gameplay catalogue (casts) is unchanged by the operator list");
        Check.Equal(uint.MaxValue, doc.RootElement.GetProperty("maxDurationSeconds").GetUInt32(), "duration bound comes from native wire encoding");
        foreach (var row in operatorRows)
        {
            var statusId = row.GetProperty("statusId").GetUInt32();
            Check.True(OperatorBuffCatalog.TryGetByStatus(statusId, out var byStatus), "every catalogue row resolves by status id");
            Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = (int)statusId }, out var byStatusParsed, out _),
                "every row can be requested by status id");
            // ROUND3: generated status-extra/hostile rows with a complete audited contract apply that contract.
            var expected = GameSessionRegistry.TryResolveAuditedOperatorStatus(statusId, out var audited) ? audited : byStatus.Definition;
            Check.True(expected == byStatusParsed, "a status-id request returns the catalogue definition (FINALISE2: hostile rows too)");
            Check.True(byStatusParsed.Kind != MountCatalog.RuntimeStatusKind, "no editor buff shares the mount status kind");
            if (row.GetProperty("source").GetString() != "class-skill")
                continue;
            var id = row.GetProperty("skillId").GetInt32();
            Check.True(SkillStatusEffectCatalog.TryGet(id, out var authored) &&
                GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["skillId"] = id }, out var parsed, out _) &&
                authored == parsed, "default request preserves authored rank, effects and duration");
            Check.Equal(authored.StatusId, row.GetProperty("statusId").GetUInt32(), "native status identity retained");
        }
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["skillId"] = 774, ["durationSeconds"] = (long)uint.MaxValue }, out var longBuff, out _) &&
            longBuff.Duration == TimeSpan.FromSeconds(uint.MaxValue) && longBuff.DodgeBonus == 3_000 &&
            longBuff.CriticalResistanceBonus == 1_000,
            "long GM duration preserves actual Gaia attributes without an arbitrary one-day cap");
        foreach (var invalid in new JsonObject[]
        {
            new() { ["skillId"] = 999999 },
            new() { ["statusId"] = 999999 },
            new() { ["statusId"] = 0 },
            // FINALISE2 (the admin F-102): 10 Bleeding, 331 Stunned and 650 Peace are now accepted (checked below).
            new() { ["statusId"] = 110 },    // not in the operator catalogue
            new() { ["skillId"] = 4852 },    // an item skill: skillId stays class-skill only
            new() { ["statusId"] = 454, ["durationSeconds"] = 0L },
            new() { ["statusId"] = 454, ["durationSeconds"] = (long)uint.MaxValue + 1 },
            new() { ["skillId"] = 774, ["durationSeconds"] = 0 },
            new() { ["skillId"] = 774, ["durationSeconds"] = -1 },
            new() { ["skillId"] = 774, ["durationSeconds"] = (long)uint.MaxValue + 1 },
            new() { ["skillId"] = 774, ["durationSeconds"] = 1.5 },
            new() { ["skillId"] = 774, ["durationSeconds"] = null }
        })
            Check.True(!GameSessionRegistry.TryReadOperatorBuff(invalid, out _, out _), "malformed, unknown or unencodable buffs refused");
        foreach (var statusId in new[] { 10, 331, 650 }) // FINALISE2: hostile and peace rows are operator-applicable (icon only where noted)
            Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = statusId }, out _, out _), "FINALISE2: hostile/peace rows accepted");
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = 1434 }, out var heal, out _) &&
            heal.HpPerSecond == 1_500 && heal.Duration == TimeSpan.FromSeconds(8),
            "status-data heal over time keeps Status.ini rate and duration");
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = 609, ["durationSeconds"] = 60L }, out var guard, out _) &&
            guard.PhysicalDefenseBonus == 50 && guard.MagicDefenseBonus == 40 && guard.Duration == TimeSpan.FromSeconds(60),
            "item buff by status id keeps authored numbers with an operator duration");
        var now = DateTimeOffset.UtcNow;
        Check.Equal(TimeSpan.Zero, GameSessionRegistry.GetRuntimeStatusExpiryDelay(now, now), "elapsed buff has no timer delay");
        Check.Equal(TimeSpan.FromSeconds(5), GameSessionRegistry.GetRuntimeStatusExpiryDelay(now.AddSeconds(5), now), "normal expiry keeps exact authored interval");
        Check.Equal(TimeSpan.FromDays(1), GameSessionRegistry.GetRuntimeStatusExpiryDelay(now.AddSeconds(uint.MaxValue), now), "long native duration uses bounded cancellable timer chunks");
        // ROUND3: every advertised generic row must resolve to authored values.
        foreach (var row in rows.Except(operatorRows))
        {
            var statusId = row.GetProperty("statusId").GetInt32();
            Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = statusId },
                out var definition, out _) && definition.StatusId == (uint)statusId && definition.SkillId == 0,
                "generic selection resolves status identity directly");
        }
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = 554 }, out var attack, out _) &&
            attack.PhysicalAttackBonus == 100 && attack.MagicAttackBonus == 80,
            "generic attack copies exact source values");
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = 100 }, out var penalty, out _) &&
            penalty.PhysicalDefenseBonus == -30 && penalty.MagicDefenseBonus == -20 && !penalty.Beneficial,
            "multi-effect negative stats retain both signed values");
        Check.True(GameSessionRegistry.TryReadOperatorBuff(new JsonObject { ["statusId"] = 520 }, out var heal520, out _) &&
            heal520.HpPerSecond == 500 && heal520.Duration == TimeSpan.FromSeconds(30),
            "one-second recovery keeps source magnitude and duration");
        foreach (var invalid in new JsonObject[]
        {
            // Merged with FINALISE2: 559 Ambrosia stays applicable from the operator catalogue (max HP/MP icon only).
            new() { ["statusId"] = 999999 },
            new() { ["statusId"] = 1500 }, // ROUND3: mounts now have their own conditional native activation.
            new() { ["skillId"] = 774, ["statusId"] = 554 },
            new() { ["statusId"] = 554, ["durationSeconds"] = 1.5 }
        })
            Check.True(!GameSessionRegistry.TryReadOperatorBuff(invalid, out _, out _),
                "unknown, partial, source-owned and ambiguous selections fail closed");
        await CheckManagementRouteAsync(registry);

    }
    private static async Task CheckManagementRouteAsync(GameSessionRegistry registry)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var state = new ServerOperationalState(ServerReadinessDependency.None);
        state.TryMarkRunning();
        var previous = OperatorAdminApi.Handler;
        OperatorAdminApi.Handler = (request, token) => registry.HandleOperatorAdminAsync(request, null!, token);
        var server = new ManagementHttpServer(new ManagementOptions { Port = port }, state.GetSnapshot,
            _ => ValueTask.FromResult(new ManagementPayload(ManagementContentType.Json, "{}"u8.ToArray())),
            _ => ValueTask.FromResult(new ManagementPayload(ManagementContentType.Json, "{}"u8.ToArray())),
            _ => false, _ => ValueTask.FromResult(ManagementDrainResult.AlreadyDraining));
        using var stop = new CancellationTokenSource();
        var task = server.RunAsync(stop.Token);
        try
        {
            var endpoint = await server.WaitUntilStartedAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Check.True(endpoint.Address.Equals(IPAddress.Loopback), "buff management catalogue remains loopback-only");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var json = JsonDocument.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/admin/buffs/catalog"));
            // ROUND3: exercise the real response cap, not just in-memory serialization.
            Check.Equal(182 + AuditedOnlyRowCount, json.RootElement.GetProperty("buffs").GetArrayLength(), "actual HTTP catalogue route returns complete authored content"); // FINALISE2
            Check.Equal(357, json.RootElement.GetProperty("statuses").GetArrayLength(), "actual HTTP catalog fits the bounded response");
        }
        finally
        {
            stop.Cancel();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            OperatorAdminApi.Handler = previous;
        }
    }

}
