using System.ComponentModel;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth login</c> — authenticates to the registry with OidcClient (Authorization Code +
/// PKCE over a loopback redirect, RFC 8252), then stores the resulting tokens in the file-based
/// credential store. The actual sign-in happens in the browser (the registry federates it to GitHub).
/// </summary>
internal sealed class LoginCommand : AsyncCommand<LoginCommand.Settings>
{
    private readonly ITokenStore _tokens;

    public LoginCommand(ITokenStore tokens) => _tokens = tokens;

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

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation) =>
        RunAsync(settings, cancellation);

    private async Task<int> RunAsync(Settings settings, CancellationToken cancellation)
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

        // Fail fast on an unreachable or incompatible registry before opening a browser.
        using (var client = new RegistryClient(registry))
        {
            RegistryMeta? meta;
            try
            {
                meta = await client.GetMetaAsync(cancellation);
            }
            catch (HttpRequestException ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not reach the registry at[/] [grey]{Markup.Escape(registry.ToString())}[/]: {Markup.Escape(ex.Message)}");
                return 1;
            }

            if (meta is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(registry.GetLeftPart(UriPartial.Authority))} is not a protostar registry[/] (no valid /v1/meta response).");
                AnsiConsole.MarkupLine("[grey]If the registry runs under .NET Aspire, use the 'api' resource URL from the dashboard, not the dashboard URL.[/]");
                return 1;
            }

            if (ApiCompatibility.Check(meta) is { } incompatibility)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(incompatibility)}[/]");
                return 1;
            }
        }

        var browser = new LoopbackBrowser(openBrowser: !settings.NoBrowser);
        var oidc = OidcClientFactory.Create(registry, browser);

        var request = new LoginRequest();
        // A provider hint lets the registry skip its chooser and forward straight to that provider.
        if (!string.IsNullOrWhiteSpace(settings.Provider))
        {
            request.FrontChannelExtraParameters = new Parameters
            {
                new KeyValuePair<string, string>("identity_provider", settings.Provider.Trim()),
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.TimeoutSeconds)));

        LoginResult result;
        try
        {
            result = await oidc.LoginAsync(request, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[red]Timed out waiting for the browser sign-in.[/]");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not reach the registry at[/] [grey]{Markup.Escape(registry.ToString())}[/]: {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (result.IsError)
        {
            var detail = result.ErrorDescription is { Length: > 0 } d ? d : result.Error ?? "the registry rejected the sign-in.";
            AnsiConsole.MarkupLine($"[red]Sign-in failed:[/] {Markup.Escape(detail)}");
            return 1;
        }

        // With LoadProfile enabled, OidcClient has already merged the userinfo claims (including the
        // custom github_login) onto the principal, so no extra round trip is needed.
        var user = result.User;
        var login = user?.FindFirst("preferred_username")?.Value ?? user?.FindFirst("github_login")?.Value;

        var saved = _tokens.Save(new StoredToken
        {
            Registry = RegistryEndpoint.CredentialKey(registry),
            AccessToken = result.AccessToken!,
            RefreshToken = result.RefreshToken,
            ExpiresAtUtc = result.AccessTokenExpiration,
            Subject = user?.FindFirst("sub")?.Value,
            Login = login,
            Name = user?.FindFirst("name")?.Value,
        });

        if (!saved)
        {
            AnsiConsole.MarkupLine("[red]Signed in, but could not save the session to the credential store.[/]");
            return 1;
        }

        var who = login is { Length: > 0 } ? $" as [aqua]{Markup.Escape(login)}[/]" : string.Empty;
        AnsiConsole.MarkupLine($"[green]Signed in[/] to [grey]{Markup.Escape(registry.GetLeftPart(UriPartial.Authority))}[/]{who}.");
        return 0;
    }
}
