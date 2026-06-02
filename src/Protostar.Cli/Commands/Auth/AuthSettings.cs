using System.ComponentModel;
using Spectre.Console.Cli;

namespace Protostar.Cli.Commands.Auth;

/// <summary>Options shared by every <c>protostar auth</c> command.</summary>
internal abstract class AuthSettings : CommandSettings
{
    [CommandOption("--registry <URL>")]
    [Description("Registry base URL. Defaults to $PROTOSTAR_REGISTRY_URL, then the built-in dev default.")]
    public string? Registry { get; init; }
}
