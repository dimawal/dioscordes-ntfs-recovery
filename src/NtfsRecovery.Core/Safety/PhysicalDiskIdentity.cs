namespace NtfsRecovery.Core.Safety;

/// <summary>
/// The set of physical disk numbers backing a path. Usually a single disk, but a
/// spanned/RAID volume can span several -- any overlap with another identity's disks is
/// what the recovery safety gate treats as "same physical disk".
/// </summary>
public readonly struct PhysicalDiskIdentity
{
    public IReadOnlySet<uint> DiskNumbers { get; }

    public PhysicalDiskIdentity(IReadOnlySet<uint> diskNumbers)
    {
        if (diskNumbers.Count == 0)
            throw new ArgumentException("A physical disk identity must reference at least one disk.", nameof(diskNumbers));
        DiskNumbers = diskNumbers;
    }

    public bool Overlaps(PhysicalDiskIdentity other) => DiskNumbers.Overlaps(other.DiskNumbers);

    public override string ToString() => string.Join(",", DiskNumbers.OrderBy(n => n).Select(n => $"PhysicalDrive{n}"));
}
