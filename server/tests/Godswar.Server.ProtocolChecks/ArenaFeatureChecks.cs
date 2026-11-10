using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// ARENA build: Duel Arena PvP admission, Parnassus entry rules and the AFK
/// auto-revival command. Also the regression guard for the shared PvP call sites:
/// with the gate off, and for every pair outside the arena combat floor with it on,
/// the new admission must equal the stock faction rule exactly.
/// </summary>
internal static class ArenaFeatureChecks
{
    public const string CheckName =
        "Arena build: duel arena PvP, Parnassus entry and AFK revival keep stock behaviour when off";

    public static Task RunAsync()
    {
        try
        {
            CheckCombatArea();
            CheckAdmission();
            CheckParnassus();
            CheckAfkRevivalCommand();
        }
        finally
        {
            ArenaFeatureGates.Override = null;
        }
        return Task.CompletedTask;
    }

    private static void CheckCombatArea()
    {
        foreach (var (x, z) in DuelArenaEntryDestinations.All)
        {
            Check.True(DuelArenaCombatArea.Contains(57, x, z), $"Gatekeeper entry point {x},{z} is on the combat floor");
        }
        Check.True(DuelArenaCombatArea.Contains(57, -48f, 40f), "the Ward's floor is the combat floor");
        foreach (var npc in DuelArenaCapturedLayout.Npcs.Where(static npc => npc.NpcKey != DuelArenaCapturedLayout.WardNpcKey))
        {
            Check.True(!DuelArenaCombatArea.Contains(57, npc.X, npc.Z), $"{npc.NpcKey} stands in the waiting area");
        }
        Check.True(!DuelArenaCombatArea.Contains(57, -104f, 96f), "transporter arrival is the waiting area");
        Check.True(!DuelArenaCombatArea.Contains(13, -5f, 32f) && !DuelArenaCombatArea.Contains(57, float.NaN, 0f),
            "other maps and bad positions are never the combat floor");
        Check.Equal((byte)57, DuelArenaCombatArea.ReviveLanding.MapId, "arena revive stays in the arena");
        Check.True(!DuelArenaCombatArea.Contains(57, DuelArenaCombatArea.ReviveLanding.X, DuelArenaCombatArea.ReviveLanding.Z),
            "arena revive lands in the waiting area");
    }

