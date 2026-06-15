namespace Protostar.Cli.Harness;

/// <summary>
/// The set of harnesses protostar knows how to wire, resolved from the DI container. The single
/// extension point: register a new <see cref="IHarness"/> in <c>Program.cs</c> and it appears here.
/// </summary>
internal interface IHarnessCatalog
{
    /// <summary>Every registered harness.</summary>
    IReadOnlyList<IHarness> All { get; }

    /// <summary>Find a harness by its id (case-insensitive), or <c>null</c> if unknown.</summary>
    IHarness? ById(string id);

    /// <summary>The registered ids joined for display, e.g. in an "unknown harness" error message.</summary>
    string KnownIds { get; }
}
