using System.Buffers.Binary;
using NtfsRecovery.Core.Partitions;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Partitions;

public class PartitionTableReaderTests
{
    private const int SectorSize = 512;

    private static void WriteMbrEntry(byte[] sector, int entryIndex, byte type, uint startLba, uint sectorCount)
    {
        int offset = 0x1BE + (entryIndex * 16);
        sector[offset + 4] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(offset + 8, 4), startLba);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(offset + 12, 4), sectorCount);
    }

    private static void WriteMbrSignature(byte[] sector)
    {
        sector[0x1FE] = 0x55;
        sector[0x1FF] = 0xAA;
    }

    /// <summary>Writes a minimal, valid NTFS boot sector at the given byte offset, so LooksLikeNtfs detection has something real to find.</summary>
    private static void WriteNtfsBootSector(byte[] image, long offset)
    {
        byte[] sector = new byte[512];
        "NTFS    "u8.CopyTo(sector.AsSpan(0x03));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(0x0B, 2), 512);
        sector[0x0D] = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(sector.AsSpan(0x28, 8), 100_000);
        sector[0x40] = unchecked((byte)(sbyte)-10);
        sector[0x44] = 1;
        sector[0x1FE] = 0x55;
        sector[0x1FF] = 0xAA;
        sector.CopyTo(image, offset);
    }

    [Fact]
    public void Read_NoMbrSignature_ReturnsNone()
    {
        byte[] image = new byte[SectorSize];
        var device = new InMemoryBlockDevice(image);

        PartitionTableReadResult result = PartitionTableReader.Read(device, SectorSize);

        Assert.Equal(PartitionTableKind.None, result.Kind);
        Assert.Empty(result.Partitions);
    }

    [Fact]
    public void Read_SimpleMbr_OnePrimaryNtfsPartition_IsDetected()
    {
        byte[] image = new byte[200 * SectorSize];
        WriteMbrEntry(image, 0, type: 0x07, startLba: 100, sectorCount: 50);
        WriteMbrSignature(image);
        WriteNtfsBootSector(image, 100L * SectorSize);

        var device = new InMemoryBlockDevice(image);
        PartitionTableReadResult result = PartitionTableReader.Read(device, SectorSize);

        Assert.Equal(PartitionTableKind.Mbr, result.Kind);
        PartitionInfo partition = Assert.Single(result.Partitions);
        Assert.Equal(100L * SectorSize, partition.StartOffset);
        Assert.Equal(50L * SectorSize, partition.LengthBytes);
        Assert.True(partition.LooksLikeNtfs);
        Assert.Equal("NTFS/exFAT", partition.TypeDescription);
    }

    [Fact]
    public void Read_MbrPartition_WithoutNtfsBootSector_LooksLikeNtfsIsFalse()
    {
        byte[] image = new byte[200 * SectorSize];
        WriteMbrEntry(image, 0, type: 0x0B, startLba: 100, sectorCount: 50); // FAT32, no NTFS boot sector written
        WriteMbrSignature(image);

        var device = new InMemoryBlockDevice(image);
        PartitionTableReadResult result = PartitionTableReader.Read(device, SectorSize);

        PartitionInfo partition = Assert.Single(result.Partitions);
        Assert.False(partition.LooksLikeNtfs);
        Assert.Equal("FAT32", partition.TypeDescription);
    }

    [Fact]
    public void Read_ExtendedPartitionChain_WalksLogicalPartitions()
    {
        byte[] image = new byte[1100 * SectorSize];

        // Primary entry 0: extended partition starting at LBA 1000.
        WriteMbrEntry(image, 0, type: 0x0F, startLba: 1000, sectorCount: 100);
        WriteMbrSignature(image);

        // First EBR at LBA 1000: logical partition at relative LBA 1 (absolute 1001),
        // entry1 points to the next EBR at LBA 1000+20=1020 (relative to extended start).
        byte[] ebr1 = new byte[SectorSize];
        WriteMbrEntry(ebr1, 0, type: 0x07, startLba: 1, sectorCount: 10);
        WriteMbrEntry(ebr1, 1, type: 0x05, startLba: 20, sectorCount: 0);
        WriteMbrSignature(ebr1);
        ebr1.CopyTo(image, 1000L * SectorSize);

        // Second EBR at LBA 1020: logical partition at relative LBA 1 (absolute 1021), no further entries.
        byte[] ebr2 = new byte[SectorSize];
        WriteMbrEntry(ebr2, 0, type: 0x07, startLba: 1, sectorCount: 10);
        WriteMbrSignature(ebr2);
        ebr2.CopyTo(image, 1020L * SectorSize);

        var device = new InMemoryBlockDevice(image);
        PartitionTableReadResult result = PartitionTableReader.Read(device, SectorSize);

        Assert.Equal(2, result.Partitions.Count);
        Assert.Equal(1001L * SectorSize, result.Partitions[0].StartOffset);
        Assert.Equal(1021L * SectorSize, result.Partitions[1].StartOffset);
    }

    [Fact]
    public void Read_Gpt_ReadsPartitionEntriesAndLabels()
    {
        byte[] image = new byte[200 * SectorSize];

        // Protective MBR.
        WriteMbrEntry(image, 0, type: 0xEE, startLba: 1, sectorCount: 199);
        WriteMbrSignature(image);

        // GPT header at LBA 1.
        byte[] header = new byte[SectorSize];
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(72, 8), 2); // PartitionEntryLBA
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80, 4), 2); // NumberOfPartitionEntries
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84, 4), 128); // SizeOfPartitionEntry
        header.CopyTo(image, 1L * SectorSize);

        // Partition entries at LBA 2 (2 x 128 bytes).
        byte[] entries = new byte[256];
        Guid basicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        basicData.ToByteArray().CopyTo(entries, 0);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(32, 8), 34);
        BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(40, 8), 133);
        System.Text.Encoding.Unicode.GetBytes("Dados").CopyTo(entries.AsSpan(56, 72));
        entries.CopyTo(image, 2L * SectorSize);

        WriteNtfsBootSector(image, 34L * SectorSize);

        var device = new InMemoryBlockDevice(image);
        PartitionTableReadResult result = PartitionTableReader.Read(device, SectorSize);

        Assert.Equal(PartitionTableKind.Gpt, result.Kind);
        PartitionInfo partition = Assert.Single(result.Partitions);
        Assert.Equal(34L * SectorSize, partition.StartOffset);
        Assert.Equal(100L * SectorSize, partition.LengthBytes);
        Assert.Equal("Dados", partition.VolumeLabel);
        Assert.True(partition.LooksLikeNtfs);
        Assert.Contains("Basic Data", partition.TypeDescription);
    }
}
