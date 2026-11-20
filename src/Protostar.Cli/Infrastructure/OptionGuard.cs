using System.Reflection;
using Spectre.Console.Cli;

namespace Protostar.Cli.Infrastructure;

/// <summary>Refuses an option that is misplaced or that the command does not declare, before anything runs.</summary>
/// <remarks>
/// <para>
/// The branches (<c>skills</c>, <c>sessions</c>, <c>project</c>, <c>auth</c>) carry no options of their own; every
/// option belongs to a subcommand. Spectre still accepts <c>protostar skills --global-only push</c>: it binds the
/// option to the branch, finds no home for it, and drops it, so <c>push</c> ran against project skills too. Strict
/// parsing would refuse it, but only as "Unknown option", which does not say the option is real and merely misplaced.
/// This guard runs once Spectre has chosen the subcommand, so the subcommand's name comes from the parser rather than
/// a second list of names here, and the error can give the command line that does what was meant. A branch's default
/// command (<c>protostar skills --global-only</c>) has no subcommand token, so its options are where they belong.
/// An option before the first command (<c>protostar --registry X skills remote</c>) never reaches the interceptor, so
/// <see cref="CheckRoot"/> covers it before Spectre parses.
/// </para>
/// <para>
/// An option the command does not declare (<c>skills pull --forse</c>) is otherwise dropped too, and the command runs
/// without the flag that was meant. Spectre already sorts every token into declared options, their values, and the
/// rest, and keeps the rest in <see cref="CommandContext.Remaining"/>; <see cref="CheckUnknown"/> refuses whatever is
/// there, so what counts as an option, a value (<c>--project=-x</c>), or <c>--opt=value</c> is Spectre's decision and
/// not a second tokenizer's. The settings type supplies the names to suggest.
/// </para>
/// </remarks>
internal sealed class OptionGuard : ICommandInterceptor
{
    /// <summary>The name Spectre gives a default command, which is never typed.</summary>
    private const string DefaultCommandName = "__default_command";

    public void Intercept(CommandContext context, CommandSettings settings)
    {
        var problem = Check(context.Arguments, context.Name)
            ?? CheckUnknown(
                context.Remaining.Parsed.Select(g => g.Key).ToList(),
                context.Arguments,
                settings.GetType(),
                CommandPath(context.Arguments, context.Name));
        if (problem is not null)
            throw new RefusedOptionException(problem);
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

    /// <summary>
    /// The error for the options in <paramref name="unknown"/>, which the command <paramref name="commandPath"/> with
    /// settings <paramref name="settingsType"/> does not declare, or null when there are none.
    /// </summary>
    /// <remarks>
    /// Each one gets the closest option the command declares, when one is close enough to be a typo. Hidden options
    /// are accepted (Spectre binds them) but never suggested. A version flag gets the root command instead, since the
    /// version belongs to <c>protostar</c> and not to the command it was typed after. Spectre reads a long name typed
    /// with one dash (<c>-force</c>) as a run of short options; when none of them is declared, the token in
    /// <paramref name="args"/> is named as typed so it can be matched against the long names.
    /// </remarks>
    internal static string? CheckUnknown(
        IReadOnlyList<string> unknown, IReadOnlyList<string> args, Type settingsType, string commandPath)
    {
        if (unknown.Count == 0)
            return null;
        unknown = RegroupShortRuns(unknown, args);

        var command = commandPath.Length == 0 ? "protostar" : $"protostar {commandPath}";
        if (unknown.Count == 1 && unknown[0] is "--version" or "-v")
            return $"{unknown[0]} is not an option of {command}. For the version, run:{Environment.NewLine}  protostar --version";

        var declared = settingsType.GetProperties()
            .Select(p => p.GetCustomAttribute<CommandOptionAttribute>())
            .OfType<CommandOptionAttribute>()
            .Where(o => !o.IsHidden)
            .SelectMany(o => o.LongNames)
            .Select(name => $"--{name}")
            .ToList();

        var help = $"{Environment.NewLine}  {command} --help";
        if (unknown.Count == 1)
        {
            var hint = Closest(unknown[0], declared) is { } near ? $" Did you mean {near}? See:" : " See:";
            return $"Unknown option {unknown[0]} for {command}.{hint}{help}";
        }

        var named = unknown.Select(option => Closest(option, declared) is { } near
            ? $"{option} (did you mean {near}?)"
            : option);
        return $"Unknown options {string.Join(", ", named)} for {command}. See:{help}";
    }

    /// <summary>
    /// <paramref name="unknown"/> with each run of short options that came from one typed token (<c>-force</c>)
    /// replaced by that token.
    /// </summary>
    private static IReadOnlyList<string> RegroupShortRuns(IReadOnlyList<string> unknown, IReadOnlyList<string> args)
    {
        var result = unknown.ToList();
        foreach (var token in args.Where(t => t.Length > 2 && t[0] == '-' && char.IsLetter(t[1]) && !t.Contains('=')))
        {
            var letters = token[1..].Select(c => $"-{c}").Distinct().ToList();
            if (!letters.All(result.Contains))
                continue;
            var at = result.IndexOf(letters[0]);
            result.RemoveAll(letters.Contains);
            result.Insert(Math.Min(at, result.Count), token);
        }

        return result;
    }

    /// <summary>The command as typed after <c>protostar</c> (<c>skills pull</c>), or empty for the root command.</summary>
    internal static string CommandPath(IReadOnlyList<string> args, string commandName)
    {
        var first = args.Count > 0 && !args[0].StartsWith('-') ? args[0] : string.Empty;
        if (commandName == DefaultCommandName || string.Equals(first, commandName, StringComparison.OrdinalIgnoreCase))
            return first;
        return $"{first} {commandName}";
    }

    /// <summary>The declared option closest to <paramref name="typed"/>, or null when none is close enough to be a typo.</summary>
    /// <remarks>
    /// Only long names are compared: one letter is too short to tell a typo from a different option. A limit of a
    /// third of the name's length, at least one edit, keeps <c>--dri</c> to <c>--dir</c> and <c>--projct</c> to
    /// <c>--project</c> without offering <c>--all</c> for <c>--yes</c>.
    /// </remarks>
    private static string? Closest(string typed, IReadOnlyList<string> declared)
    {
        var name = typed.TrimStart('-');
        if (name.Length < 2)
            return null;

        var limit = Math.Max(1, name.Length / 3);
        return declared
            .Select(option => (name: option, distance: EditDistance(name, option[2..])))
            .Where(c => c.distance <= limit)
            .OrderBy(c => c.distance)
            .Select(c => c.name)
            .FirstOrDefault();
    }

    /// <summary>Edits (insert, delete, substitute, swap two adjacent letters) from <paramref name="a"/> to <paramref name="b"/>, ignoring case.</summary>
    private static int EditDistance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }

        return d[a.Length, b.Length];
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

/// <summary>A command line with a misplaced or unknown option; Spectre prints the message and exits non-zero.</summary>
internal sealed class RefusedOptionException : Exception
{
    public RefusedOptionException(string message)
        : base(message)
    {
    }
}
