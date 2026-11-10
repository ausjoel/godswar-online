using System.Buffers.Binary;
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
    public const string CapturedAttacksCheckName =
        "Wonderland complete-capture native attacks, calibrated damage, and exact silence expiry";

    public static async Task RunCapturedAttacksAsync()
    {
        CheckCapturedAttackEvidence();
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckCapturedSilenceAttackAsync(monsters, players, 6, "platinum", 2022, 15153, 4213);
            await CheckCapturedSilenceAttackAsync(monsters, players, 8, "minotaur", 2803, 12353, 2213);
            await CheckRangedPetbirdPresentationAsync(monsters, players, 3, WonderlandMonsterRole.Petbird);
            await CheckRangedPetbirdPresentationAsync(monsters, players, 7, WonderlandMonsterRole.PutridBird);
        }
    }

    private static async Task CheckCapturedSilenceAttackAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players, int island, string key, uint nativeSkill, uint normalDamage, uint fallbackDamage)
    {
        var setup = await CreateBirdPresentationFixtureAsync(monsters, players);
        await using var f = setup.Fixture;
        await using var observer = setup.Observer;
        var viewer = setup.Viewer;
        var observerTransport = setup.ObserverTransport;
        try
        {
            f.Now = WonderlandMapChecks.EnterIsland(f.Runtime.Map, island, f.Now);
            var monster = f.Monster(key);
            f.MoveTo(monster);
            f.Character.Level = 143;
            f.Character.Profession = 3;
            f.Character.CalculatedStats = new CharacterStats { PhysicalDefense = 5379, MagicDefense = 2920,
                DamageAbsorb = 4791, PhysicalFlatAbsorption = 4791, MagicFlatAbsorption = 4791,
                CriticalResistance = 8 };
            var referenceTarget = MonsterIncomingCombatPolicy.ResolveTargetStats(f.Character, default);
            Check.True(referenceTarget.PhysicalFlatAbsorption == 4791 && referenceTarget.MagicFlatAbsorption == 4791,
                "captured native absorption populates both typed runtime mitigation channels");
            viewer.PositionX = monster.X + 18;
            viewer.PositionZ = monster.Z;
            f.Registry.UpdateCharacter(observer, viewer, advanceWorldRevision: false);
            await using (var visible = await f.Registry.BeginMonsterVisibilityTransitionAsync(observer,
                207, viewer.PositionX, viewer.PositionZ, CancellationToken.None)
                ?? throw new InvalidOperationException("Captured attack observer visibility unavailable."))
                visible.Commit();
            Check.True(f.Registry.IsMonsterVisibleTo(observer, monster.ObjectId),
                "the observer owns the actual source appearance before receiving effects");

            ulong eventId = 100_000;
            await EmitAndCheckAsync(monster, nativeSkill, normalDamage);
            Check.True(MonsterControlSkillPolicy.TryGet(600, out var silence) && silence.StatusId == 360,
                "native Silence600 supplies the captured status360 control family");
            Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, monster.ObjectId,
                    out var current, out var authority) &&
                f.Registry.TryCommitPlayerMonsterControl(f.Session, current, authority, silence, f.Now, out var result) &&
                result.Applied, "Silence is committed through real owner and monster generation authority");
            var expires = f.Now + silence.Duration;
            monster = f.Monster(key);
            Check.True(monster.ControlsAt(f.Now).HasFlag(HostileStatusControlFlags.NonMagicUsing) &&
                monster.ControlsAt(f.Now).HasFlag(HostileStatusControlFlags.NonTechniqueUsing),
                "the committed immutable source snapshot retains both native silence controls");
            f.Now = expires.AddTicks(-1);
            await EmitAndCheckAsync(monster, 2000, fallbackDamage);
            // Reuse the identical immutable snapshot at the boundary: expiry is
            // evaluated at HP commit time, not packet enqueue or wall-clock time.
            f.Now = expires;
            await EmitAndCheckAsync(monster, nativeSkill, normalDamage);

            async Task EmitAndCheckAsync(MonsterRuntimeSnapshot source, uint primary, uint expectedDamage)
            {
                Check.True(f.Runtime.Map.TryGetWonderlandSpawnPolicy(source.ObjectId, out var spawn),
                    "the source has a published captured policy");
                var baseProfile = f.Registry.AdjustPveMonsterAttackerProfile(f.Session, source, f.Now,
                    f.Registry.GameplayCatalogs.MonsterCombatProfiles.Resolve(source.Definition));
                var profile = WonderlandCapturedAttackPolicy.ApplyProfile(baseProfile, spawn, source.ControlsAt(f.Now));
                do { eventId++; }
                while (MonsterIncomingCombatPolicy.ResolveAttack(profile, f.Character, default, eventId).Outcome !=
                    CombatHitOutcome.Normal);
                Check.Equal(expectedDamage, MonsterIncomingCombatPolicy.ResolveAttack(profile, f.Character,
                    default, eventId).Damage, "the fully populated reference target predicts the exact captured hit");
                var attack = new MonsterRuntimeUpdate(MonsterRuntimeUpdateKind.Attacked, source,
                    TargetCharacterId: f.Character.Id, TargetObjectId: WorldObjectIds.ForPlayer(f.Character.Id),
                    TargetLifeRevision: f.Registry.GetPlayerLifeRevision(f.Session),
                    TargetVitalsRevision: f.Character.VitalsRevision, AttackEventId: eventId);
                var before = f.Character.CurrentHp;
                var beforeRevision = f.Character.VitalsRevision;
                var selfStart = f.Transport.ReadLegacyPackets().Count;
                var worldStart = observerTransport.ReadLegacyPackets().Count;
                await DispatchAsync();
                var self = CheckBirdVisualPrefix(f.Transport.ReadLegacyPackets().Skip(selfStart), source.ObjectId,
                    0x1448, f.Character.PositionX, f.Character.PositionZ, primary);
                var world = CheckBirdVisualPrefix(observerTransport.ReadLegacyPackets().Skip(worldStart), source.ObjectId,
                    WorldObjectIds.ForPlayer(f.Character.Id), f.Character.PositionX, f.Character.PositionZ, primary);
                Check.True(self == expectedDamage && world == expectedDamage && before - f.Character.CurrentHp == expectedDamage &&
                    f.Character.VitalsRevision == beforeRevision + 1,
                    $"{key}: exact captured pair and damage share one HP mutation for {monsters}/{players}; " +
                    $"expected={expectedDamage}, self={self}, world={world}, HPdelta={before - f.Character.CurrentHp}, " +
                    $"revisionDelta={f.Character.VitalsRevision - beforeRevision}");
                var damagePacket = f.Transport.ReadLegacyPackets().Skip(selfStart).Single(packet =>
                    packet.Length == 32 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10026);
                Check.True(damagePacket[29] == 1, "a normal native hit retains the captured damage-type byte");
                var committedHp = f.Character.CurrentHp;
                var committedSelf = f.Transport.ReadLegacyPackets().Count;
                var committedWorld = observerTransport.ReadLegacyPackets().Count;
                await DispatchAsync();
                Check.True(f.Character.CurrentHp == committedHp &&
                    !HasSourceCombat(f.Transport.ReadLegacyPackets().Skip(committedSelf), source.ObjectId) &&
                    !HasSourceCombat(observerTransport.ReadLegacyPackets().Skip(committedWorld), source.ObjectId),
                    "replaying an attack event neither repeats damage nor republishes native effects");

                async Task DispatchAsync()
                {
                    await (Task)typeof(GameSessionRegistry).GetMethod("ProcessMonsterAttackAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Registry,
                            [f.Runtime, attack, CancellationToken.None, f.Now])!;
                    await f.FlushAsync();
                    await observer.SendAsync(PacketBuilder.ServerNote("captured attack observer boundary"), CancellationToken.None);
                }
            }
        }
        finally { f.Registry.Remove(observer); }
    }

    private static bool HasSourceCombat(IEnumerable<byte[]> packets, uint source) => packets.Any(packet =>
        packet.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == source &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) is 10026 or 10046);
}
