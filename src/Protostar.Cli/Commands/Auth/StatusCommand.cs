using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth status</c>: reports which registry is in use and why, whether there is a stored session for it and,
/// if reachable, verifies it with the registry (refreshing an expired access token when possible).
/// Works offline: with no stored session it simply reports "Not logged in".
/// </summary>
internal sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    private readonly ISignInService _signIn;

    public StatusCommand(ISignInService signIn) => _signIn = signIn;

    public sealed class Settings : AuthSettings;

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        ResolvedRegistry registry;
        try
        {
            registry = RegistryEndpoint.ResolveWithSource(settings.Registry);
        }
        catch (FormatException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        return SignInPresenter.RenderStatus(await _signIn.CheckAsync(registry.Url, cancellation), registry.Source);
    }
}
