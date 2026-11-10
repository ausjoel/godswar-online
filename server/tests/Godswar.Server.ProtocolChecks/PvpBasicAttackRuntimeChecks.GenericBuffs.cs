// ROUND3: generic buffs must affect authoritative runtime state and remain fenced on removal.
using Godswar.Server.Game;
using System.Reflection;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    public const string GenericBuffCheckName = "Generic audited buffs compose stats priority expiry and operator-only removal";

    public static async Task RunGenericBuffAsync()
    {
        foreach (var mode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var socket = await RuntimePolicySessionSocket.CreateAsync();
            var registry = Registry(mode);
            var character = Player(1450, GameDefaults.SpartaCamp);
            Join(registry, socket, character);
            var now = DateTimeOffset.UtcNow;
            // ROUND3 (rig f2): no administrative status of any kind lands on a dead character.
            int livingHp;
            lock (character.VitalsSync) { livingHp = character.CurrentHp; character.CurrentHp = 0; }
            foreach (var deadId in new[] { 500, 130, 618, 10 })
            {
                Check.True(GenericStatusCatalog.TryResolve(deadId, out var deadStatus, out _), "dead-target probe status resolves");
                Check.True(!await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, deadStatus with { OperatorApplied = true },
                    now, "editor-buff", CancellationToken.None), $"operator status {deadId} is refused on a dead character");
            }
            Check.Equal(0, registry.GetRuntimeStatusAggregate(socket.Session, now).PhysicalAttack, "refused dead-target status leaves no effect");
            lock (character.VitalsSync) character.CurrentHp = livingHp;
            Check.True(GenericStatusCatalog.TryResolve(618, out var strong, out _), "strong attack status exists");
            Check.True(GenericStatusCatalog.TryResolve(614, out var weak, out _), "weak attack status exists");
            Check.True(await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session,
                strong with { OperatorApplied = true }, now, "editor-buff", CancellationToken.None), "generic status applies");
            var aggregate = registry.GetRuntimeStatusAggregate(socket.Session, now);
            Check.Equal(500, aggregate.PhysicalAttack, "physical attack is authoritative");
            Check.Equal(400, aggregate.MagicAttack, "magic attack is authoritative");
            Check.Equal(160, aggregate.Hit, "third effect is not lost");
            Check.True(!await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, weak, now,
                "weaker", CancellationToken.None), "stronger rank blocks weaker same-kind status");
            var inspected = await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now, CancellationToken.None);
            var token = ReadToken(inspected, strong.StatusId);
            Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now,
                CancellationToken.None))["removed"]!.GetValue<bool>(), "operator-marked generic status removes with exact token");
            Check.Equal(0, registry.GetRuntimeStatusAggregate(socket.Session, now).PhysicalAttack, "removal reverses stats");
            Check.True(GenericStatusCatalog.TryResolve(100, out var penalty, out _), "defense penalty resolves");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, penalty with { OperatorApplied = true },
                now, "editor-buff", CancellationToken.None);
            aggregate = registry.GetRuntimeStatusAggregate(socket.Session, now);
            Check.Equal(-30, aggregate.PhysicalDefense, "negative physical defense applies");
            Check.Equal(-20, aggregate.MagicDefense, "negative magic defense applies");
            Check.True(registry.TryGetRuntimeIncomingDamageMitigation(socket.Session, now, out var mitigation) &&
                mitigation.PhysicalDefenseBonus == -30 && mitigation.MagicDefenseBonus == -20,
                "incoming combat consumes both signed defense values");
            token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now,
                CancellationToken.None), penalty.StatusId);
            Check.True((await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now,
                CancellationToken.None))["removed"]!.GetValue<bool>(), "operator-applied debuff is removable");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, penalty, now, "native-source", CancellationToken.None);
            token = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now,
                CancellationToken.None), penalty.StatusId);
            Check.True(!(await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, token, now,
                CancellationToken.None))["ok"]!.GetValue<bool>(), "same native status identity does not grant editor removal authority");
            Check.Equal(0, registry.GetRuntimeStatusAggregate(socket.Session, now.AddSeconds(21)).PhysicalDefense,
                "expired penalty no longer modifies gameplay");
            now = now.AddSeconds(22); // Keep the ECS clock monotonic after probing expiry.
            Check.True(GenericStatusCatalog.TryResolve(500, out var exp, out _), "EXP status resolves");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, exp with { OperatorApplied = true },
                now, "editor-buff", CancellationToken.None);
            var rewards = await registry.GetRewardExperienceBoostStateAsync(socket.Session, character.AccountId,
                character.Id, character.Camp, character.CurrentMap, now, CancellationToken.None);
            Check.Equal(125, rewards.ApplyTo(100), "real registry reward reader includes runtime fighter EXP");
            Check.Equal(125, rewards.ApplyToPet(100), "real registry reward reader includes runtime pet EXP");
            Check.Equal(100, rewards.ApplyToTalent(100), "unaffected talent channel stays unchanged");

            var interruptions = 0;
            registry.RegisterSkillCastInterruptionSink(socket.Session, (reason, token, barrier) =>
            { interruptions++; return barrier ?? Task.CompletedTask; });
            Check.True(GenericStatusCatalog.TryResolve(330, out var stun, out _), "stun resolves all control flags");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, stun with { OperatorApplied = true },
                now, "editor-buff", CancellationToken.None);
            Check.Equal(1, interruptions, "control application claims an in-progress cast interruption once");
            Check.True(!registry.IsPlayerStatusMovementAllowed(socket.Session, now), "stun blocks native movement");
            Check.True(registry.GetPlayerSkillCastControl(socket.Session, now) == PlayerSkillCastControl.Stunned,
                "stun blocks authoritative skill casts");
            var handler = new GameClientHandler(socket.Session, new GenericBuffStore(), registry,
                CharacterSnapshotReaderTestFixtures.Unused, WorldContentReaderTestFixtures.Empty);
            typeof(GameClientHandler).GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(handler, character);
            bool Allowed(string name) => (bool)typeof(GameClientHandler)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler, [now])!;
            Check.True(!Allowed("IsHostileStatusBasicAttackAllowed"), "handler rejects basic attack under NonAttackUsing");
            Check.True(!Allowed("IsHostileStatusItemUseAllowed"), "handler rejects item use under NonItemUsing");
            Check.True(!Allowed("IsHostileStatusMovementAllowed"), "non-walk and realtime movement gates see the control");
            var stunToken = ReadToken(await registry.InspectRuntimeBuffsForEditorAsync(socket.Session, now,
                CancellationToken.None), stun.StatusId);
            await registry.RemoveRuntimeBuffForEditorAsync(socket.Session, stunToken, now, CancellationToken.None);
            Check.True(Allowed("IsHostileStatusBasicAttackAllowed") && Allowed("IsHostileStatusItemUseAllowed"),
                "exact operator removal restores action gates");
            Check.True(GenericStatusCatalog.TryResolve(300, out var frozen, out _), "Frozen resolves");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, frozen with { OperatorApplied = true },
                now, "editor-buff", CancellationToken.None);
            Check.Equal(2, interruptions, "Frozen interrupts once on application");
            Check.True(!registry.IsPlayerStatusMovementAllowed(socket.Session, now), "Frozen blocks movement");
            Check.True(registry.GetPlayerSkillCastControl(socket.Session, now) == PlayerSkillCastControl.None,
                "HaltIntonate does not become a continuous spell lock");
            Check.True(Allowed("IsHostileStatusBasicAttackAllowed") && Allowed("IsHostileStatusItemUseAllowed"),
                "Frozen does not invent attack or item restrictions");
            Check.True(registry.GetRuntimeControlFlags(socket.Session, now.AddSeconds(3)) == HostileStatusControlFlags.None,
                "expired control immediately stops gating actions");
            registry.UnregisterSkillCastInterruptionSink(socket.Session);
            now = now.AddSeconds(4);
            lock (character.VitalsSync)
            {
                character.CurrentHp = 100; character.CurrentMp = 10; character.MarkVitalsChanged();
            }
            Check.True(GenericStatusCatalog.TryResolve(520, out var regeneration, out _), "HP recovery resolves");
            Check.True(GenericStatusCatalog.TryResolve(510, out var mana, out _), "MP recovery resolves");
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session,
                regeneration with { Duration = TimeSpan.FromSeconds(3), OperatorApplied = true },
                now, "editor-buff", CancellationToken.None);
            await registry.ApplyRuntimeStatusAndPublishAsync(socket.Session, mana with { OperatorApplied = true },
                now, "editor-buff", CancellationToken.None);
            var pulse = typeof(GameSessionRegistry).GetMethod("AdvanceStatusPeriodicRecoveryOnceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Recover(DateTimeOffset time) => await (Task)pulse.Invoke(registry, [time, CancellationToken.None])!;
            await Recover(now.AddSeconds(1));
            Check.Equal(600, character.CurrentHp, "one recovery pulse applies the authored 500 HP");
            Check.Equal(15, character.CurrentMp, "the independent mana effect applies its authored 5 MP");
            await Recover(now.AddSeconds(1));
            Check.Equal(600, character.CurrentHp, "the same pulse deadline cannot heal twice");
            await Recover(now.AddSeconds(4));
            Check.Equal(600, character.CurrentHp, "expired recovery does not heal again");
            Check.Equal(20, character.CurrentMp, "another unexpired recovery source keeps ticking");
        }
    }
    private sealed class GenericBuffStore : GameStoreTestStub { }
}
