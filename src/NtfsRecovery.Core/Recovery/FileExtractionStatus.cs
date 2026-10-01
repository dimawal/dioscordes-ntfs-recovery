namespace NtfsRecovery.Core.Recovery;

public enum FileExtractionStatus
{
    Healthy,
    Partial,
    MetadataOnly,
    Unsupported,
    CorruptRuns,
}

public sealed record FileExtractionResult(
    FileExtractionStatus Status,
    ulong ExpectedBytes,
    ulong RecoveredBytes,
    int InvalidRunCount,
    string? Detail);
