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

/// <summary>A manifest-supplied path is absolute or escapes its allowed root.</summary>
public sealed class UnsafePathException : PublisherException
{
    public UnsafePathException(string message)
        : base(message)
    {
    }
}
