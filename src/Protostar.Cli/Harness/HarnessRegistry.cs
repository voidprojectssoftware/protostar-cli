namespace Protostar.Cli.Harness;

/// <summary>
/// The set of harnesses protostar knows how to wire. This is the single extension point: to
/// support a new harness, implement <see cref="IHarness"/> and add it to <see cref="All"/>.
/// </summary>
internal static class HarnessRegistry
{
    public static IReadOnlyList<IHarness> All { get; } =
    [
        new ClaudeCodeHarness(),
    ];

    /// <summary>Find a harness by its id (case-insensitive), or null if unknown.</summary>
    public static IHarness? ById(string id) =>
        All.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));
}
