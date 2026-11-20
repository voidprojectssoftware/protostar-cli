using System.Text.Encodings.Web;
using System.Text.Json;
using Protostar.Cli.Auth;
using Protostar.Cli.Registry;

namespace Protostar.Cli.Capture;

/// <summary>What a background backup was backing up.</summary>
internal enum CaptureKind
{
    /// <summary>A skill the harness used (the <c>PostToolUse</c> hook).</summary>
    Skills,

    /// <summary>A session transcript and its subagents (the push started by <c>Stop</c> and <c>SessionEnd</c>).</summary>
    Sessions,

    /// <summary>The memory of a session's project (the same push, after the session).</summary>
    Memory,
}

/// <summary>Why a background backup failed, for <c>protostar status</c> to say what to do.</summary>
internal enum CaptureFailure
{
    /// <summary>No stored sign-in for the registry.</summary>
    NotSignedIn,

    /// <summary>The stored sign-in expired and could not be refreshed.</summary>
    SessionExpired,

    /// <summary>The registry could not be reached.</summary>
    RegistryUnreachable,

    /// <summary>The registry refused it, or it changed both here and there; the user has to choose.</summary>
    Refused,

    /// <summary>Anything else; the message says what.</summary>
    Other,
}

/// <summary>One background backup's outcome, as kept in <c>capture-records.jsonl</c>.</summary>
/// <param name="At">When the backup finished.</param>
/// <param name="Kind">What was being backed up.</param>
/// <param name="Ok">Whether everything it tried reached the registry.</param>
/// <param name="Registry">The registry's authority (<c>https://host:port</c>); <c>null</c> when it was never resolved.</param>
/// <param name="Items">Skills checked, transcripts handled, or memory files uploaded.</param>
/// <param name="Bytes">Session bytes uploaded; <c>null</c> for the other kinds.</param>
/// <param name="Reason">Why it failed; <c>null</c> when <paramref name="Ok"/>.</param>
/// <param name="Error">The error message a person would read; <c>null</c> when <paramref name="Ok"/>.</param>
/// <remarks>Never holds transcript or memory content: counts, kinds, timestamps and error messages only.</remarks>
internal sealed record CaptureRecord(
    DateTimeOffset At, CaptureKind Kind, bool Ok, string? Registry, int Items, long? Bytes = null,
    CaptureFailure? Reason = null, string? Error = null);

/// <summary>
/// Appends <see cref="CaptureRecord"/>s to <c>~/.protostar/capture-records.jsonl</c> and reads them back, so
/// <c>protostar status</c> can tell when background backups last worked without parsing <c>capture.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// Written from hooks and the pushes they start, so appending is best effort: it never throws and takes no lock. Each
/// record is one line of well under a kilobyte written by a single append, which the OS keeps whole between processes.
/// </para>
/// <para>
/// Rotation: when the file has reached <see cref="MaxBytes"/>, the writer that notices renames it to
/// <c>capture-records.1.jsonl</c> (replacing the previous one) and starts a new file, so both together stay under
/// about twice the cap. Two processes rotating at once can lose the older generation, which only shortens the
/// history; a reader skips any line it cannot parse.
/// </para>
/// </remarks>
internal static class CaptureRecords
{
    /// <summary>The size at which the file is rotated: some thousand records, weeks of hooks.</summary>
    public const long MaxBytes = 256 * 1024;

    // The transcript options, but writing '`' and '+' as themselves: the file is read by people too, never put into HTML.
    private static readonly JsonSerializerOptions Options = new(TranscriptJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string FileName = "capture-records.jsonl";
    private const string RotatedName = "capture-records.1.jsonl";

    /// <summary>The current records file.</summary>
    public static string DefaultPath() => Path.Combine(ProtostarPaths.ConfigDir(), FileName);

    /// <summary>Appends <paramref name="record"/> to the default file. Never throws.</summary>
    public static void Append(CaptureRecord record) => Append(DefaultPath(), record);

    /// <summary>Appends <paramref name="record"/> to <paramref name="path"/>, rotating it first when full. Never throws.</summary>
    public static void Append(string path, CaptureRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length >= MaxBytes)
                Rotate(path);
            File.AppendAllText(path, JsonSerializer.Serialize(record, Options) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a record lost costs status one data point; the hook must still succeed.
        }
    }

    /// <summary>Every readable record in <paramref name="path"/> and its rotated predecessor, oldest first.</summary>
    public static IReadOnlyList<CaptureRecord> Read(string path)
    {
        var records = new List<CaptureRecord>();
        foreach (var file in new[] { RotatedPath(path), path })
        {
            foreach (var line in ReadLines(file))
            {
                try
                {
                    if (JsonSerializer.Deserialize<CaptureRecord>(line, Options) is { } record)
                        records.Add(record);
                }
                catch (JsonException)
                {
                    // A torn or foreign line: skip it.
                }
            }
        }

        return [.. records.OrderBy(r => r.At)];
    }

    private static void Rotate(string path)
    {
        try
        {
            File.Move(path, RotatedPath(path), overwrite: true);
        }
        catch (IOException)
        {
            // Another writer rotated it first (the file is gone), or holds it open on Windows: append anyway.
        }
    }

    private static string RotatedPath(string path) => Path.Combine(Path.GetDirectoryName(path)!, RotatedName);

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
