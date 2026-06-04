namespace apps;

/// <summary>
/// A read-only pass-through stream that reports the cumulative number of bytes read via a callback.
/// Used to drive download progress while the data is consumed downstream (e.g. by a decompressor).
/// Does not own <paramref name="inner"/> — the caller is responsible for disposing it.
/// </summary>
internal sealed class ProgressStream(Stream inner, Action<long> onBytesRead) : Stream
{
    private long _totalRead;

    /// <summary>Total number of bytes read from the underlying stream so far.</summary>
    public long TotalRead => _totalRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _totalRead += read;
            onBytesRead(_totalRead);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _totalRead += read;
            onBytesRead(_totalRead);
        }

        return read;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
