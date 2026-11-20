using System.Text;
using System.Text.RegularExpressions;
using Protostar.Cli.Auth;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Sessions;

namespace Protostar.Cli.Skills;

/// <summary>Production <see cref="ISkillChangeService"/>.</summary>
internal sealed partial class SkillChangeService : ISkillChangeService
{
    private readonly IAccessTokenProvider _tokens;
    private readonly IRegistryApi _registry;
    private readonly ISkillLedger _ledger;
    private readonly IProjectFactsResolver _facts;
    private readonly IProjectApi _projects;
    private readonly IMachineIdentity _machine;

    public SkillChangeService(
        IAccessTokenProvider tokens, IRegistryApi registry, ISkillLedger ledger, IProjectFactsResolver facts, IProjectApi projects, IMachineIdentity machine)
    {
        _tokens = tokens;
        _registry = registry;
        _ledger = ledger;
        _facts = facts;
        _projects = projects;
        _machine = machine;
    }

    /// <inheritdoc />
    public async Task<SkillTargetResult> FindAsync(SkillTargetRequest request, CancellationToken cancellationToken)
    {
        Uri registry;
        try
        {
            registry = RegistryEndpoint.Resolve(request.Registry);
        }
        catch (FormatException ex)
        {
            return SkillTargetResult.Fail(SkillPullRunFailure.InvalidRegistry, ex.Message);
        }

        var session = await _tokens.GetSessionAsync(registry, cancellationToken);
        if (session is null)
            return SkillTargetResult.Fail(SkillPullRunFailure.NotLoggedIn, null, registry);
        if (session.IsExpired)
            return SkillTargetResult.Fail(SkillPullRunFailure.SessionExpired, null, registry);

        var meta = await _registry.GetMetaAsync(registry, cancellationToken);
        if (meta is null)
            return SkillTargetResult.Fail(SkillPullRunFailure.RegistryUnreachable, null, registry);
        if (ApiCompatibility.Check(meta) is { } incompatibility)
            return SkillTargetResult.Fail(SkillPullRunFailure.Incompatible, incompatibility, registry);

        var list = await _registry.ListSkillsAsync(registry, session.AccessToken, cancellationToken);
        if (list.Skills is not { } skills)
            return SkillTargetResult.Fail(SkillPullRunFailure.ListFailed, list.Error, registry);

        ProjectFacts? facts = null;
        Guid? hereProject = null;
        string? hereProblem = null;
        if (request.ProjectStart is { } start)
        {
            facts = _facts.Resolve(Path.GetFullPath(start));
            var resolved = await _projects.ResolveProjectAsync(registry, session.AccessToken, _machine.Get().Id, facts, cancellationToken);
            if (resolved.Ok)
                hereProject = resolved.Value!.ProjectId;
            else
                hereProblem = resolved.Error;
        }

        var named = skills.Where(s => string.Equals(s.Name, request.Name, StringComparison.Ordinal)).ToList();
        var skill = (hereProject is { } id ? named.FirstOrDefault(s => s.ProjectId == id) : null)
            ?? named.FirstOrDefault(s => s.ProjectId is null);
        if (skill is null)
            return SkillTargetResult.Fail(SkillPullRunFailure.SkillNotFound, NotFound(request, registry, named, hereProblem), registry);

        return new SkillTargetResult(SkillPullRunFailure.None, null, registry,
            new SkillTarget(registry, session.AccessToken, skill,
                await CopiesOfAsync(registry, session.AccessToken, skill, skills, cancellationToken), skill.ProjectId is null ? null : facts));
    }

    /// <inheritdoc />
    public async Task<SkillDeleteResult> DeleteAsync(SkillTarget target, CancellationToken cancellationToken)
    {
        var deleted = await _registry.DeleteSkillAsync(target.Registry, target.AccessToken, target.Skill.Name, target.Skill.ProjectId, cancellationToken);
        return new SkillDeleteResult(target, deleted.Error);
    }

