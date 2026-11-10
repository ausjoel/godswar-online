namespace Godswar.Server.Application.Operators;

internal static class OperatorAuthority
{
    public static async Task<bool> AllowsAsync(bool enabled, bool characterFlags, int accountId, int characterId,
        IReadOnlyCollection<int> allowedAccounts, Func<int,int,CancellationToken,Task<bool>>? readFlag,
        CancellationToken cancellationToken)
    {
        if (!enabled || accountId <= 0 || characterId <= 0) return false;
        if (!characterFlags) return allowedAccounts.Contains(accountId);
        if (readFlag is null) return false;
        try { return await readFlag(accountId, characterId, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Console.WriteLine($"[operator] authority unavailable: {error.GetType().Name}");
            return false;
        }
    }
}
