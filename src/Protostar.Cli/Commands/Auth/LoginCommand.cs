using System.ComponentModel;
using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth login</c> — authenticates to the registry via the OAuth Authorization Code
/// flow with PKCE over a loopback redirect, then stores the resulting tokens in the OS credential
/// store. The actual sign-in happens in the browser (the registry federates it to GitHub).
/// </summary>
internal sealed class LoginCommand : Command<LoginCommand.Settings>
{
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

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation) =>
        RunAsync(settings, cancellation).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(Settings settings, CancellationToken cancellation)
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

        using var client = new RegistryClient(registry);

        // Fail fast on an unreachable or incompatible registry before opening a browser.
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
            AnsiConsole.MarkupLine($"[red]The registry at[/] [grey]{Markup.Escape(registry.ToString())}[/] [red]did not respond to /v1/meta.[/]");
            return 1;
        }

        if (ApiCompatibility.Check(meta) is { } incompatibility)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(incompatibility)}[/]");
            return 1;
        }

        var verifier = Pkce.CreateVerifier();
        var challenge = Pkce.Challenge(verifier);
        var state = Pkce.CreateState();

        using var loopback = new LoopbackServer();
        var authorizeUrl = BuildAuthorizeUrl(registry, loopback.RedirectUri, challenge, state, settings.Provider);

        if (!settings.NoBrowser && BrowserLauncher.TryOpen(authorizeUrl))
        {
            AnsiConsole.MarkupLine("Opening your browser to sign in. Complete the sign-in there, then return here.");
        }
        else
        {
            AnsiConsole.MarkupLine("Open this URL to sign in:");
            // Write the URL raw (not through Spectre) so it is never word-wrapped and stays copy-pasteable.
            Console.WriteLine(authorizeUrl);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.TimeoutSeconds)));

        CallbackResult callback;
        try
        {
            callback = await loopback.WaitForCallbackAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[red]Timed out waiting for the browser sign-in.[/]");
            return 1;
        }

        if (callback.Error is not null)
        {
            var detail = callback.ErrorDescription is { Length: > 0 } d ? $": {d}" : ".";
            AnsiConsole.MarkupLine($"[red]Sign-in failed ({Markup.Escape(callback.Error)}){Markup.Escape(detail)}[/]");
            return 1;
        }

        if (!string.Equals(callback.State, state, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[red]State mismatch on the sign-in response (possible CSRF). Aborting.[/]");
            return 1;
        }

        if (string.IsNullOrEmpty(callback.Code))
        {
            AnsiConsole.MarkupLine("[red]The registry did not return an authorization code.[/]");
            return 1;
        }

        var token = await client.ExchangeCodeAsync(callback.Code, verifier, loopback.RedirectUri, cancellation);
        if (!token.IsSuccess)
        {
            var detail = token.ErrorDescription ?? token.Error ?? "the registry rejected the token request.";
            AnsiConsole.MarkupLine($"[red]Token exchange failed:[/] {Markup.Escape(detail)}");
            return 1;
        }

        var info = await client.GetUserInfoAsync(token.AccessToken!, cancellation);
        var login = info?.PreferredUsername ?? info?.GitHubLogin;

        var saved = new TokenStore().Save(new StoredToken
        {
            Registry = RegistryEndpoint.CredentialKey(registry),
            AccessToken = token.AccessToken!,
            RefreshToken = token.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn),
            Subject = info?.Sub,
            Login = login,
            Name = info?.Name,
        });

        if (!saved)
        {
            AnsiConsole.MarkupLine("[red]Signed in, but could not save the session to the OS credential store.[/]");
            return 1;
        }

        var who = login is { Length: > 0 } ? $" as [aqua]{Markup.Escape(login)}[/]" : string.Empty;
        AnsiConsole.MarkupLine($"[green]Signed in[/] to [grey]{Markup.Escape(registry.GetLeftPart(UriPartial.Authority))}[/]{who}.");
        return 0;
    }

    private static string BuildAuthorizeUrl(Uri registry, string redirectUri, string challenge, string state, string? provider)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = AuthConstants.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = AuthConstants.Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        // A provider hint lets the registry skip its chooser and forward straight to that provider.
        if (!string.IsNullOrWhiteSpace(provider))
            query["identity_provider"] = provider.Trim();

        var encoded = string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return new Uri(registry, "/connect/authorize") + "?" + encoded;
    }
}
