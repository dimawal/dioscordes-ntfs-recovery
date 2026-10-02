using System.CommandLine;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Partitions;

namespace NtfsRecovery.Cli.Commands;

internal static class PartitionsCommand
{
    public static Command Build()
    {
        var imageOption = new Option<string?>("--image", "Path to a raw disk image file.");
        var diskOption = new Option<string?>("--disk", "Path to a physical drive (read-only).");

        var command = new Command("partitions", "List the MBR/GPT partitions found on a disk or image, so you don't have to guess the NTFS partition offset.")
        {
            imageOption, diskOption,
        };

        command.SetHandler((string? image, string? disk) => Task.FromResult(Run(image, disk)), imageOption, diskOption);

        return command;
    }

    private static int Run(string? imagePath, string? diskPath)
    {
        if (!DeviceFactory.TryOpen(imagePath, diskPath, out IBlockDevice? device, out string? error))
        {
            Console.Error.WriteLine($"ERROR: {error}");
            return 1;
        }

        using (device)
        {
            PartitionTableReadResult result = PartitionTableReader.Read(device!);

            if (result.Kind == PartitionTableKind.None)
            {
                Console.WriteLine("No MBR or GPT partition table found at the start of this disk/image.");
                return 0;
            }

            Console.WriteLine($"Partition table: {result.Kind}");
            Console.WriteLine();
            Console.WriteLine($"{"#",-3} {"Offset (bytes)",-18} {"Size",-14} {"Type",-28} {"NTFS?",-6} Label");

            foreach (PartitionInfo p in result.Partitions)
            {
                string sizeDisplay = FormatSize(p.LengthBytes);
                Console.WriteLine($"{p.Index,-3} {p.StartOffset,-18} {sizeDisplay,-14} {p.TypeDescription,-28} {(p.LooksLikeNtfs ? "yes" : "no"),-6} {p.VolumeLabel}");
            }

            Console.WriteLine();
            Console.WriteLine("Use a partition's Offset value with 'inspect --offset' or 'scan --partition-offset'.");
            return 0;
        }
    }

    private static string FormatSize(long bytes)
    {
        double gb = bytes / 1_000_000_000.0;
        return gb >= 1 ? $"{gb:N1} GB" : $"{bytes / 1_000_000.0:N1} MB";
    }
}
