using System.CommandLine;
using NtfsRecovery.Core.Recovery;

namespace NtfsRecovery.Cli.Commands;

internal static class TreeCommand
{
    public static Command Build()
    {
        var scanOption = new Option<string>("--scan", "Path to a scan database produced by 'scan --output'.") { IsRequired = true };
        var pathOption = new Option<string?>("--path", "Print only the subtree rooted at this virtual path (e.g. \\Mateus Campos).");
        var findKnownOption = new Option<string?>(
            "--find-known",
            "Comma-separated directory names to look up anywhere in the tree and report (operator-supplied comparison data, never hardcoded).");

        var command = new Command("tree", "Rebuild and print the virtual directory tree from a scan database.")
        {
            scanOption, pathOption, findKnownOption,
        };

        command.SetHandler((string scan, string? path, string? findKnown) => Task.FromResult(Run(scan, path, findKnown)),
            scanOption, pathOption, findKnownOption);

        return command;
    }

    private static int Run(string scanDbPath, string? path, string? findKnownCsv)
    {
        if (!TreeLoader.TryLoad(scanDbPath, out LoadedTree? loaded, out string? error))
        {
            Console.Error.WriteLine($"ERROR: {error}");
            return 1;
        }

        TreeBuildResult result = loaded!.TreeResult;

        Console.WriteLine($"Orphans: {result.OrphanCount}");
        Console.WriteLine($"Noname files: {result.NonameCount}");
        Console.WriteLine($"Cycles broken: {result.CycleCount}");
        Console.WriteLine($"Duplicate record numbers: {result.DuplicateRecordNumberCount}");
        Console.WriteLine($"Soft matches (attached without an exact parent sequence match): {result.SoftMatchCount}");
        Console.WriteLine();

        if (!string.IsNullOrEmpty(findKnownCsv))
        {
            foreach (string name in findKnownCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var matches = TreeNavigator.FindAllByName(result.Root, name).ToList();
                if (matches.Count == 0)
                {
                    Console.WriteLine($"Known directory NOT found: {name}");
                    continue;
                }

                foreach (RecoveryNode match in matches)
                {
                    Console.WriteLine($"Known directory found: {match.FullPath}");
                    Console.WriteLine($"MFT Record: {match.Record?.RecordNumber.ToString() ?? "(synthetic)"}");
                    Console.WriteLine($"Children: {match.Children.Count}");
                }
            }
            Console.WriteLine();
        }

        RecoveryNode startNode = result.Root;
        if (path is not null)
        {
            RecoveryNode? found = TreeNavigator.FindByPath(result.Root, path);
            if (found is null)
            {
                Console.Error.WriteLine($"ERROR: path not found: {path}");
                return 1;
            }
            startNode = found;
        }

        PrintTree(startNode, 0);
        return 0;
    }

    private static void PrintTree(RecoveryNode node, int depth)
    {
        string marker = node.IsDirectory ? "[D]" : "[F]";
        string label = node.Name.Length == 0 ? "\\" : node.Name;
        Console.WriteLine($"{new string(' ', depth * 2)}{marker} {label}");

        foreach (RecoveryNode child in node.Children.OrderBy(c => !c.IsDirectory).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            PrintTree(child, depth + 1);
    }
}
