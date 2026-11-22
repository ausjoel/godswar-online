using Godswar.Server.Application.Events;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W4F (wave 4 item 4): client SysMsg event-opening lines sent on the closed -> open edge.
internal static class EventOpeningNoticeChecks
{
    public const string CheckName = "W4F event-opening notices (client WeekDay.Dat lines)";

    public static Task RunAsync()
    {
        var keys = EventOpeningNoticePolicy.Notices.Select(static n => n.Key).ToArray();
        Check.Equal(keys.Length, keys.Distinct().Count(), "notice keys are unique");
        Check.True(EventOpeningNoticePolicy.Notices.All(static n =>
                new[] { n.Both, n.Athens, n.Sparta }.Where(static t => t is not null).All(static t => t!.All(static c => c is >= ' ' and <= '~'))),
            "every notice line is printable ASCII (centre notice)");
        foreach (var text in EventOpeningNoticePolicy.Notices.SelectMany(static n => new[] { n.Both, n.Athens, n.Sparta }).OfType<string>())
        {
            Check.True(Godswar.Server.Packets.PacketBuilder.CenteredAnnouncement(text).Length > 0,
                $"centre notice accepts '{text[..20]}...' ({text.Length} chars)");
        }

        var closed = keys.ToDictionary(static k => k, static _ => false);
        var oneOpen = new Dictionary<string, bool>(closed) { [keys[0]] = true };
        Check.Equal(0, EventOpeningNoticePolicy.Due(null, oneOpen).Count, "first tick only records state");
        Check.Equal(1, EventOpeningNoticePolicy.Due(closed, oneOpen).Count, "closed -> open fires once");
        Check.Equal(0, EventOpeningNoticePolicy.Due(oneOpen, oneOpen).Count, "open -> open does not repeat");
        Check.Equal(0, EventOpeningNoticePolicy.Due(oneOpen, closed).Count, "open -> closed is silent");

        var pindus = EventOpeningNoticePolicy.Notices.Single(static n => n.Key == "battlefield.pindus");
        Check.Equal(EventOpeningNoticePolicy.PindusAthensText, pindus.TextFor(GameDefaults.AthensCamp), "Athenians get the Athena line");
        Check.Equal(EventOpeningNoticePolicy.PindusSpartaText, pindus.TextFor(GameDefaults.SpartaCamp), "Spartans get the Ares line");
        var treasure = EventOpeningNoticePolicy.Notices.Single(static n => n.Key == "event.treasure_land");
        Check.Equal(EventOpeningNoticePolicy.TreasureLandText, treasure.TextFor(GameDefaults.SpartaCamp), "shared lines reach both factions");

        var definition = Finalise2Rates.Definitions.Single(static d => d.Key == Finalise2RateKeys.EventOpenNotices);
        Check.Equal(1m, definition.DefaultValue, "B21: event-opening notices are on by default (live is on)");
        return Task.CompletedTask;
    }
}
