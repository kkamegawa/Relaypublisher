using System.Security.Cryptography;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class IntuneWinToolResolverTests
{
    private static readonly byte[] ToolContent = "fake-intunewinapputil"u8.ToArray();
    private static readonly string ToolContentSha256 =
        Convert.ToHexStringLower(SHA256.HashData(ToolContent));

    private DirectoryInfo _workspace = null!;
    private string _toolsDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("toolresolver-tests-");
        _toolsDirectory = Path.Combine(_workspace.FullName, "tools");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    /// <summary>Serves ToolContent for any version without network access.</summary>
    private sealed class FakeDownloader : IIntuneWinToolDownloader
    {
        public string LatestVersion { get; set; } = "1.8.7";
        public int LatestVersionCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<string> GetLatestVersionAsync(CancellationToken cancellationToken)
        {
            LatestVersionCalls++;
            return Task.FromResult(LatestVersion);
        }

        public async Task DownloadAsync(string version, string destinationPath, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, ToolContent, cancellationToken);
        }
    }

    private static IntuneWinToolResolver CreateResolver(
        FakeDownloader downloader,
        string? environmentToolPath = null)
        => new(
            downloader,
            NullLogger<IntuneWinToolResolver>.Instance,
            name => name == IntuneWinToolResolver.ToolPathEnvironmentVariable ? environmentToolPath : null);

    private IntuneWinToolOptions Options(
        string? explicitPath = null, string? pinnedVersion = null, string? knownSha256 = null)
        => new(explicitPath, pinnedVersion, knownSha256, _toolsDirectory);

    private string WriteLocalTool(string relativePath)
    {
        var fullPath = Path.Combine(_workspace.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, ToolContent);
        return fullPath;
    }

    [TestMethod]
    public async Task ResolveAsync_ExplicitPath_IsUsedWithoutDownload()
    {
        var toolPath = WriteLocalTool("local/IntuneWinAppUtil.exe");
        var downloader = new FakeDownloader();

        var resolved = await CreateResolver(downloader).ResolveAsync(
            Options(explicitPath: toolPath), CancellationToken.None);

        Assert.AreEqual(Path.GetFullPath(toolPath), resolved.Path);
        Assert.AreEqual(ToolContentSha256, resolved.Sha256);
        // Version is unknown for a local path: it always wins over --intunewin-tool-version,
        // so echoing the pin back would misreport what actually ran.
        Assert.IsNull(resolved.Version);
        Assert.AreEqual(0, downloader.DownloadCalls);
        Assert.AreEqual(0, downloader.LatestVersionCalls);
    }

    [TestMethod]
    public async Task ResolveAsync_ExplicitPathWithPinnedVersion_ReportsVersionAsUnknown()
    {
        var toolPath = WriteLocalTool("local/IntuneWinAppUtil.exe");

        var resolved = await CreateResolver(new FakeDownloader()).ResolveAsync(
            Options(explicitPath: toolPath, pinnedVersion: "v1.8.7"), CancellationToken.None);

        Assert.IsNull(resolved.Version);
    }

    [TestMethod]
    public async Task ResolveAsync_ExplicitPathMissing_Throws()
    {
        var resolver = CreateResolver(new FakeDownloader());

        await Assert.ThrowsExactlyAsync<PackagingException>(() => resolver.ResolveAsync(
            Options(explicitPath: Path.Combine(_workspace.FullName, "missing.exe")), CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveAsync_ExplicitPathWithMismatchedKnownSha256_Throws()
    {
        var toolPath = WriteLocalTool("local/IntuneWinAppUtil.exe");
        var resolver = CreateResolver(new FakeDownloader());

        await Assert.ThrowsExactlyAsync<ChecksumMismatchException>(() => resolver.ResolveAsync(
            Options(explicitPath: toolPath, knownSha256: new string('0', 64)), CancellationToken.None));
    }

    [TestMethod]
    public async Task ResolveAsync_EnvironmentVariable_IsUsedWhenNoExplicitPath()
    {
        var toolPath = WriteLocalTool("env/IntuneWinAppUtil.exe");
        var downloader = new FakeDownloader();

        var resolved = await CreateResolver(downloader, environmentToolPath: toolPath)
            .ResolveAsync(Options(), CancellationToken.None);

        Assert.AreEqual(Path.GetFullPath(toolPath), resolved.Path);
        Assert.AreEqual(0, downloader.DownloadCalls);
    }

    [TestMethod]
    public async Task ResolveAsync_NoPin_DownloadsLatestAndRecordsSha256()
    {
        var downloader = new FakeDownloader { LatestVersion = "2.0.0" };

        var resolved = await CreateResolver(downloader).ResolveAsync(Options(), CancellationToken.None);

        Assert.AreEqual("2.0.0", resolved.Version);
        Assert.AreEqual(ToolContentSha256, resolved.Sha256);
        Assert.AreEqual(1, downloader.LatestVersionCalls);
        Assert.AreEqual(1, downloader.DownloadCalls);
        Assert.IsTrue(File.Exists(resolved.Path));
        Assert.Contains("2.0.0", resolved.Path);
    }

    [TestMethod]
    public async Task ResolveAsync_PinnedVersionCached_DoesNotDownload()
    {
        var downloader = new FakeDownloader();
        var cachedPath = Path.Combine(_toolsDirectory, "1.8.7", "IntuneWinAppUtil.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        await File.WriteAllBytesAsync(cachedPath, ToolContent);

        var resolved = await CreateResolver(downloader).ResolveAsync(
            Options(pinnedVersion: "1.8.7"), CancellationToken.None);

        Assert.AreEqual("1.8.7", resolved.Version);
        Assert.AreEqual(0, downloader.DownloadCalls);
        Assert.AreEqual(0, downloader.LatestVersionCalls);
    }

    [TestMethod]
    public async Task ResolveAsync_PinnedWithKnownSha256Match_Succeeds()
    {
        var resolved = await CreateResolver(new FakeDownloader()).ResolveAsync(
            Options(pinnedVersion: "1.8.7", knownSha256: ToolContentSha256.ToUpperInvariant()),
            CancellationToken.None);

        Assert.AreEqual(ToolContentSha256, resolved.Sha256);
    }

    [TestMethod]
    public async Task ResolveAsync_PinnedWithMismatchedKnownSha256_ThrowsAndDeletesDownload()
    {
        var resolver = CreateResolver(new FakeDownloader());

        await Assert.ThrowsExactlyAsync<ChecksumMismatchException>(() => resolver.ResolveAsync(
            Options(pinnedVersion: "1.8.7", knownSha256: new string('0', 64)), CancellationToken.None));

        Assert.IsFalse(File.Exists(Path.Combine(_toolsDirectory, "1.8.7", "IntuneWinAppUtil.exe")));
    }

    [TestMethod]
    [DataRow("../evil")]
    [DataRow("..\\evil")]
    [DataRow("/etc/passwd")]
    [DataRow("a/b")]
    public async Task ResolveAsync_PinnedVersionWithTraversalSegment_ThrowsUnsafePathException(string version)
    {
        var resolver = CreateResolver(new FakeDownloader());

        await Assert.ThrowsExactlyAsync<UnsafePathException>(() => resolver.ResolveAsync(
            Options(pinnedVersion: version), CancellationToken.None));
    }
}
