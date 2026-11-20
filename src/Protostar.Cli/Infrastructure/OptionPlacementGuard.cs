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

        var corrected = new[] { "protostar", args[0], args[at] }.Concat(between).Concat(args.Skip(at + 1));
        var what = options.Count == 1 ? $"{options[0]} goes" : $"{string.Join(", ", options)} go";
        return $"{what} after the subcommand, not before it. Run:{Environment.NewLine}  {string.Join(' ', corrected.Select(Quote))}";
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
