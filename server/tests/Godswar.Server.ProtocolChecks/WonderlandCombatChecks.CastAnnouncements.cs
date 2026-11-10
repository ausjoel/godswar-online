using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string CastAnnouncementCheckName =
        "Wonderland hostile boss windups use repeated red centered notices without modal dialogues";

    public static async Task RunCastAnnouncementsAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckBossWindupAnnouncementsAsync(monsters, players);
            await CheckBossWarningRecipientsAsync(monsters, players);
            await CheckQuietSupportWarningsAsync(monsters, players);
        }
    }

    private static async Task CheckBossWindupAnnouncementsAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        foreach (var (key, text) in new[]
        {
            ("rooster", "Flame Rooster is casting Fire Blast in 1.2s!")
        })
        {
            await using var f = await Fixture.CreateAsync(3, monsters, players);
            var source = f.Monster(key);
            var ability = WonderlandBossAbilityPolicy.For(key).Single();
            var dueAt = f.Now + ability.Windup;
            f.MoveTo(source);
            var before = f.Transport.ReadLegacyPackets().Count;
            var hp = f.Character.CurrentHp;
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
            var packets = f.Transport.ReadLegacyPackets().Skip(before).ToArray();
            Check.True(packets.Count(IsRedBossNotice) == 1 &&
                packets.Single(IsRedBossNotice).SequenceEqual(PacketBuilder.CenteredRedAnnouncement(text)),
                "the retained Rooster skill names the actual boss and its 1.2-second windup in red");
            Check.True(packets.All(packet => ReadWarningOpcode(packet) != Opcodes.ServerNote),
                "scheduling a boss cast never opens the ServerNote dialogue");
            Check.True(packets.Any(packet => IsCastWarning(packet) &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == source.ObjectId),
                "the original native cast visual is retained alongside the centered warning");
            Check.Equal(hp, f.Character.CurrentHp, "the announcement cannot inflict immediate damage");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, dueAt.AddTicks(-1), CancellationToken.None);
            Check.Equal(hp, f.Character.CurrentHp, "the complete authored reaction window remains intact");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, dueAt, CancellationToken.None);
            await f.Session.SendAsync(PacketBuilder.CenteredAnnouncement("Wonderland cast test boundary"), CancellationToken.None);
            Check.True(f.Transport.ReadLegacyPackets().Skip(before).Any(IsDamagePacket),
                $"the actual scheduled hit or miss still publishes at its exact windup boundary; {f.CastDiagnostic(key)}");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now + ability.Cooldown - TimeSpan.FromTicks(1), CancellationToken.None);
            Check.Equal(1, f.Transport.ReadLegacyPackets().Skip(before).Count(IsRedBossNotice),
                "a pending cast and cooldown ticks cannot repeat the announcement");
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now + ability.Cooldown, CancellationToken.None);
            Check.Equal(2, f.Transport.ReadLegacyPackets().Skip(before).Count(IsRedBossNotice),
                "the next real cast warns again instead of becoming a once-per-session tutorial");
            Check.True(f.Transport.ReadLegacyPackets().Skip(before)
                .All(packet => ReadWarningOpcode(packet) != Opcodes.ServerNote),
                "neither recurring cast nor impact introduces a modal skill notice");
        }
    }

    private static async Task CheckBossWarningRecipientsAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        var setup = await CreateBirdPresentationFixtureAsync(monsters, players);
        await using var f = setup.Fixture;
        await using var observer = setup.Observer;
        f.Now = WonderlandMapChecks.EnterIsland(f.Runtime.Map, 3, f.Now);
        var boss = f.Monster("rooster");
        f.MoveTo(boss);
        setup.Viewer.PositionX = boss.X;
        setup.Viewer.PositionZ = boss.Z;
        f.Registry.UpdateCharacter(observer, setup.Viewer, advanceWorldRevision: false);
        var unrelatedTransport = new FactionCrierCaptureTransport();
        await using var unrelated = new ClientSession(unrelatedTransport);
        var outsider = new GameCharacter { Id = 103, AccountId = 103, Name = "NotAdmitted", Level = 130,
            CurrentMap = 207, PositionX = boss.X, PositionZ = boss.Z, CurrentHp = 100000, MaxHp = 100000 };
        GameHandlerOwnershipTestFences.Bind(f.Registry, unrelated, outsider.AccountId, outsider);
        Check.Throws<InvalidOperationException>(() => f.Registry.JoinWorldInstance(unrelated,
            outsider.AccountId, outsider, WorldObjectIds.ForPlayer(outsider.Id), f.Runtime.InstanceId),
            "an unadmitted character is rejected before joining the encounter or receiving its warnings");
        try
        {
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now, CancellationToken.None);
            Check.Equal(1, setup.ObserverTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "a living admitted party member on the casting island receives the warning");
            Check.Equal(0, unrelatedTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "an unadmitted character cannot receive encounter warnings from matching map membership");

            var elsewhere = await f.Registry.CreateLocalWorldInstanceAsync(RealmId.Tempest, new(207), InstanceKind.Dungeon, 2);
            f.Registry.JoinWorldInstance(observer, setup.Viewer.AccountId, setup.Viewer,
                WorldObjectIds.ForPlayer(setup.Viewer.Id), elsewhere.Runtime!.InstanceId);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(10), CancellationToken.None);
            Check.Equal(1, setup.ObserverTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "the same player and map in another exact instance receive no stale warning");

            f.Registry.JoinWorldInstance(observer, setup.Viewer.AccountId, setup.Viewer,
                WorldObjectIds.ForPlayer(setup.Viewer.Id), f.Runtime.InstanceId);
            setup.Viewer.CurrentHp = 0;
            f.Registry.AdvancePlayerLifeRevision(observer, f.Now.AddSeconds(20));
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(20), CancellationToken.None);
            Check.Equal(1, setup.ObserverTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "a dead party member does not receive the next cast's warning");

            setup.Viewer.CurrentHp = setup.Viewer.MaxHp;
            f.Registry.AdvancePlayerLifeRevision(observer, f.Now.AddSeconds(30));
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(30), CancellationToken.None);
            Check.Equal(2, setup.ObserverTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "a returned living member receives a fresh warning under the current life");

            var replacementTransport = new FactionCrierCaptureTransport();
            await using var replacement = new ClientSession(replacementTransport);
            GameHandlerOwnershipTestFences.Bind(f.Registry, replacement, setup.Viewer.AccountId, setup.Viewer);
            await f.Registry.AdvanceWonderlandCombatAsync(f.Runtime, f.Now.AddSeconds(40), CancellationToken.None);
            Check.Equal(2, setup.ObserverTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "account replacement rejects late warnings to the abandoned session");
            Check.Equal(0, replacementTransport.ReadLegacyPackets().Count(IsRedBossNotice),
                "the old encounter's warning is not redirected to its replacement session");
            f.Registry.Remove(replacement);
        }
        finally
        {
            f.Registry.Remove(unrelated);
            f.Registry.Remove(observer);
        }
    }

    private static async Task CheckQuietSupportWarningsAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using (var terrain = await Fixture.CreateAsync(6, monsters, players))
        {
            terrain.Runtime.Map.TryGetWonderlandSnapshot(out var run);
            var away = run.Geometry.GroundFirePositions.First(point =>
                WonderlandTerrainPolicy.DistanceSquared(run.ActiveSpawns[0].Position, point.X, point.Z) > 24 * 24);
            MovePlayer(terrain, away.X, away.Z);
            var before = terrain.Transport.ReadLegacyPackets().Count;
            await terrain.Registry.AdvanceWonderlandCombatAsync(terrain.Runtime, terrain.Now, CancellationToken.None);
            var packets = terrain.Transport.ReadLegacyPackets().Skip(before).ToArray();
            Check.Equal(3, packets.Count(IsCastWarning), "all three terrain-fire telegraphs remain visible");
            Check.True(packets.All(packet => !IsRedBossNotice(packet) && ReadWarningOpcode(packet) != Opcodes.ServerNote),
                "frequent terrain fire does not fill the centered queue or open a dialogue");
        }
        await using var atlas = await Fixture.CreateAsync(8, monsters, players);
        var source = atlas.Monster("atlas");
        atlas.MoveTo(source);
        var start = atlas.Transport.ReadLegacyPackets().Count;
        await atlas.IncomingAsync(source);
        var actual = atlas.Transport.ReadLegacyPackets().Skip(start).ToArray();
        Check.True(actual.Any(packet => packet.Length == 24 && ReadWarningOpcode(packet) == 10046 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == source.ObjectId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)) == 2179),
            "regular Atlas attacks retain their captured native Fire Blast effect");
        Check.True(actual.All(packet => !IsCastWarning(packet) && !IsRedBossNotice(packet)) &&
            actual.Where(packet => ReadWarningOpcode(packet) == Opcodes.ServerNote).All(packet =>
                Encoding.ASCII.GetString(packet).Contains("test boundary", StringComparison.Ordinal)),
            "rapid Atlas basic attacks produce no invented windup, centered warning or modal skill notice");
    }

    private static ushort ReadWarningOpcode(byte[] packet) => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));

    private static bool IsRedBossNotice(byte[] packet) => packet.Length == 137 &&
        ReadWarningOpcode(packet) == Opcodes.PythonNote && BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)) == 50 &&
        packet[8] == 0 && ReadRedNoticeText(packet).StartsWith("|cFFFF0000", StringComparison.Ordinal);

    private static string ReadRedNoticeText(byte[] packet) =>
        Encoding.ASCII.GetString(packet, 9, 64).Split('\0')[0] +
        Encoding.ASCII.GetString(packet, 73, 64).Split('\0')[0];
}
