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

    // Dev default. Override with --registry or PROTOSTAR_REGISTRY_URL until a registry is deployed.
    public const string DefaultRegistryUrl = "https://localhost:5099";

    // Credential storage. Tokens are keyed by the registry's authority (a URI), so logging into
    // different registries keeps separate sessions.
    public const string CredentialService = "protostar";
    public const string CredentialAccount = "protostar";
}
