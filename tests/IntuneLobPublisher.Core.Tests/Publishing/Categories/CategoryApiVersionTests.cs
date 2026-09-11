using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing.Categories;

namespace IntuneLobPublisher.Core.Tests.Publishing.Categories;

[TestClass]
public sealed class CategoryApiVersionTests
{
    [TestMethod]
    public void UseBeta_Windows_IsTrue()
    {
        // win32LobApp's Graph calls all stay on beta (displayVersion/roleScopeTagIds are beta-only;
        // doc/adr/publishing.md 2026-09-10 entry), so category calls for the same app must match.
        var app = TestManifests.CreateValidApp();

        Assert.IsTrue(CategoryApiVersion.UseBeta(app));
    }

    [TestMethod]
    public void UseBeta_MacOsPkg_IsTrue()
    {
        var app = TestManifests.CreateValidMacOsApp(appType: "pkg");

        Assert.IsTrue(CategoryApiVersion.UseBeta(app));
    }

    [TestMethod]
    public void UseBeta_MacOsLob_IsTrue()
    {
        // macOSLobApp category calls also stay on beta: roleScopeTagIds is beta-only there too
        // (doc/adr/publishing.md 2026-09-10 entry), matching the app/content calls for the same app.
        var app = TestManifests.CreateValidMacOsApp(appType: "lob");

        Assert.IsTrue(CategoryApiVersion.UseBeta(app));
    }
}
