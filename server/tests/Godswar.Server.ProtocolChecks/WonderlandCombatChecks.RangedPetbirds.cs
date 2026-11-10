using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckRangedPetbirdPresentationAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players, int island, WonderlandMonsterRole role)
    {
        var setup = await CreateBirdPresentationFixtureAsync(monsters, players);
        await using var f = setup.Fixture;
        await using var observer = setup.Observer;
        f.Character.Profession = 3;
        try
        {
            f.Now = WonderlandMapChecks.EnterIsland(f.Runtime.Map, island, f.Now);
            var birds = f.Monsters().Where(m => f.Runtime.Map.TryGetWonderlandSpawnPolicy(m.ObjectId, out var p) &&
                p.Role == role).DistinctBy(m => m.Definition.TemplateKey).ToArray();
            Check.True(birds.Length == (island == 3 ? 2 : 1), "each buff bird variant is exercised");
            ulong eventId = 200_000;
            foreach (var bird in birds)
            {
                Check.True(f.Runtime.Map.TryGetWonderlandSpawnPolicy(bird.ObjectId, out var policy),
                    "ranged Petbird belongs to the published island");
                Check.True(WonderlandCapturedAttackPolicy.PrimarySkillId(policy.TemplateKey) == (island == 3 ? 2782u : 2000u) &&
                    WonderlandCapturedAttackPolicy.ResolvePrimarySkill(policy, bird, f.Now) == 2015 &&
                    WonderlandCapturedAttackPolicy.NativeAreaRadius(policy, bird, f.Now) == 0 &&
                    WonderlandCapturedAttackPolicy.ResolvePrimarySkill(policy,
                        HostileStatusControlFlags.NonMagicUsing) == 2000,
                    "historical attack remains evidence; live Fireball2015 is single-target and respects silence");
                var offset = new (float X, float Z)[] { (11, 0), (-11, 0), (0, 11), (0, -11) }
                    .First(p => WonderlandTerrainPolicy.IsCombatArea(island, bird.X + p.X, bird.Z + p.Z));
                f.Character.PositionX = bird.X + offset.X;
                f.Character.PositionZ = bird.Z + offset.Z;
                f.Registry.UpdateCharacter(f.Session, f.Character, advanceWorldRevision: false);
                setup.Viewer.PositionX = f.Character.PositionX;
                setup.Viewer.PositionZ = f.Character.PositionZ;
                f.Registry.UpdateCharacter(observer, setup.Viewer, advanceWorldRevision: false);
                await using (var visible = await f.Registry.BeginMonsterVisibilityTransitionAsync(observer, 207,
                    setup.Viewer.PositionX, setup.Viewer.PositionZ, CancellationToken.None)
                    ?? throw new InvalidOperationException("Ranged Petbird observer visibility unavailable."))
                    visible.Commit();
                var profile = f.Registry.AdjustPveMonsterAttackerProfile(f.Session, bird, f.Now,
                    f.Registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(bird.Definition));
                profile = WonderlandCapturedAttackPolicy.ApplyProfile(profile, policy, bird.ControlsAt(f.Now));
                Check.True(profile.UsesMagicDamage && profile.MagicAttack == 4000 && profile.AuthoredAttackRange == 12,
                    "ranged buff bird preserves its4000 magic attack with12-unit combat reach");
                do { eventId++; }
                while (MonsterIncomingCombatPolicy.ResolveAttack(profile, f.Character, default, eventId).Outcome !=
                    CombatHitOutcome.Normal);
                var before = f.Character.CurrentHp;
                var observerBefore = setup.Viewer.CurrentHp;
                var selfStart = f.Transport.ReadLegacyPackets().Count;
                var worldStart = setup.ObserverTransport.ReadLegacyPackets().Count;
                var attack = new MonsterRuntimeUpdate(MonsterRuntimeUpdateKind.Attacked, bird,
                    TargetCharacterId: f.Character.Id, TargetObjectId: WorldObjectIds.ForPlayer(f.Character.Id),
                    TargetLifeRevision: f.Registry.GetPlayerLifeRevision(f.Session),
                    TargetVitalsRevision: f.Character.VitalsRevision, AttackEventId: eventId);
                await (Task)typeof(GameSessionRegistry).GetMethod("ProcessMonsterAttackAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Registry,
                    [f.Runtime, attack, CancellationToken.None, f.Now])!;
                await f.FlushAsync();
                await observer.SendAsync(PacketBuilder.ServerNote("ranged Petbird observer boundary"), CancellationToken.None);
                var selfDamage = CheckBirdVisualPrefix(f.Transport.ReadLegacyPackets().Skip(selfStart), bird.ObjectId,
                    0x1448, f.Character.PositionX, f.Character.PositionZ, 2015);
                var worldDamage = CheckBirdVisualPrefix(setup.ObserverTransport.ReadLegacyPackets().Skip(worldStart),
                    bird.ObjectId, WorldObjectIds.ForPlayer(f.Character.Id), f.Character.PositionX, f.Character.PositionZ, 2015);
                var status = f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now);
                Check.True(selfDamage == 4000 && worldDamage == selfDamage && before - f.Character.CurrentHp == selfDamage &&
                    setup.Viewer.CurrentHp == observerBefore &&
                    (island == 3 ? status.AttackMultiplier == 5 : status.Hit == 25000),
                    $"{monsters}/{players}: Fireball at11 units publishes once to both views, damages only its target, " +
                    "and retains the island's bird blessing");

                // The old island7 attack was a plain melee hit. Its new spell
                // must obey interruptions even when an earlier hit is queued.
                await using (var visible = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
                    f.Character.PositionX, f.Character.PositionZ, CancellationToken.None)
                    ?? throw new InvalidOperationException("Buff bird target visibility unavailable."))
                    visible.Commit();
                Check.True(MonsterControlSkillPolicy.TryGet(600, out var silence) &&
                    f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, bird.ObjectId,
                        out var current, out var authority) &&
                    f.Registry.TryCommitPlayerMonsterControl(f.Session, current, authority,
                        silence, f.Now, out var control) && control.Applied,
                    "buff bird can be silenced through current monster authority");
                var stale = attack with { AttackEventId = ++eventId,
                    TargetVitalsRevision = f.Character.VitalsRevision };
                before = f.Character.CurrentHp;
                selfStart = f.Transport.ReadLegacyPackets().Count;
                await (Task)typeof(GameSessionRegistry).GetMethod("ProcessMonsterAttackAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Registry,
                    [f.Runtime, stale, CancellationToken.None, f.Now])!;
                await f.FlushAsync();
                Check.True(f.Character.CurrentHp == before &&
                    !HasSourceCombat(f.Transport.ReadLegacyPackets().Skip(selfStart), bird.ObjectId),
                    "a Fireball queued before silence cannot damage or publish its stale spell");
            }
        }
        finally { f.Registry.Remove(observer); }
    }
}
