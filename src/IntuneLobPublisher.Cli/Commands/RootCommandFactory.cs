using System.CommandLine;
using System.CommandLine.Help;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>Builds the `relaypublisher` root command.</summary>
internal static class RootCommandFactory
{
    public const string Description = "Publishes winget-like YAML manifests as Microsoft Intune LOB apps.";

    public static RootCommand Create(IEnumerable<Command> subcommands)
    {
        var rootCommand = new RootCommand(Description);
        foreach (var subcommand in subcommands)
        {
            rootCommand.Subcommands.Add(subcommand);
        }

        // Without its own action, System.CommandLine rejects a bare invocation with
        // "Required command was not provided." and exit code 1. winget-pkgs validation launches
        // the installed executable without arguments, so a bare run shows help and succeeds.
        rootCommand.SetAction(parseResult =>
        {
            new HelpAction().Invoke(parseResult);
            return ExitCodes.Success;
        });

        return rootCommand;
    }
}
