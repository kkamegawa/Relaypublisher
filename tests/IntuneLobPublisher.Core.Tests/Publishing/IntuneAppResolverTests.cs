using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class IntuneAppResolverTests
{
    private sealed class FakeDirectory : IIntuneAppDirectory
    {
        private readonly IReadOnlyList<IntuneAppSummary> _apps;

        public FakeDirectory(params IntuneAppSummary[] apps) => _apps = apps;

        public Task<IReadOnlyList<IntuneAppSummary>> ListAppsAsync(CancellationToken cancellationToken)
            => Task.FromResult(_apps);
    }

    private static readonly AppIdentity Identity = new("Contoso.Tool", "windows", "x64");

    private static string MetadataNotes(string packageIdentifier = "Contoso.Tool", string platform = "windows", string architecture = "x64")
        => new ManagementMetadata
        {
            PackageIdentifier = packageIdentifier,
            PackageVersion = "1.0.0",
            Platform = platform,
            Architecture = architecture,
            ManifestPath = "manifests/Contoso/Contoso.Tool/1.0.0/Contoso.Tool.yaml",
            ManifestHash = "manifest-hash",
            InputHash = "input-hash",
            SourceCommit = "abc123",
        }.Serialize();

    [TestMethod]
    public async Task ResolveAsync_NoMatch_ReturnsNotFound()
    {
        var directory = new FakeDirectory(new IntuneAppSummary("app-1", "Unrelated App", null));
        var resolver = new IntuneAppResolver(directory);

        var result = await resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None);

        Assert.AreEqual(AppResolutionOutcome.NotFound, result.Outcome);
        Assert.IsNull(result.AppId);
        Assert.IsFalse(result.NeedsNotesWriteBack);
    }

    [TestMethod]
    public async Task ResolveAsync_SingleMetadataMatch_ReturnsResolvedByMetadata()
    {
        var directory = new FakeDirectory(new IntuneAppSummary("app-1", "Contoso Tool [Windows x64]", MetadataNotes()));
        var resolver = new IntuneAppResolver(directory);

        var result = await resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None);

        Assert.AreEqual(AppResolutionOutcome.ResolvedByMetadata, result.Outcome);
        Assert.AreEqual("app-1", result.AppId);
        Assert.IsNotNull(result.Metadata);
        Assert.IsFalse(result.NeedsNotesWriteBack);
    }

    [TestMethod]
    public async Task ResolveAsync_MetadataForDifferentArchitecture_DoesNotMatch()
    {
        var directory = new FakeDirectory(new IntuneAppSummary("app-1", "Contoso Tool [Windows Arm64]", MetadataNotes(architecture: "arm64")));
        var resolver = new IntuneAppResolver(directory);

        var result = await resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None);

        Assert.AreEqual(AppResolutionOutcome.NotFound, result.Outcome);
    }

    [TestMethod]
    public async Task ResolveAsync_MultipleMetadataMatches_ThrowsAmbiguousWithoutPreferringEither()
    {
        var directory = new FakeDirectory(
            new IntuneAppSummary("app-1", "Contoso Tool [Windows x64]", MetadataNotes()),
            new IntuneAppSummary("app-2", "Contoso Tool [Windows x64] (dup)", MetadataNotes()));
        var resolver = new IntuneAppResolver(directory);

        var ex = await Assert.ThrowsExactlyAsync<AmbiguousAppMatchException>(
            () => resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None));

        Assert.HasCount(2, ex.MatchedAppIds);
        CollectionAssert.Contains(ex.MatchedAppIds.ToList(), "app-1");
        CollectionAssert.Contains(ex.MatchedAppIds.ToList(), "app-2");
    }

    [TestMethod]
    public async Task ResolveAsync_NoMetadataButDisplayNameMatches_ReturnsAdoptedAndNeedsWriteBack()
    {
        var directory = new FakeDirectory(new IntuneAppSummary("app-1", "Contoso Tool [Windows x64]", null));
        var resolver = new IntuneAppResolver(directory);

        var result = await resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None);

        Assert.AreEqual(AppResolutionOutcome.ResolvedByDisplayNameAdopted, result.Outcome);
        Assert.AreEqual("app-1", result.AppId);
        Assert.IsNull(result.Metadata);
        Assert.IsTrue(result.NeedsNotesWriteBack);
    }

    [TestMethod]
    public async Task ResolveAsync_MultipleDisplayNameMatches_ThrowsAmbiguous()
    {
        var directory = new FakeDirectory(
            new IntuneAppSummary("app-1", "Contoso Tool [Windows x64]", null),
            new IntuneAppSummary("app-2", "Contoso Tool [Windows x64]", null));
        var resolver = new IntuneAppResolver(directory);

        var ex = await Assert.ThrowsExactlyAsync<AmbiguousAppMatchException>(
            () => resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None));

        Assert.HasCount(2, ex.MatchedAppIds);
    }

    [TestMethod]
    public async Task ResolveAsync_UnrelatedAdminNotesIgnored_FallsBackToDisplayName()
    {
        var directory = new FakeDirectory(new IntuneAppSummary("app-1", "Contoso Tool [Windows x64]", "Approved by helpdesk"));
        var resolver = new IntuneAppResolver(directory);

        var result = await resolver.ResolveAsync(Identity, "Contoso Tool [Windows x64]", CancellationToken.None);

        Assert.AreEqual(AppResolutionOutcome.ResolvedByDisplayNameAdopted, result.Outcome);
    }
}
