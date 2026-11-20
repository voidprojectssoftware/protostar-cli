using Spectre.Console;

namespace Protostar.Cli.Auth;

/// <summary>The console face of <see cref="ISignInService"/> for <c>auth login</c> and <c>auth status</c>.</summary>
internal static class SignInPresenter
{
    /// <summary>Renders a sign-in and returns the exit code (0 when signed in, otherwise 1).</summary>
    public static int RenderSignIn(SignInResult result)
    {
        if (!result.Succeeded)
        {
            foreach (var line in Problem(result))
                AnsiConsole.MarkupLine(line);
            return 1;
        }

        var who = result.Login is { Length: > 0 } login ? $" as [aqua]{Markup.Escape(login)}[/]" : string.Empty;
        AnsiConsole.MarkupLine($"[green]Signed in[/] to [grey]{Markup.Escape(Authority(result.Registry))}[/]{who}.");
        return 0;
    }

    /// <summary>Renders <c>auth status</c>; always exits 0, since "not logged in" is an answer, not an error.</summary>
    /// <param name="check">The session check.</param>
    /// <param name="source">Where the registry checked was chosen, shown so a surprising registry can be traced.</param>
    public static int RenderStatus(SessionCheck check, RegistrySource source)
    {
        var authority = Markup.Escape(Authority(check.Registry));
        var login = Markup.Escape(check.Login ?? "(unknown)");
        switch (check.Verification)
        {
            case SessionVerification.NoSession:
                AnsiConsole.MarkupLine($"Not logged in to [grey]{authority}[/].");
                break;
            case SessionVerification.Verified:
                AnsiConsole.MarkupLine($"[green]Logged in[/] to [grey]{authority}[/] as [aqua]{login}[/].");
                break;
            default:
                AnsiConsole.MarkupLine($"[yellow]Logged in[/] to [grey]{authority}[/] as [aqua]{login}[/] " +
                    "[grey](could not verify with the registry; it may be unreachable or the session expired).[/]");
                break;
        }

        AnsiConsole.MarkupLine($"[grey]Registry from {Markup.Escape(Describe(source))}.[/]");
        return 0;
    }

    internal static string Describe(RegistrySource source) => source switch
    {
        RegistrySource.Flag => "--registry",
        RegistrySource.Environment => AuthConstants.RegistryEnvVar,
        RegistrySource.ConfigFile => $"{ProtostarPaths.ConfigFile()} (saved by setup or auth login)",
        _ => "the built-in default",
    };

    /// <summary>Why a sign-in failed, as markup lines; also used by <c>setup</c>.</summary>
    internal static IEnumerable<string> Problem(SignInResult result)
    {
        var registry = Markup.Escape(result.Registry.ToString());
        var detail = Markup.Escape(result.Detail ?? string.Empty);
        switch (result.Failure)
        {
            case SignInFailure.RegistryUnreachable:
                yield return $"[red]Could not reach the registry at[/] [grey]{registry}[/]: {detail}";
                break;
            case SignInFailure.NotARegistry:
                yield return $"[red]{Markup.Escape(Authority(result.Registry))} is not a protostar registry[/] (no valid /v1/meta response).";
                yield return "[grey]If the registry runs under .NET Aspire, use the 'api' resource URL from the dashboard, not the dashboard URL.[/]";
                break;
            case SignInFailure.Incompatible:
                yield return $"[red]{detail}[/]";
                break;
            case SignInFailure.TimedOut:
                yield return "[red]Timed out waiting for the browser sign-in.[/]";
                break;
            case SignInFailure.Rejected:
                yield return $"[red]Sign-in failed:[/] {detail}";
                break;
            case SignInFailure.SaveFailed:
                yield return $"[red]Signed in, but could not save the session to[/] [grey]{Markup.Escape(ProtostarPaths.ConfigDir())}[/].";
                break;
        }
    }

    private static string Authority(Uri registry) => registry.GetLeftPart(UriPartial.Authority);
}
