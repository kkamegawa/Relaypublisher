using IntuneLobPublisher.Core.Publishing.Assignments;

namespace IntuneLobPublisher.Core.Tests.Publishing.Assignments;

[TestClass]
public sealed class GraphEndpointsTests
{
    [TestMethod]
    public void ToBeta_SwapsV10SegmentWithTrailingSlash()
    {
        var beta = GraphEndpoints.ToBeta(new Uri("https://graph.microsoft.com/v1.0/"));
        Assert.AreEqual(new Uri("https://graph.microsoft.com/beta/"), beta);
    }

    [TestMethod]
    public void ToBeta_SwapsV10SegmentWithoutTrailingSlash()
    {
        var beta = GraphEndpoints.ToBeta(new Uri("https://graph.microsoft.com/v1.0"));
        Assert.AreEqual(new Uri("https://graph.microsoft.com/beta/"), beta);
    }

    [TestMethod]
    public void ToBeta_LeavesStubServerAddressesUnchanged()
    {
        var stub = new Uri("http://localhost:5000/graph/");
        Assert.AreEqual(stub, GraphEndpoints.ToBeta(stub));
    }
}
