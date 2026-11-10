namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Checks that read development-repository files which are not part of this public package
/// (AGENTS.md, PowerShell tools, extra compose/appsettings variants, docs, dev fixtures).
/// They are reported as SKIP here instead of failing on a missing file.
/// </summary>
internal static class PublicPackageSkips
{
    public const string Reason = "reads development-repository files not included in the public package";

    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Authoritative development-only training-dummy damage skills",
        "B13 privacy and deployment architecture ratchet",
        "B17 Redis coordination architecture ratchet",
        "B17 Redis coordination configuration policy",
        "B18C1 bounded opaque relay gateway",
        "B19 reconciliation architecture ratchet",
        "B20 legacy persistence telemetry coverage",
        "Development-only identity-bound training-dummy basic attacks",
        "Fail-closed durable registry composition",
        "Fail-closed server runtime and storage profiles",
        "Guarded historical outbox repair tool contract",
        "Offline LocalDevelopment fighter-level seal tool",
        "Offline unsigned fighter-EXP fixture safety",
        "Persisted per-realm game calendar authority",
    };

    public static bool Omits(string checkName) => Names.Contains(checkName);
}
