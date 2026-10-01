using System.Buffers.Binary;
using System.Text;

namespace NtfsRecovery.Core.Ntfs;

public enum NtfsNameNamespace : byte
{
    Posix = 0,
    Win32 = 1,
    Dos = 2,
    Win32AndDos = 3,
}

/// <summary>
/// $FILE_NAME attribute (type 0x30). A single MFT record can carry several of these
/// (e.g. a long Win32 name plus a generated 8.3 DOS name); callers decide which one is
/// the "primary" display name via <see cref="NtfsNameNamespace"/> priority.
/// </summary>
public sealed class FileNameAttribute : NtfsAttribute
{
    public required FileReference ParentDirectory { get; init; }
    public required DateTime? CreationTime { get; init; }
    public required DateTime? ModificationTime { get; init; }
    public required DateTime? MftModificationTime { get; init; }
    public required DateTime? AccessTime { get; init; }
    public required ulong AllocatedSize { get; init; }
    public required ulong RealSize { get; init; }
    public required uint FileAttributeFlags { get; init; }
    public required NtfsNameNamespace NameNamespace { get; init; }
    public required string FileName { get; init; }

    public static FileNameAttribute Parse(ReadOnlySpan<byte> value, AttributeCommon common)
    {
        if (value.Length < 0x42)
            throw new InvalidDataException("$FILE_NAME value is smaller than its fixed fields.");

        ulong parentRaw = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x00, 8));
        ulong creation = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x08, 8));
        ulong modification = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x10, 8));
        ulong mftModification = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x18, 8));
        ulong access = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x20, 8));
        ulong allocatedSize = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x28, 8));
        ulong realSize = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x30, 8));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(0x38, 4));
        byte nameLengthChars = value[0x40];
        byte nameTypeRaw = value[0x41];

        int nameByteLength = nameLengthChars * 2;
        if (0x42 + nameByteLength > value.Length)
            throw new InvalidDataException("$FILE_NAME declares a name longer than the attribute value.");

        string name = Encoding.Unicode.GetString(value.Slice(0x42, nameByteLength));
        NtfsNameNamespace nameNamespace = nameTypeRaw <= 3 ? (NtfsNameNamespace)nameTypeRaw : NtfsNameNamespace.Posix;

        return new FileNameAttribute
        {
            Type = common.Type,
            RawTypeCode = common.RawTypeCode,
            IsNonResident = common.IsNonResident,
            Name = common.Name,
            AttributeId = common.AttributeId,
            AttributeLength = common.AttributeLength,
            ParentDirectory = FileReference.Parse(parentRaw),
            CreationTime = NtfsTime.TryToDateTimeUtc(creation),
            ModificationTime = NtfsTime.TryToDateTimeUtc(modification),
            MftModificationTime = NtfsTime.TryToDateTimeUtc(mftModification),
            AccessTime = NtfsTime.TryToDateTimeUtc(access),
            AllocatedSize = allocatedSize,
            RealSize = realSize,
            FileAttributeFlags = flags,
            NameNamespace = nameNamespace,
            FileName = name,
        };
    }

    /// <summary>Lower is better. Win32 and Win32&amp;DOS names are the ones users recognize.</summary>
    public int NamespacePriority => NameNamespace switch
    {
        NtfsNameNamespace.Win32 => 0,
        NtfsNameNamespace.Win32AndDos => 0,
        NtfsNameNamespace.Posix => 1,
        NtfsNameNamespace.Dos => 2,
        _ => 3,
    };
}
