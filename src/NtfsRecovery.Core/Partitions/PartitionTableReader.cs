using System.Buffers.Binary;
using System.Text;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Partitions;

/// <summary>
/// Reads a disk's MBR or GPT partition table directly from raw bytes -- the same
/// read-only approach used everywhere else in this engine, so it works identically for
/// a live physical disk and for a .img file (unlike the Windows IOCTL_DISK_GET_DRIVE_LAYOUT_EX
/// API, which only works against a live \\.\PhysicalDriveN handle). Purely informational:
/// lets an operator pick a partition's offset without typing it in by hand or guessing.
/// </summary>
public static class PartitionTableReader
{
    private const int MaxExtendedChainDepth = 128;

    public static PartitionTableReadResult Read(IBlockDevice device, int sectorSize = 512)
    {
        byte[] sector0 = new byte[sectorSize];
        if (device.ReadAt(0, sector0) < sectorSize || !HasMbrSignature(sector0))
            return new PartitionTableReadResult(PartitionTableKind.None, []);

        if (TryReadGpt(device, sectorSize, out PartitionTableReadResult? gptResult))
            return gptResult!;

        var result = new List<PartitionInfo>();
        int index = 0;

        for (int i = 0; i < 4; i++)
        {
            MbrEntry entry = ParseMbrEntry(sector0, 0x1BE + (i * 16));
            if (entry.Type == 0x00)
                continue;

            if (IsExtendedType(entry.Type))
            {
                WalkExtendedChain(device, sectorSize, entry.StartLba, entry.StartLba, result, ref index, 0);
                continue;
            }

            AddEntry(device, sectorSize, result, ref index, entry.StartLba, entry.SectorCount, DescribeMbrType(entry.Type), null);
        }

        return new PartitionTableReadResult(PartitionTableKind.Mbr, result);
    }

    private static void WalkExtendedChain(
        IBlockDevice device, int sectorSize, long extendedStartLba, long currentEbrLba,
        List<PartitionInfo> result, ref int index, int depth)
    {
        if (depth >= MaxExtendedChainDepth)
            return; // guards against a corrupted/cyclic EBR chain looping forever

        byte[] ebrSector = new byte[sectorSize];
        if (device.ReadAt(currentEbrLba * sectorSize, ebrSector) < sectorSize || !HasMbrSignature(ebrSector))
            return;

        MbrEntry logical = ParseMbrEntry(ebrSector, 0x1BE);
        if (logical.Type != 0x00 && !IsExtendedType(logical.Type))
        {
            long absoluteStart = currentEbrLba + logical.StartLba;
            AddEntry(device, sectorSize, result, ref index, absoluteStart, logical.SectorCount, DescribeMbrType(logical.Type), null);
        }

        MbrEntry next = ParseMbrEntry(ebrSector, 0x1BE + 16);
        if (next.Type != 0x00 && IsExtendedType(next.Type))
        {
            long nextEbrLba = extendedStartLba + next.StartLba;
            WalkExtendedChain(device, sectorSize, extendedStartLba, nextEbrLba, result, ref index, depth + 1);
        }
    }

    private static bool TryReadGpt(IBlockDevice device, int sectorSize, out PartitionTableReadResult? result)
    {
        result = null;
        byte[] header = new byte[sectorSize];
        if (device.ReadAt(sectorSize, header) < 92)
            return false;

        if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
            return false;

        long partitionEntryLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72, 8));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4));

        if (entrySize < 128 || entryCount == 0 || entryCount > 4096)
            return false; // sanity bounds; a genuine GPT header never exceeds this

        byte[] entries = new byte[entryCount * entrySize];
        if (device.ReadAt(partitionEntryLba * sectorSize, entries) < entries.Length)
            return false;

        var partitions = new List<PartitionInfo>();
        int index = 0;

        for (int i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> entry = entries.AsSpan(i * (int)entrySize, (int)entrySize);
            ReadOnlySpan<byte> typeGuid = entry[..16];
            if (typeGuid.IndexOfAnyExcept((byte)0) < 0)
                continue; // all-zero type GUID: unused entry

            long startLba = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(32, 8));
            long endLba = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(40, 8));
            string label = Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');

            long lengthBytes = (endLba - startLba + 1) * sectorSize;
            AddEntry(device, sectorSize, partitions, ref index, startLba, lengthBytes / sectorSize, DescribeGptType(new Guid(typeGuid)), label);
        }

        result = new PartitionTableReadResult(PartitionTableKind.Gpt, partitions);
        return true;
    }

    private static void AddEntry(
        IBlockDevice device, int sectorSize, List<PartitionInfo> result, ref int index,
        long startLba, long sectorCount, string typeDescription, string? label)
    {
        long startOffset = startLba * sectorSize;
        long lengthBytes = sectorCount * sectorSize;
        bool looksLikeNtfs = TryPeekIsNtfs(device, startOffset);

        result.Add(new PartitionInfo(index++, startOffset, lengthBytes, typeDescription, looksLikeNtfs, string.IsNullOrEmpty(label) ? null : label));
    }

    private static bool TryPeekIsNtfs(IBlockDevice device, long offset)
    {
        Span<byte> sector = stackalloc byte[NtfsBootSector.RawSize];
        if (device.ReadAt(offset, sector) < NtfsBootSector.RawSize)
            return false;

        try
        {
            NtfsBootSector.Parse(sector);
            return true;
        }
        catch (InvalidNtfsBootSectorException)
        {
            return false;
        }
    }

    private readonly record struct MbrEntry(byte Type, long StartLba, long SectorCount);

    private static MbrEntry ParseMbrEntry(ReadOnlySpan<byte> sector, int offset)
    {
        byte type = sector[offset + 4];
        uint startLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(offset + 8, 4));
        uint sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(offset + 12, 4));
        return new MbrEntry(type, startLba, sectorCount);
    }

    private static bool HasMbrSignature(ReadOnlySpan<byte> sector) =>
        sector.Length >= 512 && sector[0x1FE] == 0x55 && sector[0x1FF] == 0xAA;

    private static bool IsExtendedType(byte type) => type is 0x05 or 0x0F or 0x85;

    private static string DescribeMbrType(byte type) => type switch
    {
        0x07 => "NTFS/exFAT",
        0x0B or 0x0C => "FAT32",
        0x05 or 0x0F or 0x85 => "Extended",
        0x82 => "Linux swap",
        0x83 => "Linux",
        0xEE => "GPT protective",
        _ => $"Type 0x{type:X2}",
    };

    private static string DescribeGptType(Guid typeGuid)
    {
        if (typeGuid == new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"))
            return "Microsoft Basic Data (NTFS/FAT)";
        if (typeGuid == new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"))
            return "EFI System";
        if (typeGuid == new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE"))
            return "Microsoft Reserved";
        if (typeGuid == new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"))
            return "Windows Recovery";
        return typeGuid.ToString();
    }
}
