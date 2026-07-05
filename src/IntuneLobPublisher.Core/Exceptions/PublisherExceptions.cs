namespace IntuneLobPublisher.Core.Exceptions;

/// <summary>Base type for all publisher errors so the CLI can map them to exit codes uniformly.</summary>
public abstract class PublisherException : Exception
{
    protected PublisherException(string message)
        : base(message)
    {
    }

    protected PublisherException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A manifest file could not be read or parsed.</summary>
public sealed class ManifestLoadException : PublisherException
{
    public ManifestLoadException(string message)
        : base(message)
    {
    }

    public ManifestLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A manifest was parsed but failed validation.</summary>
public sealed class ManifestValidationException : PublisherException
{
    public ManifestValidationException(string message, IReadOnlyList<string> errors)
        : base(message)
    {
        Errors = errors;
    }

    /// <summary>Individual validation error messages including the failing field path.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Staging a package failed (missing file, copy failure, missing setup file, ...).</summary>
public sealed class StagingException : PublisherException
{
    public StagingException(string message)
        : base(message)
    {
    }

    public StagingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Downloading an external source failed.</summary>
public sealed class SourceDownloadException : PublisherException
{
    public SourceDownloadException(string message)
        : base(message)
    {
    }

    public SourceDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A downloaded or staged file did not match its expected SHA256.</summary>
public sealed class ChecksumMismatchException : PublisherException
{
    public ChecksumMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>.intunewin generation failed (tool missing, tool download failed, non-zero exit, missing output).</summary>
public sealed class PackagingException : PublisherException
{
    public PackagingException(string message)
        : base(message)
    {
    }

    public PackagingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A manifest-supplied path is absolute or escapes its allowed root.</summary>
public sealed class UnsafePathException : PublisherException
{
    public UnsafePathException(string message)
        : base(message)
    {
    }
}

/// <summary>The acquired Graph token's `tid` claim does not match `--expected-tenant`. Thrown before any write.</summary>
public sealed class TenantMismatchException : PublisherException
{
    public TenantMismatchException(string expectedTenantId, string actualTenantId)
        : base($"Graph token tenant '{actualTenantId}' does not match --expected-tenant '{expectedTenantId}'.")
    {
        ExpectedTenantId = expectedTenantId;
        ActualTenantId = actualTenantId;
    }

    public string ExpectedTenantId { get; }

    public string ActualTenantId { get; }
}

/// <summary>A Microsoft Graph call failed after retries. Carries the request ids Graph returns for support/troubleshooting.</summary>
public sealed class GraphRequestException : PublisherException
{
    public GraphRequestException(string message, int? statusCode, string? clientRequestId, string? requestId)
        : base(message)
    {
        StatusCode = statusCode;
        ClientRequestId = clientRequestId;
        RequestId = requestId;
    }

    public GraphRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>HTTP status code of the final failing response, when available.</summary>
    public int? StatusCode { get; }

    /// <summary>The `client-request-id` header Graph echoes back, for support correlation. Never a secret.</summary>
    public string? ClientRequestId { get; }

    /// <summary>The `request-id` header Graph returns, for support correlation. Never a secret.</summary>
    public string? RequestId { get; }
}
