using NtfsRecovery.Core.IO;

namespace NtfsRecovery.Tests.TestSupport;

/// <summary>
/// Test double that simulates a device whose very first read at a given offset returns
/// fewer bytes than requested (a transient short read, like a USB mass-storage bridge
/// hiccup), then succeeds normally on the next call -- for verifying that callers retry
/// instead of treating the first short result as final.
/// </summary>
public sealed class FlakyBlockDevice(byte[] content, long flakyOffset, int firstCallMaxBytes) : IBlockDevice
{
    private bool _flakyCallConsumed;

    public long Length => content.Length;
    public string SourceDescription => "flaky-test-device";

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset == flakyOffset && !_flakyCallConsumed)
        {
            _flakyCallConsumed = true;
            int capped = Math.Min(buffer.Length, firstCallMaxBytes);
            content.AsSpan((int)offset, capped).CopyTo(buffer);
            if (capped < buffer.Length)
                buffer.Slice(capped).Clear();
            return capped;
        }

        int available = (int)Math.Min(buffer.Length, content.Length - offset);
        content.AsSpan((int)offset, available).CopyTo(buffer);
        if (available < buffer.Length)
            buffer.Slice(available).Clear();
        return available;
    }

    public void Dispose() { }
}

/// <summary>Test double simulating a region of the device that is persistently unreadable (always returns 0 bytes), regardless of retries.</summary>
public sealed class PersistentlyUnreadableBlockDevice(byte[] content, long unreadableStart, long unreadableEnd) : IBlockDevice
{
    public long Length => content.Length;
    public string SourceDescription => "persistently-unreadable-test-device";

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset >= unreadableStart && offset < unreadableEnd)
        {
            buffer.Clear();
            return 0;
        }

        int available = (int)Math.Min(buffer.Length, content.Length - offset);
        content.AsSpan((int)offset, available).CopyTo(buffer);
        if (available < buffer.Length)
            buffer.Slice(available).Clear();
        return available;
    }

    public void Dispose() { }
}
