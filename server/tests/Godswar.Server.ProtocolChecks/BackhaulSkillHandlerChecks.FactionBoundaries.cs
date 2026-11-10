using Godswar.Server.Domain.Characters;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    public const string FactionBoundaryCheckName =
        "Faction-bound portal casting rejection";

    public static async Task RunFactionBoundaryAsync()
    {
        await using var socket = await BackhaulSessionSocket.CreateAsync();
        var character = CreateCharacter(
            "CrossFactionBackhaul",
            GameDefaults.SpartaCamp);
        var store = new BackhaulStore(
            character,
            [new SkillState
            {
                SkillId = checked((int)FactionPortalSkillPolicy
                    .AthensCapitalPortalSkillId),
                Level = 1
            }]);
        var registry = CreateRegistry();
        GameHandlerOwnershipTestFences.Bind(
            registry,
            socket.Session,
            AccountId,
            character);
        registry.JoinMap(
            socket.Session,
            AccountId,
            character,
            WorldObjectIds.ForPlayer(CharacterId),
            worldReady: true,
            joinedAt: TestTime);
        var handler = CreateEnteredHandler(
            socket.Session,
            store,
            registry,
            character);

        await InvokePacketAsync(
            handler,
            CreateSkillCastPacket(
                FactionPortalSkillPolicy.AthensCapitalPortalSkillId,
                character.PositionX,
                character.PositionZ,
                targetX: GameDefaults.StartingPositionX,
                targetZ: GameDefaults.StartingPositionZ));

        Check.Equal(
            0,
            socket.Available,
            "a learned opposing-faction portal emits no packets");
        Check.Equal(
            150,
            character.CurrentMp,
            "a learned opposing-faction portal consumes no MP");
        Check.Equal(
            0,
            store.VitalsWrites.Count,
            "a learned opposing-faction portal persists no vitals");
        Check.Equal(
            0,
            store.PositionWrites.Count,
            "a learned opposing-faction portal persists no destination");
        Check.Equal(
            PeloponneseMapId,
            character.CurrentMap,
            "a learned opposing-faction portal cannot change maps");

        await StopHandlerAsync(handler);
        registry.Remove(socket.Session);
    }
}
