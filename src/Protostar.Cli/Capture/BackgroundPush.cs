using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Protostar.Cli.Capture;

/// <summary>Hands a session transcript to a detached <c>protostar sessions push</c> so a hook can return at once.</summary>
/// <remarks>
/// The child is the same executable as the hook (<see cref="Environment.ProcessPath"/>), so it is the installed
/// binary, never a <c>dotnet</c> host. It must not hold on to the harness's stdin, stdout or stderr: the harness
/// waits for the hook's output to close, so a child that inherited it would hold the harness up until the upload
/// finished.
/// </remarks>
internal interface IBackgroundPush
{
    /// <summary>Starts the detached push of <paramref name="transcriptPath"/> and returns without waiting for it.</summary>
    /// <param name="transcriptPath">The session transcript the hook named.</param>
    /// <param name="throttle">Passed to the child as <c>--throttle</c>; <c>null</c> to push whatever is new.</param>
    /// <exception cref="InvalidOperationException">The executable could not be located or started.</exception>
    void Start(string transcriptPath, TimeSpan? throttle);
}

/// <summary>Production <see cref="IBackgroundPush"/>.</summary>
/// <remarks>
/// <para>
/// Windows: started through the shell (<c>ShellExecuteEx</c>) with a hidden window. <c>CreateProcess</c>, which
/// .NET uses otherwise, passes every inheritable handle to the child, the hook's stdout pipe included, so the
/// harness would wait for the child. The shell gives the child its own hidden console and no inherited handles,
/// and it does not receive the harness console's Ctrl+C or close events.
/// </para>
/// <para>
/// Unix: .NET opens its own descriptors close-on-exec, so only the three standard streams reach a child. All three
/// are redirected to pipes and the parent closes its ends at once: the child reads end of input, and its writes
/// are dropped (.NET ignores a broken pipe on the console streams). The child then calls <c>setsid</c>
/// (<see cref="DetachFromTerminal"/>) so closing the terminal does not send it <c>SIGHUP</c>.
/// </para>
/// <para>The child runs <c>sessions push</c>, which starts no process of its own, so a push never starts another.</para>
/// </remarks>
internal sealed class BackgroundPush : IBackgroundPush
{
    /// <inheritdoc />
    public void Start(string transcriptPath, TimeSpan? throttle)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the protostar executable.");
        var startInfo = StartInfo(exe, Path.GetFullPath(transcriptPath), throttle, OperatingSystem.IsWindows());

        using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("The background push did not start.");
        if (startInfo.RedirectStandardInput)
        {
            child.StandardInput.Close();
            child.StandardOutput.Close();
            child.StandardError.Close();
        }
    }

    /// <summary>How the child is started; separate so the options can be checked on any OS.</summary>
    internal static ProcessStartInfo StartInfo(string exe, string transcriptPath, TimeSpan? throttle, bool windows)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            // Not the harness's working directory: a child holding a project folder open stops it being deleted on Windows.
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = windows,
            WindowStyle = windows ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            RedirectStandardInput = !windows,
            RedirectStandardOutput = !windows,
            RedirectStandardError = !windows,
        };

        startInfo.ArgumentList.Add("sessions");
        startInfo.ArgumentList.Add("push");
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add(transcriptPath);
        if (throttle is { } window)
        {
            startInfo.ArgumentList.Add("--throttle");
            startInfo.ArgumentList.Add(((int)window.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        return startInfo;
    }

    /// <summary>Called by the child: leaves the terminal's session so closing the terminal does not end the push.</summary>
    /// <remarks>A no-op on Windows, where the shell already gave the child its own console.</remarks>
    internal static void DetachFromTerminal()
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            // Fails harmlessly (EPERM) when the process already leads a process group, for example when run by hand.
            _ = SetSid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A libc without setsid: the push still runs, it just shares the terminal's fate.
        }
    }

    // DllImport rather than LibraryImport: the generated form needs unsafe code enabled for the whole project.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "setsid")]
    private static extern int SetSid();
#pragma warning restore SYSLIB1054
}
