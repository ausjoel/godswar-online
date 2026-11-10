using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WonderlandCombatChecks
{
    private static async Task CheckWonderlandStatusAuthorityAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(3, monsters, players);
        await f.IncomingAsync("petbird");
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15);
        var oldLife = f.Registry.GetPlayerLifeRevision(f.Session);
        Check.Equal(oldLife + 1, f.Registry.AdvancePlayerLifeRevision(f.Session, f.Now),
            "test advances the real authoritative life revision");
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        Check.Equal(0, StatusEntries(f).Length, "old-life status timers cannot reappear on the next life");
        Check.Equal(1, f.Registry.GetRuntimeStatusAggregate(f.Session, f.Now).AttackMultiplier,
            "old-life effects cannot continue influencing combat either");

        await f.IncomingAsync("petbird");
        await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
        await f.FlushAsync();
        AssertWonderlandStatus(f, WonderlandClientStatusIds.PetbirdBlessing, 15);
        var packetsBefore = f.Transport.ReadLegacyPackets().Count;
        var replacementTransport = new FactionCrierCaptureTransport();
        await using var replacement = new ClientSession(replacementTransport);
        GameHandlerOwnershipTestFences.Bind(f.Registry, replacement, f.Character.AccountId, f.Character);
        Check.Equal(0, await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None),
            "account replacement prevents the old session from publishing its status");
        Check.True(f.Transport.ReadLegacyPackets().Skip(packetsBefore).All(packet =>
            packet.Length != 340 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != 10167),
            "a replacement account receives no late old-session 10167");
        Check.Equal(0, replacementTransport.ReadLegacyPackets().Count,
            "encounter effects are never redirected onto the replacement session");
        f.Registry.Remove(replacement);
    }

    private static async Task CheckWonderlandArmorStatusAsync(MonsterRuntimeMode monsters, PlayerRuntimeMode players)
    {
        await using var f = await Fixture.CreateAsync(8, monsters, players);
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var previousHp = f.Character.CurrentHp;
            await f.IncomingAsync("scorpion");
            Check.True(f.Character.CurrentHp < previousHp, "the real captured Scorpion attack commits damage");
            await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
            await f.FlushAsync();
            AssertWonderlandStatus(f, 133, 20);
            f.Registry.ClearWonderlandPlayerEffects(f.Session);
            await f.Registry.ReconcileWonderlandStatusesOnceAsync(f.Now, CancellationToken.None);
            await f.FlushAsync();
            Check.Equal(0, StatusEntries(f).Length, "armor icon is removed on leaving its island");
            f.Now = f.Now.AddSeconds(1);
        }
    }
}
