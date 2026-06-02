using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth logout</c> — removes the stored session for the registry from the OS
/// credential store.
/// </summary>
internal sealed class LogoutCommand : Command<LogoutCommand.Settings>
{
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
        var store = new TokenStore();

        if (store.Load(registry) is null)
        {
            AnsiConsole.MarkupLine($"Not logged in to [grey]{Markup.Escape(authority)}[/].");
            return 0;
        }

        store.Delete(registry);
        AnsiConsole.MarkupLine($"[green]Signed out[/] of [grey]{Markup.Escape(authority)}[/].");
        return 0;
    }
}
