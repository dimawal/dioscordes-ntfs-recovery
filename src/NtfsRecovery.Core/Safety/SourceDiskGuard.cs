namespace NtfsRecovery.Core.Safety;

/// <summary>Captures which physical disk(s) the recovery source occupies, resolved once when the source is opened.</summary>
public sealed class SourceDiskGuard
{
    public required string SourceDescription { get; init; }
    public required PhysicalDiskIdentity? DiskIdentity { get; init; }

    public static SourceDiskGuard Create(string sourcePath)
    {
        PhysicalDiskIdentity? identity = OperatingSystem.IsWindows()
            ? PhysicalDiskResolver.TryResolveForPath(sourcePath)
            : null;

        return new SourceDiskGuard { SourceDescription = sourcePath, DiskIdentity = identity };
    }
}
