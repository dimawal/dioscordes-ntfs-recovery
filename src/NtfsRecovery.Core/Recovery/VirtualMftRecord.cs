using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Core.Recovery;

/// <summary>
/// A lightweight, index-friendly projection of a carved record. Wraps a
/// <see cref="ScanRecordDto"/> so the same tree-building logic works whether records
/// came straight from a live scan or were reloaded from a persisted scan database.
/// </summary>
public sealed class VirtualMftRecord
{
    public required ScanRecordDto Dto { get; init; }

    public uint RecordNumber => Dto.RecordNumber;
    public ushort SequenceNumber => Dto.SequenceNumber;
    public bool IsInUse => Dto.IsInUse;
    public bool IsDirectory => Dto.IsDirectory;
    public bool IsExtensionRecord => Dto.IsExtensionRecord;
    public long SourceOffset => Dto.SourceOffset;
    public string? Name => Dto.Name;
    public IReadOnlyList<string> AlternateNames => Dto.AlternateNames;

    public FileReference? ParentRecordReference =>
        Dto.ParentRecordNumber is ulong parentRecordNumber
            ? new FileReference(parentRecordNumber, Dto.ParentSequenceNumber ?? 0)
            : null;

    public static VirtualMftRecord? FromMftRecord(MftRecord record)
    {
        if (record.RecordNumber is null)
            return null; // no reliable identity: cannot be referenced as a parent, excluded from the index

        return new VirtualMftRecord { Dto = ScanRecordDto.FromMftRecord(record) };
    }

    public static VirtualMftRecord FromDto(ScanRecordDto dto) => new() { Dto = dto };
}
