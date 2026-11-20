using System.Text.Json;
using System.Text.Json.Nodes;

namespace Protostar.Cli.Auth;

/// <summary>Records the registry the user signed in to as the default for every later command.</summary>
/// <remarks>
/// Written by <c>auth login</c> and <c>setup</c> once sign-in succeeds, and read by <see cref="RegistryEndpoint"/>,
/// so a hook started without <c>--registry</c> or <c>PROTOSTAR_REGISTRY_URL</c> still reaches that registry.
/// </remarks>
internal interface IDefaultRegistryStore
{
    /// <summary>Saves <paramref name="registry"/> as the default; returns false when the config file cannot be written.</summary>
    bool Save(Uri registry);
}

/// <summary>Production <see cref="IDefaultRegistryStore"/>: the <c>registry</c> field of <see cref="ProtostarPaths.ConfigFile"/>.</summary>
/// <remarks>
/// The file is a JSON object with one field today, <c>registry</c>. It is read leniently: a missing, unreadable or
/// malformed file, or a <c>registry</c> that is not a string, counts as no default. Writing sets only
/// <c>registry</c> and keeps every other field, so an older CLI never drops what a newer one wrote.
/// </remarks>
internal sealed class DefaultRegistryStore : IDefaultRegistryStore
{
    private const string RegistryField = "registry";

    /// <inheritdoc />
    public bool Save(Uri registry)
    {
        try
        {
            var config = ReadObject() ?? [];
            config[RegistryField] = RegistryEndpoint.CredentialKey(registry);

            var path = ProtostarPaths.ConfigFile();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The saved default registry, or <c>null</c> when there is none.</summary>
    /// <remarks>Returned as written; <see cref="RegistryEndpoint"/> validates it like any other source.</remarks>
    public static string? Load() =>
        ReadObject()?[RegistryField] is JsonValue value && value.TryGetValue<string>(out var registry) && !string.IsNullOrWhiteSpace(registry)
            ? registry
            : null;

    private static JsonObject? ReadObject()
    {
        var path = ProtostarPaths.ConfigFile();
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
