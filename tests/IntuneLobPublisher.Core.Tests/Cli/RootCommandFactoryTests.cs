using System.CommandLine;
using System.Globalization;
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

    private static (int ExitCode, string Output, string Error) InvokeWithCulture(
        string cultureName, params string[] args)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            return Invoke(args);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("ja-JP")]
    public void Invoke_NoArguments_ShowsHelpAndReturnsSuccess(string cultureName)
    {
        var (exitCode, output, error) = InvokeWithCulture(cultureName);

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.Contains("--help", output);
        Assert.Contains("validate", output);
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("ja-JP")]
    public void Invoke_HelpOption_ReturnsSuccess(string cultureName)
    {
        var (exitCode, output, error) = InvokeWithCulture(cultureName, "--help");

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.Contains("--help", output);
        Assert.Contains("validate", output);
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    public void Invoke_UnknownSubcommand_ReturnsNonZero()
    {
        var (exitCode, _, _) = Invoke("does-not-exist");

        Assert.AreNotEqual(ExitCodes.Success, exitCode);
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("ja-JP")]
    public void Invoke_Subcommand_RunsSubcommandAction(string cultureName)
    {
        var (exitCode, output, error) = InvokeWithCulture(cultureName, "validate");

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.AreEqual(string.Empty, output);
        Assert.AreEqual(string.Empty, error);
    }
}
