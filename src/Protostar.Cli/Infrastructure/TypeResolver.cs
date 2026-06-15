using Spectre.Console.Cli;

namespace Protostar.Cli.Infrastructure;

/// <summary>
/// Resolves types for Spectre.Console.Cli from a built <see cref="IServiceProvider"/>. Disposing it
/// disposes the provider, so any singleton <see cref="IDisposable"/> services are cleaned up on exit.
/// </summary>
internal sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;

    public TypeResolver(IServiceProvider provider) => _provider = provider;

    public object? Resolve(Type? type) => type is null ? null : _provider.GetService(type);

    public void Dispose()
    {
        if (_provider is IDisposable disposable)
            disposable.Dispose();
    }
}
