using System.Buffers.Binary;

namespace NtfsRecovery.Core.Ntfs;

public sealed class StandardInformationAttribute : NtfsAttribute
{
    public required DateTime? CreationTime { get; init; }
    public required DateTime? ModificationTime { get; init; }
    public required DateTime? MftModificationTime { get; init; }
    public required DateTime? AccessTime { get; init; }
    public required uint FileAttributes { get; init; }

    public static StandardInformationAttribute Parse(ReadOnlySpan<byte> value, AttributeCommon common)
    {
        if (value.Length < 0x24)
            throw new InvalidDataException("$STANDARD_INFORMATION value is smaller than its fixed fields.");

        ulong creation = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x00, 8));
        ulong modification = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x08, 8));
        ulong mftModification = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x10, 8));
        ulong access = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(0x18, 8));
        uint fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(0x20, 4));

        return new StandardInformationAttribute
        {
            Type = common.Type,
            RawTypeCode = common.RawTypeCode,
            IsNonResident = common.IsNonResident,
            Name = common.Name,
            AttributeId = common.AttributeId,
            AttributeLength = common.AttributeLength,
            CreationTime = NtfsTime.TryToDateTimeUtc(creation),
            ModificationTime = NtfsTime.TryToDateTimeUtc(modification),
            MftModificationTime = NtfsTime.TryToDateTimeUtc(mftModification),
            AccessTime = NtfsTime.TryToDateTimeUtc(access),
            FileAttributes = fileAttributes,
        };
    }
}
