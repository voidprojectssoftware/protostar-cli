using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Protostar.Cli.Infrastructure;

/// <summary>
/// Bridges Spectre.Console.Cli's <see cref="ITypeRegistrar"/> onto a Microsoft.Extensions.DependencyInjection
/// <see cref="IServiceCollection"/>, so commands and their dependencies are resolved from the container.
/// </summary>
/// <remarks>
/// Spectre calls <see cref="Register"/> for each command type during configuration, then <see cref="Build"/>
/// once it needs to resolve. Because the container is built lazily, services registered before
/// <c>app.Run</c> and the command types Spectre registers during configuration end up in the same provider.
/// </remarks>
internal sealed class TypeRegistrar : ITypeRegistrar
{
    private readonly IServiceCollection _services;

    public TypeRegistrar(IServiceCollection services) => _services = services;

    public ITypeResolver Build() => new TypeResolver(_services.BuildServiceProvider());

    public void Register(Type service, Type implementation) =>
        _services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        _services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _services.AddSingleton(service, _ => factory());
    }
}
