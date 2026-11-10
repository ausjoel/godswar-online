using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    internal static async Task RunMedusaMonsterStunExpiryChecksAsync()
    {
        foreach (var playerMode in new[] { PlayerRuntimeMode.Legacy, PlayerRuntimeMode.Ecs })
        {
            await using var fixture = await MonsterPlayerHitFixture.CreateAsync("Euryale", playerMode);
            await PrepareMedusaMonsterVisibilityAsync(fixture.Registry, fixture.Socket.Session, fixture.Character);
            await DrainMedusaPacketsAsync(fixture.Socket);

            var now = DateTimeOffset.UtcNow;
            Check.True(fixture.Registry.TryApplyMonsterStun(
                    fixture.Character.CurrentMap, fixture.Source.ObjectId, fixture.Character.Id,
                    MonsterStunSkillCatalog.StunDuration, fixture.Source.SpawnGeneration, now, out var stun) &&
                stun.Applied,
                $"Medusa {playerMode}: native Warrior stun enters the legacy StunnedUntil path");
            Check.True(!stun.Monster.Controls.Active(now).Any() && stun.Monster.StunnedUntil.HasValue,
                "Medusa native stun requires expiry tracking independently of MonsterControlState entries");

            await fixture.Registry.PublishMonsterControlsAsync(
                fixture.Socket.Session, stun.Monster, CancellationToken.None);
            var applied = await ReadMedusaMonsterStatusAsync(fixture.Socket, fixture.Source.ObjectId);
            Check.True(BinaryPrimitives.ReadUInt32LittleEndian(applied.AsSpan(8)) == 1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(applied.AsSpan(12)) == MonsterStunSkillCatalog.StunnedStatusId,
                "Medusa native stun publishes its icon through the complete monster status composer");

            await fixture.Registry.AdvanceMonsterWorldOnceAsync(stun.StunnedUntil!.Value, CancellationToken.None);
            var cleared = await ReadMedusaMonsterStatusAsync(fixture.Socket, fixture.Source.ObjectId);
            Check.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(cleared.AsSpan(8)),
                "Medusa world pump explicitly clears the native stun icon at its deadline");
            Check.True(fixture.Map.TryGetMonsterSnapshot(fixture.Source.ObjectId, out var current) &&
                current.StunnedUntil is null,
                "Medusa native monster stun authority and presentation expire together");
        }
    }

    private static async Task<byte[]> ReadMedusaMonsterStatusAsync(
        RuntimePolicySessionSocket socket, uint monsterObjectId)
    {
        for (var index = 0; index < 256; index++)
        {
            var packet = await socket.ReadPacketAsync();
            if (packet.Length == 340 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 10167 &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == monsterObjectId)
                return packet;
        }
        throw new InvalidOperationException("The Medusa monster status snapshot was not published.");
    }
}
