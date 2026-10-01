using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Scan;

public sealed class ScanStatistics
{
    public long CandidatesFound { get; set; }
    public long ValidRecords { get; set; }
    public long Rejected { get; set; }
    public long InUseRecords { get; set; }
    public long Directories { get; set; }
    public long Files { get; set; }

    public Dictionary<MftRecordRejectReason, long> RejectReasons { get; } = new();

    public void RecordRejection(MftRecordRejectReason reason)
    {
        Rejected++;
        RejectReasons[reason] = RejectReasons.GetValueOrDefault(reason) + 1;
    }
}

public readonly record struct ScanProgress(long BytesScanned, long TotalBytes);
