using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private sealed partial class Fixture : IAsyncDisposable
    {
        private ulong _eventId;
        public required GameSessionRegistry Registry { get; init; }
        public required WorldInstanceRuntime Runtime { get; init; }
        public required ClientSession Session { get; init; }
        public required FactionCrierCaptureTransport Transport { get; init; }
        public required GameCharacter Character { get; init; }
        public DateTimeOffset Now { get; set; }

        public static async Task<Fixture> CreateAsync(int island, MonsterRuntimeMode monsterMode,
            PlayerRuntimeMode playerMode, byte camp = GameDefaults.SpartaCamp)
        {
            var transport = new FactionCrierCaptureTransport();
            var session = new ClientSession(transport);
            var registry = new GameSessionRegistry(store: null, zodiacEnergyOptions: null, monsterMode, playerMode,
                gameplayCatalogs: GameplayRuntimeCatalogs.Create(WonderlandMapChecks.Content()));
            var created = await registry.CreateLocalWorldInstanceAsync(RealmId.Tempest, new(207), InstanceKind.Dungeon, 5);
            var runtime = created.Runtime ?? throw new InvalidOperationException("Wonderland combat runtime was not created.");
            var character = new GameCharacter
            {
                Id = 101, AccountId = 101, Name = "WonderlandCombat", Level = 130, CurrentMap = 207,
                CurrentHp = 10_000_000, MaxHp = 10_000_000, CurrentMp = 1000, MaxMp = 1000,
                Camp = camp, CalculatedStats = Stats()
            };
            var fence = GameHandlerOwnershipTestFences.Bind(registry, session, character.AccountId, character);
            registry.JoinWorldInstance(session, character.AccountId, character, WorldObjectIds.ForPlayer(character.Id),
                runtime.InstanceId, joinedAt: runtime.Descriptor.CreatedAt);
            var reservation = Guid.NewGuid();
            Check.True(registry.TryStartWonderlandEncounter(runtime.InstanceId, 3,
                [new(session, character.AccountId, character.Id, character.Name, character.Level,
                    character.Profession, RealmId.Tempest,
                    runtime.InstanceId, 207, fence)], runtime.Descriptor.CreatedAt, reservation),
                "combat fixture starts a real admitted Wonderland runtime");
            registry.RecordWonderlandAdmissions(reservation, [character.Id]);
            registry.CompleteWonderlandAdmissions(reservation);
            var now = WonderlandMapChecks.EnterIsland(runtime.Map, island, runtime.Descriptor.CreatedAt);
            var result = new Fixture { Registry = registry, Runtime = runtime, Session = session,
                Transport = transport, Character = character, Now = now };
            result.MoveTo(result.Monsters().First(monster => monster.IsAlive &&
                runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) &&
                policy.Stage == island && !policy.IsAllied));
            return result;
        }

        public IReadOnlyList<MonsterRuntimeSnapshot> Monsters() => Runtime.Map.SnapshotMonsters();

        public static CharacterStats Stats(int dodge = 0) => new()
        { PhysicalAttack = 10_000, MagicAttack = 10_000, Hit = 3000, Dodge = dodge, DamageReboundFlat = 60_879 };

        public MonsterRuntimeSnapshot Monster(string key, int? island = null)
        {
            Runtime.Map.TryGetWonderlandSnapshot(out var run);
            return Monsters().First(monster => monster.IsSpawned &&
                Runtime.Map.TryGetWonderlandSpawnPolicy(monster.ObjectId, out var policy) &&
                policy.Stage == (island ?? run.CurrentIsland) && policy.MechanicKey == key);
        }

        public void MoveTo(MonsterRuntimeSnapshot monster)
        {
            Character.PositionX = monster.X;
            Character.PositionZ = monster.Z;
            Registry.UpdateCharacter(Session, Character, advanceWorldRevision: false);
        }

        public async Task IncomingAsync(string key, bool hit = true, int? island = null)
        {
            var monster = Monster(key, island);
            await IncomingAsync(monster, hit);
        }

        public async Task IncomingAsync(MonsterRuntimeSnapshot monster, bool hit = true)
        {
            MoveTo(monster);
            var profile = Registry.AdjustPveMonsterAttackerProfile(Session, monster, Now,
                Registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(monster.Definition));
            ulong eventId;
            do
            {
                eventId = ++_eventId;
            } while (MonsterIncomingCombatPolicy.ResolveAttack(profile, Character, default, eventId).Hit != hit);
            var attack = new MonsterRuntimeUpdate(MonsterRuntimeUpdateKind.Attacked, monster,
                TargetCharacterId: Character.Id, TargetObjectId: WorldObjectIds.ForPlayer(Character.Id),
                TargetLifeRevision: Registry.GetPlayerLifeRevision(Session), TargetVitalsRevision: Character.VitalsRevision,
                AttackEventId: eventId);
            var task = typeof(GameSessionRegistry).GetMethod("ProcessMonsterAttackAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Registry,
                [Runtime, attack, CancellationToken.None, Now]) as Task;
            await task!;
            await FlushAsync();
        }

        public MonsterDamageResult DirectHit(string key, uint damage)
        {
            Check.True(TryDirectHit(key, damage, out var result),
                "direct combat commits the exact monster health revision");
            return result;
        }

        public bool TryDirectHit(string key, uint damage, out MonsterDamageResult result)
            => TryDirectHit(Monster(key), damage, out result);

        public bool TryDirectHit(MonsterRuntimeSnapshot monster, uint damage, out MonsterDamageResult result)
        {
            MoveTo(monster);
            Check.True(Registry.TryCapturePlayerMonsterTarget(Session, 207, monster.ObjectId,
                out var target, out var authority), "direct combat captures exact Wonderland target authority");
            var resolution = AuthoredCombatPveCurrent.ResolveBasicAttack(new CombatAttackerStats
                { Level = 130, PhysicalAttack = 10_000, Hit = 10_000 }, new CombatTargetStats { Level = 130 }, ++_eventId)
                with { Damage = damage, Outcome = CombatHitOutcome.Normal };
            var applied = Registry.TryCommitPlayerMonsterDamageGuarded(Session, 207, target.ObjectId,
                target.RuntimeInstanceId, Character.Id, target.SpawnGeneration, target.HealthRevision,
                authority, Now, resolution, out var commit) && commit.DamageResult is not null;
            result = commit.DamageResult!;
            return applied;
        }

        public Task FlushAsync() => Session.SendAsync(PacketBuilder.ServerNote("Wonderland combat test boundary"),
            CancellationToken.None, "WonderlandCombatTestBoundary");

        public string CastDiagnostic(string key)
        {
            var states = (System.Collections.IDictionary)typeof(GameSessionRegistry)
                .GetField("_wonderlandCombat", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Registry)!;
            var state = states[Runtime.InstanceId];
            if (state is null) return "no combat state";
            var casters = (System.Collections.IDictionary)state.GetType().GetProperty("Casters")!.GetValue(state)!;
            var monster = Monster(key);
            var caster = casters[monster.ObjectId];
            var pending = caster?.GetType().GetField("Pending")!.GetValue(caster);
            var due = pending?.GetType().GetProperty("DueAt")!.GetValue(pending);
            return $"stage={state.GetType().GetField("Stage")!.GetValue(state)} last={state.GetType().GetField("LastAdvancedAt")!.GetValue(state):O} due={due:O} " +
                $"spawned={monster.IsSpawned} alive={monster.IsAlive} disconnected={Session.IsDisconnected} pos={Character.PositionX},{Character.PositionZ} life={Registry.GetPlayerLifeRevision(Session)}";
        }

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
            await Registry.DisposeAsync();
        }
    }
}
