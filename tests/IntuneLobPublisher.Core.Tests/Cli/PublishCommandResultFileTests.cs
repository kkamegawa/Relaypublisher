using System.Text.Json;
using IntuneLobPublisher.Cli.Commands;

namespace IntuneLobPublisher.Core.Tests.Cli;

[TestClass]
public sealed class PublishCommandResultFileTests
{
    private string _tempDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Directory.CreateTempSubdirectory("publish-result-file-").FullName;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    [TestMethod]
    public async Task WriteResultFileAsync_WritesCamelCaseJsonArray()
    {
        var resultFile = Path.Combine(_tempDirectory, "nested", "result.json");
        var results = new[]
        {
            new PublishCommand.PublishResultEntry(
                "Contoso.Tool",
                "windows",
                "x64",
                "manifests/contoso-tool.yaml",
                "published",
                "app-1",
                "uploaded",
                null),
            new PublishCommand.PublishResultEntry(
                "Contoso.Tool",
                "windows",
                "arm64",
                "manifests/contoso-tool.yaml",
                "skipped-downgrade",
                "app-2",
                null,
                "Manifest version is lower."),
        };

        await PublishCommand.WriteResultFileAsync(resultFile, results, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        var root = document.RootElement;
        Assert.AreEqual(JsonValueKind.Array, root.ValueKind);
        Assert.AreEqual(2, root.GetArrayLength());
        Assert.AreEqual("Contoso.Tool", root[0].GetProperty("packageIdentifier").GetString());
        Assert.AreEqual("published", root[0].GetProperty("outcome").GetString());
        Assert.AreEqual("app-1", root[0].GetProperty("appId").GetString());
        Assert.AreEqual("uploaded", root[0].GetProperty("contentOutcome").GetString());
        Assert.IsFalse(root[0].TryGetProperty("skipReason", out _));
        Assert.AreEqual("skipped-downgrade", root[1].GetProperty("outcome").GetString());
        Assert.AreEqual("Manifest version is lower.", root[1].GetProperty("skipReason").GetString());
    }

    [TestMethod]
    public async Task WriteResultFileAsync_NullPathDoesNothing()
    {
        await PublishCommand.WriteResultFileAsync(null, [], CancellationToken.None);

        Assert.IsFalse(Directory.EnumerateFileSystemEntries(_tempDirectory).Any());
    }
}
