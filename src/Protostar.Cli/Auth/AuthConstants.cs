namespace Protostar.Cli.Auth;

/// <summary>
/// Shared constants for the OAuth loopback flow against the protostar registry. These mirror what
/// the registry seeds for the <c>protostar-cli</c> public client (client id, callback path, scopes).
/// </summary>
internal static class AuthConstants
{
    public const string ClientId = "protostar-cli";
    public const string CallbackPath = "/callback";

    // offline_access yields a refresh token (the client is granted the refresh-token flow).
    public const string Scopes = "openid profile email registry offline_access";

    // The API major this CLI speaks. Checked against the registry's advertised apiMajors on connect.
    public const int SupportedApiMajor = 1;

    public const string RegistryEnvVar = "PROTOSTAR_REGISTRY_URL";

    // Dev default: the registry's pinned local Aspire port. Override with --registry or
    // PROTOSTAR_REGISTRY_URL (this becomes the deployed URL once a registry is hosted).
    public const string DefaultRegistryUrl = "https://localhost:7443";
}
