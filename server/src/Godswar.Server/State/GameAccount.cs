namespace Godswar.Server.State;

internal sealed class GameAccount
{
    public int Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public DonatorTier DonatorTier { get; set; }

    public DateTimeOffset? DonatorExpiresAt { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed record StoredAccountCredential(
    GameAccount Account,
    string Verifier);
