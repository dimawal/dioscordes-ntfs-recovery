using System.CommandLine;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Cli.Commands;

internal static class ExtractRecordCommand
{
    public static Command Build()
    {
        var recordOption = new Option<uint>("--record", "MFT record number to extract.") { IsRequired = true };
        var scanOption = new Option<string>("--scan", "Path to a scan database produced by 'scan --output'.") { IsRequired = true };
        var imageOption = new Option<string?>("--image", "Path to the source raw disk image file.");
        var diskOption = new Option<string?>("--disk", "Path to the source physical drive (read-only).");
        var outputOption = new Option<string>("--output", "Destination file path to write the recovered data to.") { IsRequired = true };

        var command = new Command("extract-record", "Recover a single file by its MFT record number.")
        {
            recordOption, scanOption, imageOption, diskOption, outputOption,
        };

        command.SetHandler((uint record, string scan, string? image, string? disk, string output) =>
            Task.FromResult(Run(record, scan, image, disk, output)),
            recordOption, scanOption, imageOption, diskOption, outputOption);

        return command;
    }

    private static int Run(uint recordNumber, string scanDbPath, string? imagePath, string? diskPath, string outputPath)
    {
        if (!TreeLoader.TryLoad(scanDbPath, out LoadedTree? loaded, out string? loadError))
        {
            Console.Error.WriteLine($"ERROR: {loadError}");
            return 1;
        }

        ScanRecordDto? record = loaded!.Records.FirstOrDefault(r => r.RecordNumber == recordNumber);
        if (record is null)
        {
            Console.Error.WriteLine($"ERROR: record {recordNumber} not found in {scanDbPath}.");
            return 1;
        }

        if (loaded.Geometry is null)
        {
            Console.Error.WriteLine("ERROR: scan database has no stored geometry (produced by an older/incompatible scan).");
            return 1;
        }

        string sourceDescription = imagePath ?? diskPath ?? string.Empty;
        if (!DeviceFactory.TryOpen(imagePath, diskPath, out IBlockDevice? device, out string? openError))
        {
            Console.Error.WriteLine($"ERROR: {openError}");
            return 1;
        }

        using (device)
        {
            string? destinationDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!SafetyGate.Check(sourceDescription, destinationDir ?? outputPath, out string? safetyError))
            {
                Console.Error.WriteLine(safetyError);
                return 3;
            }

            Directory.CreateDirectory(destinationDir!);

            FileExtractionResult result = FileExtractor.Extract(device!, loaded.Geometry.BytesPerCluster, record, outputPath);
            PrintResult(result, outputPath);
            return result.Status is FileExtractionStatus.Healthy ? 0 : 2;
        }
    }

    internal static void PrintResult(FileExtractionResult result, string outputPath)
    {
        Console.WriteLine($"Output:    {outputPath}");
        Console.WriteLine($"Status:    {result.Status}");
        Console.WriteLine($"Expected:  {result.ExpectedBytes:N0} bytes");
        Console.WriteLine($"Recovered: {result.RecoveredBytes:N0} bytes");
        if (result.InvalidRunCount > 0)
            Console.WriteLine($"Invalid runs: {result.InvalidRunCount}");
        if (result.Detail is not null)
            Console.WriteLine($"Detail:    {result.Detail}");
    }
}
