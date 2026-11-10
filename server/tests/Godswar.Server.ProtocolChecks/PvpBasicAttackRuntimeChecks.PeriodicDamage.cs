// ROUND3: actual HP loss, cadence, lifetime, native death, cancellation and potion settlement authority.
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Operators;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string PeriodicDamageCheckName = "Audited periodic HP loss preserves cadence life death persistence and potion authority";
    public static async Task RunPeriodicDamageAsync()
    {
        var rows = GenericStatusCatalog.All.Where(row => row.Effects.Any(effect => effect.Code == 27)).ToArray();
        Check.Equal(39, rows.Length, "all audited HP loss rows considered");
        foreach (var row in rows)
        {
            var accepted = GenericStatusCatalog.TryResolve(row.StatusId, out var effect, out _);
            Check.True(accepted == (row.IntervalSeconds > 0), "zero-interval loss stays refused");
            if (accepted) Check.True(effect.HpDamagePerPulse == row.Effects.Single().Value &&
                effect.DamageIntervalSeconds == row.IntervalSeconds, "magnitude and interval are exact source values");
        }
        Check.True(GenericStatusCatalog.TryResolve(14, out var unusual, out _) && unusual.Duration.TotalSeconds == 674,
            "unusual audited duration is not guessed away");
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckPeriodicDamageCadenceAsync(mode);
            await CheckPeriodicDamageDeathAsync(mode);
            await CheckPeriodicDamagePersistenceAsync(mode);
            await CheckPeriodicDamageLateFailureAsync(mode);
            await CheckPeriodicDamagePotionAsync(mode, lethal: false);
            await CheckPeriodicDamagePotionAsync(mode, lethal: true);
        }
    }

    private static Task DamagePulse(GameSessionRegistry registry, DateTimeOffset time, CancellationToken token = default) =>
        (Task)typeof(GameSessionRegistry).GetMethod("AdvanceStatusPeriodicDamageOnceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(registry, [time, token])!;

    private static SemaphoreSlim DamageGate(GameSessionRegistry registry, ClientSession session)
    {
        var states = (IDictionary)typeof(GameSessionRegistry).GetField("_playerStatusStates",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        var state = states[session]!;
        return (SemaphoreSlim)state.GetType().GetProperty("Gate")!.GetValue(state)!;
    }

    private static GameSessionRegistry DamageRegistry(PlayerRuntimeMode mode, StatusDamageStore store) =>
        new(store, null, MonsterRuntimeMode.Ecs, mode, gameplayCatalogs: GameplayContentTestFixtures.Runtime);

    private static SkillStatusEffectDefinition Damage(int id)
    {
        Check.True(GenericStatusCatalog.TryResolve(id, out var definition, out _), "audited HP loss resolves");
        return definition with { OperatorApplied = true, Duration = TimeSpan.FromMinutes(1) };
    }

    private static async Task CheckPeriodicDamageCadenceAsync(PlayerRuntimeMode mode)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        var store = new StatusDamageStore();
        var registry = DamageRegistry(mode, store);
        var character = Player(2540, GameDefaults.SpartaCamp);
        Join(registry, socket, character);
        var start = DateTimeOffset.UtcNow;
        async Task Apply(int id, double at) => Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session,
            Damage(id), start.AddSeconds(at), "dot-cadence", CancellationToken.None), "audited source applies");
        try
        {
            Check.True(!await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10) with { OperatorApplied = false },
                start, "unattributed-source", CancellationToken.None), "native skill cannot silently become administrative damage");
            await Apply(10, 0);
            var stale = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, start, CancellationToken.None), 10);
            await Apply(40, .5);
            await DamagePulse(registry, start.AddSeconds(1.9));
            Check.Equal(10000, character.CurrentHp, "no source pulses before its own deadline");
            await DamagePulse(registry, start.AddSeconds(2));
            Check.Equal(9950, character.CurrentHp, "first bleeding pulse is exact flat HP loss");
            await DamagePulse(registry, start.AddSeconds(2.5));
            Check.Equal(9890, character.CurrentHp, "staggered burning retains its separate deadline");
            await Apply(11, 2.75);
            Check.True(!await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10), start.AddSeconds(3),
                "weaker-recast", CancellationToken.None), "weaker active rank cannot replace stronger damage");
            Check.True(!(await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, stale, start.AddSeconds(3),
                CancellationToken.None))["ok"]!.GetValue<bool>(), "old source token cannot remove stronger recast");
            await DamagePulse(registry, start.AddSeconds(4));
            Check.Equal(9890, character.CurrentHp, "recast does not reuse the old bleeding deadline");
            await DamagePulse(registry, start.AddSeconds(4.5));
            await DamagePulse(registry, start.AddSeconds(4.75));
            Check.Equal(9730, character.CurrentHp, "independent burn and stronger bleeding retain exact magnitudes");
            await Apply(11, 5);
            var gate = DamageGate(registry, socket.Session);
            await gate.WaitAsync();
            Task first, second;
            try
            {
                first = DamagePulse(registry, start.AddSeconds(7));
                second = DamagePulse(registry, start.AddSeconds(7));
                Check.True(!first.IsCompleted && !second.IsCompleted, "both pumps are queued at authority");
            }
            finally { gate.Release(); }
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(9570, character.CurrentHp, "duplicate admitted pumps cannot apply one deadline twice");
            await DamagePulse(registry, start.AddSeconds(20));
            Check.Equal(9410, character.CurrentHp, "late pump consumes one pulse per source without a catch-up burst");
            var token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, start.AddSeconds(20),
                CancellationToken.None), 11);
            Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, start.AddSeconds(20),
                CancellationToken.None))["removed"]!.GetValue<bool>(), "exact source removal succeeds");
            await DamagePulse(registry, start.AddSeconds(22));
            Check.Equal(9350, character.CurrentHp, "removal stops only that source while another continues");
            token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, start.AddSeconds(22), CancellationToken.None), 40);
            await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, start.AddSeconds(22), CancellationToken.None);
            await Apply(10, 23);
            OperatorControls.UpdatePowers(character.Id, powers => powers with { Invulnerable = true });
            await DamagePulse(registry, start.AddSeconds(25));
            Check.Equal(9350, character.CurrentHp, "existing invulnerability prevents administrative HP loss");
            OperatorControls.ClearPowers(character.Id);
            await DamagePulse(registry, start.AddSeconds(25));
            Check.Equal(9350, character.CurrentHp, "invulnerable interval cannot be replayed after god mode ends");
            await DamagePulse(registry, start.AddSeconds(27));
            Check.Equal(9300, character.CurrentHp, "normal cadence resumes without changing GM policy");
            registry.AdvancePlayerLifeRevision(socket.Session, start.AddSeconds(28));
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10) with { Priority = 0 },
                start.AddSeconds(28), "new-life-lower-rank", CancellationToken.None), "old-life source cannot block a new-life weaker application");
            var oldGate = DamageGate(registry, socket.Session);
            await oldGate.WaitAsync();
            Task oldPulse;
            try
            {
                oldPulse = DamagePulse(registry, start.AddSeconds(30));
                registry.Remove(socket.Session);
                character = Player(2540, GameDefaults.SpartaCamp);
                Join(registry, socket, character);
                await Apply(10, 29);
            }
            finally { oldGate.Release(); }
            await oldPulse.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(10000, character.CurrentHp, "old enumerated state cannot damage a rejoined character");
            await DamagePulse(registry, start.AddSeconds(31));
            Check.Equal(9950, character.CurrentHp, "new state uses its own first deadline");
            await DamagePulse(registry, start.AddSeconds(90));
            Check.Equal(9950, character.CurrentHp, "expired source cannot catch up damage");
            Check.Equal(1000, character.CurrentMp, "HP loss never consumes MP");
        }
        finally { OperatorControls.ClearPowers(character.Id); registry.Remove(socket.Session); }
    }

    private static async Task CheckPeriodicDamageDeathAsync(PlayerRuntimeMode mode)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        await using var observer = await RuntimePolicySessionSocket.CreateAsync();
        var store = new StatusDamageStore();
        var registry = DamageRegistry(mode, store);
        var character = Player(2550, GameDefaults.SpartaCamp);
        character.CurrentHp = 100;
        Join(registry, socket, character);
        Join(registry, observer, Player(2551, GameDefaults.SpartaCamp));
        var credit = 0; var interruptions = 0;
        registry.RegisterQuestPlayerKillRecorder(socket.Session, (_, _) => { credit++; return Task.CompletedTask; });
        registry.RegisterQuestPlayerKillRecorder(observer.Session, (_, _) => { credit++; return Task.CompletedTask; });
        registry.RegisterSkillCastInterruptionSink(socket.Session, (reason, _, _) =>
        { if (reason == SkillCastInterruptionReason.Death) interruptions++; return Task.CompletedTask; });
        var start = DateTimeOffset.UtcNow;
        try
        {
            await registry.SetPersistentRuntimeStatusAndPublishAsync(socket.Session, MountCatalog.RuntimeStatusKind, 1100,
                1, false, .2f, true, start, "death-mount", CancellationToken.None);
            var life = registry.GetPlayerLifeRevision(socket.Session);
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(159), start, "lethal-admin-source", CancellationToken.None);
            await DamagePulse(registry, start.AddSeconds(1));
            Check.True(character.CurrentHp == 0 && store.LastHp == 0 && !store.LastCancellationCanBeRequested,
                "lethal HP loss is clamped and durably flushed before publication");
            Check.Equal(life + 1, registry.GetPlayerLifeRevision(socket.Session), "lethal loss advances native character life once");
            Check.True(interruptions == 1 && credit == 0, "death interrupts casts without inventing player kill rewards");
            Check.True(!registry.GetRuntimeStatusAggregate(socket.Session, start.AddSeconds(1)).IsRiding,
                "lethal loss clears native riding model/speed");
            foreach (var recipient in new[] { socket, observer })
            {
                var foundHp = false; var foundDeath = false;
                for (var i = 0; i < 40 && !foundDeath; i++)
                {
                    var packet = await recipient.ReadPacketAsync();
                    var opcode = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
                    if (opcode == 0x2771 && BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)) == 0) foundHp = true;
                    if (opcode == 10027)
                    {
                        foundDeath = true;
                        Check.True(foundHp && Enumerable.Range(0, 5).All(j =>
                            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8 + j * 4)) == -1),
                            "native HP/death reaches self and observer with no reward recipients");
                        if (ReferenceEquals(recipient, observer)) Check.Equal(WorldObjectIds.ForPlayer(character.Id),
                            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)), "observer death identity is world-relative");
                    }
                }
                Check.True(foundDeath, "native death packet was received");
            }
            Check.True(!await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10), start.AddSeconds(1),
                "dead-target", CancellationToken.None), "already-dead target refuses administrative damage");
            character.CurrentHp = 100;
            registry.AdvancePlayerLifeRevision(socket.Session, start.AddSeconds(2));
            await DamagePulse(registry, start.AddSeconds(3));
            Check.Equal(100, character.CurrentHp, "death-bound source cannot damage the revived life");
        }
        finally { registry.UnregisterSkillCastInterruptionSink(socket.Session); registry.Remove(socket.Session); registry.Remove(observer.Session); }
    }

    private static async Task CheckPeriodicDamagePersistenceAsync(PlayerRuntimeMode mode)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        await using var other = await RuntimePolicySessionSocket.CreateAsync();
        using var publication = new CancellationTokenSource();
        var store = new StatusDamageStore { Saved = publication.Cancel };
        var registry = DamageRegistry(mode, store);
        var character = Player(2560, GameDefaults.SpartaCamp);
        Join(registry, socket, character);
        var start = DateTimeOffset.UtcNow;
        try
        {
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10), start, "cancelled-publication", CancellationToken.None);
            try { await DamagePulse(registry, start.AddSeconds(2), publication.Token); }
            catch (OperationCanceledException) { }
            Check.True(character.CurrentHp == 9950 && store.LastHp == 9950 && !store.LastCancellationCanBeRequested,
                "publication cancellation cannot skip or refund committed HP checkpoint");
            store.Saved = null; store.FailCharacter = character.Id;
            var healthy = Player(2561, GameDefaults.SpartaCamp);
            Join(registry, other, healthy);
            await registry.ApplyRuntimeStatusAndPublishAsync(other.Session, Damage(10), start.AddSeconds(2), "independent-target", CancellationToken.None);
            await DamagePulse(registry, start.AddSeconds(4));
            Check.True(socket.Session.IsDisconnected && character.CurrentHp == 9900 && healthy.CurrentHp == 9950 && !other.Session.IsDisconnected,
                "failed checkpoint closes only its exact recipient while another character continues; no global loop failure or refund");
            store.FailCharacter = 0;
            var saves = store.Count;
            await DamagePulse(registry, start.AddSeconds(4));
            Check.True(character.CurrentHp == 9900 && store.Count == saves, "failed flush cannot cause duplicate pulse on a repeated deadline");
        }
        finally { registry.Remove(socket.Session); registry.Remove(other.Session); }
    }

    private static async Task CheckPeriodicDamageLateFailureAsync(PlayerRuntimeMode mode)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        var store = new StatusDamageStore();
        var registry = DamageRegistry(mode, store);
        var character = Player(2565, GameDefaults.SpartaCamp);
        Join(registry, socket, character);
        var start = DateTimeOffset.UtcNow;
        try
        {
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10), start, "old-checkpoint", CancellationToken.None);
            store.FailCharacter = character.Id;
            store.Failing = () =>
            {
                registry.Remove(socket.Session);
                character = Player(2565, GameDefaults.SpartaCamp);
                Join(registry, socket, character);
            };
            await DamagePulse(registry, start.AddSeconds(2));
            Check.True(!socket.Session.IsDisconnected && character.CurrentHp == 10000 && registry.GetPlayerLifeRevision(socket.Session) >= 0,
                "late checkpoint failure cannot disconnect or damage a replaced character lifetime");
        }
        finally { registry.Remove(socket.Session); }
    }

    private static async Task CheckPeriodicDamagePotionAsync(PlayerRuntimeMode mode, bool lethal)
    {
        await using var socket = await RuntimePolicySessionSocket.CreateAsync();
        var store = new StatusDamageStore();
        var registry = DamageRegistry(mode, store);
        var character = Player(lethal ? 2571 : 2570, GameDefaults.SpartaCamp);
        character.CurrentHp = lethal ? 40 : 100;
        var owner = PlayerOwnershipTestFences.ForCharacter(character.Id);
        character.CheckpointOwnerId = owner.OwnerId; character.CheckpointOwnerGeneration = owner.Generation;
        registry.ReplaceAccountSession(character.AccountId, socket.Session);
        Check.True(registry.TryBindAccountSessionOwnership(character.AccountId, socket.Session, owner), "potion fixture has real session ownership");
        Join(registry, socket, character);
        var start = DateTimeOffset.UtcNow;
        try
        {
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, Damage(10), start, "pending-potion-damage", CancellationToken.None);
            Check.True(BagConsumableEffectCatalog.TryResolve(3100, out var effect), "native HP potion resolves");
            Check.True(PendingPotionVitals.TryPrepare(character, 4000, effect, owner, out var pending, out _), "pending potion holds checkpoint authority");
            var pulse = DamagePulse(registry, start.AddSeconds(2));
            Check.True(!pulse.IsCompleted && store.Count == 0 && character.CurrentHp == (lethal ? 0 : 50),
                "damage commits but its flush waits for pending potion settlement without holding status authority");
            var evidence = new PotionUseEvidence(42, 4000, 0, 3100, pending!.Plan.AppliedAmount,
                pending.Plan.Before, pending.Plan.PersistedVitals, owner);
            Check.True(pending.TryResolve(true, evidence), "real potion evidence settles after intervening periodic loss");
            await pulse.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(character.CurrentHp == (lethal ? 0 : 250) && store.LastHp == character.CurrentHp,
                "settlement preserves damage, never resurrects, and flushes reconciled HP");
        }
        finally { character.PendingPotionUse?.AbandonUnresolved(); registry.Remove(socket.Session); }
    }

    private sealed class StatusDamageStore : GameStoreTestStub
    {
        public int Count; public int LastHp; public bool LastCancellationCanBeRequested; public int FailCharacter; public Action? Saved; public Action? Failing;
        public override Task SaveCharacterVitalsAsync(int accountId, int characterId, int currentHp, int currentMp,
            long vitalsRevision, CancellationToken cancellationToken = default)
        {
            Count++; LastCancellationCanBeRequested = cancellationToken.CanBeCanceled;
            if (FailCharacter == characterId) { Failing?.Invoke(); throw new IOException("owned checkpoint failure fixture"); }
            LastHp = currentHp; Saved?.Invoke(); return Task.CompletedTask;
        }
    }
}
