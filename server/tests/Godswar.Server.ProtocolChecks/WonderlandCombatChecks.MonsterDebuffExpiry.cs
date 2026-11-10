using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckMonsterDebuffTimersAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        foreach (var first in new[] { 70, 350, 600, 790 })
        foreach (var skill in Enumerable.Range(first, 5))
        {
            await using var f = await Fixture.CreateAsync(1, monsters, players);
            await CommitDebuffVisibilityAsync(f);
            var applied = await ApplyTimedBossControlAsync(f, skill);
            var bossId = applied.Monster.ObjectId;
            MonsterControlSkillPolicy.TryGet(skill, out var definition);
            Check.True(ReadMonsterStatusIds(MonsterStatusPackets(f, bossId).Last()).Contains(definition.StatusId),
                $"skill {skill} publishes its active debuff icon");
            var expiry = applied.ExpiresAt!.Value;
            await f.Registry.AdvanceMonsterWorldOnceAsync(expiry.AddTicks(-1), CancellationToken.None);
            await f.FlushAsync();
            Check.True(ReadMonsterStatusIds(MonsterStatusPackets(f, bossId).Last()).Contains(definition.StatusId),
                $"skill {skill} is not cleared before its actual deadline");
            await f.Registry.AdvanceMonsterWorldOnceAsync(expiry, CancellationToken.None);
            await f.FlushAsync();
            Check.Equal(0, ReadMonsterStatusIds(MonsterStatusPackets(f, bossId).Last()).Length,
                $"{monsters}/{players}: skill {skill} clears automatically at expiry without another cast");
            var cleared = MonsterStatusPackets(f, bossId).Count;
            await f.Registry.AdvanceMonsterWorldOnceAsync(expiry.AddMilliseconds(100), CancellationToken.None);
            await f.FlushAsync();
            Check.Equal(cleared, MonsterStatusPackets(f, bossId).Count,
                "an expired debuff does not flood empty icon packets on subsequent ticks");
        }
        await CheckMonsterDebuffRefreshAsync(monsters, players);
        await CheckMonsterDebuffSourceDepartureAsync(monsters, players);
        await CheckMonsterDebuffCorpseExpiryAsync(monsters, players);
        await CheckDelayedMonsterDebuffExpiryAsync(monsters, players);
    }

    private static async Task CheckMonsterDebuffRefreshAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        await CommitDebuffVisibilityAsync(f);
        var frozen = await ApplyTimedBossControlAsync(f, 354);
        var silenced = await ApplyTimedBossControlAsync(f, 604);
        var bossId = frozen.Monster.ObjectId;
        var firstSilenceExpiry = silenced.ExpiresAt!.Value;
        f.Now = frozen.ExpiresAt!.Value;
        await f.Registry.AdvanceMonsterWorldOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        Check.True(ReadMonsterStatusIds(MonsterStatusPackets(f, bossId).Last()).SequenceEqual([364u]),
            "Freeze expiry removes only Freeze while the overlapping Silence icon remains");
        f.Now = firstSilenceExpiry.AddSeconds(-1);
        var refreshed = await ApplyTimedBossControlAsync(f, 604);
        await f.Registry.AdvanceMonsterWorldOnceAsync(firstSilenceExpiry, CancellationToken.None);
        await f.FlushAsync();
        Check.True(ReadMonsterStatusIds(MonsterStatusPackets(f, bossId).Last()).SequenceEqual([364u]),
            "the old Silence deadline cannot clear a refreshed Silence");
        await f.Registry.AdvanceMonsterWorldOnceAsync(refreshed.ExpiresAt!.Value, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, ReadMonsterStatusIds(MonsterStatusPackets(f, bossId).Last()).Length,
            "the refreshed Silence clears automatically at its replacement deadline");
    }

    private static async Task CheckMonsterDebuffSourceDepartureAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        var setup = await CreateBirdPresentationFixtureAsync(monsters, players);
        await using var f = setup.Fixture;
        await using var observer = setup.Observer;
        try
        {
            var boss = f.Monster("alpha");
            f.MoveTo(boss);
            setup.Viewer.PositionX = boss.X;
            setup.Viewer.PositionZ = boss.Z;
            f.Registry.UpdateCharacter(observer, setup.Viewer, advanceWorldRevision: false);
            await CommitDebuffVisibilityAsync(f);
            await using (var visibility = await f.Registry.BeginMonsterVisibilityTransitionAsync(observer, 207,
                boss.X, boss.Z, CancellationToken.None)
                ?? throw new InvalidOperationException("Debuff observer visibility unavailable."))
                visibility.Commit();
            var silence = await ApplyTimedBossControlAsync(f, 604);
            await observer.SendAsync(PacketBuilder.ServerNote("Observer debuff boundary"), CancellationToken.None);
            var received = setup.ObserverTransport.ReadLegacyPackets().Where(p => IsMonsterStatus(p, boss.ObjectId)).ToArray();
            Check.True(ReadMonsterStatusIds(received.Last()).SequenceEqual([364u]),
                "the other admitted player receives the initial Silence icon");
            f.Registry.Remove(f.Session);
            await f.Registry.AdvanceMonsterWorldOnceAsync(silence.ExpiresAt!.Value, CancellationToken.None);
            await observer.SendAsync(PacketBuilder.ServerNote("Observer expiry boundary"), CancellationToken.None);
            received = setup.ObserverTransport.ReadLegacyPackets().Where(p => IsMonsterStatus(p, boss.ObjectId)).ToArray();
            Check.Equal(0, ReadMonsterStatusIds(received.Last()).Length,
                "boss debuff cleanup reaches the remaining viewer after the caster leaves");
        }
        finally { f.Registry.Remove(observer); }
    }

    private static async Task CommitDebuffVisibilityAsync(Fixture f)
    {
        var boss = f.Monster("alpha");
        f.MoveTo(boss);
        await using var visibility = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
            boss.X, boss.Z, CancellationToken.None)
            ?? throw new InvalidOperationException("Debuff target visibility unavailable.");
        visibility.Commit();
    }

    private static async Task<MonsterControlResult> ApplyTimedBossControlAsync(Fixture f, int skill)
    {
        Check.True(MonsterControlSkillPolicy.TryGet(skill, out var definition), "timed monster debuff exists");
        f.Character.Profession = definition.RequiredProfession;
        f.MoveTo(f.Monster("alpha"));
        Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, f.Monster("alpha").ObjectId,
                out var target, out var authority) &&
            f.Registry.TryCommitPlayerMonsterControl(f.Session, target, authority, definition, f.Now, out _),
            $"skill {skill} commits through actual monster authority");
        var applied = f.Monster("alpha");
        var expires = applied.Controls.Active(f.Now).Single(entry => entry.Definition.Kind == definition.Kind).ExpiresAt;
        await f.Registry.PublishMonsterControlsAsync(f.Session, applied, CancellationToken.None);
        await f.FlushAsync();
        return new(true, applied, expires);
    }

    private static List<byte[]> MonsterStatusPackets(Fixture f, uint id) =>
        f.Transport.ReadLegacyPackets().Where(p => IsMonsterStatus(p, id)).ToList();

    private static bool IsMonsterStatus(byte[] packet, uint id) => packet.Length == 340 &&
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10167 &&
        BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == id;

    private static uint[] ReadMonsterStatusIds(byte[] packet) => Enumerable.Range(0,
            checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))))
        .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12 + index * 4))).ToArray();
}
