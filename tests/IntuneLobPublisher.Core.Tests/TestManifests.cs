using IntuneLobPublisher.Core.Manifests;

namespace IntuneLobPublisher.Core.Tests;

/// <summary>Builds fully valid manifests that individual tests mutate into invalid shapes.</summary>
internal static class TestManifests
{
    public static IntunePackageManifest CreateValid(
        string architecture = "x64",
        string packageIdentifier = "Contoso.Tool",
        string? displayName = null)
    {
        return new IntunePackageManifest
        {
            SchemaVersion = "1.0",
            PackageIdentifier = packageIdentifier,
            PackageName = "Contoso Tool",
            Publisher = "Contoso Ltd.",
            Description = "Internal tool for Contoso employees.",
            PackageVersion = "1.2.3",
            AssignmentSync = "merge",
            Apps = [CreateValidApp(architecture, displayName ?? $"Contoso Tool [Windows {architecture}]")],
        };
    }

    public static AppManifest CreateValidApp(string architecture = "x64", string? displayName = null)
    {
        return new AppManifest
        {
            Platform = "windows",
            Architecture = architecture,
            InstallerType = "win32",
            DisplayName = displayName ?? $"Contoso Tool [Windows {architecture}]",
            Package = new WindowsPackageManifest
            {
                IntuneWin = new IntuneWinManifest { SetupFile = "install.ps1" },
                RepositoryFiles =
                [
                    new RepositoryFileManifest
                    {
                        Source = $"scripts/windows/{architecture}/install.ps1",
                        Destination = "install.ps1",
                    },
                ],
                ExternalFiles =
                [
                    new SourceManifest
                    {
                        Type = "publicHttp",
                        Url = "https://example.com/downloads/contoso-tool.exe",
                        Destination = "bin/contoso-tool.exe",
                        Sha256 = new string('a', 64),
                    },
                ],
            },
            Install = new InstallManifest
            {
                CommandLine = "powershell.exe -ExecutionPolicy Bypass -File .\\install.ps1",
                UninstallCommandLine = "powershell.exe -ExecutionPolicy Bypass -File .\\uninstall.ps1",
                InstallExperience = "system",
                RestartBehavior = "suppress",
            },
            Detection = new DetectionManifest
            {
                Type = "script",
                ScriptFile = "scripts/windows/common/detect.ps1",
            },
            Requirements = new RequirementsManifest
            {
                MinimumOSVersion = "10.0.19045",
                Architecture = architecture,
            },
            Assignments =
            [
                new AssignmentManifest
                {
                    Target = "group",
                    GroupId = "00000000-0000-0000-0000-000000000001",
                    Intent = "required",
                },
            ],
        };
    }

    public static AppManifest CreateValidFileDetectionApp(string architecture = "x64")
    {
        var app = CreateValidApp(architecture);
        app.Detection = new DetectionManifest
        {
            Type = "file",
            Path = @"C:\Program Files\Contoso Tool",
            FileOrFolderName = "contoso-tool.exe",
            OperationType = "version",
            Operator = "greaterThanOrEqual",
            ComparisonValue = "1.2.3",
            Check32BitOn64System = false,
        };
        return app;
    }

    /// <summary>A valid macOS app entry with the default AppType ("pkg").</summary>
    public static AppManifest CreateValidMacOsApp(string architecture = "arm64", string? appType = null, string? displayName = null)
    {
        return new AppManifest
        {
            Platform = "macos",
            Architecture = architecture,
            InstallerType = "pkg",
            AppType = appType,
            DisplayName = displayName ?? $"Contoso Tool [macOS {architecture}]",
            Source = new SourceManifest
            {
                Type = "azureBlob",
                AccountName = "contosopackages",
                Container = "intune-packages",
                BlobName = $"macos/contoso-tool/1.2.3/contoso-tool-{architecture}.pkg",
                Destination = $"contoso-tool-{architecture}.pkg",
                Sha256 = new string('a', 64),
                Auth = new AuthManifest { Type = "workloadIdentity" },
            },
            Requirements = new RequirementsManifest
            {
                MinimumOSVersion = "14.0",
            },
            Detection = new DetectionManifest
            {
                IncludedApps =
                [
                    new IncludedAppManifest { BundleId = "com.contoso.tool", BundleVersion = "1.2.3" },
                ],
            },
            Assignments =
            [
                new AssignmentManifest
                {
                    Target = "group",
                    GroupId = "00000000-0000-0000-0000-000000000003",
                    Intent = "required",
                },
            ],
        };
    }
}
