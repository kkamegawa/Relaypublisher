using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Staging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class IntuneWinPackagerTests
{
    private DirectoryInfo _workspace = null!;
    private string _stagingDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("packager-tests-");
        _stagingDirectory = Path.Combine(_workspace.FullName, "Contoso.Tool", "windows-x64", "staging");
        Directory.CreateDirectory(_stagingDirectory);
        File.WriteAllText(Path.Combine(_stagingDirectory, "install.ps1"), "Write-Host 'install'");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private sealed class FakeToolResolver : IIntuneWinToolResolver
    {
        public string? Version { get; set; } = "1.8.7";

        public Task<ResolvedIntuneWinTool> ResolveAsync(IntuneWinToolOptions options, CancellationToken cancellationToken)
            => Task.FromResult(new ResolvedIntuneWinTool(
                Path.Combine("tools", "1.8.7", "IntuneWinAppUtil.exe"), Version, new string('b', 64)));
    }

    /// <summary>Simulates IntuneWinAppUtil.exe: writes the expected .intunewin on success.</summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public string StandardError { get; set; } = string.Empty;
        public bool WriteOutputFile { get; set; } = true;
        public List<string>? LastArguments { get; private set; }

        public async Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            LastArguments = [.. arguments];
            if (ExitCode == 0 && WriteOutputFile)
            {
                var outputDirectory = LastArguments[LastArguments.IndexOf("-o") + 1];
                var setupFile = LastArguments[LastArguments.IndexOf("-s") + 1];
                var outputPath = Path.Combine(
                    outputDirectory, Path.GetFileNameWithoutExtension(setupFile) + ".intunewin");
                await File.WriteAllTextAsync(outputPath, $"encrypted-{Guid.NewGuid()}", cancellationToken);
            }

            return new ProcessRunResult(ExitCode, "tool output", StandardError);
        }
    }

    private static IntuneWinPackager CreatePackager(FakeProcessRunner runner, FakeToolResolver? toolResolver = null)
        => new(toolResolver ?? new FakeToolResolver(), runner, NullLogger<IntuneWinPackager>.Instance);

    private StagingResult CreateStagingResult(string setupFile = "install.ps1", bool dryRun = false)
        => new(
            "Contoso.Tool", "windows", "x64", _stagingDirectory, setupFile,
            dryRun, SummaryPath: null, RepositoryFiles: [], ExternalFiles: []);

    private IntuneWinToolOptions ToolOptions()
        => new(null, null, null, Path.Combine(_workspace.FullName, "tools"));

    private Task<IntuneWinPackageResult> PackageAsync(
        FakeProcessRunner runner, StagingResult? stagingResult = null, FakeToolResolver? toolResolver = null)
        => CreatePackager(runner, toolResolver).CreatePackageAsync(
            TestManifests.CreateValid(),
            stagingResult ?? CreateStagingResult(),
            ToolOptions(),
            CancellationToken.None);

    [TestMethod]
    public async Task CreatePackageAsync_GeneratesIntuneWinAndMetadata()
    {
        var runner = new FakeProcessRunner();

        var result = await PackageAsync(runner);

        Assert.IsTrue(File.Exists(result.IntuneWinPath));
        Assert.AreEqual("install.intunewin", Path.GetFileName(result.IntuneWinPath));
        Assert.AreEqual("1.8.7", result.ToolVersion);
        Assert.AreEqual(new string('b', 64), result.ToolSha256);
        Assert.AreEqual(64, result.InputHash.Length);

        // The tool must be invoked in quiet mode against the staging directory.
        Assert.IsNotNull(runner.LastArguments);
        Assert.Contains("-q", runner.LastArguments);
        Assert.AreEqual(_stagingDirectory, runner.LastArguments[runner.LastArguments.IndexOf("-c") + 1]);
    }

    [TestMethod]
    public async Task CreatePackageAsync_MetadataJsonContainsInputHashAndToolInfo()
    {
        var result = await PackageAsync(new FakeProcessRunner());

        Assert.IsTrue(File.Exists(result.MetadataPath));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetadataPath));
        var root = metadata.RootElement;
        Assert.AreEqual("Contoso.Tool", root.GetProperty("packageIdentifier").GetString());
        Assert.AreEqual("1.2.3", root.GetProperty("packageVersion").GetString());
        Assert.AreEqual(result.InputHash, root.GetProperty("inputHash").GetString());
        Assert.AreEqual("1.8.7", root.GetProperty("tool").GetProperty("version").GetString());
        Assert.AreEqual(new string('b', 64), root.GetProperty("tool").GetProperty("sha256").GetString());
        Assert.AreEqual(result.IntuneWinSha256, root.GetProperty("intuneWinSha256").GetString());
    }

    [TestMethod]
    public async Task CreatePackageAsync_UnpinnedLocalTool_MetadataStillContainsNullVersionField()
    {
        var result = await PackageAsync(new FakeProcessRunner(), toolResolver: new FakeToolResolver { Version = null });

        Assert.IsNull(result.ToolVersion);
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(result.MetadataPath));
        var toolElement = metadata.RootElement.GetProperty("tool");
        // The version key must be present (as null) rather than dropped, since the field's
        // absence vs. its explicit null both look like a JSON parse-time no-op but only the
        // latter tells an auditor that no pinned version was recorded.
        Assert.IsTrue(toolElement.TryGetProperty("version", out var versionProperty));
        Assert.AreEqual(JsonValueKind.Null, versionProperty.ValueKind);
    }

    [TestMethod]
    public async Task CreatePackageAsync_SameStagedInput_ProducesSameInputHashAcrossRuns()
    {
        var first = await PackageAsync(new FakeProcessRunner());
        var second = await PackageAsync(new FakeProcessRunner());

        Assert.AreEqual(first.InputHash, second.InputHash);
        // The .intunewin itself is not deterministic (random encryption key per run).
        Assert.AreNotEqual(first.IntuneWinSha256, second.IntuneWinSha256);
    }

    [TestMethod]
    public async Task CreatePackageAsync_NonZeroExit_ThrowsWithToolOutput()
    {
        var runner = new FakeProcessRunner { ExitCode = 1, StandardError = "setup file not found" };

        var exception = await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageAsync(runner));

        Assert.Contains("exited with code 1", exception.Message);
        Assert.Contains("setup file not found", exception.Message);
    }

    [TestMethod]
    public async Task CreatePackageAsync_OutputFileMissingAfterSuccess_Throws()
    {
        var runner = new FakeProcessRunner { WriteOutputFile = false };

        await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageAsync(runner));
    }

    [TestMethod]
    public async Task CreatePackageAsync_MissingSetupFile_Throws()
    {
        var exception = await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageAsync(
            new FakeProcessRunner(), CreateStagingResult(setupFile: "setup.exe")));

        Assert.Contains("setup.exe", exception.Message);
    }

    [TestMethod]
    public async Task CreatePackageAsync_DryRunStagingResult_Throws()
    {
        await Assert.ThrowsExactlyAsync<PackagingException>(() => PackageAsync(
            new FakeProcessRunner(), CreateStagingResult(dryRun: true)));
    }

    [TestMethod]
    public async Task CreatePackageAsync_UnsafeSetupFile_Throws()
    {
        await Assert.ThrowsExactlyAsync<UnsafePathException>(() => PackageAsync(
            new FakeProcessRunner(), CreateStagingResult(setupFile: "../evil.ps1")));
    }
}
