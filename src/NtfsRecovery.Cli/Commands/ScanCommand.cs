using System.CommandLine;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Cli.Commands;

internal static class ScanCommand
{
    private const int BatchFlushSize = 5000;
    private const long CheckpointIntervalBytes = 64L * 1024 * 1024;

    public static Command Build()
    {
        var imageOption = new Option<string?>("--image", "Path to a raw disk image file.");
        var diskOption = new Option<string?>("--disk", "Path to a physical drive (read-only).");
        var partitionOffsetOption = new Option<long>("--partition-offset", () => 0, "Byte offset of the NTFS partition (where its boot sector lives).");
        var startOffsetOption = new Option<long?>("--start-offset", "Byte offset to start scanning from. Defaults to --partition-offset.");
        var lengthOption = new Option<long?>("--length", "Number of bytes to scan. Defaults to the rest of the volume per its boot sector.");
        var outputOption = new Option<string?>("--output", "Path to a SQLite database to persist results into (enables resuming).");
        var resumeOption = new Option<bool>("--resume", () => false, "Resume from the last checkpoint stored in --output, if present.");
        var blockSizeOption = new Option<int>("--block-size", () => 16 * 1024 * 1024, "Read block size in bytes.");

        var command = new Command("scan", "Scan a region for carved MFT records by 'FILE' signature.")
        {
            imageOption, diskOption, partitionOffsetOption, startOffsetOption, lengthOption, outputOption, resumeOption, blockSizeOption,
        };

        command.SetHandler(context =>
        {
            string? image = context.ParseResult.GetValueForOption(imageOption);
            string? disk = context.ParseResult.GetValueForOption(diskOption);
            long partitionOffset = context.ParseResult.GetValueForOption(partitionOffsetOption);
            long? startOffset = context.ParseResult.GetValueForOption(startOffsetOption);
            long? length = context.ParseResult.GetValueForOption(lengthOption);
            string? output = context.ParseResult.GetValueForOption(outputOption);
            bool resume = context.ParseResult.GetValueForOption(resumeOption);
            int blockSize = context.ParseResult.GetValueForOption(blockSizeOption);

            context.ExitCode = Run(image, disk, partitionOffset, startOffset, length, output, resume, blockSize);
        });

        return command;
    }

    private static int Run(
        string? imagePath, string? diskPath, long partitionOffset, long? startOffsetArg, long? lengthArg,
        string? outputPath, bool resume, int blockSize)
    {
        if (!DeviceFactory.TryOpen(imagePath, diskPath, out IBlockDevice? device, out string? openError))
        {
            Console.Error.WriteLine($"ERROR: {openError}");
            return 1;
        }

        using (device)
        {
            Span<byte> sectorBuf = stackalloc byte[NtfsBootSector.RawSize];
            if (device!.ReadAt(partitionOffset, sectorBuf) < NtfsBootSector.RawSize)
            {
                Console.Error.WriteLine($"ERROR: could not read boot sector at offset {partitionOffset}.");
                return 1;
            }

            NtfsBootSector bootSector;
            try
            {
                bootSector = NtfsBootSector.Parse(sectorBuf);
            }
            catch (InvalidNtfsBootSectorException ex)
            {
                Console.Error.WriteLine($"ERROR: not a valid NTFS boot sector at offset {partitionOffset}: {ex.Message}");
                return 1;
            }

            long scanStart = startOffsetArg ?? partitionOffset;
            long scanEnd = scanStart + (lengthArg ?? (bootSector.VolumeSizeInBytes - (scanStart - partitionOffset)));

            ScanStore? store = null;
            try
            {
                if (outputPath is not null)
                {
                    store = ScanStore.OpenOrCreate(outputPath);
                    store.SaveGeometry(new ScanGeometry(partitionOffset, bootSector.BytesPerSector, bootSector.BytesPerCluster, bootSector.MftRecordSize));

                    if (resume)
                    {
                        ScanCheckpoint? checkpoint = store.TryLoadCheckpoint();
                        if (checkpoint is { } cp && cp.ScanStartOffset == scanStart && cp.ScanEndOffset == scanEnd)
                        {
                            Console.WriteLine($"Resuming scan from offset {cp.LastScannedOffset} (of [{scanStart}, {scanEnd})).");
                            scanStart = cp.LastScannedOffset;
                        }
                        else
                        {
                            Console.WriteLine("WARNING: no matching checkpoint found for this range; starting fresh.");
                        }
                    }
                }

                var scanner = new MftScanner(device, bootSector.BytesPerSector, bootSector.MftRecordSize, blockSize);
                var batch = new List<ScanRecordDto>(BatchFlushSize);
                long lastCheckpointOffset = scanStart;

                using var cts = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelHandler = (_, e) =>
                {
                    e.Cancel = true;
                    Console.Error.WriteLine("\nStopping scan gracefully (checkpoint will be saved)...");
                    cts.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;

                void FlushBatch()
                {
                    if (store is not null && batch.Count > 0)
                    {
                        store.InsertRecords(batch);
                        batch.Clear();
                    }
                }

                var progress = new Progress<ScanProgress>(p =>
                {
                    long currentOffset = scanStart + p.BytesScanned;
                    if (store is not null && currentOffset - lastCheckpointOffset >= CheckpointIntervalBytes)
                    {
                        FlushBatch();
                        store.SaveCheckpoint(new ScanCheckpoint(startOffsetArg ?? partitionOffset, scanEnd, currentOffset));
                        lastCheckpointOffset = currentOffset;
                    }
                });

                ScanStatistics stats;
                try
                {
                    stats = scanner.Scan(
                        scanStart,
                        scanEnd - scanStart,
                        record =>
                        {
                            batch.Add(ScanRecordDto.FromMftRecord(record));
                            if (batch.Count >= BatchFlushSize)
                                FlushBatch();
                        },
                        progress,
                        cts.Token);
                }
                catch (OperationCanceledException)
                {
                    FlushBatch();
                    store?.SaveCheckpoint(new ScanCheckpoint(startOffsetArg ?? partitionOffset, scanEnd, lastCheckpointOffset));
                    Console.WriteLine("Scan stopped early; progress has been checkpointed.");
                    return 2;
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }

                FlushBatch();
                store?.SaveCheckpoint(new ScanCheckpoint(startOffsetArg ?? partitionOffset, scanEnd, scanEnd));

                PrintStats(stats);
                return 0;
            }
            finally
            {
                store?.Dispose();
            }
        }
    }

    private static void PrintStats(ScanStatistics stats)
    {
        Console.WriteLine($"Candidates: {stats.CandidatesFound}");
        Console.WriteLine($"Valid FILE records: {stats.ValidRecords}");
        Console.WriteLine($"Files: {stats.Files}");
        Console.WriteLine($"Directories: {stats.Directories}");
        Console.WriteLine($"In use: {stats.InUseRecords}");
        Console.WriteLine($"Rejected: {stats.Rejected}");

        if (stats.RejectReasons.Count > 0)
        {
            Console.WriteLine("Rejected by reason:");
            foreach ((MftRecordRejectReason reason, long count) in stats.RejectReasons.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"  {reason}: {count}");
        }
    }
}
