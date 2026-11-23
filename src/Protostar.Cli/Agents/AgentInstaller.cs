using System.Security.Cryptography;
using System.Text;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Registry;
using Protostar.Cli.Skills;

namespace Protostar.Cli.Agents;

/// <summary>Writes one agent version downloaded from the registry into an agents folder on disk.</summary>
/// <remarks>
/// <para>
/// Nothing is written until the download checks out: its bytes must hash to the version's content hash, and the file must
/// land inside the agents folder, as a <c>.md</c> file, without passing through a symbolic link below that folder (the
/// agents folder itself may be a link, as a dotfiles checkout often makes it). A registry, or anything between it and
/// this machine, can therefore never place a file outside the agents folder.
/// </para>
/// <para>
/// A renamed agent's stored file keeps the <c>name</c> it was pushed with (the registry renames the agent, not its
/// file), but Claude Code names an agent by that field. So when the file's <c>name</c> is not the agent's registry name,
/// that one line is set to it before writing, and the caller records the bytes actually written.
/// </para>
/// <para>The file is written to a temporary file beside it and moved into place, so a crash never leaves a torn agent.</para>
/// </remarks>
internal static class AgentInstaller
{
    /// <summary>Checks <paramref name="content"/> and writes it to <paramref name="filePath"/> under <paramref name="agentsDirectory"/>.</summary>
    /// <param name="agentsDirectory">The personal or project agents folder the file must stay inside.</param>
    /// <param name="filePath">Where to write: an installed copy's own path, or the agents folder joined with the version's path.</param>
    /// <param name="content">The downloaded version, read without redaction.</param>
    /// <param name="name">The agent's name in the registry, which the written file's <c>name</c> field is set to.</param>
    /// <returns>The bytes as written.</returns>
    /// <exception cref="AgentInstallException">The download failed a check, or the path is unsafe; nothing was written.</exception>
    /// <exception cref="IOException">Writing failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder is not writable.</exception>
    public static byte[] Install(string agentsDirectory, string filePath, RemoteAgentContent content, string name)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(content.Content)), content.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new AgentInstallException($"The download of '{name}' does not match its checksum; it may be damaged.");

        var target = Inside(agentsDirectory, filePath);
        var bytes = Named(content.Content, name);
        Write(target, bytes);
        return bytes;
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="target"/> through a temporary file beside it, keeping an existing file's Unix permissions.</summary>
    internal static void Write(string target, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.protostar-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, bytes);
        try
        {
            if (!OperatingSystem.IsWindows() && File.Exists(target))
                File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>The full path of the version's <paramref name="relativePath"/> in <paramref name="agentsDirectory"/>, or <c>null</c> when it is not a safe path.</summary>
    /// <remarks>The path is checked before the download is fetched, so a bad one is reported without writing anything.</remarks>
    public static string? PathFor(string agentsDirectory, string relativePath) =>
        SkillInstaller.IsSafeRelativePath(relativePath) && relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(agentsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            : null;

    /// <summary>
    /// <paramref name="content"/> with its front matter's <c>name</c> set to <paramref name="name"/> when it says something
    /// else; the same bytes when it already says <paramref name="name"/> or has no <c>name</c> line to set.
    /// </summary>
    internal static byte[] Named(byte[] content, string name)
    {
        var bom = content.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(bom ? content[3..] : content);
        if (ClaudeCodeHarness.ParseAgentFrontMatter(text).Name == name || SkillChangeService.RewriteName(text, name) is not { } rewritten)
            return content;

        var body = Encoding.UTF8.GetBytes(rewritten);
        return bom ? [.. Encoding.UTF8.Preamble, .. body] : body;
    }

    // The full target path, refused when it leaves the agents folder, is not a .md file, or passes through a symbolic link
    // below the folder.
    private static string Inside(string agentsDirectory, string filePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(agentsDirectory));
        var target = Path.GetFullPath(filePath);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new AgentInstallException($"'{target}' is outside the agents folder '{root}', so it was not written.");
        if (!target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new AgentInstallException($"'{target}' is not a .md file, which Claude Code would not load, so it was not written.");

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw new AgentInstallException($"'{target}' would be written through a symbolic link at '{current}', and protostar never writes through links.");
        }

        return target;
    }
}

/// <summary>A downloaded agent failed a safety or integrity check, so nothing was written.</summary>
internal sealed class AgentInstallException(string message) : Exception(message);
