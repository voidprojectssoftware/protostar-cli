using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth logout</c>: removes the stored session for the registry from the credential store.
/// </summary>
/// <remarks>
/// The saved default registry is kept: it says which registry to use, not who is signed in, so after a logout
/// the next <c>auth login</c> and every hook still go to the same registry.
/// </remarks>
internal sealed class LogoutCommand : Command<LogoutCommand.Settings>
{
    private readonly ITokenStore _tokens;

    public LogoutCommand(ITokenStore tokens) => _tokens = tokens;

    public sealed class Settings : AuthSettings;

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(settings.Registry);
        }
        catch (FormatException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var authority = registry.GetLeftPart(UriPartial.Authority);

        if (_tokens.Load(registry) is null)
        {
            AnsiConsole.MarkupLine($"Not logged in to [grey]{Markup.Escape(authority)}[/].");
            return 0;
        }

        _tokens.Delete(registry);
        AnsiConsole.MarkupLine($"[green]Signed out[/] of [grey]{Markup.Escape(authority)}[/].");
        return 0;
    }
}
