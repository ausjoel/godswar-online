using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool CanCaptureAtlantisPet(ClientSession session, uint objectId)
    {
        if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
            session.IsDisconnected || context.MapId != 205 || context.Character.CurrentMap != 205 ||
            !context.Ownership.IsValid ||
            !IsCurrentAccountSession(context.AccountId, session, context.Ownership) ||
            !WorldInstances.TryFind(context.WorldInstanceId, out var runtime))
        {
            return false;
        }
        return InvokeWorldOwner(runtime, map => map.CanCaptureAtlantisPet(context.CharacterId, objectId));
    }

    private bool TryCaptureAtlantisPet(ClientSession session, MonsterRuntimeSnapshot expected,
        DateTimeOffset now, out MonsterDamageResult result)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) || !context.WorldReady ||
                session.IsDisconnected || context.MapId != 205 || context.Character.CurrentMap != 205 ||
                !context.Ownership.IsValid ||
                !IsCurrentAccountSession(context.AccountId, session, context.Ownership) ||
                !WorldInstances.TryFind(context.WorldInstanceId, out var runtime))
            {
                result = default!;
                return false;
            }
            var attempt = InvokeWorldOwnerAuthoritativeMutation(runtime, map =>
            {
                MonsterDamageResult value = default!;
                var captured = map.CanCaptureAtlantisPet(context.CharacterId, expected.ObjectId) &&
                    map.TryCaptureMonster(expected, now, out value);
                return (Captured: captured, Value: value);
            });
            result = attempt.Value;
            return attempt.Captured;
        }
    }
}
