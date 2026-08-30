using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.BZip2;

namespace IntuneLobPublisher.Core.Packaging;

/// <summary>
/// Isolates <see cref="BZip2InputStream"/> behind a narrow adapter so <see cref="XarPkgBundleInspector"/>
/// never takes a direct dependency on SharpZipLib types (doc/adr-phase-2.md, 2026-08-30, issue #127).
/// Two behaviors here are not obvious from the wrapped type alone and were verified against the
/// SharpZipLib source before writing this class:
///
/// - <see cref="BZip2InputStream.IsStreamOwner"/> defaults to <c>true</c>. This adapter sets it to
///   <c>false</c> so disposing this stream never disposes the caller's underlying bounded stream,
///   matching the <c>GZipStream(..., leaveOpen: true)</c> sibling used for the gzip heap-entry branch.
/// - <see cref="BZip2Exception"/> derives from <see cref="SharpZipBaseException"/>, which derives from
///   <see cref="Exception"/> directly - not <see cref="IOException"/>. The caller
///   (<c>XarPkgBundleInspector.ReadHeapEntryAsync</c>) only translates <see cref="InvalidDataException"/>,
///   <see cref="IOException"/>, and <see cref="OverflowException"/> into the hard-failure
///   <c>PkgInspectionException</c>; without translating here, a corrupt bzip2 stream would surface a raw
///   SharpZipLib exception type and bypass that hard-fail, --force-cannot-bypass contract. This class
///   translates the exception types a malformed bzip2 stream can realistically produce into
///   <see cref="InvalidDataException"/> so the caller's existing catch filter covers them.
///
/// <see cref="BZip2InputStream"/> only overrides the byte-array <see cref="Stream.Read(byte[], int, int)"/>
/// overload, not <c>Read(Span{byte})</c> or any async overload, so every other <see cref="Stream"/> read
/// path (including the caller's <c>ReadAsync(Memory{byte}, CancellationToken)</c> loop) falls back to the
/// base <see cref="Stream"/> class's synchronous-over-asynchronous default implementation, which itself
/// funnels into the overload below. This is acceptable: heap entries are capped at 16 MiB by the caller's
/// bounded read loop, and cancellation is still checked every iteration of that loop regardless.
///
/// <see cref="BZip2InputStream"/> stops at the first end-of-stream marker and does not read concatenated
/// bzip2 streams, so the caller's "trailing compressed bytes" check (comparing the underlying bounded
/// stream's remaining byte count after decompression completes) still detects appended garbage after a
/// bzip2 entry, exactly as it does today for the gzip branch.
/// </summary>
internal sealed class Bzip2DecompressionStream : Stream
{
    private readonly BZip2InputStream _inner;

    public Bzip2DecompressionStream(Stream source)
    {
        // BZip2InputStream's constructor eagerly reads and validates the first block header - unlike
        // GZipStream, construction itself can throw on malformed input, not just Read(). The translation
        // must therefore wrap construction too, or a stream that merely declares "application/x-bzip2"
        // without valid bzip2 content throws a raw BZip2Exception before this class returns at all.
        try
        {
            _inner = new BZip2InputStream(source) { IsStreamOwner = false };
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw new InvalidDataException("The bzip2 stream is corrupt or truncated.", ex);
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return _inner.Read(buffer, offset, count);
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw new InvalidDataException("The bzip2 stream is corrupt or truncated.", ex);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    // Broad by design: a crafted bzip2 selector/Huffman table can drive the decoder into BCL index/range
    // exceptions as well as its own typed exceptions, and every one of those must fail closed as
    // PkgInspectionException rather than escape as a raw BCL/SharpZipLib exception type. Cancellation
    // exceptions are deliberately excluded so the caller's cancellation handling is unaffected.
    private static bool IsTranslatable(Exception ex)
        => ex is SharpZipBaseException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException;
}
