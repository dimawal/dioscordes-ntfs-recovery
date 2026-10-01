using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Scan;

/// <summary>Serializable, flattened view of one carved MFT record's primary $DATA stream and name.</summary>
public sealed record DataRunDto(long VcnStart, long ClusterCount, long? Lcn, bool IsSparse)
{
    public static DataRunDto From(DataRun run) => new(run.VcnStart, run.ClusterCount, run.Lcn, run.IsSparse);
    public DataRun ToDataRun() => new(VcnStart, ClusterCount, Lcn, IsSparse);
}

/// <summary>One $ATTRIBUTE_LIST entry, kept so attribute-list resolution can run as a
/// post-processing pass over persisted records (see <see cref="ScanRecordAttributeListResolver"/>)
/// instead of requiring the whole scan to stay in memory at once.</summary>
public sealed record AttributeListEntryDto(NtfsAttributeType Type, uint RawTypeCode, ushort AttributeId, ulong HostRecordNumber, ushort HostSequenceNumber, string? Name)
{
    public static AttributeListEntryDto From(AttributeListEntry entry) =>
        new(entry.Type, entry.RawTypeCode, entry.AttributeId, entry.BaseFileReference.RecordNumber, entry.BaseFileReference.SequenceNumber, entry.Name);
}

/// <summary>
/// The persisted, queryable unit of a scan result. Deliberately lighter than
/// <see cref="MftRecord"/>: it keeps only what Phases 4-10 (tree reconstruction,
/// listing, extraction) need -- one primary unnamed $DATA stream and no alternate data
/// streams. This is an explicit v1 scope limit, not an oversight: ADS can be added later
/// without changing this type's shape for the common case.
///
/// Extension records ARE persisted (not merged away at scan time), carrying their claimed
/// base identity (<see cref="BaseRecordNumber"/>/<see cref="BaseSequenceNumber"/>) and base
/// records keep their raw <see cref="AttributeListEntries"/>. This lets the scanner persist
/// records incrementally and checkpoint safely without needing the whole carved record set
/// in memory at once -- merging happens afterwards, as a deterministic pass over whatever
/// is in the database (see <see cref="ScanRecordAttributeListResolver"/>).
/// </summary>
public sealed class ScanRecordDto
{
    public uint RecordNumber { get; set; }
    public ushort SequenceNumber { get; set; }
    public bool IsInUse { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsExtensionRecord { get; set; }
    public ulong? BaseRecordNumber { get; set; }
    public ushort? BaseSequenceNumber { get; set; }
    public long SourceOffset { get; set; }
    public ulong? ParentRecordNumber { get; set; }
    public ushort? ParentSequenceNumber { get; set; }
    public string? Name { get; set; }
    public List<string> AlternateNames { get; set; } = [];
    public DateTime? CreationTime { get; set; }
    public DateTime? ModificationTime { get; set; }
    public DateTime? AccessTime { get; set; }
    public uint FileAttributeFlags { get; set; }

    public bool HasData { get; set; }
    public bool IsDataNonResident { get; set; }
    public bool IsDataCompressed { get; set; }
    public ulong LogicalSize { get; set; }
    public byte[]? ResidentData { get; set; }
    public List<DataRunDto> DataRuns { get; set; } = [];
    public List<AttributeListEntryDto> AttributeListEntries { get; set; } = [];

    public static ScanRecordDto FromMftRecord(MftRecord record)
    {
        FileNameAttribute? primary = record.PrimaryFileName;
        StandardInformationAttribute? std = record.StandardInformation;
        DataAttribute? data = record.DataAttributes.FirstOrDefault(d => d.Name is null);

        return new ScanRecordDto
        {
            RecordNumber = record.RecordNumber ?? 0,
            SequenceNumber = record.SequenceNumber,
            IsInUse = record.IsInUse,
            IsDirectory = record.IsDirectory,
            IsExtensionRecord = record.IsExtensionRecord,
            BaseRecordNumber = record.IsExtensionRecord ? record.BaseFileRecord.RecordNumber : null,
            BaseSequenceNumber = record.IsExtensionRecord ? record.BaseFileRecord.SequenceNumber : null,
            SourceOffset = record.SourceOffset,
            ParentRecordNumber = primary?.ParentDirectory.RecordNumber,
            ParentSequenceNumber = primary?.ParentDirectory.SequenceNumber,
            Name = primary?.FileName,
            AlternateNames = record.FileNames.Where(f => f != primary).Select(f => f.FileName).ToList(),
            CreationTime = std?.CreationTime,
            ModificationTime = std?.ModificationTime,
            AccessTime = std?.AccessTime,
            FileAttributeFlags = std?.FileAttributes ?? 0,
            HasData = data is not null,
            IsDataNonResident = data?.IsNonResident ?? false,
            IsDataCompressed = data?.IsCompressed ?? false,
            LogicalSize = data?.LogicalSize ?? 0,
            ResidentData = data is { IsNonResident: false } ? data.ResidentData : null,
            DataRuns = data is { IsNonResident: true, DataRuns: not null }
                ? data.DataRuns.Select(DataRunDto.From).ToList()
                : [],
            AttributeListEntries = record.AttributeList?.Entries.Select(AttributeListEntryDto.From).ToList() ?? [],
        };
    }
}
