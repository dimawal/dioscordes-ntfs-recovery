using System.Buffers.Binary;
using System.Text;

namespace NtfsRecovery.Tests.TestSupport;

/// <summary>
/// Builds synthetic, byte-accurate MFT records for unit tests, so the parser can be
/// exercised without needing a real NTFS volume or disk image.
/// </summary>
public static class SyntheticMftRecordBuilder
{
    public const int DefaultRecordSize = 1024;
    public const int DefaultSectorSize = 512;

    public sealed class Options
    {
        public uint RecordNumber { get; set; } = 42;
        public ushort SequenceNumber { get; set; } = 3;
        public bool IsDirectory { get; set; }
        public bool IsInUse { get; set; } = true;
        public string FileName { get; set; } = "test.txt";
        public byte[]? ResidentData { get; set; } = Encoding.ASCII.GetBytes("Hello, NTFS recovery!");
        public ulong ParentRecordNumber { get; set; } = 5;
        public ushort ParentSequenceNumber { get; set; } = 1;
        public int RecordSize { get; set; } = DefaultRecordSize;
        public int SectorSize { get; set; } = DefaultSectorSize;
        public bool OmitTerminator { get; set; }
        public bool CorruptFixup { get; set; }
    }

    public static byte[] Build(Options options)
    {
        byte[] record = new byte[options.RecordSize];
        const int usaOffset = 0x30;
        int sectorCount = options.RecordSize / options.SectorSize;
        int usaSize = sectorCount + 1;
        int firstAttributeOffset = usaOffset + (usaSize * 2);
        firstAttributeOffset = (firstAttributeOffset + 7) & ~7; // 8-byte align, matches real layout

        Encoding.ASCII.GetBytes("FILE").CopyTo(record, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x04, 2), (ushort)usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x06, 2), (ushort)usaSize);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x10, 2), options.SequenceNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x12, 2), 1); // hard link count

        ushort flags = 0;
        if (options.IsInUse) flags |= 0x0001;
        if (options.IsDirectory) flags |= 0x0002;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x16, 2), flags);

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x1C, 4), (uint)options.RecordSize); // bytes allocated
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x2C, 4), options.RecordNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14, 2), (ushort)firstAttributeOffset);

        int pos = firstAttributeOffset;
        pos = WriteStandardInformation(record, pos);
        pos = WriteFileName(record, pos, options);
        pos = WriteResidentData(record, pos, options.ResidentData ?? []);

        int bytesInUse;
        if (options.OmitTerminator)
        {
            bytesInUse = pos;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos, 4), 0xFFFFFFFF);
            pos += 4;
            bytesInUse = pos;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18, 4), (uint)bytesInUse);

        ApplyUsa(record, usaOffset, usaSize, options.SectorSize, sectorCount, options.CorruptFixup);

        return record;
    }

    private static int WriteStandardInformation(byte[] record, int pos)
    {
        const int valueLength = 48;
        const int attrLength = 16 + 8 + valueLength;

        WriteAttributeCommonHeader(record, pos, typeCode: 0x10, length: attrLength, nonResident: false);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 0x10, 4), valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 0x14, 2), 0x18);

        int valueOffset = pos + 0x18;
        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x00, 8), now);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x08, 8), now);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x10, 8), now);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x18, 8), now);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(valueOffset + 0x20, 4), 0x20); // FILE_ATTRIBUTE_ARCHIVE

        return pos + attrLength;
    }

    private static int WriteFileName(byte[] record, int pos, Options options)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(options.FileName);
        int valueLength = 0x42 + nameBytes.Length;
        int attrLength = 16 + 8 + valueLength;

        WriteAttributeCommonHeader(record, pos, typeCode: 0x30, length: attrLength, nonResident: false);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 0x10, 4), (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 0x14, 2), 0x18);

        int valueOffset = pos + 0x18;
        ulong parentRef = (options.ParentRecordNumber & 0x0000FFFFFFFFFFFFUL) | ((ulong)options.ParentSequenceNumber << 48);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x00, 8), parentRef);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x28, 8), (ulong)(options.ResidentData?.Length ?? 0));
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(valueOffset + 0x30, 8), (ulong)(options.ResidentData?.Length ?? 0));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(valueOffset + 0x38, 4), options.IsDirectory ? 0x10u : 0x20u);
        record[valueOffset + 0x40] = (byte)options.FileName.Length;
        record[valueOffset + 0x41] = 1; // Win32 namespace
        nameBytes.CopyTo(record, valueOffset + 0x42);

        return pos + attrLength;
    }

    private static int WriteResidentData(byte[] record, int pos, byte[] data)
    {
        int attrLength = 16 + 8 + data.Length;

        WriteAttributeCommonHeader(record, pos, typeCode: 0x80, length: attrLength, nonResident: false);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 0x10, 4), (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 0x14, 2), 0x18);

        data.CopyTo(record, pos + 0x18);

        return pos + attrLength;
    }

    private static void WriteAttributeCommonHeader(byte[] record, int pos, uint typeCode, int length, bool nonResident)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 0x00, 4), typeCode);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 0x04, 4), (uint)length);
        record[pos + 0x08] = (byte)(nonResident ? 1 : 0);
        record[pos + 0x09] = 0; // name length
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 0x0A, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 0x0C, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 0x0E, 2), 0);
    }

    private static void ApplyUsa(byte[] record, int usaOffset, int usaSize, int sectorSize, int sectorCount, bool corrupt)
    {
        const ushort usn = 0x0001;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset, 2), usn);

        for (int i = 0; i < sectorCount; i++)
        {
            int sectorEnd = ((i + 1) * sectorSize) - 2;
            ushort realValue = (ushort)(0xBEEF + i);

            // Simulate the on-disk state: sector trailing bytes carry the USN marker.
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd, 2), corrupt && i == 0 ? (ushort)0xDEAD : usn);

            // The USA entry holds the real data that fixup should restore.
            int usaEntryOffset = usaOffset + 2 + (i * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaEntryOffset, 2), realValue);
        }
    }
}
