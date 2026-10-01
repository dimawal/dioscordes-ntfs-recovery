using System.Buffers.Binary;
using System.Text;

namespace NtfsRecovery.Core.Ntfs;

public sealed record MftRecordParseFailure(MftRecordRejectReason Reason, string Detail);

/// <summary>
/// Validates and fully parses a single MFT record from raw bytes. This is the shared
/// engine behind both the carving scanner (which only cares whether a "FILE" candidate
/// is genuine) and any direct record lookup -- there is exactly one code path that
/// understands the NTFS record layout, so a bug fixed here fixes both callers.
///
/// Pure and read-only with respect to the source: callers always pass a byte span that
/// is either a private copy or a scanner's reusable scratch buffer, never a view that
/// could be written back to the device. The fixup step mutates the given span in place,
/// which is safe precisely because that span's lifetime belongs entirely to the caller's
/// in-memory parsing of this one candidate.
/// </summary>
public static class MftRecordParser
{
    private const int MinimumHeaderSize = 0x30;

    public static bool TryParse(
        ReadOnlySpan<byte> rawRecord,
        int bytesPerSector,
        long sourceOffset,
        out MftRecord? record,
        out MftRecordParseFailure? failure)
    {
        record = null;
        failure = null;

        if (rawRecord.Length < MinimumHeaderSize || rawRecord.Length < bytesPerSector)
        {
            failure = new MftRecordParseFailure(MftRecordRejectReason.TooSmall, $"Record buffer is only {rawRecord.Length} bytes.");
            return false;
        }

        if (rawRecord[0] != (byte)'F' || rawRecord[1] != (byte)'I' || rawRecord[2] != (byte)'L' || rawRecord[3] != (byte)'E')
        {
            failure = new MftRecordParseFailure(MftRecordRejectReason.InvalidSignature, "Missing 'FILE' signature.");
            return false;
        }

        // BytesAllocated/BytesInUse live outside any sector-ending region, so they can
        // be validated against the raw (not-yet-fixed-up) buffer. Doing this before the
        // fixup gives a precise "buffer too small for the declared record" diagnosis
        // instead of a misleading USA failure caused by the same truncation.
        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(0x04, 2));
        ushort usaSize = BinaryPrimitives.ReadUInt16LittleEndian(rawRecord.Slice(0x06, 2));
        uint bytesAllocatedRaw = BinaryPrimitives.ReadUInt32LittleEndian(rawRecord.Slice(0x1C, 4));

        if (bytesAllocatedRaw == 0 || bytesAllocatedRaw > (uint)rawRecord.Length)
        {
            failure = new MftRecordParseFailure(
                MftRecordRejectReason.BytesAllocatedExceedsBuffer,
                $"BytesAllocated={bytesAllocatedRaw} exceeds buffer length {rawRecord.Length}.");
            return false;
        }

        // Fixup mutates in place; operate on a private copy so the caller's buffer
        // (which may be a scanner's reused scratch region) is never altered.
        byte[] buffer = rawRecord.ToArray();
        Span<byte> span = buffer;

        if (!UsaFixup.TryApply(span, bytesPerSector, usaOffset, usaSize, out string? usaError))
        {
            bool isGeometryIssue = usaError is not null && usaError.Contains("fit", StringComparison.OrdinalIgnoreCase)
                || usaError!.Contains("zero", StringComparison.OrdinalIgnoreCase)
                || usaError!.Contains("extends", StringComparison.OrdinalIgnoreCase);

            failure = new MftRecordParseFailure(
                isGeometryIssue ? MftRecordRejectReason.InvalidUsaGeometry : MftRecordRejectReason.FixupMismatch,
                usaError ?? "USA fixup failed.");
            return false;
        }

