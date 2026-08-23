using IntuneLobPublisher.Core.Packaging;
using Microsoft.Extensions.Logging;

namespace IntuneLobPublisher.Core.Publishing;

/// <summary>
/// The <see cref="IPlatformAppPublisher"/> for <c>Platform: macos</c>: <c>macOSPkgApp</c>
/// (<c>AppType: pkg</c>, the default, beta-only) or <c>macOSLobApp</c> (<c>AppType: lob</c>, v1.0),
/// per app (doc/00-overview.md §6.13). One instance handles both AppTypes; the same content-upload
/// mechanics apply to either, so only one <see cref="PkgContentPreparer"/> is needed.
/// </summary>
public sealed class MacOsAppPublisher : IPlatformAppPublisher
{
    private readonly IMacOsAppClient _appClient;
    private readonly IMobileAppContentUploadOrchestrator _contentOrchestrator;
    private readonly IUploadableContentExtractor _extractor;
    private readonly ILogger<MacOsAppPublisher> _logger;

    public MacOsAppPublisher(
        IMacOsAppClient appClient,
        IMobileAppContentUploadOrchestrator contentOrchestrator,
        IUploadableContentExtractor extractor,
        ILogger<MacOsAppPublisher> logger)
    {
        _appClient = appClient;
        _contentOrchestrator = contentOrchestrator;
        _extractor = extractor;
        _logger = logger;
    }

    public async Task EnsureMappableAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var iconBytes = await ManifestAssetReader.ReadIconAsync(request, request.Manifest, cancellationToken).ConfigureAwait(false);
        var scripts = await ManifestAssetReader.ReadMacOsScriptsAsync(request, request.App, _logger, cancellationToken).ConfigureAwait(false);
        MacOsAppPayloadMapper.Map(request.Manifest, request.App, iconBytes, scripts);
    }

    public async Task<string> CreateAppAsync(PublishRequest request, string notes, CancellationToken cancellationToken)
    {
        var iconBytes = await ManifestAssetReader.ReadIconAsync(request, request.Manifest, cancellationToken).ConfigureAwait(false);
        var scripts = await ManifestAssetReader.ReadMacOsScriptsAsync(request, request.App, _logger, cancellationToken).ConfigureAwait(false);
        var payload = MacOsAppPayloadMapper.Map(request.Manifest, request.App, iconBytes, scripts, notes);
        var target = MacOsAppPayloadMapper.ResolveTarget(request.App);
        return await _appClient.CreateAppAsync(payload, target.UseBeta, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAppAsync(string appId, PublishRequest request, ContentUploadOptions options, CancellationToken cancellationToken)
    {
        var target = MacOsAppPayloadMapper.ResolveTarget(request.App);

        // Guards against an app left mid-"processing" by an interrupted previous run: this PATCH is the
        // first Graph write of the run, ahead of any content upload.
        await _contentOrchestrator.WaitWhilePublishingStateProcessingAsync(appId, options, target.UseBeta, cancellationToken)
            .ConfigureAwait(false);

        var iconBytes = await ManifestAssetReader.ReadIconAsync(request, request.Manifest, cancellationToken).ConfigureAwait(false);
        var scripts = await ManifestAssetReader.ReadMacOsScriptsAsync(request, request.App, _logger, cancellationToken).ConfigureAwait(false);
        var payload = MacOsAppPayloadMapper.Map(request.Manifest, request.App, iconBytes, scripts);
        await _appClient.UpdateAppAsync(appId, payload, target.UseBeta, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContentUploadResult> PublishContentAsync(
        string appId,
        PublishRequest request,
        PackageArtifacts artifacts,
        string? storedInputHash,
        ManagementMetadata metadata,
        ContentUploadOptions options,
        CancellationToken cancellationToken)
    {
        var target = MacOsAppPayloadMapper.ResolveTarget(request.App);
        return _contentOrchestrator.PublishContentAsync(
            appId,
            new PublishableContent(artifacts.ContentPath, artifacts.Metadata.InputHash),
            storedInputHash,
            metadata,
            options,
            _extractor,
            target.ODataType,
            target.UseBeta,
            cancellationToken);
    }
}
