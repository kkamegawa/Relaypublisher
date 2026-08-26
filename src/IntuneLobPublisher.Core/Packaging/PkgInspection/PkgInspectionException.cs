namespace IntuneLobPublisher.Core.Exceptions;

/// <summary>
/// A PKG cannot be inspected safely. This is deliberately a <see cref="PublisherException"/> so callers
/// can report it using the same hard-failure path as other packaging errors; semantic warnings are not
/// represented by this exception.
/// </summary>
public sealed class PkgInspectionException : PublisherException
{
    public PkgInspectionException(string message)
        : base(message)
    {
    }

    public PkgInspectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
