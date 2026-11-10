using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class DuelArenaTransporterHandlerChecks
{
    private static async Task CheckCapturedServicesAsync()
    {
        foreach (var endpoint in new[]
                 { (Key: "Arena_005", Id: 5200u, Dialog: 32),
                   (Key: "Arena_006", Id: 5203u, Dialog: 95) })
        {
            var spawn = NpcContentBaselineV7.LoadDefinitions()
                .Single(npc => npc.NpcKey == endpoint.Key);
            await using var fixture = await CreateFixtureAsync(
                new Endpoint(spawn, spawn.X, spawn.Z), captured: true);
            await InvokeAsync(fixture.Handler, CapturedArenaClick(endpoint.Id));
            var ack = fixture.ReadPackets().Last(packet => ReadOpcode(packet) == 10067);
            var script = Encoding.ASCII.GetString(ack.AsSpan(16)).Split('\0')[0];
            Check.True(ack.Length == 48 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)) == endpoint.Id &&
                BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(8)) == 512 &&
                BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(12)) == endpoint.Dialog &&
                script == endpoint.Key,
                "Arena support advertisements match the captured actor, flags, function and script");

            var before = fixture.ReadPackets().Count;
            await InvokeAsync(fixture.Handler, CapturedArenaAction(endpoint.Id, endpoint.Dialog));
            var emitted = fixture.ReadPackets().Skip(before).ToArray();
            if (endpoint.Dialog == 32)
            {
                var expected = Convert.FromHexString(
                    "60005627501400002000000001000000640000006500000066000000" +
                    "00000000000000000000000000000000000000000000000000000000" +
                    "00000000000000000000000000000000000000000000000000000000" +
                    "000000000000000001206300");
                Check.True(emitted is [var menu] && menu.SequenceEqual(expected),
                    "Physician treatment menu matches the full captured 96-byte response");
                await AssertPhysicianRequestGuardsAsync(fixture, spawn);
            }
            else
            {
                Check.Equal(0, emitted.Length,
                    "support advertisements do not invent an uncaptured initial submenu or result");
            }

            Check.Equal(0, fixture.Store.PositionWrites.Count,
                "browsing Arena services cannot issue an inferred teleport");
            Check.Equal(2000, fixture.Character.CurrentHp,
                "browsing treatment does not report or apply unobserved healing");
        }
    }

    private static async Task AssertPhysicianRequestGuardsAsync(Fixture fixture, NpcSpawnDefinition physician)
    {
        var invalid = CapturedArenaAction(5200, 32);
        BinaryPrimitives.WriteInt32LittleEndian(invalid.Buffer.AsSpan(20), 0);
        var before = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, invalid);
        var mismatch = CapturedArenaAction(5200, 32);
        BinaryPrimitives.WriteInt32LittleEndian(mismatch.Buffer.AsSpan(12), 95);
        await InvokeAsync(fixture.Handler, mismatch);
        fixture.Character.PositionX = physician.X + 13f;
        await InvokeAsync(fixture.Handler, CapturedArenaAction(5200, 32));
        fixture.Character.PositionX = physician.X;
        fixture.Character.CurrentHp = 0;
        await InvokeAsync(fixture.Handler, CapturedArenaAction(5200, 32));
        fixture.Character.CurrentHp = 2000;
        Check.Equal(before, fixture.ReadPackets().Count,
            "Physician browsing rejects altered arguments, mismatched dialogs, distance and death");
    }
}
