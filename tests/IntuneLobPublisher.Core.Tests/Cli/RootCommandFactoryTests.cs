using System.CommandLine;
using IntuneLobPublisher.Cli.Commands;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// winget-pkgs validation launches the installed executable without arguments and fails on a
/// non-zero exit code (issue #177), so a bare invocation must show help and succeed.
/// </summary>
[TestClass]
public sealed class RootCommandFactoryTests
{
    private static RootCommand CreateRootCommand()
    {
        var validate = new Command("validate", "Validates one or more manifest files.");
        validate.SetAction(_ => ExitCodes.Success);
        return RootCommandFactory.Create([validate]);
    }

    private static (int ExitCode, string Output, string Error) Invoke(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = CreateRootCommand()
            .Parse(args)
            .Invoke(new InvocationConfiguration { Output = output, Error = error });
        return (exitCode, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Invoke_NoArguments_ShowsHelpAndReturnsSuccess()
    {
        var (exitCode, output, _) = Invoke();

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.Contains("Usage:", output);
        Assert.Contains("validate", output);
    }

    [TestMethod]
    public void Invoke_HelpOption_ReturnsSuccess()
    {
        var (exitCode, output, _) = Invoke("--help");

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.Contains("Usage:", output);
    }

    [TestMethod]
    public void Invoke_UnknownSubcommand_ReturnsNonZero()
    {
        var (exitCode, _, _) = Invoke("does-not-exist");

        Assert.AreNotEqual(ExitCodes.Success, exitCode);
    }

    [TestMethod]
    public void Invoke_Subcommand_RunsSubcommandAction()
    {
        var (exitCode, output, _) = Invoke("validate");

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("Usage:", output);
    }
}
