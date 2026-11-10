using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Characters;

/// <summary>
/// The operation identity a raw legacy client's lifecycle request is given.
/// </summary>
/// <remarks>
/// A legacy session has no secure operation transport, so the stock client cannot
/// name its own command - and it cannot be patched into one. Refusing the request
/// therefore meant a raw deployment could never create or delete a character at
/// all, which is what this identity exists to fix.
/// <para>
/// Deriving it from the request keeps the durable executor the single writer,
/// which is the whole reason lifecycle runs through it: the same request always
/// maps to the same command, so a retry replays the executor's receipt instead of
/// creating a second character. The namespace prefix keeps a derived id from ever
/// colliding with one a secure client generated.
/// </para>
/// </remarks>
internal static class LegacyCharacterLifecycleIdentity
{
    private static readonly byte[] Namespace =
        Convert.FromHexString("6F1C7A2E9B4D4E0A8C3F5D6E7A8B9C0D");

    /// <summary>
    /// The identity of one raw creation: the account, its realm and the name it
    /// asked for. A second request for another name is a different command, while
    /// a retry of this one is the same command.
    /// </summary>
    public static Guid ForCreate(int accountId, RealmId realmId, string name) =>
        Derive($"create:{accountId}:{realmId.Value}:{name}");

    /// <summary>The identity of one raw deletion, keyed by account and name.</summary>
    public static Guid ForDelete(int accountId, string name) =>
        Derive($"delete:{accountId}:{name}");

    private static Guid Derive(string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        var input = new byte[Namespace.Length + payload.Length];
        Namespace.CopyTo(input, 0);
        payload.CopyTo(input, Namespace.Length);
        var hash = SHA256.HashData(input);
        // The version and variant bits of a name-based (v5 shaped) identifier, so
        // a derived id is recognisable and never looks like a random one.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}
