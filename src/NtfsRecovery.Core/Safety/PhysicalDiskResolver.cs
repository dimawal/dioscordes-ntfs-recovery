using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NtfsRecovery.Core.Safety;

/// <summary>
/// Resolves the physical disk(s) backing a path: either a direct
/// \\.\PhysicalDriveN device path, or an ordinary file/directory path (resolved via its
/// volume's disk extents, so even a spanned/RAID volume is reported fully). Used
/// exclusively to decide whether a recovery destination shares a physical disk with the
/// source -- never to read or write disk content.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PhysicalDiskResolver
{
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    public static PhysicalDiskIdentity? TryResolveForPath(string path)
    {
        if (TryParsePhysicalDriveNumber(path, out uint driveNumber))
            return new PhysicalDiskIdentity(new HashSet<uint> { driveNumber });

        string? fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            return null; // UNC paths, relative paths, etc.: cannot be mapped to a local physical disk

        string volumePath = $@"\\.\{root[0]}:";
        return TryResolveVolumeDiskExtents(volumePath);
    }

    private static PhysicalDiskIdentity? TryResolveVolumeDiskExtents(string volumeDevicePath)
    {
        // A desired-access of 0 ("query metadata only, no read/write intent") is
        // sufficient for this IOCTL and, unlike GENERIC_READ, does not require
        // Administrator privileges to open a volume device handle.
        using SafeFileHandle handle = CreateFileW(
            volumeDevicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        byte[] buffer = new byte[1024];
        if (!DeviceIoControl(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out uint bytesReturned, IntPtr.Zero))
            return null;

        HashSet<uint> diskNumbers = ParseVolumeDiskExtents(buffer.AsSpan(0, (int)bytesReturned));
        return diskNumbers.Count > 0 ? new PhysicalDiskIdentity(diskNumbers) : null;
    }

    /// <summary>
    /// Parses a raw VOLUME_DISK_EXTENTS buffer: a 4-byte extent count (with 4 bytes of
    /// padding for 8-byte alignment) followed by that many 24-byte DISK_EXTENT entries
    /// (uint DiskNumber, int64 StartingOffset, int64 ExtentLength). Pulled out as a pure
    /// function so the parsing logic can be unit tested without a real device handle.
    /// </summary>
    internal static HashSet<uint> ParseVolumeDiskExtents(ReadOnlySpan<byte> buffer)
    {
        var result = new HashSet<uint>();
        if (buffer.Length < 8)
            return result;

        uint extentCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        const int headerSize = 8;
        const int extentSize = 24;

        for (int i = 0; i < extentCount; i++)
        {
            int offset = headerSize + (i * extentSize);
            if (offset + 4 > buffer.Length)
                break;

            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4)));
        }

        return result;
    }

    internal static bool TryParsePhysicalDriveNumber(string path, out uint driveNumber)
    {
        Match match = Regex.Match(path, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && uint.TryParse(match.Groups[1].Value, out driveNumber))
            return true;

        driveNumber = 0;
        return false;
    }
}
