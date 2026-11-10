using Godswar.Server.Application.Gates;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

// PAN CHAIN (W3, the admin F-45/F-49/F-50/F-37/F-70): switches, rosters, windows, the GENERATED draw table.
internal static class PanChainChecks
{
    public const string CheckName = "PAN CHAIN: Treasure Land, Sicily, envoys and the Avatar draw are gated and well formed";

    private static readonly string[] Variables =
    [
        EventDestinationsProtocol.TreasureLandVariable, EventDestinationsProtocol.TreasureLandWindowVariable,
        EventDestinationsProtocol.SicilyVariable, EventDestinationsProtocol.SicilyWindowVariable,
        PanInvitationProtocol.EnvoyVariable, PanInvitationProtocol.AvatarDrawVariable, PansLabyrinthProtocol.FeatureVariable
    ];

    public static Task RunAsync()
    {
        var saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Variables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            CheckSwitchedOff();
            CheckDrawTable();
            CheckWindows();
            CheckGates();
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.TreasureLandVariable, "1");
            Environment.SetEnvironmentVariable(EventDestinationsProtocol.SicilyVariable, "1");
            CheckRosters();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        return Task.CompletedTask;
    }

    private static void CheckSwitchedOff()
    {
        Check.True(!EventDestinationsProtocol.TreasureLandEnabled && !EventDestinationsProtocol.SicilyEnabled,
            "destinations off by default");
        Check.True(!PanInvitationProtocol.EnvoyEnabled && !PanInvitationProtocol.AvatarDrawEnabled, "envoys/draw off by default");
        Check.True(!EventDestinationsProtocol.IsTreasureLandClick("Sparta_072", 1, 202), "202 not intercepted while off");
        Check.True(!EventDestinationsProtocol.IsSicilyClick("Athens_072", 1, 700), "700 not intercepted while off");
        IReadOnlyList<NpcSpawnDefinition> empty = [];
        Check.Equal(0, EventDestinationsProtocol.AddNpcs(25, empty).Count, "map 25 roster unchanged while off");
        Check.Equal(0, EventDestinationsProtocol.AddNpcs(32, empty).Count, "map 32 roster unchanged while off");
        // The draw needs the labyrinth switch as well.
        Environment.SetEnvironmentVariable(PanInvitationProtocol.AvatarDrawVariable, "1");
        Check.True(!PanInvitationProtocol.AvatarDrawEnabled, "draw stays off without GODSWAR_PAN_LABYRINTH");
        Environment.SetEnvironmentVariable(PanInvitationProtocol.AvatarDrawVariable, null);
    }

    private static void CheckDrawTable()
    {
        var entries = PanAvatarDrawTable.Entries;
        Check.True(entries.Count >= 5, "draw table has rows");
        Check.True(entries.All(static e => e.Quantity is >= 1 and <= 99), "every draw quantity is 1-99 (stacks never above 99)");
        Check.True(entries.All(static e => e.Weight > 0 && e.ItemId != 0), "every draw row has an item and a weight");
        Check.True(entries.All(static e => !PanInvitationProtocol.IsPart(e.ItemId) && e.ItemId != PanInvitationProtocol.CertificateItemId),
            "the draw never pays parts or certificates");
        Check.Equal(entries[0].ItemId, PanAvatarDrawTable.Pick(0).ItemId, "roll 0 picks the first row");
        Check.Equal(entries[^1].ItemId, PanAvatarDrawTable.Pick(PanAvatarDrawTable.TotalWeight - 1).ItemId, "last roll picks the last row");
        var threw = false;
        try
        {
            PanAvatarDrawTable.Parse("item_id,quantity,weight,note\n4211,100,1,too many\n");
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        Check.True(threw, "a quantity above 99 is rejected");
    }

    private static void CheckWindows()
    {
        // 2026-11-08 is a Sunday, 2026-11-13 a Friday (realm time).
        Check.True(EventDestinationsProtocol.TreasureLandOpenAt(new DateTime(2026, 11, 8, 22, 30, 0)), "Treasure Land open Sun 22:30");
        Check.True(!EventDestinationsProtocol.TreasureLandOpenAt(new DateTime(2026, 11, 8, 23, 0, 0)), "Treasure Land closed Sun 23:00");
        Check.True(!EventDestinationsProtocol.TreasureLandOpenAt(new DateTime(2026, 11, 9, 22, 30, 0)), "Treasure Land closed Mon");
        Check.True(EventDestinationsProtocol.SicilyFreeAt(new DateTime(2026, 11, 13, 22, 0, 0)), "Sicily free Fri 22:00");
        Check.True(!EventDestinationsProtocol.SicilyFreeAt(new DateTime(2026, 11, 13, 21, 59, 0)), "Sicily not free Fri 21:59");
        Check.True(EventDestinationsProtocol.SicilySpellForbiddenAt(new DateTime(2026, 11, 13, 10, 0, 0)), "no spell on a Friday");
        Check.True(!EventDestinationsProtocol.SicilySpellForbiddenAt(new DateTime(2026, 11, 12, 10, 0, 0)), "spell on a Thursday");
        Check.Equal(new DateOnly(2026, 11, 9), PanInvitationProtocol.WeekKey(new DateOnly(2026, 11, 9)), "Monday keys its own week");
        Check.Equal(new DateOnly(2026, 11, 9), PanInvitationProtocol.WeekKey(new DateOnly(2026, 11, 15)), "Sunday keys the Monday before");
    }

    private static void CheckGates()
    {
        var treasure = GateCatalog.Find(GateKeys.TreasureLand);
        var sicily = GateCatalog.Find(GateKeys.Sicily);
        Check.True(treasure is not null && sicily is not null, "catalogue has Treasure Land and Sicily");
        Check.Equal((60, 200), (EventDestinationsProtocol.TreasureLandMinimumLevel, EventDestinationsProtocol.TreasureLandMaximumLevel),
            "F-70 Treasure Land 60-140 -> 60-200");
        Check.Equal((25, 200), (EventDestinationsProtocol.SicilyMinimumLevel, EventDestinationsProtocol.SicilyMaximumLevel),
            "F-70 Sicily 25-140 -> 25-200");
    }

    private static void CheckRosters()
    {
        IReadOnlyList<NpcSpawnDefinition> empty = [];
        var agate = EventDestinationsProtocol.AddNpcs(25, empty);
        var sicily = EventDestinationsProtocol.AddNpcs(32, empty);
        Check.Equal(8, agate.Count, "Treasure Land roster");
        Check.Equal(6, sicily.Count, "Sicily roster");
        var all = agate.Concat(sicily).Concat(PansLabyrinthProtocol.Npcs()).ToList();
        Check.Equal(all.Count, all.Select(static n => n.ObjectId).Distinct().Count(), "object ids unique across Pan / Agate / Sicily");
        Check.True(all.All(static n => n.ObjectId is >= 1500 and <= 7999), "object ids inside the client NPC range");
        Check.True(agate.Single(static n => n.NpcKey == "Agate_011") is { X: -190f, Z: 8f }, "Athenian Quartermaster at the Address.ini point");
        Check.True(sicily.Single(static n => n.NpcKey == "Sicily_002") is { X: -168f, Z: -198f }, "Sparta Transporter at the Address.ini point");
        Check.True(EventDestinationsProtocol.IsHomeTransporter(sicily.Single(static n => n.NpcKey == "Sicily_001")), "Sicily_001 sends home");
        Check.True(EventDestinationsProtocol.IsTreasureLandClick("Sparta_072", 1, 202), "202 intercepted while on");
        Check.True(EventDestinationsProtocol.IsSicilyClick("Athens_072", 1, 700), "700 intercepted while on");
        Check.Equal(8, EventDestinationsProtocol.AddNpcs(25, agate).Count, "re-adding the roster does not duplicate it");
    }
}
