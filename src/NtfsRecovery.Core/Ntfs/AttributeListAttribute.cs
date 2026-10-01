using System.Buffers.Binary;
using System.Text;

namespace NtfsRecovery.Core.Ntfs;

/// <summary>One entry of an $ATTRIBUTE_LIST, pointing at an attribute possibly stored in an extension record.</summary>
public sealed record AttributeListEntry(
    NtfsAttributeType Type,
    uint RawTypeCode,
    ushort AttributeId,
    ulong StartingVcn,
    FileReference BaseFileReference,
    string? Name);

/// <summary>
/// $ATTRIBUTE_LIST (type 0x20). Present when a record's attributes don't fit in a
/// single MFT entry; each entry points at the base record or an extension record that
/// actually holds the attribute (see Phase 9 / AttributeListResolver).
/// </summary>
public sealed class AttributeListAttribute : NtfsAttribute
{
    public required IReadOnlyList<AttributeListEntry> Entries { get; init; }

    public static AttributeListAttribute Parse(ReadOnlySpan<byte> value, AttributeCommon common)
    {
        var entries = new List<AttributeListEntry>();
        int pos = 0;

        while (pos + 0x1A <= value.Length)
        {
            uint typeCode = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(pos + 0x00, 4));
            ushort recordLength = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(pos + 0x04, 2));
            byte nameLength = value[pos + 0x06];
            byte nameOffset = value[pos + 0x07];
            ulong startingVcn = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(pos + 0x08, 8));
            ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(pos + 0x10, 8));
            ushort attributeId = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(pos + 0x18, 2));

            if (recordLength < 0x1A || pos + recordLength > value.Length)
                break; // malformed entry; stop rather than read garbage

            string? name = null;
            if (nameLength > 0 && pos + nameOffset + (nameLength * 2) <= value.Length)
            {
                name = Encoding.Unicode.GetString(value.Slice(pos + nameOffset, nameLength * 2));
            }

            entries.Add(new AttributeListEntry(
                ToKnownType(typeCode),
                typeCode,
                attributeId,
                startingVcn,
                FileReference.Parse(baseRef),
                name));

            pos += recordLength;
        }

        return new AttributeListAttribute
        {
            Type = common.Type,
            RawTypeCode = common.RawTypeCode,
            IsNonResident = common.IsNonResident,
            Name = common.Name,
            AttributeId = common.AttributeId,
            AttributeLength = common.AttributeLength,
            Entries = entries,
        };
    }

    private static NtfsAttributeType ToKnownType(uint rawTypeCode) =>
        Enum.IsDefined(typeof(NtfsAttributeType), rawTypeCode) ? (NtfsAttributeType)rawTypeCode : NtfsAttributeType.Unknown;
}
