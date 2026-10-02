using NtfsRecovery.Gui.Disks;

namespace NtfsRecovery.Gui.ViewModels;

/// <summary>One row in the physical disk list, letting the user pick a device instead of typing \\.\PhysicalDriveN by hand.</summary>
public sealed class DiskItemViewModel(PhysicalDiskInfo info)
{
    public string DeviceId { get; } = info.DeviceId;
    public string SizeDisplay { get; } = FormatSize(info.SizeBytes);

    public string Display =>
        $"{DeviceId}  {info.Model}  {SizeDisplay}" +
        (string.IsNullOrEmpty(info.InterfaceType) ? "" : $"  [{info.InterfaceType}]") +
        (string.IsNullOrEmpty(info.MediaType) ? "" : $"  {info.MediaType}");

    private static string FormatSize(long bytes)
    {
        double gb = bytes / 1_000_000_000.0;
        return gb >= 1 ? $"{gb:N1} GB" : $"{bytes / 1_000_000.0:N1} MB";
    }

    public override string ToString() => Display;
}
