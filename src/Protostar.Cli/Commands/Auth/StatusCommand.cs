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

        using var client = new RegistryClient(registry);

        // Refresh a stale access token if we can, so a verified status survives short sessions.
        if (stored.IsExpired && stored.RefreshToken is { Length: > 0 } refreshToken)
        {
            try
            {
                var refreshed = await client.RefreshAsync(refreshToken, cancellation);
                if (refreshed.IsSuccess)
                {
                    stored = stored with
                    {
                        AccessToken = refreshed.AccessToken!,
                        RefreshToken = refreshed.RefreshToken ?? stored.RefreshToken,
                        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn),
                    };
                    store.Save(stored);
                }
            }
            catch (HttpRequestException)
            {
                // Fall through to the offline view below.
            }
        }

        UserInfo? info = null;
        try
        {
            info = await client.GetUserInfoAsync(stored.AccessToken, cancellation);
        }
        catch (HttpRequestException)
        {
            // Registry unreachable; show the stored identity instead.
        }

        var login = info?.PreferredUsername ?? info?.GitHubLogin ?? stored.Login ?? "(unknown)";

        if (info is not null)
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
