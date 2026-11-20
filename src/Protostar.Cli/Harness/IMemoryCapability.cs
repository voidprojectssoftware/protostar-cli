namespace Protostar.Cli.Harness;

/// <summary>Capability: a harness that keeps agent memory per project in folders protostar can find on disk.</summary>
/// <remarks>
/// The harness owns its layout (where project folders and their memory live, and how it names a project's folder);
/// <see cref="Memory.MemorySyncService"/> owns what to back up. A project folder's real working directory comes from
/// the transcripts in it, never from its name (ADR 0002: the names are lossy).
/// </remarks>
internal interface IMemoryCapability
{
    /// <summary>Every project folder under <paramref name="location"/>, with its memory folder when it has one.</summary>
    /// <remarks>Returns an empty list (never throws) when there are none or the folders are unreadable.</remarks>
    IReadOnlyList<HarnessProjectFolder> LocateProjectFolders(HarnessLocation location);

    /// <summary>The project folder a session transcript sits in, as a hook names the transcript.</summary>
    /// <returns><c>null</c> when the path is not one of this harness's session transcripts.</returns>
    HarnessProjectFolder? ProjectFolderOf(string transcriptPath);

    /// <summary>The working directory a transcript in <paramref name="folder"/> records, newest transcript first.</summary>
    /// <returns><c>null</c> when the folder has no transcript, or none of the few newest records one.</returns>
    string? ReadWorkingDirectory(HarnessProjectFolder folder);

    /// <summary>The name the harness gives the project folder of <paramref name="directory"/>.</summary>
    /// <remarks>Encodes forward only. Used to check that a folder belongs to a known path, never to turn a name back into one.</remarks>
    string ProjectFolderName(string directory);
}

/// <summary>One of a harness's per-project folders.</summary>
/// <param name="Harness">The harness id.</param>
/// <param name="Path">The project folder.</param>
/// <param name="MemoryPath">Its memory folder, or <c>null</c> when it has none.</param>
internal sealed record HarnessProjectFolder(string Harness, string Path, string? MemoryPath)
{
    /// <summary>The folder's own name, as the harness encoded it.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
}
