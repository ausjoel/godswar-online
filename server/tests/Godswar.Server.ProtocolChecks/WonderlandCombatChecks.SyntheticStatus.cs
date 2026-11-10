using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private sealed partial class Fixture
    {
        public void ApplySyntheticAlliedStun(uint objectId)
        {
            Runtime.Owner.Invoke(map =>
            {
                var wrapper = map.InitializeMonsters([], Now);
                var owners = (IDictionary)wrapper.GetType().GetField("_owners",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wrapper)!;
                var entry = ((WonderlandSpawnPolicy Policy, IMonsterMapRuntime Runtime))owners[objectId]!;
                Check.True(entry.Policy.IsAllied, "synthetic NPC control is scoped to the protected allied actor");
                MonsterControlSkillPolicy.TryGet(74, out var stun);
                var actor = entry.Runtime.Snapshot().Single();
                Check.True(entry.Runtime.TryApplyControl(objectId, Character.Id, stun, actor.SpawnGeneration,
                    Now, out var result) && result.Applied, "the underlying real NPC runtime accepts a test control");
                return true;
            }, TimeSpan.FromSeconds(3));
        }

        // Tests of the encounter status compositor still need a deterministic
        // stun source. The captured island-two attacks do not author this effect.
        // Seed only its existing state, then exercise real controls/publication,
        // expiry, life fences and movement handlers without adding a live proc.
        public async Task ApplySyntheticEncounterStunAsync()
        {
            await Registry.AdvanceWonderlandCombatAsync(Runtime, Now, CancellationToken.None);
            var context = Runtime.Map.Snapshot().Single(member => member.Session == Session);
            var registryType = typeof(GameSessionRegistry);
            var gate = registryType.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registry)!;
            lock (gate)
            {
                var states = (IDictionary)registryType.GetField("_wonderlandCombat",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registry)!;
                var state = states[Runtime.InstanceId]!;
                var players = (IDictionary)state.GetType().GetProperty("Players")!.GetValue(state)!;
                var effectType = registryType.GetNestedType("WonderlandPlayerEffects", BindingFlags.NonPublic)!;
                var effects = players[Session] ?? Activator.CreateInstance(effectType,
                    [context, Registry.GetPlayerLifeRevision(Session)])!;
                effectType.GetField("StunUntil")!.SetValue(effects, Now.AddSeconds(2));
                players[Session] = effects;
                ((ConcurrentDictionary<ClientSession, byte>)registryType.GetField("_wonderlandStatusSessions",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registry)!).TryAdd(Session, 0);
            }
            await Registry.RequestSkillCastInterruptionAsync(Session, SkillCastInterruptionReason.Stunned,
                CancellationToken.None);
        }
    }
}