    /// <inheritdoc />
    public async Task<SkillRenameResult> RenameAsync(SkillTarget target, string newName, CancellationToken cancellationToken)
    {
        var skill = target.Skill;
        var renamed = await _registry.RenameSkillAsync(target.Registry, target.AccessToken, skill.Name, skill.ProjectId, newName, cancellationToken);
        if (!renamed.Ok)
            return new SkillRenameResult(target, null, renamed.Error, []);
        newName = renamed.Value!;
        if (newName == skill.Name)
            return new SkillRenameResult(target, newName, null, []);

        var push = await PushRenamedManifestAsync(target, newName, cancellationToken);

        var copies = new List<LocalCopyRename>();
        foreach (var copy in target.Copies)
        {
            var (path, manifestRenamed, note) = RenameCopy(copy.Path, skill.Name, newName);
            if (path != copy.Path)
                _ledger.Forget(target.Registry, copy.Path);
            _ledger.Record(target.Registry, path, RecordAfterRename(copy, path, newName, push));
            if (copy.HasLocalChanges)
                note = Join(note, "it has changes not yet pushed; `protostar skills push` sends them under the new name");
            copies.Add(new LocalCopyRename(copy.Path, path, manifestRenamed, note));
        }

        return new SkillRenameResult(target, newName, null, copies, push?.Outcome);
    }

