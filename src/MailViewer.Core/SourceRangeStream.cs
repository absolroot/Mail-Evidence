using Microsoft.Win32.SafeHandles;

namespace MailViewer.Core;

internal sealed class SourceRangeStream(SafeFileHandle handle, ByteRange range, CancellationToken ct = default) : Stream
{
    private long position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => range.Length;

    public override long Position
    {
        get => position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ct.ThrowIfCancellationRequested();
        if (position >= Length || buffer.IsEmpty)
            return 0;

        var limit = (int)Math.Min(buffer.Length, Length - position);
        var count = RandomAccess.Read(handle, buffer[..limit], range.Offset + position);
        if (count == 0)
            throw new EndOfStreamException("분석 중 원본 파일의 길이가 달라졌습니다.");

        position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
