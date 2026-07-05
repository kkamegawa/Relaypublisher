using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class InputHashCalculatorTests
{
    private DirectoryInfo _workspace = null!;
    private string _stagingDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspace = Directory.CreateTempSubdirectory("inputhash-tests-");
        _stagingDirectory = Path.Combine(_workspace.FullName, "staging");
        WriteStagedFile("install.ps1", "Write-Host 'install'");
        WriteStagedFile("bin/contoso-tool.exe", "binary-content");
    }

    [TestCleanup]
    public void Cleanup() => _workspace.Delete(recursive: true);

    private void WriteStagedFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_stagingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_SameInput_ProducesSameHash()
    {
        var first = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);
        var second = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        Assert.AreEqual(first, second);
        Assert.AreEqual(64, first.Length);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_FileContentChange_ChangesHash()
    {
        var before = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        WriteStagedFile("install.ps1", "Write-Host 'changed'");
        var after = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_ManifestChange_ChangesHash()
    {
        var before = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        var changed = TestManifests.CreateValid();
        changed.PackageVersion = "9.9.9";
        var after = await InputHashCalculator.ComputeInputHashAsync(
            changed, _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public async Task ComputeInputHashAsync_AddedFile_ChangesHash()
    {
        var before = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        WriteStagedFile("extra.txt", "extra");
        var after = await InputHashCalculator.ComputeInputHashAsync(
            TestManifests.CreateValid(), _stagingDirectory, CancellationToken.None);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void ComputeManifestHash_IsStableForEqualManifests()
    {
        Assert.AreEqual(
            InputHashCalculator.ComputeManifestHash(TestManifests.CreateValid()),
            InputHashCalculator.ComputeManifestHash(TestManifests.CreateValid()));
    }
}
