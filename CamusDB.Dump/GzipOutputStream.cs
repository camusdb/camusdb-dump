/**
 * This file is part of CamusDB
 *
 * For the full copyright and license information, please view the LICENSE.txt
 * file that was distributed with this source code.
 */

using System.IO.Compression;

namespace CamusDB.Dump;

/// <summary>
/// A write-only gzip stream that is a valid gzip file however little is written to it.
///
/// <para><see cref="GZipStream"/> gets the empty case wrong two ways. Disposed with nothing written, it
/// writes nothing, and a zero-byte <c>.gz</c> fails <c>gzip -t</c>. Flushed asynchronously before any
/// data, it writes the header and a sync-flush block, then skips the final block and the trailer on
/// dispose, leaving a file that ends early. A dump can write nothing — <c>--no-header</c> on a database
/// with no tables, or a run that fails before its first statement — so both reach real output.</para>
///
/// <para>Flushes are therefore held back until the first byte, and a stream disposed without one gets
/// the empty gzip member written directly.</para>
/// </summary>
internal sealed class GzipOutputStream : Stream
{
    /// <summary>
    /// A complete gzip member holding no data: the header, an empty final deflate block, and a zero
    /// CRC-32 and length. The OS byte says unknown, as .NET's own header does.
    /// </summary>
    private static readonly byte[] EmptyMember =
    [
        0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xff,
        0x03, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

    private readonly Stream destination;

    private readonly GZipStream gzip;

    private bool wroteBytes;

    private bool disposed;

    public GzipOutputStream(Stream destination)
    {
        this.destination = destination;
        gzip = new GZipStream(destination, CompressionLevel.Optimal);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
            return;

        wroteBytes = true;
        gzip.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return ValueTask.CompletedTask;

        wroteBytes = true;
        return gzip.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        if (wroteBytes)
            gzip.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
        => wroteBytes ? gzip.FlushAsync(cancellationToken) : Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;

            if (!wroteBytes)
                destination.Write(EmptyMember);

            // Writes the final block and the trailer, then closes the destination.
            gzip.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;

            if (!wroteBytes)
                await destination.WriteAsync(EmptyMember).ConfigureAwait(false);

            await gzip.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
