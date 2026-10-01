using System.Buffers.Binary;
using System.Text;

namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// Parsed representation of an NTFS boot sector (first 512+ bytes of a volume).
/// Pure value type: parsing never touches the underlying device, so it is trivially
/// testable against synthetic byte arrays.
/// </summary>
public sealed class NtfsBootSector
{
    public const int RawSize = 512;

    private static readonly byte[] ExpectedOemId = Encoding.ASCII.GetBytes("NTFS    ");

    public required ushort BytesPerSector { get; init; }
    public required byte SectorsPerCluster { get; init; }
    public required ulong TotalSectors { get; init; }
    public required long MftLcn { get; init; }
    public required long MftMirrLcn { get; init; }
    public required int MftRecordSize { get; init; }
    public required int IndexRecordSize { get; init; }
    public required ulong VolumeSerialNumber { get; init; }

    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    public long MftOffset => MftLcn * BytesPerCluster;

    public long MftMirrOffset => MftMirrLcn * BytesPerCluster;

    public long VolumeSizeInBytes => (long)TotalSectors * BytesPerSector;

    /// <summary>
    /// Parses an NTFS boot sector from exactly 512 bytes.
    /// Throws <see cref="InvalidNtfsBootSectorException"/> when the OEM ID or the
    /// 0x55AA end-of-sector signature does not match NTFS.
    /// </summary>
    public static NtfsBootSector Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < RawSize)
            throw new ArgumentException($"Boot sector buffer must be at least {RawSize} bytes.", nameof(sector));

        ReadOnlySpan<byte> oemId = sector.Slice(0x03, 8);
        if (!oemId.SequenceEqual(ExpectedOemId))
        {
            throw new InvalidNtfsBootSectorException(
                $"OEM ID '{Encoding.ASCII.GetString(oemId)}' does not match expected 'NTFS    '.");
        }

        ushort endMarker = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(0x1FE, 2));
        if (endMarker != 0xAA55)
        {
            throw new InvalidNtfsBootSectorException(
                $"End-of-sector signature 0x{endMarker:X4} does not match expected 0xAA55.");
        }

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector.Slice(0x0B, 2));
        byte sectorsPerCluster = sector[0x0D];
        ulong totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(sector.Slice(0x28, 8));
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(0x30, 8));
        long mftMirrLcn = BinaryPrimitives.ReadInt64LittleEndian(sector.Slice(0x38, 8));
        sbyte rawClustersPerMftRecord = unchecked((sbyte)sector[0x40]);
        sbyte rawClustersPerIndexRecord = unchecked((sbyte)sector[0x44]);
        ulong volumeSerialNumber = BinaryPrimitives.ReadUInt64LittleEndian(sector.Slice(0x48, 8));

        if (bytesPerSector == 0 || sectorsPerCluster == 0)
        {
            throw new InvalidNtfsBootSectorException(
                $"Invalid geometry: bytesPerSector={bytesPerSector}, sectorsPerCluster={sectorsPerCluster}.");
        }

        int bytesPerCluster = bytesPerSector * sectorsPerCluster;

        return new NtfsBootSector
        {
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            TotalSectors = totalSectors,
            MftLcn = mftLcn,
            MftMirrLcn = mftMirrLcn,
            MftRecordSize = ResolveRecordSize(rawClustersPerMftRecord, bytesPerCluster),
            IndexRecordSize = ResolveRecordSize(rawClustersPerIndexRecord, bytesPerCluster),
            VolumeSerialNumber = volumeSerialNumber,
        };
    }

    /// <summary>
    /// NTFS encodes record size either as a positive cluster count ("N clusters per
    /// record") or, when negative, as a power-of-two byte count: size = 2^|value| bytes.
    /// </summary>
    private static int ResolveRecordSize(sbyte rawValue, int bytesPerCluster)
    {
        if (rawValue > 0)
            return rawValue * bytesPerCluster;

        return 1 << -rawValue;
    }
}

public sealed class InvalidNtfsBootSectorException(string message) : Exception(message);
