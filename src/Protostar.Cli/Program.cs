using Microsoft.Extensions.DependencyInjection;
using Protostar.Cli;
using Protostar.Cli.Auth;
using Protostar.Cli.Capture;
using Protostar.Cli.Commands;
using Protostar.Cli.Commands.Auth;
using Protostar.Cli.Harness;
using Protostar.Cli.Harness.ClaudeCode;
using Protostar.Cli.Hooks;
using Protostar.Cli.Infrastructure;
using Protostar.Cli.Mcp;
using Protostar.Cli.Memory;
using Protostar.Cli.Projects;
using Protostar.Cli.Registry;
using Protostar.Cli.Search;
using Protostar.Cli.Sessions;
using Protostar.Cli.Setup;
using Protostar.Cli.Skills;
using Protostar.Cli.Status;
using Spectre.Console;
using Spectre.Console.Cli;

// Business-logic services are registered in the DI container and constructor-injected into commands.
// This keeps commands thin and lets unit tests substitute fakes for the file- and network-touching
// services instead of reaching for real disk or registries.
var services = new ServiceCollection();
services.AddSingleton<ISkillService, SkillService>();
services.AddSingleton<IHookInstallService, HookInstallService>();
services.AddSingleton<ITokenStore, TokenStore>();
services.AddSingleton<IDefaultRegistryStore, DefaultRegistryStore>();
services.AddSingleton<IAccessTokenProvider, AccessTokenProvider>();
services.AddSingleton<RegistryApi>();
services.AddSingleton<IRegistryApi>(sp => sp.GetRequiredService<RegistryApi>());
services.AddSingleton<ITranscriptApi>(sp => sp.GetRequiredService<RegistryApi>());
services.AddSingleton<IRegistryCapabilities, RegistryCapabilities>();
services.AddSingleton<ISessionLedger, SessionLedger>();
services.AddSingleton<IMachineIdentity, MachineIdentity>();
services.AddSingleton(TimeProvider.System);
services.AddSingleton<IProjectFactsResolver, ProjectFactsResolver>();
services.AddSingleton<ISessionSyncService, SessionSyncService>();
services.AddSingleton<ISessionReader, SessionReader>();
services.AddSingleton<ISessionSkills, SessionSkills>();
services.AddSingleton<IProjectApi>(sp => sp.GetRequiredService<RegistryApi>());
services.AddSingleton<IProjectService, ProjectService>();
services.AddSingleton<IMemoryApi>(sp => sp.GetRequiredService<RegistryApi>());
services.AddSingleton<IMemoryLedger, MemoryLedger>();
services.AddSingleton<IMemorySyncService, MemorySyncService>();
services.AddSingleton<ISkillLedger, SkillLedger>();
services.AddSingleton<ISkillPushService, SkillPushService>();
services.AddSingleton<ISkillPullService, SkillPullService>();
services.AddSingleton<ISkillChangeService, SkillChangeService>();
services.AddSingleton<IBackgroundPush, BackgroundPush>();
services.AddSingleton<ICaptureService, CaptureService>();
services.AddSingleton<ISignInService, SignInService>();
services.AddSingleton<ISetupService, SetupService>();
services.AddSingleton<ISyncStatusService, SyncStatusService>();
services.AddSingleton<ISearchApi>(sp => sp.GetRequiredService<RegistryApi>());
services.AddSingleton<ISearchService, SearchService>();
services.AddSingleton<IAgentHistoryService, AgentHistoryService>();

// Harnesses and their collaborators live in the container too: register one IHarness per harness
// (the extension point) plus the catalog that exposes them and the ClaudeCode skill parser/mapper.
services.AddSingleton<IClaudeCodeSkillParser, ClaudeCodeSkillParser>();
services.AddSingleton<IClaudeCodeSkillMapper, ClaudeCodeSkillMapper>();
services.AddSingleton<IHarness, ClaudeCodeHarness>();
services.AddSingleton<IHarnessCatalog, HarnessCatalog>();

// Spectre.Console.Cli command app. `protostar` runs DefaultCommand; `--version`/`-v` and `--help`
// are provided by the framework. The registrar resolves commands (and their dependencies) from the
// container above.
var app = new CommandApp<DefaultCommand>(new TypeRegistrar(services));

// The top-level command and branch names, collected as they are registered below, for OptionGuard.CheckRoot.
var commandNames = new List<string>();
string Named(string name)
{
    commandNames.Add(name);
    return name;
}

