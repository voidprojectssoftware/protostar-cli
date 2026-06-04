using System.Security.Claims;
using Protostar.Cli.Auth;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>
/// <c>protostar auth status</c> — reports whether there is a stored session for the registry and,
/// if reachable, verifies it by calling the userinfo endpoint (refreshing an expired access token
/// when possible). Works offline: with no stored session it simply reports "Not logged in".
/// </summary>
internal sealed class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : AuthSettings;

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation) =>
        RunAsync(settings, cancellation);

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

        var authority = registry.GetLeftPart(UriPartial.Authority);
        var store = new TokenStore();
        var stored = store.Load(registry);

        if (stored is null)
        {
            AnsiConsole.MarkupLine($"Not logged in to [grey]{Markup.Escape(authority)}[/].");
            return 0;
        }

        var oidc = OidcClientFactory.Create(registry);

        // Refresh a stale access token if we can, so a verified status survives short sessions.
        if (stored.IsExpired && stored.RefreshToken is { Length: > 0 } refreshToken)
        {
            try
            {
                var refreshed = await oidc.RefreshTokenAsync(refreshToken, backChannelParameters: null, scope: null, cancellation);
                if (!refreshed.IsError && !string.IsNullOrEmpty(refreshed.AccessToken))
                {
                    stored = stored with
                    {
                        AccessToken = refreshed.AccessToken,
                        RefreshToken = string.IsNullOrEmpty(refreshed.RefreshToken) ? stored.RefreshToken : refreshed.RefreshToken,
                        ExpiresAtUtc = refreshed.AccessTokenExpiration,
                    };
                    store.Save(stored);
                }
            }
            catch (HttpRequestException)
            {
                // Fall through to the offline view below.
            }
        }

        IEnumerable<Claim>? claims = null;
        try
        {
            var info = await oidc.GetUserInfoAsync(stored.AccessToken, cancellation);
            if (!info.IsError)
                claims = info.Claims;
        }
        catch (HttpRequestException)
        {
            // Registry unreachable; show the stored identity instead.
        }

        var verifiedLogin = claims?.FirstOrDefault(c => c.Type == "preferred_username")?.Value
            ?? claims?.FirstOrDefault(c => c.Type == "github_login")?.Value;
        var login = verifiedLogin ?? stored.Login ?? "(unknown)";

        if (claims is not null)
        {
            AnsiConsole.MarkupLine($"[green]Logged in[/] to [grey]{Markup.Escape(authority)}[/] as [aqua]{Markup.Escape(login)}[/].");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Logged in[/] to [grey]{Markup.Escape(authority)}[/] as [aqua]{Markup.Escape(login)}[/] " +
                "[grey](could not verify with the registry; it may be unreachable or the session expired).[/]");
        }

        return 0;
    }
}
