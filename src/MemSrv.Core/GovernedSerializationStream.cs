using System.Diagnostics;
using System.Security.Cryptography;

namespace MemSrv.Core;

/// <summary>
/// Governed write-only serialization plumbing. Derived sinks decide only what
/// to do with each validated byte span; deadline enforcement and Stream
/// behavior stay identical for fidelity counting and signature hashing.
/// </summary>
internal abstract class GovernedSerializationStream(TimeSpan deadline) : Stream
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public void AssertWithinDeadline()
    {
        if (_clock.Elapsed > deadline)
        {
            throw new SafetyScanException(
                "capture serialization exceeded the governed " +
                $"{deadline.TotalSeconds:0}-second deadline");
        }
    }

    public override void Flush() => AssertWithinDeadline();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The buffer range is invalid.");
        }
        Add(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer);

    public override void WriteByte(byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        Add(buffer);
    }

    private void Add(ReadOnlySpan<byte> buffer)
    {
        AssertWithinDeadline();
        WriteToSink(buffer);
        BytesWritten = checked(BytesWritten + buffer.Length);
    }

    protected abstract void WriteToSink(ReadOnlySpan<byte> buffer);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();
}

internal sealed class CountingSerializationStream(TimeSpan deadline)
    : GovernedSerializationStream(deadline)
{
    protected override void WriteToSink(ReadOnlySpan<byte> buffer)
    {
    }
}

internal sealed class HashingSerializationStream(
    IncrementalHash hash,
    TimeSpan deadline) : GovernedSerializationStream(deadline)
{
    protected override void WriteToSink(ReadOnlySpan<byte> buffer) =>
        hash.AppendData(buffer);
}

internal sealed class BoundedBufferSerializationStream(
    long maximumBytes,
    TimeSpan deadline) : GovernedSerializationStream(deadline)
{
    private readonly MemoryStream _buffer = new();

    public ReadOnlyMemory<byte> WrittenMemory =>
        _buffer.GetBuffer().AsMemory(0, checked((int)_buffer.Length));

    protected override void WriteToSink(ReadOnlySpan<byte> buffer)
    {
        if (BytesWritten > maximumBytes - buffer.Length)
        {
            throw new CaptureRepresentationLimitException(
                $"the governed capture representation exceeded {maximumBytes} bytes");
        }
        _buffer.Write(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class CaptureRepresentationLimitException(string message)
    : Exception(message);
