using NtfsRecovery.Core.IO;

namespace NtfsRecovery.Cli;

/// <summary>Shared, read-only device-opening logic for every command that touches a source image or physical disk.</summary>
internal static class DeviceFactory
{
    public static bool TryOpen(string? imagePath, string? diskPath, out IBlockDevice? device, out string? error)
    {
        device = null;
        error = null;

        if (string.IsNullOrEmpty(imagePath) == string.IsNullOrEmpty(diskPath))
        {
            error = "specify exactly one of --image or --disk.";
            return false;
        }

        if (diskPath is not null && !OperatingSystem.IsWindows())
        {
            error = "--disk is only supported on Windows.";
            return false;
        }

        try
        {
            device = imagePath is not null
                ? new ImageFileReader(imagePath)
                : new RawDiskReader(diskPath!);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"failed to open source read-only: {ex.Message}";
            return false;
        }
    }
}
