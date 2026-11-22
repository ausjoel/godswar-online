using System.Buffers.Binary;
using Godswar.Server.Application.Finalise2;
using Godswar.Server.Application.Rates;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

// W6-B (the admin 2026-11-13): the Wishing Pool wait read "100 minutes", its "another wish?" text had no answer, and
// every donator / Battle Pass account showed a nameless buff with a wrapped timer.
internal static class W6BWellAndStatusChecks
{
    public const string CheckName =
        "W6-B: Wishing Pool wait is whole seconds, common result offers Draw again, account-bonus icons are gated";

    public static Task RunAsync()
    {
        // NpcFunSkillbook.lua tail 2: seconds = (SubID - 2) / 100, minutes = (s - s % 60) / 60.
        static (int Minutes, int Seconds) ClientReads(int subId)
        {
            Check.Equal(2, subId % 100, "the wait uses the script's tail-2 family");
            var s = (subId - 2) / 100;
            return ((s - s % 60) / 60, s % 60);
        }

        Check.Equal((59, 59), ClientReads(GameClientHandler.EncodeWishingPoolWait(new TimeSpan(0, 59, 59))),
            "59:59 left reads 59 minutes 59 seconds (was 99 minutes 19 seconds)");
        Check.Equal((60, 0), ClientReads(GameClientHandler.EncodeWishingPoolWait(TimeSpan.FromHours(1))),
            "a full hour reads 60 minutes 0 seconds");
        Check.Equal((14, 30), ClientReads(GameClientHandler.EncodeWishingPoolWait(new TimeSpan(0, 14, 30))),
            "14:30 left reads 14 minutes 30 seconds");
        Check.Equal((0, 1), ClientReads(GameClientHandler.EncodeWishingPoolWait(TimeSpan.FromMilliseconds(200))),
            "a fraction of a second rounds up, never 0:00");
        Check.Equal((60, 0), ClientReads(GameClientHandler.EncodeWishingPoolWait(TimeSpan.FromHours(5))),
            "the wait never reads more than the one-hour interval");

        Check.True(GameClientHandler.WishingPoolCommonResultReply.SequenceEqual([4, 103]),
            "the common-book result is [4 Draw again, 103 text]");

        // Account-bonus statuses (no row in clients up to 1.5.0).
        var effects = new ClientStatusEffect[]
        {
            new(511, 28_800),
            new(ExperienceStatusIds.OctagramPatron, uint.MaxValue),
            new(ExperienceStatusIds.PremiumBattlePass, 414_468_000),
            new(ExperienceStatusIds.FactionAreaExperience, 43_200),
            new(1390, uint.MaxValue)
        };
        var hidden = ClientStatusWirePolicy.Apply(effects, showAccountBonusIcons: false);
        Check.True(hidden.Select(static e => e.StatusId).SequenceEqual([511u, 1390u]),
            "switch off: 1504 / 1506 / 1507 are not sent, stock statuses are");
        var shown = ClientStatusWirePolicy.Apply(effects, showAccountBonusIcons: true);
        Check.Equal(5, shown.Count, "switch on: every status is sent");
        Check.Equal(uint.MaxValue, shown.Single(static e => e.StatusId == 1507).RemainingSeconds,
            "a 2040 Battle Pass is sent as permanent, not as a wrapped 32-bit millisecond timer");
        Check.Equal(28_800u, shown.Single(static e => e.StatusId == 511).RemainingSeconds,
            "ordinary countdowns are untouched");
        Check.Equal(4_294_967u, ClientStatusWirePolicy.Apply(
                [new ClientStatusEffect(511, 4_294_967)], true)[0].RemainingSeconds,
            "the longest timer that still fits is kept");
        Check.Equal(uint.MaxValue, ClientStatusWirePolicy.Apply(
                [new ClientStatusEffect(511, 4_294_968)], true)[0].RemainingSeconds,
            "one second more becomes permanent");
        var untouched = new ClientStatusEffect[] { new(511, 60), new(1390, uint.MaxValue) };
        Check.True(ReferenceEquals(untouched, ClientStatusWirePolicy.Apply(untouched, false)),
            "a list with nothing to change is passed through");
        foreach (var id in new[] { 1500u, 1501u, 1502u, 1503u, 1504u, 1506u, 1507u })
        {
            Check.True(ClientStatusWirePolicy.IsAccountBonusStatus(id), $"{id} is an account-bonus status");
        }
        Check.True(!ClientStatusWirePolicy.IsAccountBonusStatus(1505) &&
                   !ClientStatusWirePolicy.IsAccountBonusStatus(511) &&
                   !ClientStatusWirePolicy.IsAccountBonusStatus(1007),
            "stock statuses are never hidden");

        var definition = ServerRateCatalog.Definitions.Single(
            static d => d.Key == Finalise2RateKeys.AccountBonusIcons);
        Check.Equal(1m, definition.DefaultValue, "B21: status.account_bonus_icons is on by default (live is on)");

        // The packet itself: switch off drops the icon and the count with it.
        ServerRates.Install(ServerRatesSnapshot.Create([KeyValuePair.Create(Finalise2RateKeys.AccountBonusIcons, 0m)], 1,
            DateTimeOffset.UtcNow, "check", "test")); // B21: the switch now defaults on
        var packet = PacketBuilder.PlayerStatusEffects(
            Program.CreateCharacterForW6B(),
            effects,
            ClientStatusAggregate.Empty);
        Check.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)),
            "10167 carries 2 statuses with the switch off");
        Check.Equal(511u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4)), "first id is 511");
        Check.Equal(1390u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(16, 4)), "second id is 1390");
        return Task.CompletedTask;
    }
}
