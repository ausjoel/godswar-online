using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckWonderlandActionGatesAsync(
        MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using (var f = await Fixture.CreateAsync(2, monsters, players))
        {
            var handler = CreateControlHandler(f);
            await f.ApplySyntheticEncounterStunAsync();
            await CheckWonderlandStunInputBurstAsync(f, handler);
            AssertActionGates(handler, f.Now, PlayerSkillCastControl.Stunned,
                movement: false, basicAttack: false, item: false);
            AssertActionGates(handler, f.Now.AddSeconds(2), PlayerSkillCastControl.None,
                movement: true, basicAttack: true, item: true);
        }

        await using (var f = await Fixture.CreateAsync(4, monsters, players))
        {
            var handler = CreateControlHandler(f);
            var normalMonsters = f.Monsters().Where(monster =>
                f.Runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) &&
                policy.Stage == 4 && !policy.IsBoss).ToArray();
            Check.True(normalMonsters.Length > 0, "every captured island-four normal monster is covered");
            foreach (var monster in normalMonsters)
            {
                f.Registry.ClearWonderlandPlayerEffects(f.Session);
                await f.IncomingAsync(monster);
                AssertActionGates(handler, f.Now, PlayerSkillCastControl.Silenced,
                    movement: true, basicAttack: true, item: true);
            }
            f.Registry.ClearWonderlandPlayerEffects(f.Session);
            await f.IncomingAsync("rock");
            AssertActionGates(handler, f.Now, PlayerSkillCastControl.None,
                movement: true, basicAttack: true, item: true);

            await f.IncomingAsync(normalMonsters[0]);
            Check.True(await f.Registry.ApplyRuntimeStatusAndPublishAsync(f.Session,
                new SkillStatusEffectDefinition(0, 330, 330, 1, false,
                    TimeSpan.FromMinutes(1), TimeSpan.Zero, 0, 0), f.Now,
                "ordinary-stun-over-wonderland-silence", CancellationToken.None),
                "ordinary stun applies while Wonderland silence is active");
            Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) == PlayerSkillCastControl.Stunned,
                "Wonderland silence cannot hide a stronger ordinary stun");
            Check.True(!f.Registry.IsPlayerStatusMovementAllowed(f.Session, f.Now),
                "combined ordinary stun and Wonderland silence blocks movement");
        }
    }

    private static GameClientHandler CreateControlHandler(Fixture f)
    {
        var handler = new GameClientHandler(f.Session, new ControlGateStore(), f.Registry,
            CharacterSnapshotReaderTestFixtures.Unused, WorldContentReaderTestFixtures.Empty);
        Set("_account", new AccountIdentity(f.Character.AccountId, "wonderland-controls"));
        Set("_character", f.Character);
        Set("_registered", true);
        Set("_worldPresenceAnnounced", true);
        return handler;

        void Set(string field, object value) => typeof(GameClientHandler)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, value);
    }

    private static void AssertActionGates(GameClientHandler handler, DateTimeOffset now,
        PlayerSkillCastControl skill, bool movement, bool basicAttack, bool item)
    {
        // Legacy movement and realtime ingress share the hostile movement gate;
        // both new casts and pending completions share this skill resolver.
        Check.Equal(movement, Invoke<bool>("IsHostileStatusMovementAllowed", now),
            "native movement ingress obeys the active Wonderland control");
        Check.Equal(movement, Invoke<bool>("IsElementalMovementAllowed", now),
            "legacy walk ingress obeys the active Wonderland control");
        Check.True(skill == Invoke<PlayerSkillCastControl>("ResolvePlayerSkillCastControl", now, false),
            "new skill casts obey the active Wonderland control");
        Check.True(skill == Invoke<PlayerSkillCastControl>("ResolvePlayerSkillCastControl", now, true),
            "pending skill completion obeys the active Wonderland control");
        Check.Equal(basicAttack, Invoke<bool>("IsHostileStatusBasicAttackAllowed", now),
            "basic attacks stop under stun and remain available under silence");
        Check.Equal(item, Invoke<bool>("IsHostileStatusItemUseAllowed", now),
            "item use matches the native control definition");

        T Invoke<T>(string name, params object[] args) => (T)typeof(GameClientHandler)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler, args)!;
    }

    private sealed class ControlGateStore : GameStoreTestStub;
}
