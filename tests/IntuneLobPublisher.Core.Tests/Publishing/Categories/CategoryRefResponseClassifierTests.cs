using IntuneLobPublisher.Core.Publishing.Categories;

namespace IntuneLobPublisher.Core.Tests.Publishing.Categories;

/// <summary>
/// The duplicate-<c>$ref</c> shape Intune returns is not documented, so the matching is intentionally
/// narrow. These pin what is and is not accepted as "already in the desired state".
/// </summary>
[TestClass]
public sealed class CategoryRefResponseClassifierTests
{
    [TestMethod]
    [DataRow(400, "BadRequest", "One or more added object references already exist for the following modified properties: 'categories'.")]
    [DataRow(400, "ObjectsAlreadyLinked", "Bad request.")]
    [DataRow(409, "Conflict", "The reference already exists.")]
    [DataRow(409, "Conflict", "Objects are already linked.")]
    public void IsAlreadyRelated_ExplicitDuplicateSignal_ReturnsTrue(int statusCode, string code, string message)
    {
        Assert.IsTrue(CategoryRefResponseClassifier.IsAlreadyRelated(statusCode, code, message));
    }

    [TestMethod]
    [DataRow(400, "BadRequest", "The value provided for @odata.id is invalid.")]
    [DataRow(409, "Conflict", "The app is locked by another operation.")]
    [DataRow(403, "Forbidden", "The reference already exists.")]
    [DataRow(404, "NotFound", "Resource not found.")]
    [DataRow(500, "InternalError", "Already exists.")]
    public void IsAlreadyRelated_AnythingElse_ReturnsFalse(int statusCode, string code, string message)
    {
        Assert.IsFalse(CategoryRefResponseClassifier.IsAlreadyRelated(statusCode, code, message));
    }

    [TestMethod]
    public void IsAlreadyRelated_MissingErrorBody_ReturnsFalse()
    {
        Assert.IsFalse(CategoryRefResponseClassifier.IsAlreadyRelated(400, null, null));
    }

    [TestMethod]
    [DataRow(404, true)]
    [DataRow(400, false)]
    [DataRow(409, false)]
    [DataRow(204, false)]
    public void IsAlreadyUnrelated_OnlyNotFoundCounts(int statusCode, bool expected)
    {
        Assert.AreEqual(expected, CategoryRefResponseClassifier.IsAlreadyUnrelated(statusCode));
    }
}
