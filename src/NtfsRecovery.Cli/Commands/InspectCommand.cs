using System.CommandLine;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Cli.Commands;

internal static class InspectCommand
{
    public static Command Build()
    {
        var imageOption = new Option<string?>("--image", "Path to a raw disk image file (.img, .dd, .raw).");
        var diskOption = new Option<string?>("--disk", "Path to a physical drive, e.g. \\\\.\\PhysicalDrive1. Opened strictly read-only.");
        var offsetOption = new Option<long>("--offset", () => 0, "Byte offset of the NTFS partition start within the image or disk.");

        var command = new Command("inspect", "Parse and print the NTFS boot sector parameters.")
        {
            imageOption, diskOption, offsetOption,
        };

        command.SetHandler((string? image, string? disk, long offset) => Task.FromResult(Run(image, disk, offset)),
            imageOption, diskOption, offsetOption);

        return command;
    }

    private static int Run(string? imagePath, string? diskPath, long offset)
    {
        if (offset < 0)
        {
            Console.Error.WriteLine("ERROR: --offset must not be negative.");
            return 1;
        }

        if (!DeviceFactory.TryOpen(imagePath, diskPath, out IBlockDevice? device, out string? openError))
        {
            Console.Error.WriteLine($"ERROR: {openError}");
            return 1;
        }

        using (device)
        {
            Span<byte> sector = stackalloc byte[NtfsBootSector.RawSize];
            int read = device!.ReadAt(offset, sector);
            if (read < NtfsBootSector.RawSize)
            {
                Console.Error.WriteLine($"ERROR: only read {read} of {NtfsBootSector.RawSize} boot sector bytes at offset {offset}.");
                return 1;
            }

            NtfsBootSector bootSector;
            try
            {
                bootSector = NtfsBootSector.Parse(sector);
            }
            catch (InvalidNtfsBootSectorException ex)
            {
                Console.Error.WriteLine($"ERROR: not a valid NTFS boot sector at offset {offset}: {ex.Message}");
                return 1;
            }

            PrintBootSector(device.SourceDescription, offset, bootSector);
            return 0;
        }
    }

    private static void PrintBootSector(string source, long offset, NtfsBootSector bootSector)
    {
        Console.WriteLine($"Source:               {source}");
        Console.WriteLine($"Partition offset:     {offset} bytes");
        Console.WriteLine($"Bytes per sector:     {bootSector.BytesPerSector}");
        Console.WriteLine($"Sectors per cluster:  {bootSector.SectorsPerCluster}");
        Console.WriteLine($"Bytes per cluster:    {bootSector.BytesPerCluster}");
        Console.WriteLine($"Total sectors:        {bootSector.TotalSectors}");
        Console.WriteLine($"Volume size:          {bootSector.VolumeSizeInBytes:N0} bytes");
        Console.WriteLine($"MFT LCN:              {bootSector.MftLcn}");
        Console.WriteLine($"MFT offset:           {bootSector.MftOffset} bytes");
        Console.WriteLine($"MFTMirr LCN:          {bootSector.MftMirrLcn}");
        Console.WriteLine($"MFTMirr offset:       {bootSector.MftMirrOffset} bytes");
        Console.WriteLine($"MFT record size:      {bootSector.MftRecordSize} bytes");
        Console.WriteLine($"Index record size:    {bootSector.IndexRecordSize} bytes");
        Console.WriteLine($"Volume serial number: {bootSector.VolumeSerialNumber:X16}");
    }
}
