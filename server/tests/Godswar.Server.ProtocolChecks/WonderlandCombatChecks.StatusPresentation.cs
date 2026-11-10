using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    public const string StatusPresentationCheckName =
        "Wonderland timed buffs and controls merge into native status UI without notification boxes";

    public static async Task RunStatusPresentationAsync()
    {
        foreach (var monsters in new[] { MonsterRuntimeMode.Legacy, MonsterRuntimeMode.Ecs })
        foreach (var players in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await CheckWonderlandBirdStatusAsync(monsters, players);
            await CheckWonderlandControlStatusAsync(monsters, players, 2, "derskey",
                WonderlandClientStatusIds.Stunned);
            await CheckWonderlandControlStatusAsync(monsters, players, 4, "support",
                WonderlandClientStatusIds.Silenced);
            await CheckWonderlandHitStatusAsync(monsters, players);
            await CheckWonderlandStatusAuthorityAsync(monsters, players);
            await CheckWonderlandArmorStatusAsync(monsters, players);
            await CheckWonderlandQueryClearAsync(monsters, players);
            await CheckWonderlandViewerStatusFenceAsync(monsters, players);
        }
    }

    private static async Task CheckWonderlandBirdStatusAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, players);
        Check.True(SkillStatusEffectCatalog.TryGet(340, out var zeal), "native Sacred Zeal status exists");
        Check.True(await f.Registry.ApplyRuntimeStatusAndPublishAsync(f.Session, zeal, f.Now,
            "wonderland-status-test", CancellationToken.None), "Sacred Zeal publishes before Petbird blessing");
        var start = f.Transport.ReadLegacyPackets().Count;
        f.Character.CalculatedStats = Fixture.Stats(int.MaxValue);
        await f.IncomingAsync("petbird", hit: false);
        Check.Equal(1, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "real dodged Petbird attack publishes a native status snapshot");
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15, zeal.StatusId);
        Check.Equal(5, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
            "presentation retains the authoritative total fivefold attack modifier");
        Check.Equal(zeal.HitBonus, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).Hit,
            "composing the complete status list does not duplicate Sacred Zeal mechanics");
        Check.Equal(0, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "unchanged effect identity does not spam another full status packet");

        f.Now = f.Now.AddSeconds(1);
        await f.IncomingAsync("petbird", hit: false);
        Check.Equal(1, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "a later bird attack refreshes the same icon countdown");
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15, zeal.StatusId);
        Check.True(!f.Transport.ReadLegacyPackets().Skip(start).Any(packet =>
            System.Text.Encoding.Latin1.GetString(packet).Contains("Petbird blessing:", StringComparison.Ordinal)),
            "Petbird no longer emits a notification box");

        // A normal class-buff refresh must not erase the independent encounter layer.
        Check.True(await f.Registry.ApplyRuntimeStatusAndPublishAsync(f.Session, zeal, f.Now,
            "wonderland-status-refresh", CancellationToken.None), "Sacred Zeal refresh publishes");
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15, zeal.StatusId);

        f.Runtime.Map.AdvanceWonderland(f.Now.AddSeconds(15));
        Check.Equal(1, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now.AddSeconds(15), CancellationToken.None),
            "exact expiry publishes a complete list clearing only the Wonderland icon");
        await f.FlushAsync();
        Check.True(StatusEntries(f).Select(x => x.Id).SequenceEqual([zeal.StatusId]),
            "Sacred Zeal survives the bird blessing expiry");
        Check.Equal(1, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now.AddSeconds(15)).AttackMultiplier,
            "the buff mechanics and native icon expire at the same deadline");

        f.Now = f.Now.AddSeconds(16);
        await f.IncomingAsync("petbird", hit: false);
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        f.Registry.ClearWonderlandPlayerEffects(f.Session);
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        Check.True(StatusEntries(f).Select(x => x.Id).SequenceEqual([zeal.StatusId]),
            "same-map island travel clears the encounter icon while preserving ordinary buffs");
    }

    private static async Task CheckWonderlandControlStatusAsync(MonsterRuntimeMode monsters,
        PlayerRuntimeMode players, int island, string key, uint statusId)
    {
        await using var f = await Fixture.CreateAsync(island, monsters, players);
        var seconds = island == 2 ? 2u : 4u;
        if (island == 2) await f.ApplySyntheticEncounterStunAsync();
        else await f.IncomingAsync(key);
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        AssertWonderlandStatus(f, statusId, seconds);
        Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now) ==
            (island == 2 ? PlayerSkillCastControl.Stunned : PlayerSkillCastControl.Silenced),
            "the displayed control retains the original server enforcement");
        f.Now = f.Now.AddSeconds(1);
        if (island == 2) await f.ApplySyntheticEncounterStunAsync();
        else await f.IncomingAsync(key);
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        AssertWonderlandStatus(f, statusId, seconds);
        f.Runtime.Map.AdvanceWonderland(f.Now.AddSeconds(seconds));
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now.AddSeconds(seconds), CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, StatusEntries(f).Length, "control expiry clears the native status list");
        Check.True(f.Registry.GetPlayerSkillCastControl(f.Session, f.Now.AddSeconds(seconds)) == PlayerSkillCastControl.None,
            "control expiration also restores authoritative actions");
        Check.True(!f.Transport.ReadLegacyPackets().Any(packet =>
            System.Text.Encoding.Latin1.GetString(packet).Contains("for 2 seconds.", StringComparison.Ordinal)),
            "the control does not produce the former notification box");
    }

    private static async Task CheckWonderlandHitStatusAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(7, monsters, players);
        await f.IncomingAsync("putridbird");
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PutridBirdBlessing, 15);
        Check.Equal(25_000, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).Hit,
            "the accuracy blessing remains one authoritative 25000-Hit bonus");
        var snapshot = await f.Registry.SendStatusSnapshotToSelfAsync(f.Session, f.Now, CancellationToken.None,
            "wonderland-status-query");
        Check.True(snapshot is not null && snapshot.Effects.Any(x => x.StatusId == WonderlandClientStatusIds.PutridBirdBlessing),
            "ordinary full status queries preserve the encounter source layer");
        f.Character.CurrentHp = 0;
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, StatusEntries(f).Length, "death invalidates the old-life encounter icon");
        f.Character.CurrentHp = f.Character.MaxHp;
        f.Registry.Remove(f.Session);
        Check.Equal(0, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "disconnected sessions are forgotten without sending stale status packets");
    }

    private static void AssertWonderlandStatus(Fixture f, uint id, uint seconds, uint? ordinary = null)
    {
        var entries = StatusEntries(f);
        Check.True(entries.Count(x => x.Id == id) == 1 && entries.Single(x => x.Id == id).Seconds == seconds,
            $"native 10167 contains exactly one status {id} with its {seconds}-second countdown");
        Check.Equal(ordinary.HasValue ? 2 : 1, entries.Length, "complete status list retains only expected active sources");
        if (ordinary.HasValue) Check.True(entries.Any(x => x.Id == ordinary), "ordinary class-buff icon is preserved");
    }

    private static (uint Id, uint Seconds)[] StatusEntries(Fixture f)
    {
        var packet = f.Transport.ReadLegacyPackets().Last(packet => packet.Length == 340 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10167);
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Check.True(count <= PlayerStatusComposer.MaximumTotalStatuses, "native status count respects its 20-slot limit");
        return Enumerable.Range(0, count).Select(index => (
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12 + index * 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(92 + index * 4)))).ToArray();
    }
}
