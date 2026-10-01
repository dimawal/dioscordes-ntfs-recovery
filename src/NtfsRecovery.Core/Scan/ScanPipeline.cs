using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Scan;

public sealed record ScanPipelineResult(ScanStatistics Statistics, IReadOnlyList<ScanRecordDto> Records);

/// <summary>
/// Runs the carving scanner and flattens every valid record it finds (including
/// extension records) to a <see cref="ScanRecordDto"/>, with no attribute-list merging.
/// This keeps each carved record independent and immediately persistable -- a long scan
/// can checkpoint and flush records in batches without needing the full result set in
/// memory. Call <see cref="ScanRecordAttributeListResolver.Resolve"/> afterwards (either
/// on this call's output directly, or after reloading everything from a
/// <see cref="ScanStore"/>) to merge fragmented $DATA runs back into their base records.
/// </summary>
public static class ScanPipeline
{
    public static ScanPipelineResult Run(
        MftScanner scanner,
        long startOffset,
        long length,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var records = new List<ScanRecordDto>();

        ScanStatistics stats = scanner.Scan(
            startOffset,
            length,
            record => records.Add(ScanRecordDto.FromMftRecord(record)),
            progress,
            cancellationToken);

        return new ScanPipelineResult(stats, records);
    }
}
