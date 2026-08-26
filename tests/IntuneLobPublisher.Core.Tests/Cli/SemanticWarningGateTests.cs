using IntuneLobPublisher.Cli.Commands;

namespace IntuneLobPublisher.Core.Tests.Cli;

/// <summary>
/// Pins the doc/05-operation.md decision table for macOS PKG semantic inspection warnings: no warnings
/// always proceeds; on a TTY a single confirmation covers the whole batch; off a TTY, only <c>--force</c>
/// can proceed. Mirrors the pure-check + injected-environment shape of <see cref="CredentialDeterminismCheckTests"/>
/// so the decision is fully testable without a real console.
/// </summary>
[TestClass]
public sealed class SemanticWarningGateTests
{
    [TestMethod]
    public void Decide_NoWarnings_ReturnsNoWarningsRegardlessOfForceOrInteractivity()
    {
        var decision = SemanticWarningGate.Decide(
            hasWarnings: false, force: false, interactive: true, confirm: () => throw new InvalidOperationException("must not prompt"));

        Assert.AreEqual(WarningGateDecision.NoWarnings, decision);
    }

    [TestMethod]
    public void Decide_Force_ReturnsForceAcknowledgedWithoutPrompting()
    {
        var decision = SemanticWarningGate.Decide(
            hasWarnings: true, force: true, interactive: true, confirm: () => throw new InvalidOperationException("must not prompt"));

        Assert.AreEqual(WarningGateDecision.ForceAcknowledged, decision);
    }

    [TestMethod]
    public void Decide_ForceOffTty_StillReturnsForceAcknowledged()
    {
        // --force must work identically whether or not a TTY is attached (CI has no TTY).
        var decision = SemanticWarningGate.Decide(
            hasWarnings: true, force: true, interactive: false, confirm: () => throw new InvalidOperationException("must not prompt"));

        Assert.AreEqual(WarningGateDecision.ForceAcknowledged, decision);
    }

    [TestMethod]
    public void Decide_InteractiveAccept_ReturnsAcknowledged()
    {
        var decision = SemanticWarningGate.Decide(
            hasWarnings: true, force: false, interactive: true, confirm: () => true);

        Assert.AreEqual(WarningGateDecision.Acknowledged, decision);
    }

    [TestMethod]
    public void Decide_InteractiveDecline_ReturnsDeclined()
    {
        var decision = SemanticWarningGate.Decide(
            hasWarnings: true, force: false, interactive: true, confirm: () => false);

        Assert.AreEqual(WarningGateDecision.Declined, decision);
    }

    [TestMethod]
    public void Decide_NonInteractiveWithoutForce_ReturnsForceRequiredWithoutPrompting()
    {
        var decision = SemanticWarningGate.Decide(
            hasWarnings: true, force: false, interactive: false, confirm: () => throw new InvalidOperationException("must not prompt off a TTY"));

        Assert.AreEqual(WarningGateDecision.ForceRequired, decision);
    }

    [TestMethod]
    public void IsInteractive_BothStreamsAttached_ReturnsTrue()
    {
        Assert.IsTrue(SemanticWarningGate.IsInteractive(() => false, () => false));
    }

    [TestMethod]
    public void IsInteractive_InputRedirected_ReturnsFalse()
    {
        Assert.IsFalse(SemanticWarningGate.IsInteractive(() => true, () => false));
    }

    [TestMethod]
    public void IsInteractive_OutputRedirected_ReturnsFalse()
    {
        // A prompt written to a redirected stdout would never be seen by anyone able to answer it.
        Assert.IsFalse(SemanticWarningGate.IsInteractive(() => false, () => true));
    }

    [TestMethod]
    public void ConfirmOnConsole_YAnswer_ReturnsTrue()
    {
        using var input = new StringReader("y\n");
        using var output = new StringWriter();

        Assert.IsTrue(SemanticWarningGate.ConfirmOnConsole(input, output));
        StringAssert.Contains(output.ToString(), "[y/N]");
    }

    [TestMethod]
    [DataRow("Y")]
    [DataRow(" y ")]
    public void ConfirmOnConsole_CaseAndWhitespaceInsensitiveY_ReturnsTrue(string answer)
    {
        using var input = new StringReader(answer + "\n");
        using var output = new StringWriter();

        Assert.IsTrue(SemanticWarningGate.ConfirmOnConsole(input, output));
    }

    [TestMethod]
    [DataRow("n")]
    [DataRow("")]
    [DataRow("yes")]
    public void ConfirmOnConsole_AnythingElse_ReturnsFalse(string answer)
    {
        using var input = new StringReader(answer + "\n");
        using var output = new StringWriter();

        Assert.IsFalse(SemanticWarningGate.ConfirmOnConsole(input, output));
    }

    [TestMethod]
    public void ConfirmOnConsole_Eof_ReturnsFalse()
    {
        // ReadLine returns null at EOF (redirected empty input); default to "no", not a crash.
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();

        Assert.IsFalse(SemanticWarningGate.ConfirmOnConsole(input, output));
    }
}
