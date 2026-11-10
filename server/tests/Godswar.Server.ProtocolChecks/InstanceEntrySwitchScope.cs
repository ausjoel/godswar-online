using Godswar.Server.Application.Rates;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// INSTANCES: opens the instance entry switches (server_rates, default closed)
/// for a check that exercises an instance's own entry flow, and restores the
/// previous rates when disposed.
/// </summary>
internal sealed class InstanceEntrySwitchScope : IDisposable
{
    private readonly ServerRatesSnapshot _previous;

    private InstanceEntrySwitchScope(bool harborAttack, bool trainingGround, bool warriorTraining = false)
    {
        _previous = ServerRates.Current;
        var values = _previous.Values.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        values[ServerRateKeys.HarborAttackEntry] = harborAttack ? 1m : 0m;
        values[ServerRateKeys.TrainingGroundEntry] = trainingGround ? 1m : 0m;
        values[ServerRateKeys.WarriorTrainingEntry] = warriorTraining ? 1m : 0m; // WARRIOR (F-51)
        ServerRates.Install(ServerRatesSnapshot.Create(
            values,
            _previous.Revision + 1,
            DateTimeOffset.UtcNow,
            "instance-entry-check",
            "test"));
    }

    public static InstanceEntrySwitchScope Open() => new(true, true, true);

    public static InstanceEntrySwitchScope Set(
        bool harborAttack,
        bool trainingGround,
        bool warriorTraining = false) =>
        new(harborAttack, trainingGround, warriorTraining);

    public void Dispose() => ServerRates.Install(_previous);
}
