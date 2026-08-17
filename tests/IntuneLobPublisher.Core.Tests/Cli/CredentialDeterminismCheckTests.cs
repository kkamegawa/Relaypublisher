using IntuneLobPublisher.Cli.Commands;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Tests.Cli;

[TestClass]
public sealed class CredentialDeterminismCheckTests
{
    /// <summary>Captures (level, message) pairs without touching the process environment.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Func<string, string?> Environment(string? value) => _ => value;

    [TestMethod]
    public void IsCredentialChainPinned_VariableSet_ReturnsTrue()
    {
        Assert.IsTrue(CredentialDeterminismCheck.IsCredentialChainPinned(Environment("AzureCliCredential")));
    }

    [TestMethod]
    public void IsCredentialChainPinned_VariableMissing_ReturnsFalse()
    {
        Assert.IsFalse(CredentialDeterminismCheck.IsCredentialChainPinned(Environment(null)));
    }

    [TestMethod]
    public void IsCredentialChainPinned_VariableWhitespace_ReturnsFalse()
    {
        Assert.IsFalse(CredentialDeterminismCheck.IsCredentialChainPinned(Environment("   ")));
    }

    [TestMethod]
    public void IsCredentialChainPinned_QueriesOnlyTheCredentialVariable()
    {
        var requestedNames = new List<string>();
        Func<string, string?> environment = name =>
        {
            requestedNames.Add(name);
            return "AzureCliCredential";
        };

        CredentialDeterminismCheck.IsCredentialChainPinned(environment);

        Assert.HasCount(1, requestedNames);
        Assert.AreEqual("AZURE_TOKEN_CREDENTIALS", requestedNames[0]);
    }

    [TestMethod]
    public void WarnIfCredentialChainNotPinned_VariableMissing_LogsWarning()
    {
        var logger = new CapturingLogger();

        CredentialDeterminismCheck.WarnIfCredentialChainNotPinned(logger, Environment(null));

        Assert.HasCount(1, logger.Entries);
        Assert.AreEqual(LogLevel.Warning, logger.Entries[0].Level);
        Assert.AreEqual(CredentialDeterminismCheck.NotPinnedWarning, logger.Entries[0].Message);
        StringAssert.Contains(logger.Entries[0].Message, "AZURE_TOKEN_CREDENTIALS");
        StringAssert.Contains(logger.Entries[0].Message, "AzureCliCredential");
    }

    [TestMethod]
    public void WarnIfCredentialChainNotPinned_VariablePinned_LogsNothing()
    {
        var logger = new CapturingLogger();

        CredentialDeterminismCheck.WarnIfCredentialChainNotPinned(logger, Environment("AzureCliCredential"));

        Assert.IsEmpty(logger.Entries);
    }
}
