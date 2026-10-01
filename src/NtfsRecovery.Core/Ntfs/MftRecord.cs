namespace NtfsRecovery.Core.Ntfs;

[Flags]
public enum MftRecordFlags : ushort
{
    None = 0,
    InUse = 0x0001,
    Directory = 0x0002,
}

/// <summary>
/// A fully parsed MFT record: header fields plus all attributes successfully decoded
/// from the record body. <see cref="RecordNumber"/> comes from the record's own
/// self-reported "number of this MFT record" header field (present since Windows XP),
/// which is the only reliable identity we have when carving records that are no longer
/// reachable through a healthy $MFT -- it must never be assumed to equal the record's
/// position within the scanned region.
/// </summary>
public sealed class MftRecord
{
    public required long SourceOffset { get; init; }
    public required uint? RecordNumber { get; init; }
    public required ushort SequenceNumber { get; init; }
    public required bool IsInUse { get; init; }
    public required bool IsDirectory { get; init; }
    public required FileReference BaseFileRecord { get; init; }
    public required uint BytesInUse { get; init; }
    public required uint BytesAllocated { get; init; }
    public required IReadOnlyList<NtfsAttribute> Attributes { get; init; }

    /// <summary>True when this record only extends another record's attribute set (via $ATTRIBUTE_LIST).</summary>
    public bool IsExtensionRecord => BaseFileRecord.RecordNumber != 0;

    public IEnumerable<FileNameAttribute> FileNames => Attributes.OfType<FileNameAttribute>();

    public IEnumerable<DataAttribute> DataAttributes => Attributes.OfType<DataAttribute>();

    public StandardInformationAttribute? StandardInformation =>
        Attributes.OfType<StandardInformationAttribute>().FirstOrDefault();

    public AttributeListAttribute? AttributeList =>
        Attributes.OfType<AttributeListAttribute>().FirstOrDefault();

    /// <summary>
    /// Picks the best display name among all $FILE_NAME attributes, preferring Win32
    /// (or Win32&amp;DOS) namespace names over plain DOS 8.3 names. All names remain
    /// available via <see cref="FileNames"/> for debugging.
    /// </summary>
    public FileNameAttribute? PrimaryFileName =>
        FileNames.OrderBy(f => f.NamespacePriority).FirstOrDefault();
}
