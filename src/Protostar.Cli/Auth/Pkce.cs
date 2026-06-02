using System.Security.Cryptography;
using System.Text;

namespace Protostar.Cli.Auth;

/// <summary>
/// PKCE (RFC 7636) helpers. A public client can't keep a secret, so each login generates a random
/// verifier and sends only its SHA-256 challenge up front; the verifier is revealed at token
/// exchange, proving the same client started and finished the flow.
/// </summary>
internal static class Pkce
{
    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
