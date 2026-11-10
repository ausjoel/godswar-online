using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly Dictionary<WorldInstanceId, WonderlandCompletionNotice> _wonderlandCompletionNotices = [];

    private void CaptureWonderlandCompletionNoticeLocked(WonderlandTitleRequest request, int leaderId,
        IReadOnlyList<GameSessionContext> finishers)
    {
        if (request.IslandNumber != 8 || finishers.Count == 0 ||
            _wonderlandCompletionNotices.ContainsKey(request.WorldInstanceId)) return;
        var leader = finishers.FirstOrDefault(member => member.CharacterId == leaderId) ?? finishers[0];
        var text = BuildWonderlandCompletionAnnouncement(leader.Character.Name, request.AdmittedMembers.Count == 1);
        _wonderlandCompletionNotices.Add(request.WorldInstanceId, new(request.RequestHash, request.RealmId,
            finishers.Select(member => member.Character.Camp).Distinct().ToArray(),
            finishers.Select(member => member.CharacterId).ToArray(), text));
    }

    internal static string BuildWonderlandCompletionAnnouncement(string name, bool solo)
    {
        var subject = new string(name.Take(32).Select(character => character is >= ' ' and <= '~' ? character : '?').ToArray());
        if (string.IsNullOrWhiteSpace(subject)) subject = "The adventurer";
        return solo
            ? $"{subject} cleared all 8 Wonderland islands solo and earned the title Wonderland Sovereign!"
            : $"{subject}'s party cleared all 8 Wonderland islands and earned the title Wonderland Sovereign!";
    }

    private void MarkWonderlandCompletionNoticeSettled(WonderlandTitleRequest request)
    {
        if (request.IslandNumber != 8) return;
        lock (_gate)
            if (_wonderlandCompletionNotices.TryGetValue(request.WorldInstanceId, out var notice) &&
                notice.RequestHash == request.RequestHash)
                notice.Ready = true;
    }

    private async Task PublishExitedWonderlandNoticesAsync()
    {
        WorldInstanceId[] instances;
        lock (_gate) instances = _wonderlandCompletionNotices.Keys.ToArray();
        foreach (var instanceId in instances) await PublishExitedWonderlandNoticeAsync(instanceId);
    }

    private async Task PublishExitedWonderlandNoticeAsync(WorldInstanceId instanceId)
    {
        var writes = new List<(ClientSession Session, Task Write)>();
        lock (_gate)
        {
            if (!_wonderlandCompletionNotices.TryGetValue(instanceId, out var notice) ||
                !notice.Ready || notice.Published || HasPendingWonderlandTitles(instanceId) ||
                _sessions.Values.Any(current => current.WorldInstanceId == instanceId) ||
                WorldInstances.TryFind(instanceId, out var runtime) && runtime.Map.Population != 0)
                return;
            // Transfer commits precede destination readiness. Keep the notice
            // until connected finishers can receive it in their destination scene.
            if (_sessions.Values.Any(current => current.RealmId == notice.RealmId &&
                    notice.CharacterIds.Contains(current.CharacterId) && !current.Session.IsDisconnected &&
                    IsCurrentAccountSession(current.AccountId, current.Session, current.Ownership) &&
                    !current.WorldReady)) return;
            var packet = PacketBuilder.CenteredAnnouncement(notice.Text);
            // Claim once before admitting any transport writes. A failed or
            // lost write cannot replay the notice to recipients already queued.
            notice.Published = true;
            foreach (var current in _sessions.Values.Where(current => current.WorldReady &&
                         !current.Session.IsDisconnected && current.RealmId == notice.RealmId &&
                         notice.Camps.Contains(current.Character.Camp) &&
                         IsCurrentAccountSession(current.AccountId, current.Session, current.Ownership)))
            {
                if (current.Session.TryAdmitExact(packet, out var write)) writes.Add((current.Session, write));
                else current.Session.Disconnect();
            }
            if (!_wonderlandAdmissions.ContainsKey(instanceId)) _wonderlandCompletionNotices.Remove(instanceId);
        }
        foreach (var entry in writes)
        {
            try { await entry.Write; }
            catch (Exception) { entry.Session.Disconnect(); }
        }
    }

    private void ForgetWonderlandCompletionNotice(WorldInstanceId instanceId)
    {
        // Empty runtime retirement need not wait for destination loading. Only
        // scalar announcement evidence survives, until the next ready world tick.
        lock (_gate)
            if (_wonderlandCompletionNotices.TryGetValue(instanceId, out var notice) && notice.Published)
                _wonderlandCompletionNotices.Remove(instanceId);
    }

    private sealed class WonderlandCompletionNotice(string requestHash, RealmId realmId, byte[] camps,
        int[] characterIds, string text)
    {
        public string RequestHash { get; } = requestHash;
        public RealmId RealmId { get; } = realmId;
        public byte[] Camps { get; } = camps;
        public int[] CharacterIds { get; } = characterIds;
        public string Text { get; } = text;
        public bool Ready { get; set; }
        public bool Published { get; set; }
    }
}
