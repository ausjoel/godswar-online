using System.Collections.Concurrent;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // MapIdToNameConfig maps content map 205 to native scene 224.
    private const ushort AtlantisClientSceneId = 224;
    private readonly ConcurrentDictionary<WorldInstanceId, ushort> _atlantisDailyLimits = [];
    private readonly ConcurrentDictionary<ClientSession, AtlantisUiStamp> _atlantisUi = [];
    // The party this run was admitted for. The panel publishes it instead of the
    // connected sessions, so a member who drops stays on the roster as offline.
    private readonly ConcurrentDictionary<
        WorldInstanceId,
        LegacyInstancePartyMember[]> _atlantisRunRosters = [];

    private void ForgetAtlantisRun(WorldInstanceId instanceId)
    {
        _atlantisDailyLimits.TryRemove(instanceId, out _);
        _atlantisRunRosters.TryRemove(instanceId, out _);
        ForgetInstanceRunMembership(instanceId);
        ForgetAtlantisTermination(instanceId);
        ForgetAtlantisCompletion(instanceId);
        ForgetAtlantisCompletionRewards(instanceId);
        foreach (var cached in _atlantisUi.Where(pair => pair.Value.InstanceId == instanceId))
        {
            _atlantisUi.TryRemove(cached);
        }
    }

    internal bool TryStartAtlantisEncounter(
        WorldInstanceId instanceId,
        ushort dailyEntryLimit,
        IReadOnlyList<(int CharacterId, int Level)> admittedParty,
        DateTimeOffset startedAt,
        Guid admissionReservationId = default,
        IReadOnlyList<LegacyInstancePartyMember>? admissionMembers = null)
    {
        if (dailyEntryLimit == 0 || !WorldInstances.TryFind(instanceId, out var runtime))
        {
            return false;
        }
        var started = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
        {
            if (admissionMembers is not null)
            {
                map.ConfigureAtlantisRewardAdmission(admissionReservationId, admissionMembers);
            }
            if (!map.TryConfigureAtlantisWaves(_gameplayCatalogs.Content, admittedParty, startedAt) ||
                !map.TryStartAtlantisEncounter(startedAt, out _))
            {
                return false;
            }
            _ = map.TrySpawnPendingAtlantisWave(startedAt, out _);
            return true;
        });
        if (started)
        {
            if (admissionReservationId != Guid.Empty)
            {
                _atlantisAdmissionInstances.TryAdd(admissionReservationId, instanceId);
            }
            _atlantisDailyLimits.TryAdd(instanceId, dailyEntryLimit);
            _atlantisLeaderCharacterIds.TryAdd(instanceId, admittedParty[0].CharacterId);
            if (admissionMembers is not null)
            {
                _atlantisRunRosters[instanceId] = [.. admissionMembers];
                // The one per-run member record the roster publishes from opens
                // here, with the party this run was registered for.
                BeginInstanceRunMembership(instanceId,
                    [.. admissionMembers.Select(ToRosterEntry)]);
            }
        }
        return started;
    }

    internal bool TryGetAtlantisEncounterSnapshot(
        WorldInstanceId instanceId,
        out AtlantisRunSnapshot snapshot)
    {
        snapshot = default!;
        if (!WorldInstances.TryFind(instanceId, out var runtime))
        {
            return false;
        }
        var result = InvokeWorldOwner(runtime, map =>
            map.TryGetAtlantisRunSnapshot(out var run) ? run : null);
        if (result is null)
        {
            return false;
        }
        snapshot = result;
        return true;
    }

    private AtlantisRunDelivery? CaptureAtlantisRunDelivery(
        WorldInstanceRuntime runtime,
        DateTimeOffset now)
    {
        if (runtime.MapId != DynamicDungeonContentMapPolicy.AtlantisPortalMapId ||
            !_atlantisDailyLimits.TryGetValue(runtime.InstanceId, out var dailyLimit))
        {
            return null;
        }
        var run = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
        {
            if (!map.TryAdvanceAtlantisEncounter(now, out var snapshot))
            {
                return null;
            }
            _ = map.TrySpawnPendingAtlantisWave(now, out _);
            return snapshot;
        });
        if (run is null)
        {
            return null;
        }

        lock (_gate)
        {
            foreach (var cached in _atlantisUi)
            {
                if (!_sessions.TryGetValue(cached.Key, out var current) ||
                    current.Session.IsDisconnected ||
                    current.WorldInstanceId != cached.Value.InstanceId)
                {
                    _atlantisUi.TryRemove(cached);
                }
            }
            var members = _sessions.Values
                .Where(context => context.WorldReady &&
                    !context.Session.IsDisconnected &&
                    context.WorldInstanceId == runtime.InstanceId &&
                    context.RealmId == runtime.RealmId &&
                    context.MapId == DynamicDungeonContentMapPolicy.AtlantisPortalMapId &&
                    context.Character.CurrentMap == context.MapId &&
                    context.Ownership.IsValid &&
                    IsCurrentAccountSession(context.AccountId, context.Session, context.Ownership))
                .OrderBy(static context => context.CharacterId)
                .Select(static context => new AtlantisRunMember(
                    context.Session, context.AccountId, context.CharacterId,
                    context.Character.Name, context.Character.Level,
                    context.Character.Profession, context.Character.Camp, context.Ownership))
                .ToArray();
            return new(runtime, run, dailyLimit, members, now);
        }
    }

    private sealed record AtlantisRunDelivery(
        WorldInstanceRuntime Runtime,
        AtlantisRunSnapshot Run,
        ushort DailyEntryLimit,
        IReadOnlyList<AtlantisRunMember> Members,
        DateTimeOffset ObservedAt);

    private sealed record AtlantisRunMember(
        ClientSession Session,
        int AccountId,
        int CharacterId,
        string Name,
        int Level,
        byte Profession,
        byte Camp,
        PlayerOwnershipFence Ownership);

    private sealed record AtlantisUiStamp(
        WorldInstanceId InstanceId,
        AtlantisRunState State,
        int RemainingSeconds,
        int Points,
        string Roster);
}
