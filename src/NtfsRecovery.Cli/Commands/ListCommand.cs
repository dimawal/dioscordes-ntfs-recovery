using System.CommandLine;
using NtfsRecovery.Core.Recovery;

namespace NtfsRecovery.Cli.Commands;

internal static class ListCommand
{
    public static Command Build()
    {
        var pathArgument = new Argument<string>("path", () => "\\", "Virtual directory path to list.");
        var scanOption = new Option<string>("--scan", "Path to a scan database produced by 'scan --output'.") { IsRequired = true };

        var command = new Command("list", "List the children of a virtual directory.") { pathArgument, scanOption };

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

        if (!node.IsDirectory)
        {
            Console.Error.WriteLine($"ERROR: not a directory: {path}");
            return 1;
        }

        Console.WriteLine($"{"Type",-5} {"Name",-40} {"Size",12} {"Record",8} {"State",-9} {"Runs",5}");
        foreach (RecoveryNode child in node.Children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            string type = child.IsDirectory ? "DIR" : "FILE";
            string record = child.Record?.RecordNumber.ToString() ?? "-";
            string state = child.Record is null ? "synthetic" : child.Record.IsInUse ? "in-use" : "deleted";
            ulong size = child.Record?.Dto.LogicalSize ?? 0;
            int runs = child.Record?.Dto.DataRuns.Count ?? 0;

            Console.WriteLine($"{type,-5} {child.Name,-40} {size,12} {record,8} {state,-9} {runs,5}");
        }

        return 0;
    }
}