    private static void CheckAdmission()
    {
        var registry = new GameSessionRegistry(gameplayCatalogs: GameplayContentTestFixtures.Runtime);
        var catalog = GameplayContentTestFixtures.Runtime.PvpWorldAuthority;
        var now = DateTimeOffset.Parse("2026-11-03T10:00:00Z");
        Check.True(catalog.IsSafeZone(57), "fixture: map 57 is a safe zone for the stock rule");

        var pairs = new List<(GameCharacter A, GameCharacter B, string Name)>();
        foreach (var (mapA, xa, za, mapB, xb, zb, label) in new (byte, float, float, byte, float, float, string)[]
                 {
                     (57, -5, 24, 57, -5, 28, "arena floor"),
                     (57, -104, 96, 57, -104, 92, "arena waiting area"),
                     (57, -5, 24, 57, -104, 92, "floor to waiting area"),
                     (13, 100, -100, 13, 102, -100, "faction map 13"),
                     (68, 0, 0, 68, 2, 0, "Parnassus"),
                     (1, 165, -97, 1, 166, -97, "Athens city"),
                     (13, -5, 24, 57, -5, 24, "different maps")
                 })
        {
            foreach (var (campA, campB) in new (byte, byte)[] { (0, 0), (0, 1), (1, 1), (1, 0) })
            {
                pairs.Add((Player(100, mapA, xa, za, campA), Player(200, mapB, xb, zb, campB),
                    $"{label} camps {campA}/{campB}"));
            }
        }

        ArenaFeatureGates.Override = false;
        foreach (var (a, b, name) in pairs)
        {
            Check.Equal(catalog.EvaluateOpposingFaction(a, b, now), registry.EvaluatePlayerPvpAdmission(a, b, now),
                $"gate off: stock admission unchanged ({name})");
        }

        ArenaFeatureGates.Override = true;
        foreach (var (a, b, name) in pairs)
        {
            var arena = DuelArenaCombatArea.Contains(a.CurrentMap, a.PositionX, a.PositionZ) &&
                DuelArenaCombatArea.Contains(b.CurrentMap, b.PositionX, b.PositionZ);
            var result = registry.EvaluatePlayerPvpAdmission(a, b, now);
            if (!arena)
            {
                Check.Equal(catalog.EvaluateOpposingFaction(a, b, now), result,
                    $"gate on, outside the arena floor: stock admission unchanged ({name})");
                continue;
            }

            Check.True(result.Allowed && result.EntitlementKind == PvpEntitlementKind.MutualDuel &&
                result.Admits(a.Id, b.Id, 57), $"gate on: arena floor admits any two players ({name})");
        }

        var first = Player(100, 57, -5, 24, 1);
        var second = Player(200, 57, -5, 28, 1);
        var party = GameSessionRegistry.EvaluateDuelArenaPair(first, second, now, sameParty: true);
        Check.True(!party.Allowed && party.Failure == PvpEligibilityFailure.MissingEntitlement,
            "teammates cannot damage each other (IGG rule 3)");
        Check.True(!GameSessionRegistry.EvaluateDuelArenaPair(first, first, now, sameParty: false).Allowed,
            "no self target");
        second.CurrentHp = 0;
        Check.True(!GameSessionRegistry.EvaluateDuelArenaPair(first, second, now, sameParty: false).Allowed,
            "no dead target");

        var line = GameSessionRegistry.DuelArenaDefeatLine("Rigtest", (1, 0), "Réb", (0, 1));
        Check.Equal("Duel Arena: Rigtest (1W 0L) defeated R?b (0W 1L).", line, "defeat notice text");
        Check.Equal(260, PacketBuilder.ServerNote(line).Length, "defeat notice is a native server note");
        Check.True(PacketBuilder.ServerNote(GameSessionRegistry.DuelArenaDefeatLine(
            new string('x', 32), (999, 999), new string('y', 32), (999, 999))).Length == 260,
            "longest names still fit a server note");
    }

