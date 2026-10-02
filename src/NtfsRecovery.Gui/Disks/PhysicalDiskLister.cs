using System.Management;

namespace NtfsRecovery.Gui.Disks;

/// <summary>Enumerates physical disks via WMI (Win32_DiskDrive) so the user can pick one by model/size instead of guessing a device path.</summary>
public static class PhysicalDiskLister
{
    public static List<PhysicalDiskInfo> List()
    {
        var disks = new List<PhysicalDiskInfo>();

        using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Index, Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive");
        foreach (ManagementBaseObject obj in searcher.Get())
        {
            using ManagementObject disk = (ManagementObject)obj;

            string deviceId = disk["DeviceID"]?.ToString() ?? "";
            if (deviceId.Length == 0)
                continue;

            int index = Convert.ToInt32(disk["Index"] ?? 0);
            string model = disk["Model"]?.ToString() ?? "(desconhecido)";
            long size = disk["Size"] is null ? 0 : Convert.ToInt64(disk["Size"]);
            string interfaceType = disk["InterfaceType"]?.ToString() ?? "";
            string mediaType = disk["MediaType"]?.ToString() ?? "";

            disks.Add(new PhysicalDiskInfo(deviceId, index, model, size, interfaceType, mediaType));
        }

        disks.Sort((a, b) => a.Index.CompareTo(b.Index));
        return disks;
    }
}
