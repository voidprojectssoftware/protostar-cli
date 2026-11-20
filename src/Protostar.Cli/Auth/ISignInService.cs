namespace Protostar.Cli.Auth;

/// <summary>Signs in to a registry through the browser, and checks whether the stored session still works.</summary>
/// <remarks>
/// Shared by <c>auth login</c>, <c>auth status</c> and <c>setup</c>, so the sign-in flow and the meaning of
/// "signed in" exist once. Returns data only; the commands' presenters write the console output, except for the
/// sign-in URL, which <see cref="LoopbackBrowser"/> prints while it waits for the browser.
/// </remarks>
internal interface ISignInService
{
    /// <summary>Loads the stored session for <paramref name="registry"/> (refreshing it when expired) and asks the registry whether it is still good.</summary>
    Task<SessionCheck> CheckAsync(Uri registry, CancellationToken cancellationToken);

    /// <summary>Runs the browser sign-in against <paramref name="registry"/> and stores the session.</summary>
    /// <remarks>Checks the registry's <c>/v1/meta</c> compatibility first, so an unreachable or wrong URL fails before a browser opens.</remarks>
    Task<SignInResult> SignInAsync(Uri registry, SignInOptions options, CancellationToken cancellationToken);
}

/// <summary>How the browser sign-in runs.</summary>
/// <param name="Provider">Identity provider hint that skips the registry's chooser (for example <c>github</c>); <c>null</c> shows the chooser.</param>
/// <param name="NoBrowser">Print the sign-in URL instead of opening a browser.</param>
/// <param name="Timeout">How long to wait for the browser; values under 30 seconds are raised to 30.</param>
internal sealed record SignInOptions(string? Provider, bool NoBrowser, TimeSpan Timeout);

/// <summary>What the registry said about the stored session.</summary>
internal enum SessionVerification
{
    /// <summary>No session is stored for the registry.</summary>
    NoSession,

    /// <summary>The registry accepted the session.</summary>
    Verified,

    /// <summary>The registry answered but did not accept the session (expired, revoked, or issued by a registry since reset).</summary>
    Rejected,

    /// <summary>The registry could not be reached, so the session could not be checked.</summary>
    Unreachable,
}

/// <summary>The stored session for a registry and whether the registry accepts it.</summary>
/// <param name="Registry">The registry checked.</param>
/// <param name="Session">The stored session; <c>null</c> for <see cref="SessionVerification.NoSession"/>.</param>
/// <param name="Verification">What the registry said.</param>
/// <param name="Login">The registry's login for the user when verified, otherwise the stored one; <c>null</c> when unknown.</param>
internal sealed record SessionCheck(Uri Registry, StoredToken? Session, SessionVerification Verification, string? Login);

/// <summary>Why a sign-in did not complete.</summary>
internal enum SignInFailure
{
    /// <summary>Signed in and the session was stored.</summary>
    None,

    /// <summary>The registry did not answer; see the detail.</summary>
    RegistryUnreachable,

    /// <summary>The URL answered, but not with a protostar <c>/v1/meta</c> document.</summary>
    NotARegistry,

    /// <summary>The registry does not speak this CLI's API major; the detail says what to do.</summary>
    Incompatible,

    /// <summary>The browser sign-in was not completed in time.</summary>
    TimedOut,

    /// <summary>The registry refused the sign-in; see the detail.</summary>
    Rejected,

    /// <summary>Signed in, but the session could not be written to the credential store.</summary>
    SaveFailed,
}

/// <summary>The outcome of a browser sign-in.</summary>
/// <param name="Failure">Why it did not complete, or <see cref="SignInFailure.None"/>.</param>
/// <param name="Detail">The error text for a failure that has one.</param>
/// <param name="Registry">The registry signed in to.</param>
/// <param name="Login">The signed-in login, when the registry supplied one.</param>
internal sealed record SignInResult(SignInFailure Failure, string? Detail, Uri Registry, string? Login)
{
    public bool Succeeded => Failure == SignInFailure.None;
}
