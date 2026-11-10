using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    public const string AtlantisPetCaptureCheckName = "Atlantis native net target authority and durable capture context";

    public static async Task RunAtlantisPetCaptureAsync()
    {
        await using var fixture = await CreateAtlantisOpalFixtureAsync(null, null, partySize: 1);
        var leader = fixture.Leader;
        await EnterAtlantisAsync(fixture, InstanceCallerProtocol.AtlantisEnterSubId);
        await CompleteAtlantisSceneReadinessAsync(leader.Handler);
        var instanceId = GetSourceInstanceId(leader);
        var net = CompactItemEntry.Empty with { Id = 10084, Stack = 1, Grade = 1, Quality = 1 };
        leader.Character.KitBag = KitBagSlots.SetSlot(leader.Character.KitBag, 15, net.ToCompactString());
        var executor = new DelegatingPetDurableCommandExecutor
        {
            // This check observes the real handler's authoritative envelope.
            // The separate PostgreSQL check proves inventory commit/projection.
            Activate = _ => PetDurableExecutionResult.NonDurable(PetDurableExecutionDisposition.InvalidIntent)
        };
        SetHandlerField(leader.Handler, "_petDurableCommands", executor);
        var resolver = typeof(GameClientHandler).GetMethod("TryResolvePetCaptureTarget", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var validity = typeof(GameClientHandler).GetMethod("IsPetCaptureCompletionValid", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var completion = typeof(GameClientHandler).GetMethod("CompletePetCaptureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var pet in AtlantisPetSpawnPolicy.Spawns)
        {
            var request = CreateAtlantisNetRequest(pet.ObjectId);
            object?[] unseen = [request, null, null];
            Check.True(!(bool)resolver.Invoke(leader.Handler, unseen)!, "distant unseen pet cannot start capture");
            leader.Character.PositionX = pet.Placement.X;
            leader.Character.PositionZ = pet.Placement.Z;
            var packetCount = leader.ReadPackets().Count;
            await leader.Registry.AdvanceMonsterWorldOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Check.True(leader.ReadPackets().Skip(packetCount).Any(packet => packet.Length >= 108 &&
                ReadOpcode(packet) == 10020 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == pet.ObjectId &&
                BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)) == 205),
                "approaching each requested location publishes its native map-205 pet appearance");
            object?[] resolved = [request, null, null];
            Check.True((bool)resolver.Invoke(leader.Handler, resolved)!, "visible live Atlantis pet resolves through the native net handler");
            var target = (MonsterRuntimeSnapshot)resolved[1]!;
            var kind = resolved[2]!;
            Check.True((bool)validity.Invoke(leader.Handler, [request, target, kind])! &&
                !(bool)validity.Invoke(leader.Handler,
                    [request, target with { HealthRevision = target.HealthRevision + 1 }, kind])!,
                "capture completion requires the exact target health revision");
            var originalX = leader.Character.PositionX;
            leader.Character.PositionX += 30;
            Check.True(!(bool)validity.Invoke(leader.Handler, [request, target, kind])!,
                "moving beyond net range interrupts Atlantis capture");
            leader.Character.PositionX = originalX;
            var previousCalls = executor.ActivateCount;
            await (Task)completion.Invoke(leader.Handler, [request, target, kind, CancellationToken.None])!;
            var envelope = executor.ActivationEnvelope!;
            Check.True(executor.ActivateCount == previousCalls + 1 &&
                BagItemActivationCommandEnvelope.Validate(envelope) == CommandEnvelopeValidation.Valid &&
                envelope.Command.KitBagSlot == 15 && envelope.Command.Capture is
                { Difficulty: MedusaEncounterDifficulty.Normal } intent &&
                (pet.ObjectId == AtlantisPetSpawnPolicy.MermaidObjectId
                    ? intent.Context == PetCaptureContext.AtlantisMermaidDew && intent.EggItemId == 10132
                    : intent.Context == PetCaptureContext.AtlantisMerman && intent.EggItemId == 10158) &&
                intent.TargetObjectId == pet.ObjectId && intent.TargetRuntimeInstanceId == target.RuntimeInstanceId &&
                intent.TargetSpawnGeneration == target.SpawnGeneration && intent.TargetHealthRevision == target.HealthRevision,
                "actual handler passes server-observed target evidence and Merman egg context to durable net activation");
            await (Task)completion.Invoke(leader.Handler, [request, target, kind, CancellationToken.None])!;
            Check.Equal(previousCalls + 1, executor.ActivateCount, "lost target claim cannot issue a second net command");
        }
        object?[] wrongTarget = [CreateAtlantisNetRequest(42000), null, null];
        Check.True(!(bool)resolver.Invoke(leader.Handler, wrongTarget)!, "normal scored wave monsters are never capture targets");
        Check.True(leader.Registry.TryGetAtlantisEncounterSnapshot(instanceId, out var run) && run.TeamPoints == 0 &&
            leader.Registry.GetMapMonsterSnapshots(leader.Session, 205).Count(value =>
                value.IsAlive && IsAtlantisScoringObject(value.ObjectId)) == 12,
            "two native pet captures leave all12 scored monsters and zero points unchanged");
    }

    private static PetCaptureRequest CreateAtlantisNetRequest(uint objectId)
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 28);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), Opcodes.PetCaptureRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), objectId);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 15);
        Check.True(PetCaptureRequest.TryRead(new GamePacket(bytes), out var request),
            "native map-independent net packet accepts server-owned Atlantis target identity");
        return request;
    }
}
