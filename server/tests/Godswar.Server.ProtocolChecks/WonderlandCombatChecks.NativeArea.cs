using System.Reflection;
using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string NativeAreaCheckName = "Wonderland native area attacks preserve participant radius, silence, lethal primary and replay fences";

    public static async Task RunNativeAreaAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        foreach (var scenario in new[] { "near", "far", "silenced", "lethal-primary", "newer-tick",
                     "moved-after-primary", "live-zero-id", "expired-after-primary" })
        {
            var setup = await CreateBirdPresentationFixtureAsync(monsters, players);
            await using var f = setup.Fixture;
            await using var observer = setup.Observer;
            var viewer = setup.Viewer;
            try
            {
                f.Now = WonderlandMapChecks.EnterIsland(f.Runtime.Map, 6, f.Now);
                var source = f.Monster("platinum");
                f.MoveTo(source);
                f.Character.Profession = 3;
                f.Character.CalculatedStats = new CharacterStats { CriticalResistance = 100000 };
                viewer.CalculatedStats = new CharacterStats { CriticalResistance = 100000 };
                viewer.PositionX = source.X + (scenario == "far" ? 7 : 2);
                viewer.PositionZ = source.Z;
                f.Registry.UpdateCharacter(observer, viewer, advanceWorldRevision: false);
                await using (var visibility = await f.Registry.BeginMonsterVisibilityTransitionAsync(observer,
                    207, viewer.PositionX, viewer.PositionZ, CancellationToken.None)
                    ?? throw new InvalidOperationException("Area observer visibility unavailable.")) visibility.Commit();
                if (scenario == "silenced")
                {
                    Check.True(MonsterControlSkillPolicy.TryGet(600, out var silence) &&
                        f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, source.ObjectId, out var captured, out var authority) &&
                        f.Registry.TryCommitPlayerMonsterControl(f.Session, captured, authority, silence, f.Now, out var result) &&
                        result.Applied, "area suppression uses a real committed silence");
                    source = f.Monster("platinum");
                }
                if (scenario == "lethal-primary") f.Character.CurrentHp = 1;
                Check.True(f.Runtime.Map.TryGetWonderlandSpawnPolicy(source.ObjectId, out var spawn), "area source is published");
                var profile = WonderlandCapturedAttackPolicy.ApplyProfile(
                    f.Registry.AdjustPveMonsterAttackerProfile(f.Session, source, f.Now,
                        f.Registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(source.Definition)), spawn, source.ControlsAt(f.Now));
                ulong eventId = 300000;
                while (MonsterIncomingCombatPolicy.ResolveAttack(profile, f.Character, default, ++eventId).Outcome != CombatHitOutcome.Normal) { }
                var attack = new MonsterRuntimeUpdate(MonsterRuntimeUpdateKind.Attacked, source,
                    TargetCharacterId: f.Character.Id, TargetObjectId: WorldObjectIds.ForPlayer(f.Character.Id),
                    TargetLifeRevision: f.Registry.GetPlayerLifeRevision(f.Session),
                    TargetVitalsRevision: f.Character.VitalsRevision, AttackEventId: scenario == "live-zero-id" ? 0 : eventId);
                var observerBefore = viewer.CurrentHp;
                var observerRevision = viewer.VitalsRevision;
                var packetStart = setup.ObserverTransport.ReadLegacyPackets().Count;
                if (scenario == "newer-tick")
                    f.Registry.WonderlandNativeAreaDrainingHook = () => f.Runtime.Map.AdvanceWonderland(f.Now.AddMilliseconds(500));
                if (scenario == "expired-after-primary")
                    f.Registry.WonderlandNativeAreaDrainingHook = () => f.Runtime.Map.AdvanceWonderland(f.Now.AddMinutes(41));
                if (scenario == "moved-after-primary")
                    f.Registry.WonderlandNativeAreaDrainingHook = () =>
                    {
                        viewer.PositionX = source.X + 7;
                        f.Registry.UpdateCharacter(observer, viewer, advanceWorldRevision: false);
                    };
                await DispatchAsync();
                await f.FlushAsync();
                await observer.SendAsync(PacketBuilder.ServerNote("native area boundary"), CancellationToken.None);
                var ownHits = setup.ObserverTransport.ReadLegacyPackets().Skip(packetStart).Where(packet =>
                    packet.Length == 32 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10026 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)) == 0x1448).ToArray();
                Check.True(ownHits.Length == (scenario is "near" or "lethal-primary" or "newer-tick" or "live-zero-id" ? 1 : 0),
                    $"{monsters}/{players}/{scenario}: one secondary attack only inside the unsilenced native radius");
                var damage = ownHits.Length == 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(ownHits[0].AsSpan(24));
                if (damage == uint.MaxValue) damage = 0;
                Check.True(observerBefore - viewer.CurrentHp == damage &&
                    viewer.VitalsRevision == observerRevision + (damage > 0 ? 1 : 0),
                    "the secondary's native result matches exactly one HP commit, including a miss");
                if (scenario is "far" or "silenced")
                    Check.True(viewer.CurrentHp == observerBefore, "outside or silenced area causes no secondary damage");
                if (scenario == "lethal-primary") Check.True(f.Character.CurrentHp == 0, "a lethal primary still commits its area batch");
                var primaryAfter = f.Character.CurrentHp;
                var observerAfter = viewer.CurrentHp;
                var observerAfterRevision = viewer.VitalsRevision;
                try { await DispatchAsync(); }
                catch (Exception error) when (error.GetType().Name == "MonsterAttackTargetUnavailableException") { }
                Check.True(f.Character.CurrentHp == primaryAfter && viewer.CurrentHp == observerAfter &&
                    viewer.VitalsRevision == observerAfterRevision, "a duplicate primary cannot replay or allocate secondary hits");

                async Task DispatchAsync() => await (Task)typeof(GameSessionRegistry).GetMethod("ProcessMonsterAttackAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Registry,
                        [f.Runtime, attack, CancellationToken.None, f.Now])!;
            }
            finally { f.Registry.WonderlandNativeAreaDrainingHook = null; f.Registry.Remove(observer); }
        }
    }
}
