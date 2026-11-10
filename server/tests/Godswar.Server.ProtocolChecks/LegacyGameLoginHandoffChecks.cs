using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

// LOGINFIX (2026-11-05): the legacy game login is admitted only after a password login for the same
// account from the same address, once, within the lifetime. End-to-end coverage: Development/loginfix-2026-11-05.
internal static class LegacyGameLoginHandoffChecks
{
    public static Task RunAsync()
    {
        LegacyGameLoginHandoff.ClearForTests();
        const long now = 1_000_000;
        var life = (long)LegacyGameLoginHandoff.Lifetime.TotalMilliseconds;

        Check.Equal("1.2.3.4", LegacyGameLoginHandoff.AddressOf("1.2.3.4:5678"), "IPv4 endpoint keeps the address only");
        Check.Equal("1.2.3.4", LegacyGameLoginHandoff.AddressOf("[::ffff:1.2.3.4]:5678"), "IPv4-mapped IPv6 maps to IPv4");
        Check.True(LegacyGameLoginHandoff.AddressOf("unknown") is null, "a non-IP endpoint has no address");
        Check.True(LegacyGameLoginHandoff.AddressOf("fixture:1234") is null, "a fixture endpoint has no address");

        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "1.2.3.4:1", now), "no password login: refused");

        LegacyGameLoginHandoff.Issue(7, "1.2.3.4:1000", now);
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(8, "1.2.3.4:1001", now + 1), "another account from the same address: refused");
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "5.6.7.8:1001", now + 1), "same account from another address: refused");
        Check.Equal(true, LegacyGameLoginHandoff.TryConsume(7, "[::ffff:1.2.3.4]:1001", now + 1), "same account and address (other port, mapped form): admitted");
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "1.2.3.4:1002", now + 2), "the record is used up");

        LegacyGameLoginHandoff.Issue(7, "1.2.3.4:1000", now);
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "1.2.3.4:1001", now + life), "at the lifetime: refused");

        LegacyGameLoginHandoff.Issue(7, "1.2.3.4:1000", now);
        LegacyGameLoginHandoff.Issue(9, "9.9.9.9:1", now + life + 1);   // issuing prunes expired records
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "1.2.3.4:1001", now + 1), "expired records are pruned");
        Check.Equal(true, LegacyGameLoginHandoff.TryConsume(9, "9.9.9.9:2", now + life), "a fresh record survives pruning");

        var grace = (long)LegacyGameLoginHandoff.ReconnectGrace.TotalMilliseconds;
        LegacyGameLoginHandoff.Issue(7, "1.2.3.4:1000", now, LegacyGameLoginHandoff.ReconnectGrace);
        Check.Equal(true, LegacyGameLoginHandoff.TryConsume(7, "1.2.3.4:1001", now + grace - 1), "reconnect grace: admitted inside the grace");
        LegacyGameLoginHandoff.Issue(7, "1.2.3.4:1000", now, LegacyGameLoginHandoff.ReconnectGrace);
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "5.6.7.8:1001", now + 1), "reconnect grace: another address refused");
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "1.2.3.4:1001", now + grace), "reconnect grace: refused at its end");

        LegacyGameLoginHandoff.Issue(7, "unknown", now);
        Check.Equal(false, LegacyGameLoginHandoff.TryConsume(7, "unknown", now + 1), "a non-IP endpoint never admits");

        Check.Equal(true, LegacyGameLoginHandoff.ReadEnabled(null), "unset: on");
        Check.Equal(true, LegacyGameLoginHandoff.ReadEnabled("1"), "1: on");
        Check.Equal(false, LegacyGameLoginHandoff.ReadEnabled("0"), "0: off");
        Check.Equal(false, LegacyGameLoginHandoff.ReadEnabled(" false "), "false: off");
        Check.Equal(false, LegacyGameLoginHandoff.ReadEnabled("OFF"), "off: off");
        LegacyGameLoginHandoff.ClearForTests();
        return Task.CompletedTask;
    }
}
