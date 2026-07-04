using System.CommandLine;

namespace IntuneLobPublisher.Cli.Commands;

/// <summary>`publish` is a stub until the Graph flow lands (issue-003).</summary>
internal static class PublishCommand
{
    public static Command Create()
    {
        var manifestOption = CommandSupport.ManifestOption();
        var manifestListOption = CommandSupport.ManifestListOption();
        var repoRootOption = CommandSupport.RepoRootOption();
        var verboseOption = CommandSupport.VerboseOption();

        var command = new Command("publish", "Publishes staged packages to Microsoft Intune (not implemented yet).");
        command.Options.Add(manifestOption);
        command.Options.Add(manifestListOption);
        command.Options.Add(repoRootOption);
        command.Options.Add(verboseOption);

        command.SetAction(_ =>
        {
            Console.Error.WriteLine("publish is not implemented yet. The Microsoft Graph flow is tracked by issue-003.");
            return ExitCodes.NotImplemented;
        });

        return command;
    }
}
