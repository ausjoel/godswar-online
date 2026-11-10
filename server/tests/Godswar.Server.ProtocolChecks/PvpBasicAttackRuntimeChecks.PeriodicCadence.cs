// ROUND3: application-owned periodic deadlines, duplicate admission and replaced-session fencing.
using System.Collections;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string PeriodicCadenceCheckName = "Periodic status recovery keeps independent cadence and exact session authority";

    public static async Task RunPeriodicCadenceAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var socket = await RuntimePolicySessionSocket.CreateAsync();
            var registry = Registry(mode);
            var character = Player(2520, GameDefaults.SpartaCamp);
            character.CurrentHp = 100;
            character.CurrentMp = 10;
            Join(registry, socket, character);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var method = typeof(GameSessionRegistry).GetMethod("AdvanceStatusPeriodicRecoveryOnceAsync", flags)!;
            Task Pulse(DateTimeOffset at) => (Task)method.Invoke(registry, [at, CancellationToken.None])!;
            object State() => ((IDictionary)typeof(GameSessionRegistry).GetField("_playerStatusStates", flags)!
                .GetValue(registry)!)[socket.Session]!;
            SemaphoreSlim Gate()
            {
                var state = State();
                return (SemaphoreSlim)state.GetType().GetProperty("Gate")!.GetValue(state)!;
            }
            Check.True(GenericStatusCatalog.TryResolve(520, out var health, out _), "audited health recovery resolves");
            Check.True(GenericStatusCatalog.TryResolve(510, out var mana, out _), "audited mana recovery resolves");
            var start = DateTimeOffset.UtcNow;
            health = health with { Duration = TimeSpan.FromMinutes(1), OperatorApplied = true };
            mana = mana with { Duration = TimeSpan.FromMinutes(1), OperatorApplied = true };
            try
            {
                await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, health, start, "cadence-hp", CancellationToken.None);
                await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, mana, start.AddMilliseconds(500), "cadence-mp", CancellationToken.None);
                await Pulse(start.AddSeconds(1));
                Check.Equal(600, character.CurrentHp, "adding mana does not postpone an existing health pulse");
                Check.Equal(10, character.CurrentMp, "new mana source cannot pulse before its own first deadline");
                await Pulse(start.AddMilliseconds(1500));
                Check.Equal(600, character.CurrentHp, "mana deadline does not advance health early");
                Check.Equal(15, character.CurrentMp, "mana uses its independently authored cadence");
                await Pulse(start.AddSeconds(2));
                Check.Equal(1100, character.CurrentHp, "health retains its next deadline");
                Check.Equal(15, character.CurrentMp, "health deadline does not advance mana early");
                await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, health, start.AddMilliseconds(2250), "cadence-recast", CancellationToken.None);
                await Pulse(start.AddMilliseconds(2500));
                Check.Equal(1100, character.CurrentHp, "same-kind recast restarts only its own cadence");
                Check.Equal(20, character.CurrentMp, "recast of health does not postpone mana");
                await Pulse(start.AddMilliseconds(3250));
                Check.Equal(1600, character.CurrentHp, "recast first deadline is one second after application");
                var gate = Gate();
                await gate.WaitAsync();
                Task first, second;
                try
                {
                    first = Pulse(start.AddMilliseconds(4250));
                    second = Pulse(start.AddMilliseconds(4250));
                    Check.True(!first.IsCompleted && !second.IsCompleted, "both pumps queue behind status authority");
                }
                finally { gate.Release(); }
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
                Check.Equal(2100, character.CurrentHp, "two admitted pumps cannot consume one health deadline twice");
                Check.Equal(25, character.CurrentMp, "late cadence consumes one mana pulse without catch-up bursts");
                var token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session,
                    start.AddMilliseconds(4250), CancellationToken.None), health.StatusId);
                await gate.WaitAsync();
                Task<System.Text.Json.Nodes.JsonObject> removal;
                Task afterRemoval;
                try
                {
                    removal = registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token,
                        start.AddMilliseconds(4500), CancellationToken.None);
                    afterRemoval = Pulse(start.AddMilliseconds(5250));
                }
                finally { gate.Release(); }
                Check.True((await removal.WaitAsync(TimeSpan.FromSeconds(3)))["removed"]!.GetValue<bool>(), "exact removal wins before queued pulse");
                await afterRemoval.WaitAsync(TimeSpan.FromSeconds(3));
                Check.Equal(2100, character.CurrentHp, "removed source contributes no later pulse");
                Check.Equal(30, character.CurrentMp, "removing one channel leaves the other cadence intact");

                character.CurrentHp = 0;
                registry.AdvancePlayerLifeRevision(socket.Session, start.AddMilliseconds(5500));
                await Pulse(start.AddMilliseconds(6250));
                Check.Equal(30, character.CurrentMp, "dead characters cannot receive recovery");
                character.CurrentHp = 100;
                registry.AdvancePlayerLifeRevision(socket.Session, start.AddMilliseconds(6500));
                await Pulse(start.AddMilliseconds(6750));
                Check.Equal(30, character.CurrentMp, "revival cannot replay a missed dead-life pulse");
                await Pulse(start.AddMilliseconds(7250));
                Check.Equal(35, character.CurrentMp, "surviving source resumes at its next normal deadline");

                // Hold an enumerated old state while the same session is removed and rejoined with a fresh life/state.
                await gate.WaitAsync();
                Task stale;
                try
                {
                    stale = Pulse(start.AddMilliseconds(8250));
                    Check.True(!stale.IsCompleted, "old-state pump waits before mutation");
                    registry.Remove(socket.Session);
                    var replacement = Player(2520, GameDefaults.SpartaCamp);
                    replacement.CurrentHp = 100;
                    replacement.CurrentMp = 10;
                    Join(registry, socket, replacement);
                    await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, mana,
                        start.AddSeconds(8), "replacement-cadence", CancellationToken.None);
                    character = replacement;
                }
                finally { gate.Release(); }
                await stale.WaitAsync(TimeSpan.FromSeconds(3));
                Check.Equal(10, character.CurrentMp, "enumerated old state cannot heal a replacement character");
                await Pulse(start.AddSeconds(9));
                Check.Equal(15, character.CurrentMp, "new state heals once at its own first deadline");
            }
            finally { registry.Remove(socket.Session); }
        }
    }
}
