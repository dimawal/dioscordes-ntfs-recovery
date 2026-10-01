using System.CommandLine;
using NtfsRecovery.Core.Recovery;

namespace NtfsRecovery.Cli.Commands;

internal static class InfoCommand
{
    public static Command Build()
    {
        var pathArgument = new Argument<string>("path", "Virtual path to a file or directory.");
        var scanOption = new Option<string>("--scan", "Path to a scan database produced by 'scan --output'.") { IsRequired = true };

        var command = new Command("info", "Print full metadata for a file or directory.") { pathArgument, scanOption };

        command.SetHandler((string path, string scan) => Task.FromResult(Run(path, scan)), pathArgument, scanOption);

        return command;
    }

    private static int Run(string path, string scanDbPath)
    {
        if (!TreeLoader.TryLoad(scanDbPath, out LoadedTree? loaded, out string? error))
        {
            Console.Error.WriteLine($"ERROR: {error}");
            return 1;
        }

        RecoveryNode? node = TreeNavigator.FindByPath(loaded!.TreeResult.Root, path);
        if (node is null)
        {
            Console.Error.WriteLine($"ERROR: path not found: {path}");
            return 1;
        }

        Console.WriteLine($"Path:              {node.FullPath}");
        Console.WriteLine($"Type:              {(node.IsDirectory ? "Directory" : "File")}");

        if (node.Record is null)
        {
            Console.WriteLine("(synthetic node; no MFT record)");
            return 0;
        }

        VirtualMftRecord record = node.Record;
        Console.WriteLine($"MFT Record:        {record.RecordNumber}");
        Console.WriteLine($"Sequence Number:   {record.SequenceNumber}");
        Console.WriteLine($"State:             {(record.IsInUse ? "in-use" : "deleted")}");
        Console.WriteLine($"Source offset:     {record.SourceOffset}");
        Console.WriteLine($"Alternate names:   {(record.AlternateNames.Count == 0 ? "(none)" : string.Join(", ", record.AlternateNames))}");
        Console.WriteLine($"Created:           {record.Dto.CreationTime?.ToString("u") ?? "(unknown)"}");
        Console.WriteLine($"Modified:          {record.Dto.ModificationTime?.ToString("u") ?? "(unknown)"}");
        Console.WriteLine($"Accessed:          {record.Dto.AccessTime?.ToString("u") ?? "(unknown)"}");
        Console.WriteLine($"File attributes:   0x{record.Dto.FileAttributeFlags:X}");

        if (!node.IsDirectory)
        {
            Console.WriteLine($"Has $DATA:         {record.Dto.HasData}");
            Console.WriteLine($"Non-resident:      {record.Dto.IsDataNonResident}");
            Console.WriteLine($"Compressed:        {record.Dto.IsDataCompressed}");
            Console.WriteLine($"Logical size:      {record.Dto.LogicalSize:N0} bytes");
            Console.WriteLine($"Data runs:         {record.Dto.DataRuns.Count}");
        }
        else
        {
            Console.WriteLine($"Children:          {node.Children.Count}");
        }

        return 0;
    }
}
