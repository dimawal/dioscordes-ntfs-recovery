using System.Runtime.Versioning;
using NtfsRecovery.Core.Safety;
using Xunit;

namespace NtfsRecovery.Tests.Safety;

[SupportedOSPlatform("windows")]
public class PhysicalDiskResolverTests
{
    [Theory]
    [InlineData(@"\\.\PhysicalDrive0", 0u)]
    [InlineData(@"\\.\PhysicalDrive1", 1u)]
    [InlineData(@"\\.\physicaldrive12", 12u)]
    public void TryParsePhysicalDriveNumber_ParsesDriveNumber(string path, uint expected)
    {
        bool ok = PhysicalDiskResolver.TryParsePhysicalDriveNumber(path, out uint number);

        Assert.True(ok);
        Assert.Equal(expected, number);
    }

    [Fact]
    public void TryParsePhysicalDriveNumber_NonMatchingPath_ReturnsFalse()
    {
        bool ok = PhysicalDiskResolver.TryParsePhysicalDriveNumber(@"D:\Recovery\file.bin", out _);

        Assert.False(ok);
    }

    /// <summary>Builds a synthetic VOLUME_DISK_EXTENTS buffer: 4-byte count + 4 padding, then 24-byte DISK_EXTENT entries.</summary>
    private static byte[] BuildVolumeDiskExtentsBuffer(params uint[] diskNumbers)
    {
        byte[] buffer = new byte[8 + (diskNumbers.Length * 24)];
        BitConverter.GetBytes((uint)diskNumbers.Length).CopyTo(buffer, 0);

        for (int i = 0; i < diskNumbers.Length; i++)
        {
            int offset = 8 + (i * 24);
            BitConverter.GetBytes(diskNumbers[i]).CopyTo(buffer, offset);
        }

        return buffer;
    }

    [Fact]
    public void ParseVolumeDiskExtents_SingleExtent_ReturnsOneDiskNumber()
    {
        byte[] buffer = BuildVolumeDiskExtentsBuffer(2);

        HashSet<uint> disks = PhysicalDiskResolver.ParseVolumeDiskExtents(buffer);

        Assert.Equal(new HashSet<uint> { 2 }, disks);
    }

    [Fact]
    public void ParseVolumeDiskExtents_SpannedVolume_ReturnsAllDiskNumbers()
    {
        byte[] buffer = BuildVolumeDiskExtentsBuffer(1, 2, 3);

        HashSet<uint> disks = PhysicalDiskResolver.ParseVolumeDiskExtents(buffer);

        Assert.Equal(new HashSet<uint> { 1, 2, 3 }, disks);
    }

    [Fact]
    public void ParseVolumeDiskExtents_TruncatedBuffer_StopsSafelyWithoutThrowing()
    {
        byte[] buffer = BuildVolumeDiskExtentsBuffer(1, 2, 3).AsSpan(0, 20).ToArray(); // claims 3 extents, has ~0.5

        HashSet<uint> disks = PhysicalDiskResolver.ParseVolumeDiskExtents(buffer);

        Assert.Equal(new HashSet<uint> { 1 }, disks);
    }

    [Fact]
    public void ParseVolumeDiskExtents_EmptyBuffer_ReturnsEmpty()
    {
        HashSet<uint> disks = PhysicalDiskResolver.ParseVolumeDiskExtents(ReadOnlySpan<byte>.Empty);

        Assert.Empty(disks);
    }
}
