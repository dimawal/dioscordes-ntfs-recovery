namespace NtfsRecovery.Core.Partitions;

public enum PartitionTableKind
{
    None,
    Mbr,
    Gpt,
}

/// <summary>One partition entry read from a disk's partition table (never written to).</summary>
public sealed record PartitionInfo(
    int Index,
    long StartOffset,
    long LengthBytes,
    string TypeDescription,
    bool LooksLikeNtfs,
    string? VolumeLabel);

public sealed record PartitionTableReadResult(PartitionTableKind Kind, IReadOnlyList<PartitionInfo> Partitions);
