using Spectre.Console.Cli;

namespace Protostar.Cli.Infrastructure;

/// <summary>Refuses a command line that puts an option between a branch and its subcommand, before anything runs.</summary>
/// <remarks>
/// The branches (<c>skills</c>, <c>sessions</c>, <c>project</c>, <c>auth</c>) carry no options of their own; every
/// option belongs to a subcommand. Spectre still accepts <c>protostar skills --global-only push</c>: it binds the
/// option to the branch, finds no home for it, and drops it, so <c>push</c> ran against project skills too. Strict
/// parsing would refuse it, but only as "Unknown option", which does not say the option is real and merely misplaced.
/// This guard runs once Spectre has chosen the subcommand, so the subcommand's name comes from the parser rather than
/// a second list of names here, and the error can give the command line that does what was meant. A branch's default
/// command (<c>protostar skills --global-only</c>) has no subcommand token, so its options are where they belong.
/// An option before the first command (<c>protostar --registry X skills remote</c>) never reaches the interceptor, so
/// <see cref="CheckRoot"/> covers it before Spectre parses.
/// </remarks>
internal sealed class OptionPlacementGuard : ICommandInterceptor
{
    /// <summary>The name Spectre gives a default command, which is never typed.</summary>
    private const string DefaultCommandName = "__default_command";

    public void Intercept(CommandContext context, CommandSettings settings)
    {
        var problem = Check(context.Arguments, context.Name);
        if (problem is not null)
            throw new MisplacedOptionException(problem);
    }

    /// <summary>
    /// The error for <paramref name="args"/> when an option sits between the branch and the subcommand
    /// <paramref name="commandName"/>, or null when every option is after it.
    /// </summary>
    internal static string? Check(IReadOnlyList<string> args, string commandName)
    {
        if (commandName == DefaultCommandName || args.Count < 2)
            return null;

        // A top-level command is the first token. Under a branch, the subcommand is the first token after it with
        // the command's name; an option value that happens to equal the name only moves the split earlier, and
        // whatever precedes it is still before the subcommand.
        if (string.Equals(args[0], commandName, StringComparison.OrdinalIgnoreCase))
            return null;
        var at = FindFrom(args, commandName, 1);
        if (at < 0)
            return null;

        var between = args.Skip(1).Take(at - 1).ToList();
        var options = between.Where(t => t.StartsWith('-')).ToList();
        if (options.Count == 0)
            return null;

        return Message(options, new[] { args[0], args[at] }.Concat(between).Concat(args.Skip(at + 1)));
    }

    /// <summary>
    /// The error for <paramref name="args"/> when it starts with options and a command named in
    /// <paramref name="commandNames"/> follows them, or null otherwise.
    /// </summary>
    /// <remarks>
    /// The root command takes no options but <c>--help</c> and <c>--version</c>, so anything before the first command
    /// is misplaced. Spectre would bind it to the root's default command and then fail on the command name itself
    /// ("Unknown command 'skills'. Did you mean 'skills'?"), or swallow the name as the option's value and quietly run
    /// the default command (<c>protostar --yes setup</c>). The suggested line moves the leading options to the end,
    /// which is after the subcommand whether the command is a branch or not. A version flag first is left to Spectre,
    /// which prints the version whatever follows.
    /// </remarks>
    internal static string? CheckRoot(IReadOnlyList<string> args, IReadOnlyCollection<string> commandNames)
    {
        if (args.Count < 2 || !args[0].StartsWith('-') || args[0] is "-v" or "--version")
            return null;

        // As in Check, an option value equal to a command name only moves the split earlier.
        var at = -1;
        for (var i = 1; i < args.Count && at < 0; i++)
            if (commandNames.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                at = i;
        if (at < 0)
            return null;

        var leading = args.Take(at).ToList();
        return Message(leading.Where(t => t.StartsWith('-')).ToList(), args.Skip(at).Concat(leading));
    }

    private static string Message(IReadOnlyList<string> options, IEnumerable<string> corrected)
    {
        var what = options.Count == 1 ? $"{options[0]} goes" : $"{string.Join(", ", options)} go";
        var line = string.Join(' ', corrected.Prepend("protostar").Select(Quote));
        return $"{what} after the subcommand, not before it. Run:{Environment.NewLine}  {line}";
    }

    private static int FindFrom(IReadOnlyList<string> args, string value, int start)
    {
        for (var i = start; i < args.Count; i++)
            if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string Quote(string token) => token.Contains(' ') ? $"\"{token}\"" : token;
}

/// <summary>A command line with an option before its subcommand; Spectre prints the message and exits non-zero.</summary>
internal sealed class MisplacedOptionException : Exception
{
    public MisplacedOptionException(string message)
        : base(message)
    {
    }
}
