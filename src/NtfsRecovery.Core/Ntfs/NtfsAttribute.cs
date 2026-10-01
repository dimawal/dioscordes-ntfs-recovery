namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// Common header fields shared by every NTFS attribute, resident or not. Specific
/// attribute types subclass this and add their own parsed value.
/// </summary>
public abstract class NtfsAttribute
{
    public required NtfsAttributeType Type { get; init; }
    public required uint RawTypeCode { get; init; }
    public required bool IsNonResident { get; init; }
    public required string? Name { get; init; }
    public required ushort AttributeId { get; init; }
    public required uint AttributeLength { get; init; }
}

/// <summary>
/// Fallback representation for attribute types this engine does not specially interpret
/// (e.g. $INDEX_ROOT, $SECURITY_DESCRIPTOR, $OBJECT_ID), and also used for a non-resident
/// $ATTRIBUTE_LIST whose entries live in cluster data this parser cannot reach on its own
/// (resolving those requires an <see cref="IO.IBlockDevice"/>; see AttributeListResolver).
/// Exactly one of <see cref="RawValue"/> (resident) or <see cref="DataRuns"/> (non-resident)
/// is populated, matching <see cref="NtfsAttribute.IsNonResident"/>.
/// </summary>
public sealed class GenericAttribute : NtfsAttribute
{
    public byte[]? RawValue { get; init; }
    public IReadOnlyList<DataRun>? DataRuns { get; init; }
    public ulong AllocatedSize { get; init; }
    public ulong RealSize { get; init; }
}
