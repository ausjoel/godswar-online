using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task CheckWonderlandFinalExitLifeRacesAsync(InstanceCallerFixture actor,
        WorldInstanceId instanceId)
    {
        var originalStore = GetHandlerField<IGameStore>(actor.Handler, "_store")!;
        var npc = WonderlandTraversalPolicy.GetTeleporter(8);
        try
        {
            foreach (var revivedBeforeCommit in new[] { false, true })
            {
                actor.Character.CurrentHp = actor.Character.MaxHp;
                var originalLife = actor.Registry.GetPlayerLifeRevision(actor.Session);
                Check.True(actor.Registry.TryResolveWonderlandFinalExit(actor.Session, instanceId, originalLife, out _),
                    "the completed, settled final exit is eligible at wall time before injecting the life race");
                var sourceX = actor.Character.PositionX;
                var sourceZ = actor.Character.PositionZ;
                var writes = new WonderlandExitPositionStore(() =>
                {
                    // A completed run may still deliver an already-committed
                    // Atlas death blast while this destination write is awaited.
                    actor.Character.CurrentHp = 0;
                    actor.Character.MarkVitalsChanged();
                    actor.Registry.AdvancePlayerLifeRevision(actor.Session);
                    if (revivedBeforeCommit)
                    {
                        actor.Character.CurrentHp = actor.Character.MaxHp;
                        actor.Character.MarkVitalsChanged();
                        actor.Registry.AdvancePlayerLifeRevision(actor.Session);
                    }
                });
                SetHandlerField<IGameStore>(actor.Handler, "_store", writes);
                await InvokeAsync(actor.Handler, CreateWonderlandTransportClick(npc.ObjectId));
                var before = actor.ReadPackets().Count;
                await InvokeAsync(actor.Handler, CreateWonderlandTransportAction(npc.ObjectId, 57));
                Check.True(writes.Positions.Count == 2 && writes.Positions[0].Map != 207 &&
                    writes.Positions[1] == (207, sourceX, sourceZ),
                    "death or revive during the destination checkpoint triggers exact source-position compensation");
                Check.True(actor.Character.CurrentMap == 207 && GetSourceInstanceId(actor) == instanceId &&
                    actor.Character.PositionX == sourceX && actor.Character.PositionZ == sourceZ &&
                    actor.Registry.GetPlayerLifeRevision(actor.Session) > originalLife &&
                    actor.ReadPackets().Skip(before).All(packet => ReadOpcode(packet) != Opcodes.SceneChange),
                    "the membership commit cannot transfer a different life or dead player through a voluntary exit");
            }
        }
        finally
        {
            SetHandlerField(actor.Handler, "_store", originalStore);
            actor.Character.CurrentHp = actor.Character.MaxHp;
            actor.Registry.UpdateCharacter(actor.Session, actor.Character, advanceWorldRevision: false);
        }
    }

    private sealed class WonderlandExitPositionStore(Action afterDestinationWrite) : GameStoreTestStub
    {
        public List<(byte Map, float X, float Z)> Positions { get; } = [];

        public override Task SaveCharacterPositionAsync(int accountId, int characterId, byte currentMap,
            float positionX, float positionZ, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Positions.Add((currentMap, positionX, positionZ));
            if (Positions.Count == 1) afterDestinationWrite();
            return Task.CompletedTask;
        }
    }
}
