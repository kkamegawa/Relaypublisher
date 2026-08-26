using System.Reflection;

namespace IntuneLobPublisher.Core;

/// <summary>
/// The running CLI's informational version. CI must pin the exact same version across
/// plan/validate/package/publish jobs (doc/03-ci-github-actions.md, doc/04-ci-azure-pipelines.md); this
/// is what <c>package</c> records as <c>PackageMetadata.CliVersion</c> and what publish's preflight
/// compares against before trusting a staged macOS artifact (issue #116).
/// </summary>
public static class CliVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetEntryAssembly();
        return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "unknown";
    }
}
