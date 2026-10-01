namespace NtfsRecovery.Core.Ntfs;

/// <summary>One decoded entry of a non-resident $DATA run list.</summary>
/// <param name="VcnStart">First virtual cluster number covered by this run.</param>
/// <param name="ClusterCount">Number of clusters covered by this run.</param>
/// <param name="Lcn">
/// Absolute logical cluster number where this run's data begins, or null when
/// <see cref="IsSparse"/> is true (sparse runs have no backing storage).
/// </param>
/// <param name="IsSparse">True when this run is a sparse hole (reads as zeros).</param>
public readonly record struct DataRun(long VcnStart, long ClusterCount, long? Lcn, bool IsSparse)
{
    public long VcnEndExclusive => VcnStart + ClusterCount;
}
