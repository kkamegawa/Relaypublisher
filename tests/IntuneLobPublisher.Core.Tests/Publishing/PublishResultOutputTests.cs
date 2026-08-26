using System.Text.Json;
using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Publishing.Categories;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class PublishResultOutputTests
{
    [TestMethod]
    public void Serialize_PublishedEntry_WritesStableMachineReadableFields()
    {
        var request = CreateRequest();
        var result = new PublishResult(
            PublishOutcome.Published,
            "app-1",
            AppCreated: true,
            ContentUploadOutcome.Uploaded,
            AssignmentPlan: null,
            SkipReason: null);

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromResult(request, result)]);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement[0];
        Assert.AreEqual("Contoso.Tool", entry.GetProperty("packageIdentifier").GetString());
        Assert.AreEqual("1.2.3", entry.GetProperty("packageVersion").GetString());
        Assert.AreEqual("windows", entry.GetProperty("platform").GetString());
        Assert.AreEqual("x64", entry.GetProperty("architecture").GetString());
        Assert.AreEqual("manifests/contoso-tool.yaml", entry.GetProperty("manifestPath").GetString());
        Assert.AreEqual("published", entry.GetProperty("outcome").GetString());
        Assert.AreEqual("app-1", entry.GetProperty("appId").GetString());
        Assert.AreEqual("uploaded", entry.GetProperty("contentOutcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("skipReason").ValueKind);
    }

    [TestMethod]
    public void Serialize_DryRunNewApp_WritesNullAppIdAndContentOutcome()
    {
        var request = CreateRequest();
        var result = new PublishResult(
            PublishOutcome.DryRunCompleted,
            AppId: null,
            AppCreated: false,
            ContentOutcome: null,
            AssignmentPlan: null,
            SkipReason: null);

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromResult(request, result)]);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement[0];
        Assert.AreEqual("dry-run", entry.GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("appId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("contentOutcome").ValueKind);
    }

    [TestMethod]
    [DataRow(PublishOutcome.SkippedDowngrade, "skipped-downgrade")]
    [DataRow(PublishOutcome.SkippedPlatformNotSupported, "skipped-platform")]
    public void Serialize_SkipEntries_WriteOutcomeAndReason(PublishOutcome outcome, string expectedWireValue)
    {
        var request = CreateRequest();
        var result = new PublishResult(
            outcome,
            "app-1",
            AppCreated: false,
            ContentOutcome: null,
            AssignmentPlan: null,
            SkipReason: "skip reason");

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromResult(request, result)]);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement[0];
        Assert.AreEqual(expectedWireValue, entry.GetProperty("outcome").GetString());
        Assert.AreEqual("skip reason", entry.GetProperty("skipReason").GetString());
    }

    [TestMethod]
    public void Serialize_FailedEntry_WritesFailureMessageAsSkipReason()
    {
        var request = CreateRequest();

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromFailure(request, "content failed")]);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement[0];
        Assert.AreEqual("failed", entry.GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("appId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("contentOutcome").ValueKind);
        Assert.AreEqual("content failed", entry.GetProperty("skipReason").GetString());
    }

    [TestMethod]
    public void Serialize_MixedEntries_KeepsEveryAggregatedResult()
    {
        var request = CreateRequest();
        var entries = new[]
        {
            PublishResultOutput.FromResult(
                request,
                new PublishResult(PublishOutcome.Published, "app-1", false, ContentUploadOutcome.SkippedUnchanged, null, null)),
            PublishResultOutput.FromResult(
                request,
                new PublishResult(PublishOutcome.SkippedDowngrade, "app-2", false, null, null, "older version")),
            PublishResultOutput.FromFailure(request, "upload failed"),
        };

        var json = PublishResultOutput.Serialize(entries);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(3, document.RootElement.GetArrayLength());
        Assert.AreEqual("published", document.RootElement[0].GetProperty("outcome").GetString());
        Assert.AreEqual("skipped-unchanged", document.RootElement[0].GetProperty("contentOutcome").GetString());
        Assert.AreEqual("skipped-downgrade", document.RootElement[1].GetProperty("outcome").GetString());
        Assert.AreEqual("failed", document.RootElement[2].GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task WriteAsync_MissingParentDirectory_ThrowsPublisherException()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var resultPath = Path.Combine(missingDirectory, "publish-results.json");

        var exception = await Assert.ThrowsExactlyAsync<PublishResultOutputException>(
            () => PublishResultOutput.WriteAsync(resultPath, [], CancellationToken.None));

        StringAssert.Contains(exception.Message, "does not exist");
    }

    private static CategoryPlan CategoryPlanWith(params CategoryPlanAction[] actions)
        => new(
            "app-1",
            Requested: true,
            [.. actions.Select((action, index) => new CategoryPlanEntry(action, $"cat-{index}", $"Category {index}"))]);

    [TestMethod]
    public void Serialize_PublishedEntry_KeepsTheExistingFieldOrderAndAppendsCategoryOutcome()
    {
        // categoryOutcome, warningCodes and forceAcknowledged are purely additive: every previously
        // written field keeps its name, type and position (issue #99, issue #116).
        var request = CreateRequest();
        var result = new PublishResult(
            PublishOutcome.Published,
            "app-1",
            AppCreated: true,
            ContentUploadOutcome.Uploaded,
            AssignmentPlan: null,
            SkipReason: null,
            CategoryPlan: CategoryPlanWith(CategoryPlanAction.Add));

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromResult(request, result)]);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement[0];
        CollectionAssert.AreEqual(
            new[]
            {
                "packageIdentifier", "packageVersion", "platform", "architecture", "manifestPath",
                "outcome", "appId", "contentOutcome", "skipReason", "categoryOutcome",
                "warningCodes", "forceAcknowledged",
            },
            entry.EnumerateObject().Select(p => p.Name).ToList());
        Assert.AreEqual("applied", entry.GetProperty("categoryOutcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("warningCodes").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("forceAcknowledged").ValueKind);
    }

    [TestMethod]
    public void Serialize_PublishedEntryWithWarnings_WritesWarningCodesAndForceAcknowledged()
    {
        var request = CreateRequest();
        var result = new PublishResult(
            PublishOutcome.Published, "app-1", AppCreated: true, ContentUploadOutcome.Uploaded,
            AssignmentPlan: null, SkipReason: null, CategoryPlan: null);

        var json = PublishResultOutput.Serialize(
            [PublishResultOutput.FromResult(request, result, ["ManifestBundleNotFound"], forceAcknowledged: true)]);

        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement[0];
        CollectionAssert.AreEqual(
            new[] { "ManifestBundleNotFound" },
            entry.GetProperty("warningCodes").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.IsTrue(entry.GetProperty("forceAcknowledged").GetBoolean());
    }

    [TestMethod]
    public void Serialize_PublishedEntryWithNoCategoryDiff_WritesUnchanged()
    {
        var request = CreateRequest();
        var result = new PublishResult(
            PublishOutcome.Published, "app-1", false, ContentUploadOutcome.SkippedUnchanged, null, null,
            CategoryPlanWith(CategoryPlanAction.Keep));

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromResult(request, result)]);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("unchanged", document.RootElement[0].GetProperty("categoryOutcome").GetString());
    }

    [TestMethod]
    public void Serialize_PublishedEntryWithoutManifestCategories_WritesNotRequested()
    {
        var request = CreateRequest();
        var result = new PublishResult(
            PublishOutcome.Published, "app-1", false, ContentUploadOutcome.Uploaded, null, null,
            CategoryPlan.NotRequested("app-1"));

        var json = PublishResultOutput.Serialize([PublishResultOutput.FromResult(request, result)]);

        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("not-requested", document.RootElement[0].GetProperty("categoryOutcome").GetString());
    }

    [TestMethod]
    public void Serialize_DryRunAndSkips_LeaveCategoryOutcomeNull()
    {
        // Nothing was written, so claiming a category outcome would be wrong.
        var request = CreateRequest();
        var entries = new[]
        {
            PublishResultOutput.FromResult(
                request,
                new PublishResult(
                    PublishOutcome.DryRunCompleted, "app-1", false, null, null, null,
                    CategoryPlanWith(CategoryPlanAction.Add))),
            PublishResultOutput.FromResult(
                request,
                new PublishResult(PublishOutcome.SkippedDowngrade, "app-1", false, null, null, "older version")),
            PublishResultOutput.FromFailure(request, "category sync failed"),
        };

        var json = PublishResultOutput.Serialize(entries);

        using var document = JsonDocument.Parse(json);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("categoryOutcome").ValueKind);
        }

        // Issue #99 explicitly accepts that a late failure still reports a null appId.
        Assert.AreEqual(JsonValueKind.Null, document.RootElement[2].GetProperty("appId").ValueKind);
    }

    [TestMethod]
    public async Task WriteAsync_InvalidPath_ThrowsPublisherException()
    {
        var exception = await Assert.ThrowsExactlyAsync<PublishResultOutputException>(
            () => PublishResultOutput.WriteAsync("bad\0path.json", [], CancellationToken.None));

        StringAssert.Contains(exception.Message, "invalid");
    }

    private static PublishRequest CreateRequest()
    {
        var manifest = TestManifests.CreateValid();
        return new PublishRequest(
            manifest,
            manifest.Apps[0],
            "manifests/contoso-tool.yaml",
            Directory.GetCurrentDirectory(),
            Directory.GetCurrentDirectory(),
            "commit-1",
            AllowDowngrade: false,
            DryRun: false);
    }
}
