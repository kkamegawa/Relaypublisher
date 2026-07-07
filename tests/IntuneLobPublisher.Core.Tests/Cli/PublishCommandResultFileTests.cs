using System.Text.Json;
using IntuneLobPublisher.Cli.Commands;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

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
    public async Task WriteResultFileAsync_WritesStableJsonArray()
    {
        var resultFile = Path.Combine(_tempDirectory, "result.json");
        var results = new[]
        {
            new PublishResultEntry(
                "Contoso.Tool",
                "1.2.3",
                "windows",
                "x64",
                "manifests/contoso-tool.yaml",
                "published",
                "app-1",
                "uploaded",
                null),
            new PublishResultEntry(
                "Contoso.Tool",
                "1.2.2",
                "windows",
                "arm64",
                "manifests/contoso-tool.yaml",
                "skipped-downgrade",
                "app-2",
                null,
                "Manifest version is lower."),
            new PublishResultEntry(
                "Contoso.Tool",
                "1.2.3",
                "windows",
                "x86",
                "manifests/contoso-tool.yaml",
                "failed",
                null,
                null,
                "Content upload failed."),
        };

        await PublishCommand.WriteResultFileAsync(resultFile, results, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile));
        var root = document.RootElement;
        Assert.AreEqual(JsonValueKind.Array, root.ValueKind);
        Assert.AreEqual(3, root.GetArrayLength());
        Assert.AreEqual("Contoso.Tool", root[0].GetProperty("packageIdentifier").GetString());
        Assert.AreEqual("1.2.3", root[0].GetProperty("packageVersion").GetString());
        Assert.AreEqual("published", root[0].GetProperty("outcome").GetString());
        Assert.AreEqual("app-1", root[0].GetProperty("appId").GetString());
        Assert.AreEqual("uploaded", root[0].GetProperty("contentOutcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, root[0].GetProperty("skipReason").ValueKind);
        Assert.AreEqual("skipped-downgrade", root[1].GetProperty("outcome").GetString());
        Assert.AreEqual("Manifest version is lower.", root[1].GetProperty("skipReason").GetString());
        Assert.AreEqual("failed", root[2].GetProperty("outcome").GetString());
        Assert.AreEqual("Content upload failed.", root[2].GetProperty("skipReason").GetString());
    }

    [TestMethod]
    public async Task WriteResultFileAsync_NullPathDoesNothing()
    {
        await PublishCommand.WriteResultFileAsync(null, [], CancellationToken.None);

        Assert.IsFalse(Directory.EnumerateFileSystemEntries(_tempDirectory).Any());
    }

    [TestMethod]
    public async Task WriteResultFileAsync_MissingParentDirectoryThrowsPublishResultOutputException()
    {
        var resultFile = Path.Combine(_tempDirectory, "nested", "result.json");

        var exception = await Assert.ThrowsExactlyAsync<PublishResultOutputException>(
            () => PublishCommand.WriteResultFileAsync(resultFile, [], CancellationToken.None));

        StringAssert.Contains(exception.Message, "does not exist");
    }

    [TestMethod]
    public async Task WriteResultFileAsync_DirectoryPathThrowsPublishResultOutputException()
    {
        var exception = await Assert.ThrowsExactlyAsync<PublishResultOutputException>(
            () => PublishCommand.WriteResultFileAsync(_tempDirectory, [], CancellationToken.None));

        StringAssert.Contains(exception.Message, "directory");
    }
}
