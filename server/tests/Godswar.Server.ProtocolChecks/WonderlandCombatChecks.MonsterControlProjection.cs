using System.Buffers.Binary;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckMonsterControlProjectionAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(1, monsters, players);
        f.MoveTo(f.Monster("alpha"));
        var before = f.Transport.ReadLegacyPackets().Count;
        var transition = await f.Registry.BeginMonsterVisibilityTransitionAsync(f.Session, 207,
            f.Character.PositionX, f.Character.PositionZ, CancellationToken.None);
        Check.True(transition is not null && transition.Delta.Entering.Any(m => m.ObjectId == f.Monster("alpha").ObjectId),
            "new viewer has an actual pending appearance before controls land");
        Task? olderPublication = null;
        try
        {
            var frozen = Apply(354);
            olderPublication = f.Registry.PublishMonsterControlsAsync(f.Session, frozen, CancellationToken.None);
            Check.True(!olderPublication.IsCompleted, "older control publication waits behind the real appearance lease");
            Apply(604);
            await f.Registry.SendMonsterControlAppearancesAsync(f.Session, transition!.Delta.Entering, CancellationToken.None);
            transition.Commit();
        }
        finally { await transition!.DisposeAsync(); }
        await olderPublication!;
        await f.FlushAsync();
        var snapshots = f.Transport.ReadLegacyPackets().Skip(before).Where(IsBossStatus).ToArray();
        Check.Equal(2, snapshots.Length, "new appearance and delayed publication each admit the current control list");
        Check.True(snapshots.All(packet => StatusIds(packet).Order().SequenceEqual(new uint[] { 305, 364 })),
            "a newer Silence survives both initial hydration and an older Freeze publication resuming afterward");

        var current = f.Monster("alpha");
        await f.Registry.PublishMonsterControlsAsync(f.Session, current with { RuntimeInstanceId = Guid.NewGuid() }, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(2, f.Transport.ReadLegacyPackets().Skip(before).Count(IsBossStatus),
            "an obsolete runtime identity cannot publish controls onto a same-ID actor");
        f.Runtime.Map.AdvanceWonderland(f.Now.AddMinutes(1));
        await f.Registry.PublishMonsterControlsAsync(f.Session, current, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, StatusIds(f.Transport.ReadLegacyPackets().Last(IsBossStatus)).Length,
            "refresh after authoritative expiry emits a complete empty control list");

        MonsterRuntimeSnapshot Apply(int skill)
        {
            MonsterControlSkillPolicy.TryGet(skill, out var definition);
            f.Character.Profession = definition.RequiredProfession;
            Check.True(f.Registry.TryCapturePlayerMonsterTarget(f.Session, 207, f.Monster("alpha").ObjectId,
                out var target, out var authority) && f.Registry.TryCommitPlayerMonsterControl(f.Session, target,
                authority, definition, f.Now, out _), "projection race applies a real authority-fenced boss control");
            return f.Monster("alpha");
        }
        bool IsBossStatus(byte[] packet) => packet.Length == 340 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10167 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == f.Monster("alpha").ObjectId;
        uint[] StatusIds(byte[] packet) => Enumerable.Range(0,
                checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8))))
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12 + 4 * index))).ToArray();
    }
}