    // Pushes the registry's current version with its SKILL.md name set to the new name, as the next version, so the
    // skill's own content agrees with its name; null when its SKILL.md declares no name, so nothing needs to change.
    private async Task<(SkillPushOutcome Outcome, string Hash)?> PushRenamedManifestAsync(SkillTarget target, string newName, CancellationToken cancellationToken)
    {
        var skill = target.Skill;
        var download = await _registry.GetSkillFilesAsync(target.Registry, target.AccessToken, newName, skill.ProjectId, null, cancellationToken);
        if (download.Content is not { } content)
            return (new SkillPushOutcome(SkillPushStatus.Failed, null, download.Error), "");

        var files = new List<SkillUploadFile>();
        var changed = false;
        foreach (var file in content.Files)
        {
            if (!string.Equals(file.RelativePath, "SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(new SkillUploadFile(file.RelativePath, file.Content));
                continue;
            }

            var bom = file.Content.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var rewritten = RewriteName(Encoding.UTF8.GetString(bom ? file.Content[3..] : file.Content), newName);
            changed |= rewritten is not null;
            files.Add(rewritten is null ? new SkillUploadFile(file.RelativePath, file.Content)
                : new SkillUploadFile(file.RelativePath, [.. bom ? Encoding.UTF8.Preamble : [], .. Encoding.UTF8.GetBytes(rewritten)]));
        }

        if (!changed)
            return null;

        var project = target.ProjectFacts is { } facts ? new SkillProject(_machine.Get().Id, facts) : null;
        var pushed = await _registry.PushSkillAsync(target.Registry, target.AccessToken, newName, files, project, cancellationToken);
        if (pushed.Succeeded && pushed.ProjectId != skill.ProjectId)
            pushed = new SkillPushOutcome(SkillPushStatus.Failed, null, "The registry filed the renamed SKILL.md under another project.");
        return (pushed, SkillFileReader.Hash(files));
    }

    // A copy's ledger record under its new name. An unedited copy is recorded with its rewritten content, so it stays
    // unedited in the ledger's eyes: up to date when it now equals the version just pushed, otherwise still the version
    // it last synced (an update is available). An edited copy keeps its old record, so its changes stay protected.
    private static SkillLedgerEntry RecordAfterRename(LocalSkillCopy copy, string path, string newName, (SkillPushOutcome Outcome, string Hash)? push)
    {
        if (copy.HasLocalChanges)
            return copy.Recorded with { Name = newName };

        string hash;
        try
        {
            hash = SkillFileReader.Hash(SkillFileReader.Read(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return copy.Recorded with { Name = newName };
        }

        var version = push is { Outcome.Succeeded: true } p && p.Hash == hash ? p.Outcome.Version : copy.Recorded.Version;
        return copy.Recorded with { Name = newName, ContentHash = hash, Version = version };
    }

    // The copies the ledger records as synced with exactly this registry skill (name and project), that still exist,
    // including those recorded under the project a link moved the skill out of, once their record follows it.
    private async Task<List<LocalSkillCopy>> CopiesOfAsync(
        Uri registry, string accessToken, RegistrySkill skill, IReadOnlyList<RegistrySkill> skills, CancellationToken cancellationToken)
    {
        var copies = new List<LocalSkillCopy>();
        foreach (var (path, entry) in _ledger.Entries(registry).OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!string.Equals(entry.Name, skill.Name, StringComparison.Ordinal) || !Directory.Exists(path))
                continue;
            var recorded = await MovedSkillLedger.FollowAsync(_registry, _ledger, registry, accessToken, path, entry, skill, skills, cancellationToken);
            if (recorded!.ProjectId != skill.ProjectId)
                continue;

            try
            {
                copies.Add(new LocalSkillCopy(path, recorded, SkillFileReader.Hash(SkillFileReader.Read(path)) != recorded.ContentHash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: it cannot be shown to be free of local work.
                copies.Add(new LocalSkillCopy(path, recorded, true));
            }
        }

        return copies;
    }

    // Rewrites the copy's SKILL.md name and renames its folder when the folder carried the old name.
    private static (string Path, bool ManifestRenamed, string? Note) RenameCopy(string path, string oldName, string newName)
    {
        string? note = null;
        var manifestRenamed = false;
        var manifest = Path.Combine(path, "SKILL.md");
        try
        {
            if (File.Exists(manifest))
            {
                var bytes = File.ReadAllBytes(manifest);
                var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                var text = Encoding.UTF8.GetString(bom ? bytes[3..] : bytes);
                if (RewriteName(text, newName) is { } rewritten)
                {
                    File.WriteAllText(manifest, rewritten, new UTF8Encoding(bom));
                    manifestRenamed = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = $"its SKILL.md could not be updated ({ex.Message})";
        }

        if (!string.Equals(Path.GetFileName(path), oldName, StringComparison.Ordinal))
            return (path, manifestRenamed, Join(note, $"its folder is not named '{oldName}', so it was left where it is"));
        if (!SkillInstaller.IsSafeDirectoryName(newName))
            return (path, manifestRenamed, Join(note, $"'{newName}' cannot be a folder name here, so the folder kept its name"));

        var parent = Path.GetDirectoryName(path)!;
        var destination = Path.Combine(parent, newName);
        try
        {
            if (Directory.EnumerateFileSystemEntries(parent).Any(e => string.Equals(Path.GetFileName(e), newName, StringComparison.OrdinalIgnoreCase)))
                return (path, manifestRenamed, Join(note, $"a folder named '{newName}' is already beside it, so the folder kept its name"));
            Directory.Move(path, destination);
            return (destination, manifestRenamed, note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (path, manifestRenamed, Join(note, $"the folder could not be renamed ({ex.Message})"));
        }
    }

    /// <summary>
    /// <paramref name="text"/> (a SKILL.md) with the <c>name</c> field of its front matter set to <paramref name="newName"/>;
    /// <c>null</c> when it has no front matter or no top-level <c>name</c> field, so the folder name names the skill.
    /// </summary>
    /// <remarks>Only that one line changes; line endings and everything else are kept byte for byte.</remarks>
    internal static string? RewriteName(string text, string newName)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r').Trim() != "---")
            return null;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim() == "---")
                return null;
            if (!NameField().IsMatch(line))
                continue;

            var ending = lines[i].EndsWith('\r') ? "\r" : "";
            lines[i] = $"name: {YamlScalar(newName)}{ending}";
            return string.Join('\n', lines);
        }

        return null;
    }

    // A plain scalar when the name is plainly safe in YAML, otherwise a double-quoted one.
    private static string YamlScalar(string value) =>
        PlainScalar().IsMatch(value) ? value : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string NotFound(SkillTargetRequest request, Uri registry, IReadOnlyList<RegistrySkill> named, string? hereProblem)
    {
        var authority = registry.GetLeftPart(UriPartial.Authority);
        var message = request.ProjectStart is null
            ? $"There is no global skill named '{request.Name}' in {authority}."
            : $"There is no skill named '{request.Name}' in this folder's project or among global skills in {authority}.";
        var elsewhere = named.Where(s => s.ProjectId is not null).Select(s => s.ProjectName ?? s.ProjectId.ToString()).ToList();
        if (elsewhere.Count > 0)
            message += $" It is a project skill of {string.Join(", ", elsewhere)}: run this from a checkout of that project, or pass --project <DIR>.";
        if (hereProblem is not null)
            message += $" (Could not tell which project this folder is in: {hereProblem})";
        return message + " Run `protostar skills remote` to see what is there.";
    }

    private static string? Join(string? a, string b) => a is null ? b : $"{a}; {b}";

    [GeneratedRegex(@"^name\s*:")]
    private static partial Regex NameField();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$")]
    private static partial Regex PlainScalar();
}
