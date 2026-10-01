namespace NtfsRecovery.Core.Ntfs;

/// <summary>Common header fields decoded once by <see cref="MftRecordParser"/> and handed to each typed attribute parser.</summary>
public readonly record struct AttributeCommon(
    NtfsAttributeType Type,
    uint RawTypeCode,
    bool IsNonResident,
    string? Name,
    ushort AttributeId,
    uint AttributeLength);
