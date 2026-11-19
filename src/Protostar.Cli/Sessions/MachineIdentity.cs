using System.Text.Json;
using Protostar.Cli.Auth;

namespace Protostar.Cli.Sessions;

/// <summary>This machine's identity in the registry: an id generated once and kept in <c>~/.protostar/machine.json</c>.</summary>
internal interface IMachineIdentity
{
    /// <summary>The machine, created and saved on first use.</summary>
    MachineInfo Get();

    /// <summary>Whether this machine has registered with <paramref name="registry"/>.</summary>
    bool IsRegisteredWith(Uri registry);

    /// <summary>Records that this machine registered with <paramref name="registry"/>.</summary>
    void MarkRegistered(Uri registry);
}

/// <summary>This machine's id, display name and operating system.</summary>
internal sealed record MachineInfo(Guid Id, string Name, string OperatingSystem);

/// <summary>Production <see cref="IMachineIdentity"/>.</summary>
/// <remarks>
/// The id survives CLI reinstalls because it lives in the config directory, not with the binary. Deleting
/// <c>machine.json</c> makes the machine register as a new one.
/// </remarks>
internal sealed class MachineIdentity : IMachineIdentity
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string FilePath => Path.Combine(ProtostarPaths.ConfigDir(), "machine.json");

    private MachineFile? _cached;

    /// <inheritdoc />
    public MachineInfo Get()
    {
        var file = Load();
        return new MachineInfo(file.Id, file.Name, OperatingSystemName());
    }

    /// <inheritdoc />
    public bool IsRegisteredWith(Uri registry) => Load().RegisteredWith.Contains(RegistryEndpoint.CredentialKey(registry));

    /// <inheritdoc />
    public void MarkRegistered(Uri registry)
    {
        var file = Load();
        if (file.RegisteredWith.Add(RegistryEndpoint.CredentialKey(registry)))
            Save(file);
    }

    private MachineFile Load()
    {
        if (_cached is not null)
            return _cached;

        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<MachineFile>(File.ReadAllText(FilePath)) is { Id: var id } existing && id != Guid.Empty)
                return _cached = existing;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: fall through and create a new identity.
        }

        var created = new MachineFile { Id = Guid.NewGuid(), Name = Environment.MachineName };
        Save(created);
        return _cached = created;
    }

    private static void Save(MachineFile file)
    {
        try
        {
            Directory.CreateDirectory(ProtostarPaths.ConfigDir());
            var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: an unsaved identity means this run uses a fresh id.
        }
    }

    private static string OperatingSystemName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unknown";

    private sealed class MachineFile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "unnamed";
        public HashSet<string> RegisteredWith { get; set; } = new(StringComparer.Ordinal);
    }
}
