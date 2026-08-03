using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Packaging;
using IntuneLobPublisher.Core.Staging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>The <see cref="IPlatformAppPublisher"/> for <c>Platform: windows</c> (<c>win32LobApp</c>), always on Graph v1.0.</summary>
public sealed class WindowsAppPublisher : IPlatformAppPublisher
{
    private readonly IWin32LobAppClient _appClient;
    private readonly IMobileAppContentUploadOrchestrator _contentOrchestrator;
    private readonly IUploadableContentExtractor _extractor;

    public WindowsAppPublisher(
        IWin32LobAppClient appClient,
        IMobileAppContentUploadOrchestrator contentOrchestrator,
        IUploadableContentExtractor extractor)
    {
        _appClient = appClient;
        _contentOrchestrator = contentOrchestrator;
        _extractor = extractor;
    }

    public async Task EnsureMappableAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var (detectionScript, iconBytes) = await ReadAssetsAsync(request, cancellationToken).ConfigureAwait(false);
        Win32LobAppPayloadMapper.Map(request.Manifest, request.App, detectionScript, iconBytes);
    }

    public async Task<string> CreateAppAsync(PublishRequest request, string notes, CancellationToken cancellationToken)
    {
        var (detectionScript, iconBytes) = await ReadAssetsAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = Win32LobAppPayloadMapper.Map(request.Manifest, request.App, detectionScript, iconBytes, notes);
        return await _appClient.CreateAppAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAppAsync(string appId, PublishRequest request, CancellationToken cancellationToken)
    {
        var (detectionScript, iconBytes) = await ReadAssetsAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = Win32LobAppPayloadMapper.Map(request.Manifest, request.App, detectionScript, iconBytes);
        await _appClient.UpdateAppAsync(appId, payload, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContentUploadResult> PublishContentAsync(
        string appId,
        PublishRequest request,
        PackageArtifacts artifacts,
        string? storedInputHash,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        CancellationToken cancellationToken)
        => _contentOrchestrator.PublishContentAsync(
            appId,
            new PublishableContent(artifacts.ContentPath, artifacts.Metadata.InputHash),
            storedInputHash,
            metadata,
            options,
            _extractor,
            oDataType: "#microsoft.graph.win32LobApp",
            useBeta: false,
            cancellationToken);

    private static async Task<(string DetectionScript, byte[]? IconBytes)> ReadAssetsAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var detectionScript = await ReadDetectionScriptAsync(request, cancellationToken).ConfigureAwait(false);
        var iconBytes = await ManifestAssetReader.ReadIconAsync(request, request.Manifest, cancellationToken).ConfigureAwait(false);
        return (detectionScript, iconBytes);
    }

    private static async Task<string> ReadDetectionScriptAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var app = request.App;
        var scriptPath = PathSafety.ResolveWithin(request.RepositoryRoot, app.Detection!.ScriptFile!, "Detection.ScriptFile");
        if (!File.Exists(scriptPath))
        {
            throw new ManifestLoadException($"Detection script '{app.Detection.ScriptFile}' does not exist under '{request.RepositoryRoot}'.");
        }

        return await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
    }
}
