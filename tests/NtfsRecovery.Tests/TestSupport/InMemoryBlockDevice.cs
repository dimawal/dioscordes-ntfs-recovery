using NtfsRecovery.Core.IO;

namespace NtfsRecovery.Tests.TestSupport;

/// <summary>Trivial read-only <see cref="IBlockDevice"/> backed by a byte array, for scanner tests.</summary>
public sealed class InMemoryBlockDevice(byte[] content) : IBlockDevice
{
    public long Length { get; } = content.Length;

    public string SourceDescription => "in-memory-test-device";

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset >= content.Length)
            return 0;

        int available = (int)Math.Min(buffer.Length, content.Length - offset);
        content.AsSpan((int)offset, available).CopyTo(buffer);
        if (available < buffer.Length)
            buffer.Slice(available).Clear();
        return available;
    }

    public void Dispose() { }
}
