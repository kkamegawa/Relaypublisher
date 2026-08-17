using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Manifests;
using IntuneLobPublisher.Core.Publishing;
using IntuneLobPublisher.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntuneLobPublisher.Core.Tests.Publishing;

/// <summary>
/// Exercises <see cref="MacOsAppPublisher"/> against real files on disk, since its script-reading
/// logic (CRLF normalization, base64 encoding, missing-file errors) lives in the internal
/// <c>ManifestAssetReader</c> and is only reachable through this public entry point.
/// </summary>
[TestClass]
public sealed class MacOsAppPublisherTests
{
    private DirectoryInfo _repoRoot = null!;

    [TestInitialize]
    public void Initialize() => _repoRoot = Directory.CreateTempSubdirectory("macos-publisher-tests-");

    [TestCleanup]
    public void Cleanup() => _repoRoot.Delete(recursive: true);

    private sealed class FakeMacOsAppClient : IMacOsAppClient
    {
        public MacOsAppPayloadBase? LastCreatePayload { get; private set; }

        public Task<string> CreateAppAsync(MacOsAppPayloadBase payload, bool useBeta, CancellationToken cancellationToken)
        {
            LastCreatePayload = payload;
            return Task.FromResult("app-1");
        }

        public Task UpdateAppAsync(string appId, MacOsAppPayloadBase payload, bool useBeta, CancellationToken cancellationToken)
        {
            LastCreatePayload = payload;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingContentOrchestrator : IMobileAppContentUploadOrchestrator
    {
        public Task<ContentUploadResult> PublishContentAsync(
            string appId,
            PublishableContent content,
            string? storedInputHash,
            ManagementMetadata metadata,
            ContentUploadOptions options,
            IUploadableContentExtractor extractor,
            string oDataType,
            bool useBeta,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by these tests.");
    }

    private sealed class ThrowingContentExtractor : IUploadableContentExtractor
    {
        public IUploadableContent Extract(string contentPath) => throw new NotSupportedException("Not exercised by these tests.");
    }

    private MacOsAppPublisher CreatePublisher(out FakeMacOsAppClient client)
    {
        client = new FakeMacOsAppClient();
        return new MacOsAppPublisher(
            client,
            new ThrowingContentOrchestrator(),
            new ThrowingContentExtractor(),
            NullLogger<MacOsAppPublisher>.Instance);
    }

    private void WriteScript(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repoRoot.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void WriteScript(string relativePath, byte[] content)
    {
        var fullPath = Path.Combine(_repoRoot.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
    }

    private PublishRequest CreateRequest(AppManifest app, IntunePackageManifest manifest) => new(
        manifest,
        app,
        "manifests/contoso-tool-macos-arm64.yaml",
        _repoRoot.FullName,
        "out",
        "abc123",
        AllowDowngrade: false,
        DryRun: false);

    [TestMethod]
    public async Task CreateAppAsync_ScriptWithCrlf_NormalizesToLfBeforeBase64Encoding()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/preinstall.sh" };
        manifest.Apps = [app];
        WriteScript(app.Scripts.PreInstall, "#!/bin/bash\r\necho pre\r\n");

        var publisher = CreatePublisher(out var client);
        await publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None);

        var payload = (MacOsPkgAppPayload)client.LastCreatePayload!;
        var decoded = Convert.FromBase64String(payload.PreInstallScript!.ScriptContent);
        Assert.AreEqual("#!/bin/bash\necho pre\n", System.Text.Encoding.UTF8.GetString(decoded));
    }

    [TestMethod]
    public async Task CreateAppAsync_ScriptWithLfOnly_RoundTripsUnchanged()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/preinstall.sh" };
        manifest.Apps = [app];
        WriteScript(app.Scripts.PreInstall, "#!/bin/bash\necho pre\n");

        var publisher = CreatePublisher(out var client);
        await publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None);

        var payload = (MacOsPkgAppPayload)client.LastCreatePayload!;
        var decoded = Convert.FromBase64String(payload.PreInstallScript!.ScriptContent);
        Assert.AreEqual("#!/bin/bash\necho pre\n", System.Text.Encoding.UTF8.GetString(decoded));
    }

    [TestMethod]
    public async Task CreateAppAsync_NoScriptsBlock_LeavesScriptPropertiesNull()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        manifest.Apps = [app];

        var publisher = CreatePublisher(out var client);
        await publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None);

        var payload = (MacOsPkgAppPayload)client.LastCreatePayload!;
        Assert.IsNull(payload.PreInstallScript);
        Assert.IsNull(payload.PostInstallScript);
    }

    [TestMethod]
    public async Task CreateAppAsync_MissingScriptFile_ThrowsManifestLoadException()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/missing.sh" };
        manifest.Apps = [app];

        var publisher = CreatePublisher(out _);

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateAppAsync_InvalidUtf8Script_ThrowsManifestLoadException()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/preinstall.sh" };
        manifest.Apps = [app];
        WriteScript(app.Scripts.PreInstall, [.. System.Text.Encoding.UTF8.GetBytes("#!/bin/bash\n"), 0xFF]);

        var publisher = CreatePublisher(out _);

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateAppAsync_ScriptWithoutShebang_ThrowsManifestLoadException()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/preinstall.sh" };
        manifest.Apps = [app];
        WriteScript(app.Scripts.PreInstall, "echo pre\n");

        var publisher = CreatePublisher(out _);

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateAppAsync_ScriptTooLarge_ThrowsManifestLoadException()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PreInstall = "scripts/macos/preinstall.sh" };
        manifest.Apps = [app];
        // Bigger than any valid UTF-8 encoding of a script within the character limit, so the reader's
        // size guard (mirroring ManifestAssetValidator's) rejects it before ever reading it as text.
        WriteScript(app.Scripts.PreInstall, new byte[(int)(ManifestValues.MaxMacOsAppScriptBytes + 1)]);

        var publisher = CreatePublisher(out _);

        await Assert.ThrowsExactlyAsync<ManifestLoadException>(
            () => publisher.CreateAppAsync(CreateRequest(app, manifest), notes: "{}", CancellationToken.None));
    }

    [TestMethod]
    public async Task UpdateAppAsync_ScriptWithCrlf_NormalizesToLf()
    {
        var manifest = TestManifests.CreateValid();
        var app = TestManifests.CreateValidMacOsApp();
        app.Scripts = new MacOsScriptsManifest { PostInstall = "scripts/macos/postinstall.sh" };
        manifest.Apps = [app];
        WriteScript(app.Scripts.PostInstall, "#!/bin/bash\r\necho post\r\n");

        var publisher = CreatePublisher(out var client);
        await publisher.UpdateAppAsync("app-1", CreateRequest(app, manifest), CancellationToken.None);

        var payload = (MacOsPkgAppPayload)client.LastCreatePayload!;
        var decoded = Convert.FromBase64String(payload.PostInstallScript!.ScriptContent);
        Assert.AreEqual("#!/bin/bash\necho post\n", System.Text.Encoding.UTF8.GetString(decoded));
    }
}
