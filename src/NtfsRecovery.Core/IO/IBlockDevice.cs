namespace NtfsRecovery.Core.IO;

/// <summary>
/// Read-only abstraction over a block device (raw disk image or physical disk).
/// Intentionally exposes no write members: the type system itself makes it
/// impossible for any consumer to write to a source device through this contract.
/// </summary>
public interface IBlockDevice : IDisposable
{
    /// <summary>Total addressable length of the device, in bytes.</summary>
    long Length { get; }

    /// <summary>Human-readable identifier of the underlying device (file path or physical drive path).</summary>
    string SourceDescription { get; }

    /// <summary>
    /// Reads <paramref name="buffer"/>.Length bytes starting at absolute byte <paramref name="offset"/>.
    /// Returns the number of bytes actually read, which may be less than the buffer length
    /// at end of device. Implementations must zero-fill any unread tail of
    /// <paramref name="buffer"/> (positions [return value, buffer.Length)) rather than
    /// leaving it unmodified, since callers routinely reuse the same backing buffer across
    /// many calls -- leaving it untouched would let stale bytes from an earlier, unrelated
    /// read masquerade as real data for a short read.
    /// </summary>
    int ReadAt(long offset, Span<byte> buffer);
}
