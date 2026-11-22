using Godswar.Server.Application.Gates;
using Godswar.Server.Application.Npc;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.Troy;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Application.Events;

/// <summary>
/// W4F: one client SysMsg line per event. <see cref="Athens"/> / <see cref="Sparta"/> are the faction lines (null = that
/// faction gets nothing); <see cref="Both"/> is the line for everyone.
/// </summary>
internal sealed record EventOpeningNotice(string Key, string? Both, string? Athens = null, string? Sparta = null)
{
    public string? TextFor(byte camp) => Both ?? (camp == GameDefaults.SpartaCamp ? Sparta : Athens);
}

/// <summary>
/// W4F (wave 4 item 4): the client's own Localization/en_us/Text/SysMsg/WeekDay.Dat event lines (index in brackets),
/// verbatim, printable ASCII. Each fires on the closed -> open edge of <see cref="GateSchedules.IsOpen"/>, the same
/// answer the entry gates use, so editor windows, "always" and "closed" modes and the env switches all apply.
/// </summary>
internal static class EventOpeningNoticePolicy
{
    // [0] Sicily: free entry on Fridays (no Mystic Spell needed). The client's curly apostrophe in "don't" is ASCII here.
    public const string SicilyText = "With Hades' power ebbing, you don't even need a Mystic Spell to enter Sicily. Hurry up!";
    // [1]
    public const string TreasureLandText = "The Treasure Land is now open. Hurry over in search of bountiful treasures held by Hermes.";
    // [2] / [3]
    public const string FarmAthensText = "Lelantine Farm is in danger! Athenian warriors, take up arms and stop the ferocious hounds.";
    public const string FarmSpartaText = "Lelantine Farm is in danger! Spartan warriors, take up arms and stop the ferocious hounds.";
    // [4] / [5]
    public const string PindusAthensText = "Athenian warriors! March to the Pindus Mountains. Fight in the name of Athena!";
    public const string PindusSpartaText = "Spartan warriors! March to the Pindus Mountains. Fight in the name of Ares!";
    // [7] / [8]
    public const string NiMiniAthensText = "For a Athenian Level 50-89 warrior, Ni Mini Valley offers a chance to display your ability!";
    public const string NiMiniSpartaText = "For a Spartan Level 50-89 warrior, Ni Mini Valley offers a chance to display your ability!";
    // [9]
    public const string PansLabyrinthText = "Welcome to the mysterious Pan Labyrinth, with so much treasure to explore and discover!";
    // [12] "in 1 minute": evaluated one minute ahead.
    public const string TroyText = "Trojan Expedition to start in 1 minute! Find Event Transporter in the main city to get in action!";

    private static readonly TimeSpan TroyLead = TimeSpan.FromMinutes(1);

    private sealed record Entry(EventOpeningNotice Notice, Func<bool> Enabled, string GateKey, TimeSpan Lead);

    private static readonly Entry[] Entries =
    [
        new(new(GateKeys.TreasureLand, TreasureLandText), static () => EventDestinationsProtocol.TreasureLandEnabled,
            GateKeys.TreasureLand, TimeSpan.Zero),
        new(new(GateKeys.Sicily, SicilyText), static () => EventDestinationsProtocol.SicilyEnabled,
            GateKeys.Sicily, TimeSpan.Zero),
        new(new(GateKeys.PansLabyrinth, PansLabyrinthText), static () => PansLabyrinthProtocol.IsEnabled,
            GateKeys.PansLabyrinth, TimeSpan.Zero),
        new(new(LevelBandGates.Pindus, null, PindusAthensText, PindusSpartaText), static () => true,
            LevelBandGates.Pindus, TimeSpan.Zero),
        new(new(LevelBandGates.NiMiniUpper, null, NiMiniAthensText, NiMiniSpartaText), static () => true,
            LevelBandGates.NiMiniUpper, TimeSpan.Zero),
        new(new(LevelBandGates.LelantineFarm, null, FarmAthensText, FarmSpartaText), static () => true,
            LevelBandGates.LelantineFarm, TimeSpan.Zero),
        new(new(TroyEvent.GateKey, TroyText), static () => TroyEvent.EntryOn, TroyEvent.GateKey, TroyLead)
    ];

    public static IReadOnlyList<EventOpeningNotice> Notices { get; } = Entries.Select(static e => e.Notice).ToArray();

    /// <summary>Open state per notice key at <paramref name="instant"/> (plus each entry's lead). Disabled = closed.</summary>
    public static Dictionary<string, bool> Snapshot(RealmCalendar calendar, DateTimeOffset instant)
    {
        var result = new Dictionary<string, bool>(Entries.Length, StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            bool open;
            try
            {
                open = entry.Enabled() && GateSchedules.IsOpen(entry.GateKey, calendar, instant + entry.Lead);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                open = false;
            }

            result[entry.Notice.Key] = open;
        }

        return result;
    }

    /// <summary>Notices whose state went closed -> open. No previous snapshot (first tick) = nothing.</summary>
    public static IReadOnlyList<EventOpeningNotice> Due(
        IReadOnlyDictionary<string, bool>? previous, IReadOnlyDictionary<string, bool> current)
    {
        if (previous is null)
        {
            return [];
        }

        return Notices.Where(notice =>
                current.TryGetValue(notice.Key, out var now) && now &&
                previous.TryGetValue(notice.Key, out var before) && !before)
            .ToArray();
    }
}
