using System.Text;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.ProtocolChecks;

internal sealed class FixedPasswordAccountAuthenticator(
    string expectedPassword = "password",
    int accountId = 7) : IAccountAuthenticator
{
    private readonly byte[] _expectedPassword =
        Encoding.ASCII.GetBytes(expectedPassword);

    public int Calls { get; private set; }

    public string? LastUsername { get; private set; }

    public byte[] LastPassword { get; private set; } = [];

    public Task<AccountAuthenticationResult> AuthenticateAsync(
        string username,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastUsername = username;
        LastPassword = password.ToArray();
        var accepted = password.Span.SequenceEqual(_expectedPassword);
        return Task.FromResult(
            new AccountAuthenticationResult(
                accepted
                    ? AccountAuthenticationStatus.Accepted
                    : AccountAuthenticationStatus.Rejected,
                accepted
                    ? new AccountIdentity(accountId, username)
                    : null));
    }
}
