namespace Godswar.Server.Application.Operators;

// ROUND2: loopback management endpoints for live edits of ONLINE characters
// (GodsWar-2.51/Polish/PATCHES-ROUND2.md). The management server (Operations)
// cannot reference the game layer, so the game registers its handler here at
// start-up, like the RATES hooks in ServerRates.

/// <summary>One /admin request: path, and the JSON body (empty for GET).</summary>
internal sealed record OperatorAdminRequest(
    string Method,
    string Path,
    byte[] Body);

/// <summary>An HTTP status and a JSON body.</summary>
internal sealed record OperatorAdminResponse(
    int StatusCode,
    string Json);

internal static class OperatorAdminApi
{
    /// <summary>Set by the game at start-up; null = 503.</summary>
    public static Func<OperatorAdminRequest, CancellationToken, Task<OperatorAdminResponse>>?
        Handler { get; set; }
}
