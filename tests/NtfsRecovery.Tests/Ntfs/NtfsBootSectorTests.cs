using NtfsRecovery.Core.Ntfs;
using Xunit;

namespace NtfsRecovery.Tests.Ntfs;

public class NtfsBootSectorTests
{
    /// <summary>
    /// Builds a synthetic 512-byte NTFS boot sector with the given geometry, mirroring
    /// the layout documented in the Microsoft NTFS reference (OEM ID at 0x03, BPB at
    /// 0x0B, extended BPB at 0x24, 0x55AA signature at 0x1FE).
    /// </summary>
    private static byte[] BuildBootSector(
        ushort bytesPerSector = 512,
        byte sectorsPerCluster = 8,
        ulong totalSectors = 1_000_000,
        long mftLcn = 786432,
        long mftMirrLcn = 2,
        sbyte clustersPerMftRecord = -10, // 2^10 = 1024 bytes
        sbyte clustersPerIndexRecord = 1,
        ulong volumeSerialNumber = 0x1122334455667788)
    {
        byte[] sector = new byte[512];

        "NTFS    "u8.CopyTo(sector.AsSpan(0x03));

        BitConverter.GetBytes(bytesPerSector).CopyTo(sector, 0x0B);
        sector[0x0D] = sectorsPerCluster;
        BitConverter.GetBytes(totalSectors).CopyTo(sector, 0x28);
        BitConverter.GetBytes(mftLcn).CopyTo(sector, 0x30);
        BitConverter.GetBytes(mftMirrLcn).CopyTo(sector, 0x38);
        sector[0x40] = unchecked((byte)clustersPerMftRecord);
        sector[0x44] = unchecked((byte)clustersPerIndexRecord);
        BitConverter.GetBytes(volumeSerialNumber).CopyTo(sector, 0x48);

        sector[0x1FE] = 0x55;
        sector[0x1FF] = 0xAA;

        return sector;
    }

    [Fact]
    public void Parse_ValidBootSector_ExtractsGeometry()
    {
        byte[] sector = BuildBootSector();

        NtfsBootSector result = NtfsBootSector.Parse(sector);

        Assert.Equal(512, result.BytesPerSector);
        Assert.Equal(8, result.SectorsPerCluster);
        Assert.Equal(4096, result.BytesPerCluster);
        Assert.Equal(1_000_000UL, result.TotalSectors);
        Assert.Equal(786432L, result.MftLcn);
        Assert.Equal(2L, result.MftMirrLcn);
        Assert.Equal(786432L * 4096, result.MftOffset);
        Assert.Equal(2L * 4096, result.MftMirrOffset);
        Assert.Equal(0x1122334455667788UL, result.VolumeSerialNumber);
    }

    [Theory]
    [InlineData(-10, 1024)] // negative: 2^10 bytes
    [InlineData(-9, 512)]
    [InlineData(1, 4096)] // positive: 1 cluster * 4096 bytes/cluster
    [InlineData(2, 8192)]
    public void Parse_ResolvesMftRecordSize_FromSignedClusterField(sbyte rawValue, int expectedBytes)
    {
        byte[] sector = BuildBootSector(sectorsPerCluster: 8, clustersPerMftRecord: rawValue);

        NtfsBootSector result = NtfsBootSector.Parse(sector);

        Assert.Equal(expectedBytes, result.MftRecordSize);
    }

    [Fact]
    public void Parse_WrongOemId_Throws()
    {
        byte[] sector = BuildBootSector();
        "FAT32   "u8.CopyTo(sector.AsSpan(0x03));

        Assert.Throws<InvalidNtfsBootSectorException>(() => NtfsBootSector.Parse(sector));
    }

    [Fact]
    public void Parse_MissingEndSignature_Throws()
    {
        byte[] sector = BuildBootSector();
        sector[0x1FE] = 0x00;
        sector[0x1FF] = 0x00;

        Assert.Throws<InvalidNtfsBootSectorException>(() => NtfsBootSector.Parse(sector));
    }

    [Fact]
    public void Parse_BufferTooSmall_Throws()
    {
        byte[] tooSmall = new byte[100];

        Assert.Throws<ArgumentException>(() => NtfsBootSector.Parse(tooSmall));
    }

    [Fact]
    public void Parse_ZeroBytesPerSector_Throws()
    {
        byte[] sector = BuildBootSector(bytesPerSector: 0);

        Assert.Throws<InvalidNtfsBootSectorException>(() => NtfsBootSector.Parse(sector));
    }
}
