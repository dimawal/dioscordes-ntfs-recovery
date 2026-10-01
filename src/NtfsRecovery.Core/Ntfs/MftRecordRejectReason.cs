namespace NtfsRecovery.Core.Ntfs;

/// <summary>Why a "FILE" candidate failed validation, used both by unit tests and scan statistics.</summary>
public enum MftRecordRejectReason
{
    TooSmall,
    InvalidSignature,
    InvalidUsaGeometry,
    FixupMismatch,
    InvalidFirstAttributeOffset,
    BytesInUseExceedsAllocated,
    BytesAllocatedExceedsBuffer,
    MissingAttributeTerminator,
    AttributeOutOfBounds,
    MalformedAttribute,
}
