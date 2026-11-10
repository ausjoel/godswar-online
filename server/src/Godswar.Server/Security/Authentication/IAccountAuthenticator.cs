namespace Godswar.Server.Security.Authentication;

internal interface IAccountAuthenticator
{
    Task<AccountAuthenticationResult> AuthenticateAsync(
        string username,
        ReadOnlyMemory<byte> password,
        CancellationToken cancellationToken = default);
}
