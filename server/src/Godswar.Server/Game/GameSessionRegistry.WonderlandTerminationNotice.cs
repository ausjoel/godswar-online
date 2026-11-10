using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private void CaptureWonderlandTerminationNoticeLocked(WorldInstanceRuntime runtime,
        WonderlandSnapshot run, WonderlandAdmission admission, IReadOnlyList<GameSessionContext> members)
    {
        if (run.State is not (WonderlandRunState.Cancelled or WonderlandRunState.TimedOut) ||
            members.Count == 0 || _wonderlandCompletionNotices.ContainsKey(runtime.InstanceId)) return;
        var leader = admission.OriginalMembers.First(member => member.CharacterId == admission.LeaderId);
        var text = BuildWonderlandTerminationAnnouncement(leader.CharacterName,
            admission.Entrants.Count == 1, run.CompletedIslands, run.State == WonderlandRunState.TimedOut);
        _wonderlandCompletionNotices.Add(runtime.InstanceId, new(TerminationNoticeKey(run), runtime.RealmId,
            members.Select(member => member.Character.Camp).Distinct().ToArray(),
            members.Select(member => member.CharacterId).ToArray(), text) { Ready = true });
    }

    private void CaptureWonderlandTerminationNotice(WorldInstanceRuntime runtime, WonderlandSnapshot run,
        WonderlandAdmission admission, IReadOnlyList<GameSessionContext> members)
    {
        if (run.State is not (WonderlandRunState.Cancelled or WonderlandRunState.TimedOut))
            return;
        lock (_gate) CaptureWonderlandTerminationNoticeLocked(runtime, run, admission, members);
    }

    internal static string BuildWonderlandTerminationAnnouncement(string name, bool solo,
        int completedIslands, bool timedOut)
    {
        var subject = new string(name.Take(32).Select(character => character is >= ' ' and <= '~' ? character : '?').ToArray());
        if (string.IsNullOrWhiteSpace(subject)) subject = "The adventurer";
        if (!solo) subject += "'s party";
        return timedOut
            ? $"{subject}'s Wonderland time expired with {Math.Clamp(completedIslands, 0, 8)}/8 islands cleared."
            : $"{subject} ended the Wonderland run with {Math.Clamp(completedIslands, 0, 8)}/8 islands cleared.";
    }

    private static string TerminationNoticeKey(WonderlandSnapshot run) => $"terminal:{run.State}:{run.TerminalAt:O}";
}
