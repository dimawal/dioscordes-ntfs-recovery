using System.CommandLine;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Recovery;

namespace NtfsRecovery.Cli.Commands;

internal static class RecoverCommand
{
    public static Command Build()
    {
        var pathArgument = new Argument<string>("path", "Virtual path to the file or directory to recover.");
        var destinationOption = new Option<string>("--destination", "Destination directory on a DIFFERENT physical disk from the source.") { IsRequired = true };
        var scanOption = new Option<string>("--scan", "Path to a scan database produced by 'scan --output'.") { IsRequired = true };
        var imageOption = new Option<string?>("--image", "Path to the source raw disk image file.");
        var diskOption = new Option<string?>("--disk", "Path to the source physical drive (read-only).");

        var command = new Command("recover", "Recover a file or an entire directory subtree to a destination folder.")
        {
            pathArgument, destinationOption, scanOption, imageOption, diskOption,
        };

        command.SetHandler((string path, string destination, string scan, string? image, string? disk) =>
            Task.FromResult(Run(path, destination, scan, image, disk)),
            pathArgument, destinationOption, scanOption, imageOption, diskOption);

        return command;
    }

    private static int Run(string path, string destinationRoot, string scanDbPath, string? imagePath, string? diskPath)
    {
        if (!TreeLoader.TryLoad(scanDbPath, out LoadedTree? loaded, out string? loadError))
        {
            Console.Error.WriteLine($"ERROR: {loadError}");
            return 1;
        }

        if (loaded!.Geometry is null)
        {
            Console.Error.WriteLine("ERROR: scan database has no stored geometry (produced by an older/incompatible scan).");
            return 1;
        }

        RecoveryNode? startNode = TreeNavigator.FindByPath(loaded.TreeResult.Root, path);
        if (startNode is null)
        {
            Console.Error.WriteLine($"ERROR: path not found: {path}");
            return 1;
        }

        string sourceDescription = imagePath ?? diskPath ?? string.Empty;
        Directory.CreateDirectory(destinationRoot);
        if (!SafetyGate.Check(sourceDescription, destinationRoot, out string? safetyError))
        {
            Console.Error.WriteLine(safetyError);
            return 3;
        }

        if (!DeviceFactory.TryOpen(imagePath, diskPath, out IBlockDevice? device, out string? openError))
        {
            Console.Error.WriteLine($"ERROR: {openError}");
            return 1;
        }

        using (device)
        {
            int healthy = 0, partial = 0, unsupported = 0, corrupt = 0, metadataOnly = 0;

            foreach (RecoveryNode fileNode in TreeNavigator.EnumerateFiles(startNode))
            {
                string relativePath = GetRelativePath(startNode, fileNode);
                string destinationPath = Path.Combine(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                FileExtractionResult result = FileExtractor.Extract(device!, loaded.Geometry.BytesPerCluster, fileNode.Record!.Dto, destinationPath);

                switch (result.Status)
                {
                    case FileExtractionStatus.Healthy: healthy++; break;
                    case FileExtractionStatus.Partial: partial++; break;
                    case FileExtractionStatus.Unsupported: unsupported++; break;
                    case FileExtractionStatus.CorruptRuns: corrupt++; break;
                    case FileExtractionStatus.MetadataOnly: metadataOnly++; break;
                }

                if (result.Status != FileExtractionStatus.Healthy)
                    Console.WriteLine($"[{result.Status}] {fileNode.FullPath}");
            }

            Console.WriteLine();
            Console.WriteLine($"Healthy: {healthy}, Partial: {partial}, Unsupported: {unsupported}, CorruptRuns: {corrupt}, MetadataOnly: {metadataOnly}");
            return 0;
        }
    }

    private static string GetRelativePath(RecoveryNode root, RecoveryNode node)
    {
        var segments = new List<string>();
        RecoveryNode? current = node;
        while (current is not null && current != root)
        {
            segments.Insert(0, current.Name);
            current = current.Parent;
        }
        if (segments.Count == 0)
            segments.Add(node.Name); // node == root: recovering a single file directly, not a subtree

        return Path.Combine([.. segments]);
    }
}
