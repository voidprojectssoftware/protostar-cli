namespace Protostar.Cli.Harness;

/// <inheritdoc />
internal sealed class HarnessCatalog : IHarnessCatalog
{
    private readonly IReadOnlyList<IHarness> _all;

    public HarnessCatalog(IEnumerable<IHarness> harnesses) => _all = harnesses.ToList();

    /// <inheritdoc />
    public IReadOnlyList<IHarness> All => _all;

    /// <inheritdoc />
    public IHarness? ById(string id) =>
        _all.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public string KnownIds => string.Join(", ", _all.Select(h => h.Id));
}
