using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Packaging;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class ManifestLoaderTests
{
    private const string FullManifestYaml =
        """
        SchemaVersion: "1.0"
        PackageIdentifier: Contoso.Tool
        PackageName: Contoso Tool
        Publisher: Contoso Ltd.
        Description: Internal tool for Contoso employees.
        PackageVersion: 1.2.3
        AssignmentSync: merge
        Owner: IT Department
        Icon: assets/icons/contoso-tool.png
        RoleScopeTagIds:
          - "0"

        Apps:
          - Platform: windows
            Architecture: x64
            InstallerType: win32
            DisplayName: Contoso Tool [Windows x64]

            Package:
              IntuneWin:
                SetupFile: install.ps1
              RepositoryFiles:
                - Source: scripts/windows/x64/install.ps1
                  Destination: install.ps1
              ExternalFiles:
                - Type: githubRelease
                  Owner: contoso
                  Repository: internal-tools
                  Tag: v1.2.3
                  AssetName: contoso-tool-1.2.3-x64.exe
                  Destination: bin/contoso-tool.exe
                  Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                  Auth:
                    Type: token
                    SecretName: GH_RELEASE_PAT

            Install:
              CommandLine: powershell.exe -ExecutionPolicy Bypass -File .\install.ps1
              UninstallCommandLine: powershell.exe -ExecutionPolicy Bypass -File .\uninstall.ps1
              InstallExperience: system
              RestartBehavior: suppress
              ReturnCodes:
                - Code: 0
                  Type: success
                - Code: 3010
                  Type: softReboot

            Detection:
              Type: script
              ScriptFile: scripts/windows/common/detect.ps1
              RunAs32Bit: false
              EnforceSignatureCheck: false

            Requirements:
              MinimumOSVersion: 10.0.19045
              Architecture: x64

            Assignments:
              - Target: group
                GroupId: "00000000-0000-0000-0000-000000000001"
                Intent: required
                FilterId: "00000000-0000-0000-0000-00000000000f"
                FilterMode: include
                Settings:
                  Notifications: showAll
                  RestartGracePeriodMinutes: 1440
        """;

    private static async Task<IntunePackageManifest> LoadFromTextAsync(string yaml)
    {
        var directory = Directory.CreateTempSubdirectory("manifest-loader-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "manifest.yaml");
            await File.WriteAllTextAsync(path, yaml);
            return await new ManifestLoader().LoadAsync(path, CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadAsync_FullManifest_PopulatesTopLevelFields()
    {
        var manifest = await LoadFromTextAsync(FullManifestYaml);

        Assert.AreEqual("1.0", manifest.SchemaVersion);
        Assert.AreEqual("Contoso.Tool", manifest.PackageIdentifier);
        Assert.AreEqual("Contoso Tool", manifest.PackageName);
        Assert.AreEqual("Contoso Ltd.", manifest.Publisher);
        Assert.AreEqual("Internal tool for Contoso employees.", manifest.Description);
        Assert.AreEqual("1.2.3", manifest.PackageVersion);
        Assert.AreEqual("merge", manifest.AssignmentSync);
        Assert.AreEqual("IT Department", manifest.Owner);
        Assert.AreEqual("assets/icons/contoso-tool.png", manifest.Icon);
        CollectionAssert.AreEqual(new[] { "0" }, manifest.RoleScopeTagIds);
        Assert.HasCount(1, manifest.Apps);
    }

    [TestMethod]
    public async Task LoadAsync_FullManifest_PopulatesAppEntry()
    {
        var manifest = await LoadFromTextAsync(FullManifestYaml);
        var app = manifest.Apps[0];

        Assert.AreEqual("windows", app.Platform);
        Assert.AreEqual("x64", app.Architecture);
        Assert.AreEqual("win32", app.InstallerType);
        Assert.AreEqual("Contoso Tool [Windows x64]", app.DisplayName);
        Assert.AreEqual("install.ps1", app.Package?.IntuneWin?.SetupFile);
        Assert.AreEqual("scripts/windows/x64/install.ps1", app.Package?.RepositoryFiles[0].Source);
        Assert.AreEqual("system", app.Install?.InstallExperience);
        Assert.AreEqual(2, app.Install?.ReturnCodes?.Count);
        Assert.AreEqual(3010, app.Install?.ReturnCodes?[1].Code);
        Assert.AreEqual("softReboot", app.Install?.ReturnCodes?[1].Type);
        Assert.AreEqual("script", app.Detection?.Type);
        Assert.IsFalse(app.Detection?.RunAs32Bit);
        Assert.AreEqual("10.0.19045", app.Requirements?.MinimumOSVersion);
        Assert.AreEqual("x64", app.Requirements?.Architecture);
    }

    [TestMethod]
    public async Task LoadAsync_FullManifest_PopulatesUnifiedSourceItemAndAssignment()
    {
        var manifest = await LoadFromTextAsync(FullManifestYaml);
        var app = manifest.Apps[0];

        var external = app.Package!.ExternalFiles[0];
        Assert.AreEqual("githubRelease", external.Type);
        Assert.AreEqual("contoso", external.Owner);
        Assert.AreEqual("internal-tools", external.Repository);
        Assert.AreEqual("v1.2.3", external.Tag);
        Assert.AreEqual("contoso-tool-1.2.3-x64.exe", external.AssetName);
        Assert.AreEqual("bin/contoso-tool.exe", external.Destination);
        Assert.AreEqual("token", external.Auth?.Type);
        Assert.AreEqual("GH_RELEASE_PAT", external.Auth?.SecretName);

        var assignment = app.Assignments[0];
        Assert.AreEqual("group", assignment.Target);
        Assert.AreEqual("00000000-0000-0000-0000-000000000001", assignment.GroupId);
        Assert.AreEqual("required", assignment.Intent);
        Assert.AreEqual("include", assignment.FilterMode);
        Assert.AreEqual("showAll", assignment.Settings?.Notifications);
        Assert.AreEqual(1440, assignment.Settings?.RestartGracePeriodMinutes);
    }

    [TestMethod]
    public async Task LoadAsync_MacOsScripts_PopulatesPreAndPostInstall()
    {
        var manifest = await LoadFromTextAsync(
            """
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            PackageName: Contoso Tool
            Publisher: Contoso Ltd.
            Description: Internal tool for Contoso employees.
            PackageVersion: 1.2.3

            Apps:
              - Platform: macos
                Architecture: arm64
                InstallerType: pkg
                AppType: pkg
                DisplayName: Contoso Tool [macOS Arm64]

                Source:
                  Type: publicHttp
                  Url: https://example.com/downloads/contoso-tool-arm64.pkg
                  Destination: contoso-tool-arm64.pkg
                  Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

                Requirements:
                  MinimumOSVersion: "14.0"

                Detection:
                  IncludedApps:
                    - BundleId: com.contoso.tool
                      BundleVersion: 1.2.3

                Scripts:
                  PreInstall: scripts/macos/contoso-tool/preinstall.sh
                  PostInstall: scripts/macos/contoso-tool/postinstall.sh
            """);
        var app = manifest.Apps[0];

        Assert.AreEqual("scripts/macos/contoso-tool/preinstall.sh", app.Scripts?.PreInstall);
        Assert.AreEqual("scripts/macos/contoso-tool/postinstall.sh", app.Scripts?.PostInstall);
    }

    [TestMethod]
    public async Task LoadAsync_MacOsAppWithoutScripts_ScriptsIsNull()
    {
        var manifest = await LoadFromTextAsync(
            """
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            Apps:
              - Platform: macos
                Architecture: arm64
                InstallerType: pkg
            """);
        var app = manifest.Apps[0];

        Assert.IsNull(app.Scripts);
    }

    [TestMethod]
    public async Task LoadAsync_CategoriesOmitted_IsNull()
    {
        var manifest = await LoadFromTextAsync(
            """
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            Apps:
              - Platform: windows
                Architecture: x64
            """);

        Assert.IsNull(manifest.Apps[0].Categories, "Omitted Categories must stay distinguishable from an empty list.");
    }

    [TestMethod]
    public async Task LoadAsync_CategoriesEmptyList_IsEmptyNotNull()
    {
        var manifest = await LoadFromTextAsync(
            """
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            Apps:
              - Platform: windows
                Architecture: x64
                Categories: []
            """);

        Assert.IsNotNull(manifest.Apps[0].Categories);
        Assert.IsEmpty(manifest.Apps[0].Categories!);
    }

    [TestMethod]
    public async Task LoadAsync_CategoriesWithValues_PreservesOrderAndSpelling()
    {
        var manifest = await LoadFromTextAsync(
            """
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            Apps:
              - Platform: windows
                Architecture: x64
                Categories:
                  - Business Apps
                  - Productivity
            """);

        CollectionAssert.AreEqual(new[] { "Business Apps", "Productivity" }, manifest.Apps[0].Categories);
    }

    [TestMethod]
    public async Task LoadAsync_UnknownKeys_AreIgnored()
    {
        var manifest = await LoadFromTextAsync(
            """
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            FutureMinorVersionField: something new
            Apps: []
            """);

        Assert.AreEqual("Contoso.Tool", manifest.PackageIdentifier);
    }

    [TestMethod]
    public async Task LoadAsync_FormattingCommentsAndPropertyOrderDoNotChangeManifestHash()
    {
        var formatted = await LoadFromTextAsync(
            """
            # Formatting and comments are not hash inputs.
            SchemaVersion: "1.0"
            PackageIdentifier: Contoso.Tool
            PackageName: Contoso Tool
            Apps: []
            """);
        var reordered = await LoadFromTextAsync(
            """
            Apps: []
            PackageName: Contoso Tool
            # The same model in a different YAML layout.
            PackageIdentifier: Contoso.Tool
            SchemaVersion: "1.0"
            """);

        Assert.AreEqual(
            InputHashCalculator.ComputeManifestHash(formatted),
            InputHashCalculator.ComputeManifestHash(reordered));
    }

    [TestMethod]
    public async Task LoadAsync_MalformedYaml_ThrowsManifestLoadException()
    {
        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => LoadFromTextAsync("SchemaVersion: [unclosed"));
    }

    [TestMethod]
    public async Task LoadAsync_EmptyFile_ThrowsManifestLoadException()
    {
        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => LoadFromTextAsync(string.Empty));
    }

    [TestMethod]
    public async Task LoadAsync_MissingFile_ThrowsManifestLoadException()
    {
        var loader = new ManifestLoader();
        var missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.yaml");

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => loader.LoadAsync(missing, CancellationToken.None));
    }
}
