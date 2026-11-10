using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    // A GM goes through the same fenced transfer a party member takes into a run, so instance
    // capacity and ownership checks all apply. The GM is not added to the run's admitted party:
    // a relog lands in the capital, rewards refuse the GM, and the run's end sends the GM home with everyone.
    private async Task<string> OperatorEnterRunAsync(
        GameSessionContext target,
        OperatorInstanceView run,
        CancellationToken cancellationToken)
    {
        if (!_registry.TryGetSessionWorldInstanceId(_session, out var source) ||
            !_registry.TryGetWorldInstance(source, out var here) ||
            here.Kind != InstanceKind.OpenWorld)
        {
            return "/goto into a run works from an open map; /leave first";
        }

        var where = target.Character;
        var moved = await OperatorTransferAsync(
            source,
            target.WorldInstanceId,
            checked((byte)where.CurrentMap),
            where.PositionX,
            where.PositionZ,
            cancellationToken);
        await LogOperatorAsync("goto-run", target.CharacterName, $"run={run.Label} moved={moved}", cancellationToken);
        return moved
            ? $"into {target.CharacterName}'s run ({run.Label}). /leave returns you to your capital"
            : $"refused: {run.Label} is full or a transfer is already running";
    }

    private async Task<string> OperatorLeaveRunAsync(CancellationToken cancellationToken)
    {
        if (!_registry.TryGetSessionWorldInstanceId(_session, out var source) ||
            !_registry.TryGetWorldInstance(source, out var here) ||
            here.Kind != InstanceKind.Dungeon)
        {
            return "you are not in a run";
        }

        var capital = _character!.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        var moved = await OperatorTransferAsync(
            source,
            _registry.GetDefaultWorldInstanceId(capital),
            capital,
            GameDefaults.StartingPositionX,
            GameDefaults.StartingPositionZ,
            cancellationToken);
        await LogOperatorAsync("leave-run", _character.Name, $"moved={moved}", cancellationToken);
        return moved ? "back to your capital" : "refused: a transfer is already running";
    }

    private async Task<bool> OperatorTransferAsync(
        WorldInstanceId source,
        WorldInstanceId target,
        byte targetMap,
        float x,
        float z,
        CancellationToken cancellationToken)
    {
        if (_character is null || !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            return false;
        }

        // The chat dispatcher already holds the character gate, like the run-end leave.
        return await TryBeginAuthoritativeInstanceTransitionAsync(
            new AuthoritativeInstanceTransitionCommand(
                _character.Id,
                source,
                checked((byte)_character.CurrentMap),
                ownership,
                target,
                targetMap,
                x,
                z),
            cancellationToken);
    }

    // Test servers only: an empty private run, because a real one needs a party of three and daily entries.
    private async Task<string> OperatorStartTestRunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("GODSWAR_RIG_TEST_RUNS") != "1")
        {
            return "/testrun is for test servers";
        }

        if (args.Length != 4 ||
            !byte.TryParse(args[1], out var map) ||
            !float.TryParse(args[2], out var x) ||
            !float.TryParse(args[3], out var z))
        {
            return "usage: /testrun <map> <x> <z>";
        }

        if (!_registry.TryGetSessionWorldInstanceId(_session, out var source) ||
            !_registry.TryGetWorldInstance(source, out var here))
        {
            return "not in the world";
        }

        var creation = await _registry.CreateLocalWorldInstanceAsync(
            here.RealmId,
            new WorldMapId(map),
            InstanceKind.Dungeon,
            Godswar.Server.Protocol.PartyProtocol.MaximumMembers,
            cancellationToken);
        if (creation.Runtime?.Descriptor is not { } run)
        {
            return $"could not create a run: {creation.Status}";
        }

        var moved = await OperatorTransferAsync(source, run.InstanceId, map, x, z, cancellationToken);
        return $"test run moved={moved}";
    }
}
