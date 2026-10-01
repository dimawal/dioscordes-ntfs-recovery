using NtfsRecovery.Core.Safety;

namespace NtfsRecovery.Cli;

/// <summary>The one place every write-capable command must pass through before touching the destination.</summary>
internal static class SafetyGate
{
    public static bool Check(string sourceDescription, string destinationPath, out string? error)
    {
        SourceDiskGuard guard = SourceDiskGuard.Create(sourceDescription);
        DestinationValidationResult result = DestinationValidator.Validate(guard, destinationPath);

        error = result.IsAllowed ? null : result.ErrorMessage;
        return result.IsAllowed;
    }
}
