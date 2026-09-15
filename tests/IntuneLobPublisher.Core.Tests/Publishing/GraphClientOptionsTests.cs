using IntuneLobPublisher.Core.Publishing;

namespace IntuneLobPublisher.Core.Tests.Publishing;

[TestClass]
public sealed class GraphClientOptionsTests
{
    [TestMethod]
    public void BaseAddress_DefaultsToBeta()
    {
        var options = new GraphClientOptions();

        Assert.AreEqual(new Uri("https://graph.microsoft.com/beta/"), options.BaseAddress);
    }

    [TestMethod]
    public void BaseAddress_NoTrailingSlash_IsNormalizedSoThePathPrefixSurvives()
    {
        var options = new GraphClientOptions { BaseAddress = new Uri("https://stub.example.local/custom-prefix") };

        Assert.AreEqual(new Uri("https://stub.example.local/custom-prefix/"), options.BaseAddress);
        Assert.AreEqual(
            new Uri("https://stub.example.local/custom-prefix/deviceAppManagement/mobileApps"),
            new Uri(options.BaseAddress, "deviceAppManagement/mobileApps"));
    }

    [TestMethod]
    public void BaseAddress_WithTrailingSlash_IsUnchanged()
    {
        var options = new GraphClientOptions { BaseAddress = new Uri("https://stub.example.local/custom-prefix/") };

        Assert.AreEqual(new Uri("https://stub.example.local/custom-prefix/"), options.BaseAddress);
    }
}
