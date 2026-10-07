namespace AiGateway.Core.Common;

/// <summary>
/// Bungkus stream baca yang melempar <see cref="ResponseTooLargeException"/> begitu total byte yang terbaca melebihi
/// batas. Dipakai untuk respons streaming upstream: <see cref="StreamReader"/> menumpuk satu baris utuh di memori,
/// jadi tanpa batas di tingkat byte satu baris tanpa newline bisa menghabiskan memori proses.
/// </summary>
public sealed class LimitedReadStream(Stream inner, int maxBytes) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        _read += read;
        if (_read > maxBytes) throw new ResponseTooLargeException(maxBytes);
        return read;
    }
}
