using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Scan;

public sealed record MftCandidateValidationResult(bool IsValid, MftRecord? Record, MftRecordRejectReason? RejectReason, string? Detail)
{
    public static MftCandidateValidationResult Valid(MftRecord record) => new(true, record, null, null);

    public static MftCandidateValidationResult Invalid(MftRecordRejectReason reason, string detail) =>
        new(false, null, reason, detail);
}

/// <summary>
/// Thin scan-facing wrapper around <see cref="MftRecordParser"/>. Exists as its own type
/// (rather than having the scanner call the parser directly) so the carving-specific
/// acceptance policy -- "a FILE signature is only a candidate; it is not a record until
/// every structural check passes" -- lives in one obvious place, matching how the scan
/// statistics are reported to the operator.
/// </summary>
public static class MftCandidateValidator
{
    public static MftCandidateValidationResult Validate(ReadOnlySpan<byte> rawRecord, int bytesPerSector, long sourceOffset)
    {
        if (MftRecordParser.TryParse(rawRecord, bytesPerSector, sourceOffset, out MftRecord? record, out MftRecordParseFailure? failure))
        {
            return MftCandidateValidationResult.Valid(record!);
        }

        return MftCandidateValidationResult.Invalid(failure!.Reason, failure.Detail);
    }
}
