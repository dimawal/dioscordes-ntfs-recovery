namespace NtfsRecovery.Gui.Disks;

/// <summary>One physical disk reported by WMI, used to let the user pick a device instead of typing \\.\PhysicalDriveN by hand.</summary>
public sealed record PhysicalDiskInfo(string DeviceId, int Index, string Model, long SizeBytes, string InterfaceType, string MediaType);
