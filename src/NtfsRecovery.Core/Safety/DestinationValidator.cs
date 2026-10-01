namespace NtfsRecovery.Core.Safety;

public sealed record DestinationValidationResult(bool IsAllowed, string? ErrorMessage)
{
    public static DestinationValidationResult Allowed() => new(true, null);
    public static DestinationValidationResult Rejected(string message) => new(false, message);
}

/// <summary>
/// The mandatory gate before any byte of recovered data is written: a destination is
/// only accepted once its physical disk is confirmed to differ from the source's. Any
/// failure to determine either side's physical disk is treated as a rejection -- this
/// gate fails closed, never open, per the project's non-negotiable safety requirement.
/// </summary>
public static class DestinationValidator
{
    public const string SameDiskMessage =
        "ERROR:\nRecovery destination is located on the source physical disk.\nWriting recovered data to the source disk may destroy unrecovered files.";

    /// <summary>Pure decision logic, independent of how the identities were resolved -- fully unit-testable.</summary>
    public static DestinationValidationResult Evaluate(
        PhysicalDiskIdentity? sourceIdentity,
        PhysicalDiskIdentity? destinationIdentity,
        string sourceDescription,
        string destinationDescription)
    {
        if (sourceIdentity is null)
        {
            return DestinationValidationResult.Rejected(
                $"Could not confirm the physical disk backing the source ('{sourceDescription}'). " +
                "Refusing to recover without that guarantee. Querying disk identity typically requires running as Administrator.");
        }

        if (destinationIdentity is null)
        {
            return DestinationValidationResult.Rejected(
                $"Could not confirm the physical disk backing the destination ('{destinationDescription}'). " +
                "Refusing to recover without that guarantee. Querying disk identity typically requires running as Administrator.");
        }

        if (sourceIdentity.Value.Overlaps(destinationIdentity.Value))
            return DestinationValidationResult.Rejected(SameDiskMessage);

        return DestinationValidationResult.Allowed();
    }

    public static DestinationValidationResult Validate(SourceDiskGuard source, string destinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DestinationValidationResult.Rejected(
                "Physical disk identification is only implemented for Windows. Refusing to recover without that guarantee.");
        }

        PhysicalDiskIdentity? destinationIdentity = PhysicalDiskResolver.TryResolveForPath(destinationPath);
        return Evaluate(source.DiskIdentity, destinationIdentity, source.SourceDescription, destinationPath);
    }
}
