using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Core.Recovery;

/// <summary>
/// Reconstructs a file's bytes from a carved record's $DATA attribute and writes them to
/// a destination chosen by the caller. Reads the source device strictly read-only
/// (through <see cref="IBlockDevice"/>, which has no write members); this type never
/// touches the source and only ever opens <paramref name="destinationPath"/> for writing.
///
/// NTFS compression is explicitly out of scope for this version: compressed streams are
/// reported as <see cref="FileExtractionStatus.Unsupported"/> rather than guessed at.
/// </summary>
public static class FileExtractor
{
    private const int CopyBufferSize = 1024 * 1024;

    public static FileExtractionResult Extract(
        IBlockDevice sourceDevice,
        int bytesPerCluster,
        ScanRecordDto record,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!record.HasData)
            return new FileExtractionResult(FileExtractionStatus.MetadataOnly, 0, 0, 0, "Record has no $DATA attribute.");

        if (record.IsDataCompressed)
            return new FileExtractionResult(FileExtractionStatus.Unsupported, record.LogicalSize, 0, 0, "NTFS-compressed streams are not supported in this version.");

        if (!record.IsDataNonResident)
        {
            byte[] bytes = record.ResidentData ?? [];
            File.WriteAllBytes(destinationPath, bytes);
            return new FileExtractionResult(FileExtractionStatus.Healthy, (ulong)bytes.Length, (ulong)bytes.Length, 0, null);
        }

        return ExtractNonResident(sourceDevice, bytesPerCluster, record, destinationPath, cancellationToken);
    }

    private static FileExtractionResult ExtractNonResident(
        IBlockDevice sourceDevice,
        int bytesPerCluster,
        ScanRecordDto record,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ulong logicalSize = record.LogicalSize;
        List<DataRunDto> runs = [.. record.DataRuns.OrderBy(r => r.VcnStart)];

        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] copyBuffer = new byte[CopyBufferSize];

        ulong written = 0;
        ulong genuinelyRecovered = 0;
        int invalidRuns = 0;

        foreach (DataRunDto run in runs)
        {
            if (written >= logicalSize)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            long runByteLength = run.ClusterCount * (long)bytesPerCluster;
            ulong remainingLogical = logicalSize - written;
            long bytesForThisRun = Math.Min(runByteLength, (long)remainingLogical);
            if (bytesForThisRun <= 0)
                continue;

            if (run.IsSparse)
            {
                WriteZeros(output, bytesForThisRun);
                written += (ulong)bytesForThisRun;
                genuinelyRecovered += (ulong)bytesForThisRun; // sparse zeros are the correct value, not data loss
                continue;
            }

            if (run.Lcn is null || run.Lcn < 0)
            {
                invalidRuns++;
                WriteZeros(output, bytesForThisRun);
                written += (ulong)bytesForThisRun;
                continue;
            }

            long sourceOffset = run.Lcn.Value * bytesPerCluster;
            long remaining = bytesForThisRun;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkSize = (int)Math.Min(remaining, copyBuffer.Length);
                int actuallyRead = sourceDevice.ReadAt(sourceOffset, copyBuffer.AsSpan(0, chunkSize));
                output.Write(copyBuffer, 0, chunkSize);

                genuinelyRecovered += (ulong)actuallyRead;
                if (actuallyRead < chunkSize)
                {
                    // Short read from the source (e.g. this cluster lies outside an image
                    // that only covers part of the volume): the gap in copyBuffer beyond
                    // actuallyRead is still zero from allocation, so the output stays a
                    // correctly-sized file with a zero-filled hole where data is missing.
                }

                remaining -= chunkSize;
                sourceOffset += chunkSize;
                written += (ulong)chunkSize;
            }
        }

        if (written < logicalSize)
        {
            WriteZeros(output, (long)(logicalSize - written));
            written = logicalSize;
        }

        FileExtractionStatus status = invalidRuns > 0
            ? FileExtractionStatus.CorruptRuns
            : genuinelyRecovered < logicalSize
                ? FileExtractionStatus.Partial
                : FileExtractionStatus.Healthy;

        return new FileExtractionResult(status, logicalSize, genuinelyRecovered, invalidRuns, null);
    }

    private static void WriteZeros(Stream output, long count)
    {
        if (count <= 0)
            return;

        byte[] zeros = new byte[Math.Min(count, CopyBufferSize)];
        long remaining = count;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, zeros.Length);
            output.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
    }
}
