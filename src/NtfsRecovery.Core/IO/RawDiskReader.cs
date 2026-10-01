using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NtfsRecovery.Core.IO;

/// <summary>
/// Read-only access to a physical disk device (e.g. \\.\PhysicalDrive1) on Windows.
///
/// Safety invariant: this class never requests GENERIC_WRITE from the OS. The native
/// handle is opened with GENERIC_READ only, so writing to the source disk through this
/// type is not just discouraged by convention — it is impossible at the Win32 API level.
///
/// Physical drive handles only accept sector-aligned offsets and lengths, so every read
/// is widened to the surrounding sector boundary and the requested slice is copied out
/// of that aligned buffer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawDiskReader : IBlockDevice
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const int INVALID_HANDLE_VALUE = -1;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    private readonly SafeFileHandle _handle;
    private readonly int _sectorSize;
    private bool _disposed;

    public RawDiskReader(string physicalDrivePath, int sectorSize = 512)
    {
        if (string.IsNullOrWhiteSpace(physicalDrivePath))
            throw new ArgumentException("Physical drive path must not be empty.", nameof(physicalDrivePath));

        if (sectorSize <= 0 || (sectorSize & (sectorSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be a positive power of two.");

        SourceDescription = physicalDrivePath;
        _sectorSize = sectorSize;

        _handle = CreateFileW(
            physicalDrivePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Failed to open '{physicalDrivePath}' for read-only access (Win32 error {error}). " +
                "Administrator privileges are typically required to open a physical drive.");
        }

        Length = QueryLength();
    }

    public long Length { get; }

    public string SourceDescription { get; }

    private long QueryLength()
    {
        if (GetFileSizeEx(_handle, out long size) && size > 0)
            return size;

        // GetFileSizeEx frequently fails or returns 0 for physical drive handles because
        // they have no conventional "file size". Fall back to the caller-supplied geometry
        // (the CLI/caller is expected to know disk size from a trustworthy source) by
        // reporting 0 here; higher layers should not rely on Length for physical drives
        // without independently confirming capacity.
        return 0;
    }

    public int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        if (buffer.IsEmpty)
            return 0;

        long alignedStart = AlignDown(offset, _sectorSize);
        long alignedEndExclusive = AlignUp(offset + buffer.Length, _sectorSize);
        int alignedLength = checked((int)(alignedEndExclusive - alignedStart));

        byte[] aligned = System.Buffers.ArrayPool<byte>.Shared.Rent(alignedLength);
        try
        {
            int totalRead = RandomAccess.Read(_handle, aligned.AsSpan(0, alignedLength), alignedStart);

            int sliceStart = checked((int)(offset - alignedStart));
            int available = Math.Max(0, totalRead - sliceStart);
            int toCopy = Math.Min(available, buffer.Length);

            if (toCopy > 0)
                aligned.AsSpan(sliceStart, toCopy).CopyTo(buffer);

            if (toCopy < buffer.Length)
                buffer.Slice(toCopy).Clear();

            return toCopy;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(aligned);
        }
    }

    private static long AlignDown(long value, int alignment) => value - (value % alignment);

    private static long AlignUp(long value, int alignment)
    {
        long remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _handle.Dispose();
        _disposed = true;
    }
}