    private static void CheckParnassus()
    {
        var published = BattlefieldTransporterProtocol.AthensInitialMenuSubIds;
        ArenaFeatureGates.Override = false;
        Check.True(GameClientHandler.BattlefieldTransporterMenu(published).SequenceEqual(published),
            "gate off: the transporter menu is the published one");
        ArenaFeatureGates.Override = true;
        var menu = GameClientHandler.BattlefieldTransporterMenu(published);
        Check.True(menu.SequenceEqual(published.Append(1002)), "gate on: Parnassus (1002) appended once");
        Check.True(ParnassusProtocol.WithEntryButton(menu).SequenceEqual(menu), "never appended twice");

        var root = Enumerable.Repeat(-1, 18).ToArray();
        Check.True(ParnassusProtocol.IsEntryClick(1, 1002, root), "root click on 1002");
        Check.True(!ParnassusProtocol.IsEntryClick(1, 1001, root) && !ParnassusProtocol.IsEntryClick(2, 1002, root) &&
            !ParnassusProtocol.IsEntryClick(1, 1002, root.Take(17).ToArray()) &&
            !ParnassusProtocol.IsEntryClick(1, 1002, [1002, .. root.Skip(1)]), "other clicks are not Parnassus");
        foreach (var (time, open) in new (string, bool)[]
                 {
                     ("11:59", false), ("12:00", true), ("17:59", true), ("18:00", false), ("18:59", false),
                     ("19:00", true), ("23:54", true), ("23:55", false), ("00:30", false)
                 })
        {
            Check.Equal(open, ParnassusProtocol.IsOpen(TimeOnly.Parse(time)), $"IGG access period at {time}");
        }
        Check.Equal((220f, -211f), ParnassusProtocol.Arrival(0), "Sparta arrives at its Main City Teleporter point");
        Check.Equal((-212f, 223f), ParnassusProtocol.Arrival(1), "Athens arrives at its Main City Teleporter point");
        Check.True(ParnassusProtocol.IsReturnTransporter(68, "Parnassus_003") &&
            ParnassusProtocol.IsReturnTransporter(68, "Parnassus_004") &&
            !ParnassusProtocol.IsReturnTransporter(1, "Parnassus_003") &&
            !ParnassusProtocol.IsReturnTransporter(68, "Parnassus_001"), "only the two map-68 transporters");
        // Review fix: the return transporters answer with the switch off too (no stranding on rollback).
        foreach (var gate in new[] { false, true })
        {
            ArenaFeatureGates.Override = gate;
            Check.True(GameClientHandler.ServesParnassusReturn(68, "Parnassus_003") &&
                GameClientHandler.ServesParnassusReturn(68, "Parnassus_004"),
                $"gate {(gate ? "on" : "off")}: Parnassus return transporters still answer on map 68");
            Check.True(!GameClientHandler.ServesParnassusReturn(1, "Parnassus_003") &&
                !GameClientHandler.ServesParnassusReturn(0, "Parnassus_004") &&
                !GameClientHandler.ServesParnassusReturn(68, "Parnassus_001") &&
                !GameClientHandler.ServesParnassusReturn(1, "BattlefieldTransporter"),
                $"gate {(gate ? "on" : "off")}: no other NPC is taken over");
        }
        ArenaFeatureGates.Override = true;
        Check.Equal(1005, ParnassusProtocol.ReturnButton("Parnassus_003"), "Athens return button");
        Check.Equal(1006, ParnassusProtocol.ReturnButton("Parnassus_004"), "Sparta return button");
        Check.True(BattlefieldTransporterProtocol.AthensInitialMenuSubIds.All(static id => id != 1002) &&
            BattlefieldTransporterProtocol.SpartaInitialMenuSubIds.All(static id => id != 1002),
            "the hashed published menus are unchanged");
    }

    private static void CheckAfkRevivalCommand()
    {
        var (off, offReply) = GameClientHandler.ParseAfkRevivalCommand([], null, 10);
        Check.True(off is null && offReply.Contains("OFF", StringComparison.Ordinal) &&
            offReply.Contains("10 Gold", StringComparison.Ordinal), "status when off names the guide price");
        var (on, _) = GameClientHandler.ParseAfkRevivalCommand(["on"], null, 10);
        Check.True(on is { DelaySeconds: 10, Limit: 0 }, "default: 10 s, no limit");
        var (custom, reply) = GameClientHandler.ParseAfkRevivalCommand(["on", "30", "3"], null, 10);
        Check.True(custom is { DelaySeconds: 30, Limit: 3 } && reply.Contains("at most 3", StringComparison.Ordinal),
            "delay and limit");
        var (bad, badReply) = GameClientHandler.ParseAfkRevivalCommand(["on", "2"], custom, 10);
        Check.True(ReferenceEquals(bad, custom) && badReply.Contains("5-300", StringComparison.Ordinal),
            "a bad delay keeps the old setting");
        var (stopped, _) = GameClientHandler.ParseAfkRevivalCommand(["off"], custom, 10);
        Check.True(stopped is null, "off");
        foreach (var r in new[] { offReply, reply, badReply })
        {
            Check.Equal(260, PacketBuilder.ServerNote(r).Length, "replies are native server notes");
        }
        ArenaFeatureGates.Override = null;
        Check.Equal(ArenaFeatureGates.GuideAfkRevivalGold, 10, "IGG price");
    }

    private static GameCharacter Player(int id, byte map, float x, float z, byte camp) =>
        new()
        {
            Id = id,
            AccountId = id,
            Name = $"Player{id}",
            CurrentMap = map,
            PositionX = x,
            PositionZ = z,
            Camp = camp,
            Level = 120,
            CurrentHp = 10_000,
            MaxHp = 10_000
        };
}