app.Configure(config =>
{
    config.SetApplicationName("protostar");
    config.SetApplicationVersion(CliInfo.Version);

    // Every option belongs to a subcommand; one typed before it (`skills --global-only push`) is refused with the
    // command line that puts it after, and one the command does not declare (`skills pull --forse`) is refused with
    // the closest real one, instead of either being silently dropped. See OptionGuard.
    config.SetInterceptor(new OptionGuard());

    config.AddCommand<SetupCommand>(Named("setup"))
        .WithDescription("Set up this machine in one step: sign in, install hooks, push and pull skills, and back up sessions. Safe to run again.");
    config.AddCommand<SyncStatusCommand>(Named("status"))
        .WithDescription("Show whether this machine is syncing: sign-in, hooks, and how background backups have gone. Exits 1 when something needs attention.");
    config.AddCommand<InstallCommand>(Named("install"))
        .WithDescription("Install protostar to a per-user directory and add it to PATH.");
    config.AddCommand<UninstallCommand>(Named("uninstall"))
        .WithDescription("Remove an installed protostar binary.");
    config.AddCommand<InstallHooksCommand>(Named("install-hooks"))
        .WithDescription("Detect supported harnesses and install protostar capture hooks idempotently.");
    config.AddCommand<CaptureCommand>(Named("capture"))
        .WithDescription("Capture a harness hook event (invoked by installed hooks).")
        .IsHidden();
    config.AddBranch(Named("skills"), skills =>
    {
        skills.SetDescription("List skills on this machine, back them up to the registry, pull them from it, or rename or delete them there.");
        skills.SetDefaultCommand<SkillsCommand>();
        skills.AddCommand<SkillsPushCommand>("push")
            .WithDescription("Back up local skills to the registry (all, or the named ones).");
        skills.AddCommand<SkillsRemoteCommand>("remote")
            .WithDescription("List the skills in the registry and whether each is on this machine.");
        skills.AddCommand<SkillsPullCommand>("pull")
            .WithDescription("Install or update skills from the registry (all, or the named ones).");
        skills.AddCommand<SkillsRenameCommand>("rename")
            .WithDescription("Rename a skill in the registry, keeping its history; this machine's copies follow.");
        skills.AddCommand<SkillsDeleteCommand>("delete")
            .WithDescription("Delete a skill and all its versions from the registry. Local copies are never deleted.");
    });

    config.AddBranch(Named("sessions"), sessions =>
    {
        sessions.SetDescription("Back up session transcripts, see what is backed up, and read them back.");
        sessions.AddCommand<SessionsPushCommand>("push")
            .WithDescription("Back up every session transcript on this machine to the registry.");
        sessions.AddCommand<SessionsStatusCommand>("status")
            .WithDescription("Show what is and is not backed up on this machine. Works offline; exits 1 while anything is pending.");
        sessions.AddCommand<SessionsListCommand>("list")
            .WithDescription("List sessions backed up from every machine.");
        sessions.AddCommand<SessionsShowCommand>("show")
            .WithDescription("Read a backed-up session as a conversation: prompts and replies, with tool calls and subagents on one line each.");
    });

    config.AddCommand<SearchCommand>(Named("search"))
        .WithDescription("Search your backed-up sessions and memory, from every machine. Secrets are redacted by the registry.")
        .WithExample("search", "\"parentUuid ordering\"")
        .WithExample("search", "DNS", "--kind", "memory", "--project", ".");

    config.AddCommand<McpCommand>(Named("mcp"))
        .WithDescription("Serve your backed-up sessions and memory to coding agents as read-only MCP tools, on stdio. Register it with `claude mcp add protostar -- protostar mcp`.");

    config.AddBranch(Named("memory"), memory =>
    {
        memory.SetDescription("Back up the memory your agents keep about each project, bring it to this machine, and see what is not backed up.");
        memory.AddCommand<MemoryPushCommand>("push")
            .WithDescription("Back up the agent memory on this machine to the registry: only what changed, and deletions as markers.");
        memory.AddCommand<MemoryStatusCommand>("status")
            .WithDescription("Show which memory files are not backed up. Works offline; exits 1 while anything is pending.");
        memory.AddCommand<MemoryPullCommand>("pull")
            .WithDescription("Bring the registry's memory to the checkouts of each project on this machine, never over a local file.");
    });

    // Not AddBranch<ProjectSettings>: branch settings bind only before the subcommand, so `project link x --dir y`
    // silently acted on the current folder. Like the other branches, each command's settings carry the options.
    config.AddBranch(Named("project"), project =>
    {
        project.SetDescription("See which project this folder belongs to, the same on every machine, and link or unlink it.");
        project.SetDefaultCommand<ProjectShowCommand>();
        project.AddCommand<ProjectShowCommand>("show")
            .WithDescription("Show this folder's project and how it was recognised (the default).");
        project.AddCommand<ProjectLinkCommand>("link")
            .WithDescription("Link this folder to a project, by id or name. Its sessions move with it.");
        project.AddCommand<ProjectUnlinkCommand>("unlink")
            .WithDescription("Unlink this folder; a fork's folder with no link gets a project of its own.");
    });

    config.AddBranch(Named("auth"), auth =>
    {
        auth.SetDescription("Authenticate to the protostar registry.");
        auth.AddCommand<LoginCommand>("login")
            .WithDescription("Sign in to the registry via your browser and store the session.");
        auth.AddCommand<LogoutCommand>("logout")
            .WithDescription("Remove the stored session for the registry.");
        auth.AddCommand<StatusCommand>("status")
            .WithDescription("Show whether you are signed in to the registry.");
    });
});

// Spectre fails on an option before the first command while parsing, before the interceptor could run.
if (OptionGuard.CheckRoot(args, commandNames) is { } misplaced)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(misplaced)}");
    return -1;
}

return app.Run(args);
