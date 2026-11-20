using System.ComponentModel;
using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth login</c>: signs in to the registry through the browser and stores the session.
/// </summary>
/// <remarks>The flow lives in <see cref="SignInService"/>, shared with <c>protostar setup</c>.</remarks>
internal sealed class LoginCommand : AsyncCommand<LoginCommand.Settings>
{
    private readonly ISignInService _signIn;

    public LoginCommand(ISignInService signIn) => _signIn = signIn;

    public sealed class Settings : AuthSettings
    {
        [CommandOption("--provider <NAME>")]
        [Description("Skip the registry's sign-in chooser and go straight to this provider (e.g. github).")]
        public string? Provider { get; init; }

        [CommandOption("--no-browser")]
        [Description("Print the sign-in URL instead of opening a browser automatically.")]
        public bool NoBrowser { get; init; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("How long to wait for the browser sign-in to complete (default 300).")]
        public int TimeoutSeconds { get; init; } = 300;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
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

        var options = new SignInOptions(settings.Provider, settings.NoBrowser, TimeSpan.FromSeconds(settings.TimeoutSeconds));
        return SignInPresenter.RenderSignIn(await _signIn.SignInAsync(registry, options, cancellation));
    }
}
