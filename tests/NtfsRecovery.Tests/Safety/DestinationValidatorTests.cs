using NtfsRecovery.Core.Safety;
using Xunit;

namespace NtfsRecovery.Tests.Safety;

public class DestinationValidatorTests
{
    private static PhysicalDiskIdentity Disk(params uint[] numbers) => new(new HashSet<uint>(numbers));

    [Fact]
    public void Evaluate_DifferentDisks_IsAllowed()
    {
        DestinationValidationResult result = DestinationValidator.Evaluate(Disk(0), Disk(1), "source", "dest");

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Evaluate_SameDisk_IsRejectedWithRequiredMessage()
    {
        DestinationValidationResult result = DestinationValidator.Evaluate(Disk(1), Disk(1), "source", "dest");

        Assert.False(result.IsAllowed);
        Assert.Contains("Recovery destination is located on the source physical disk.", result.ErrorMessage);
        Assert.Contains("Writing recovered data to the source disk may destroy unrecovered files.", result.ErrorMessage);
    }

    [Fact]
    public void Evaluate_OverlappingSpannedVolume_IsRejected()
    {
        // Source lives on disk 2; destination is a spanned volume covering disks 1-3.
        DestinationValidationResult result = DestinationValidator.Evaluate(Disk(2), Disk(1, 2, 3), "source", "dest");

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void Evaluate_UnknownSourceIdentity_FailsClosed()
    {
        DestinationValidationResult result = DestinationValidator.Evaluate(null, Disk(1), "source", "dest");

        Assert.False(result.IsAllowed);
        Assert.Contains("source", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_UnknownDestinationIdentity_FailsClosed()
    {
        DestinationValidationResult result = DestinationValidator.Evaluate(Disk(1), null, "source", "dest");

        Assert.False(result.IsAllowed);
        Assert.Contains("destination", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_BothUnknown_FailsClosed()
    {
        DestinationValidationResult result = DestinationValidator.Evaluate(null, null, "source", "dest");

        Assert.False(result.IsAllowed);
    }
}