        ushort sequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x10, 2));
        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x14, 2));
        ushort recordFlags = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0x16, 2));
        uint bytesInUse = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x18, 4));
        uint bytesAllocated = bytesAllocatedRaw;
        ulong baseFileRecordRaw = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(0x20, 8));

        if (bytesInUse < MinimumHeaderSize || bytesInUse > bytesAllocated)
        {
            failure = new MftRecordParseFailure(
                MftRecordRejectReason.BytesInUseExceedsAllocated,
                $"BytesInUse={bytesInUse} is invalid against BytesAllocated={bytesAllocated}.");
            return false;
        }

        if (firstAttributeOffset < MinimumHeaderSize || firstAttributeOffset > bytesInUse)
        {
            failure = new MftRecordParseFailure(
                MftRecordRejectReason.InvalidFirstAttributeOffset,
                $"FirstAttributeOffset={firstAttributeOffset} is out of [{MinimumHeaderSize}, {bytesInUse}].");
            return false;
        }

        uint? selfReportedRecordNumber = null;
        if (usaOffset >= 0x2C + 4)
        {
            uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x2C, 4));
            if (candidate != 0 || firstAttributeOffset > 0x2C + 4)
                selfReportedRecordNumber = candidate;
        }

        var attributes = new List<NtfsAttribute>();
        int pos = firstAttributeOffset;
        bool terminatorFound = false;

        while (pos + 4 <= bytesInUse)
        {
            uint typeCode = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos, 4));
            if (typeCode == 0xFFFFFFFF)
            {
                terminatorFound = true;
                break;
            }

            if (pos + 8 > bytesInUse)
            {
                failure = new MftRecordParseFailure(MftRecordRejectReason.MalformedAttribute, "Attribute header truncated.");
                return false;
            }

            uint attrLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 4, 4));
            if (attrLength < 16 || pos + attrLength > bytesInUse || pos + attrLength > buffer.Length)
            {
                failure = new MftRecordParseFailure(
                    MftRecordRejectReason.AttributeOutOfBounds,
                    $"Attribute at {pos} declares length {attrLength}, exceeding record bounds.");
                return false;
            }

            if (!TryParseAttribute(span, pos, (int)attrLength, typeCode, out NtfsAttribute? attribute, out string? attrError))
            {
                failure = new MftRecordParseFailure(MftRecordRejectReason.MalformedAttribute, attrError ?? "Malformed attribute.");
                return false;
            }

            attributes.Add(attribute!);
            pos += (int)attrLength;
        }

        if (!terminatorFound)
        {
            failure = new MftRecordParseFailure(MftRecordRejectReason.MissingAttributeTerminator, "No 0xFFFFFFFF attribute terminator found before BytesInUse.");
            return false;
        }

        record = new MftRecord
        {
            SourceOffset = sourceOffset,
            RecordNumber = selfReportedRecordNumber,
            SequenceNumber = sequenceNumber,
            IsInUse = (recordFlags & (ushort)MftRecordFlags.InUse) != 0,
            IsDirectory = (recordFlags & (ushort)MftRecordFlags.Directory) != 0,
            BaseFileRecord = FileReference.Parse(baseFileRecordRaw),
            BytesInUse = bytesInUse,
            BytesAllocated = bytesAllocated,
            Attributes = attributes,
        };
        return true;
    }

    private static bool TryParseAttribute(
        ReadOnlySpan<byte> record,
        int pos,
        int attrLength,
        uint typeCode,
        out NtfsAttribute? attribute,
        out string? error)
    {
        attribute = null;
        error = null;

        if (pos + 0x10 > record.Length)
        {
            error = "Attribute common header truncated.";
            return false;
        }

        byte nonResidentFlag = record[pos + 0x08];
        byte nameLength = record[pos + 0x09];
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(pos + 0x0A, 2));
        ushort attributeId = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(pos + 0x0E, 2));

        string? name = null;
        if (nameLength > 0)
        {
            int nameByteLength = nameLength * 2;
            if (nameOffset + nameByteLength > attrLength)
            {
                error = "Attribute name extends past attribute bounds.";
                return false;
            }
            name = Encoding.Unicode.GetString(record.Slice(pos + nameOffset, nameByteLength));
        }

        bool isNonResident = nonResidentFlag != 0;
        var common = new AttributeCommon(ToKnownType(typeCode), typeCode, isNonResident, name, attributeId, (uint)attrLength);

        if (!isNonResident)
        {
            if (pos + 0x18 > record.Length)
            {
                error = "Resident attribute fixed fields truncated.";
                return false;
            }

            uint valueLength = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(pos + 0x10, 4));
            ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(pos + 0x14, 2));

            if ((long)valueOffset + valueLength > attrLength)
            {
                error = "Resident attribute value extends past attribute bounds.";
                return false;
            }

            ReadOnlySpan<byte> value = record.Slice(pos + valueOffset, (int)valueLength);
            attribute = BuildResidentAttribute(common, value);
            return true;
        }
        else
        {
            if (pos + 0x40 > record.Length)
            {
                error = "Non-resident attribute fixed fields truncated.";
                return false;
            }

            ushort dataRunOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(pos + 0x20, 2));
            ushort compressionUnit = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(pos + 0x22, 2));
            ulong allocatedSize = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(pos + 0x28, 8));
            ulong realSize = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(pos + 0x30, 8));
            ulong initializedSize = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(pos + 0x38, 8));

            if (dataRunOffset > attrLength)
            {
                error = "Non-resident data run offset extends past attribute bounds.";
                return false;
            }

            ReadOnlySpan<byte> runListBytes = record.Slice(pos + dataRunOffset, attrLength - dataRunOffset);
            List<DataRun> runs;
            try
            {
                runs = DataRunParser.Parse(runListBytes);
            }
            catch (InvalidDataException ex)
            {
                error = $"Malformed data run list: {ex.Message}";
                return false;
            }

            attribute = BuildNonResidentAttribute(common, runs, allocatedSize, realSize, initializedSize, compressionUnit);
            return true;
        }
    }

    private static NtfsAttribute BuildResidentAttribute(AttributeCommon common, ReadOnlySpan<byte> value)
    {
        try
        {
            return common.Type switch
            {
                NtfsAttributeType.StandardInformation => StandardInformationAttribute.Parse(value, common),
                NtfsAttributeType.FileName => FileNameAttribute.Parse(value, common),
                NtfsAttributeType.Data => DataAttribute.ParseResident(value, common),
                NtfsAttributeType.AttributeList => AttributeListAttribute.Parse(value, common),
                _ => new GenericAttribute
                {
                    Type = common.Type,
                    RawTypeCode = common.RawTypeCode,
                    IsNonResident = false,
                    Name = common.Name,
                    AttributeId = common.AttributeId,
                    AttributeLength = common.AttributeLength,
                    RawValue = value.ToArray(),
                },
            };
        }
        catch (InvalidDataException)
        {
            // A well-formed-looking but semantically broken attribute (e.g. truncated
            // $FILE_NAME) degrades to a generic blob rather than rejecting the whole record.
            return new GenericAttribute
            {
                Type = common.Type,
                RawTypeCode = common.RawTypeCode,
                IsNonResident = false,
                Name = common.Name,
                AttributeId = common.AttributeId,
                AttributeLength = common.AttributeLength,
                RawValue = value.ToArray(),
            };
        }
    }

    private static NtfsAttribute BuildNonResidentAttribute(
        AttributeCommon common,
        IReadOnlyList<DataRun> runs,
        ulong allocatedSize,
        ulong realSize,
        ulong initializedSize,
        ushort compressionUnit)
    {
        if (common.Type == NtfsAttributeType.Data)
        {
            return DataAttribute.ParseNonResident(common, runs, allocatedSize, realSize, initializedSize, compressionUnit);
        }

        return new GenericAttribute
        {
            Type = common.Type,
            RawTypeCode = common.RawTypeCode,
            IsNonResident = true,
            Name = common.Name,
            AttributeId = common.AttributeId,
            AttributeLength = common.AttributeLength,
            DataRuns = runs,
            AllocatedSize = allocatedSize,
            RealSize = realSize,
        };
    }

    private static NtfsAttributeType ToKnownType(uint rawTypeCode) =>
        Enum.IsDefined(typeof(NtfsAttributeType), rawTypeCode) ? (NtfsAttributeType)rawTypeCode : NtfsAttributeType.Unknown;
}
