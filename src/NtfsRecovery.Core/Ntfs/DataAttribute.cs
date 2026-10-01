namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// $DATA attribute (type 0x80). Exactly one of (<see cref="ResidentData"/>) or
/// (<see cref="DataRuns"/>, non-null) is populated, matching <see cref="NtfsAttribute.IsNonResident"/>.
/// </summary>
public sealed class DataAttribute : NtfsAttribute
{
    public byte[]? ResidentData { get; init; }

    public IReadOnlyList<DataRun>? DataRuns { get; init; }
    public ulong AllocatedSize { get; init; }
    public ulong RealSize { get; init; }
    public ulong InitializedSize { get; init; }
    public ushort CompressionUnit { get; init; }

    public bool IsCompressed => CompressionUnit != 0;

    public ulong LogicalSize => IsNonResident ? RealSize : (ulong)(ResidentData?.Length ?? 0);

    public static DataAttribute ParseResident(ReadOnlySpan<byte> value, AttributeCommon common) => new()
    {
        Type = common.Type,
        RawTypeCode = common.RawTypeCode,
        IsNonResident = common.IsNonResident,
        Name = common.Name,
        AttributeId = common.AttributeId,
        AttributeLength = common.AttributeLength,
        ResidentData = value.ToArray(),
    };

    public static DataAttribute ParseNonResident(
        AttributeCommon common,
        IReadOnlyList<DataRun> dataRuns,
        ulong allocatedSize,
        ulong realSize,
        ulong initializedSize,
        ushort compressionUnit) => new()
    {
        Type = common.Type,
        RawTypeCode = common.RawTypeCode,
        IsNonResident = common.IsNonResident,
        Name = common.Name,
        AttributeId = common.AttributeId,
        AttributeLength = common.AttributeLength,
        DataRuns = dataRuns,
        AllocatedSize = allocatedSize,
        RealSize = realSize,
        InitializedSize = initializedSize,
        CompressionUnit = compressionUnit,
    };
}
