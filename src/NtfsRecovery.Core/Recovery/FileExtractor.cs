using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Core.Recovery;

/// <summary>
/// Reconstructs a file's bytes from a carved record's $DATA attribute and writes them to
/// a destination chosen by the caller. Reads the source device strictly read-only
/// (through <see cref="IBlockDevice"/>, which has no write members); this type never
/// touches the source and only ever opens <paramref name="destinationPath"/> for writing.
///
/// A $DATA run's LCN (logical cluster number) is relative to the start of the NTFS
/// *volume*, not the start of the physical disk. Every byte offset this class computes
/// from a run must therefore add <c>partitionOffset</c> -- the volume's absolute start
/// offset on the device -- before calling <see cref="IBlockDevice.ReadAt"/>, which always
/// operates in whole-device-relative terms. Skipping that add reads from the wrong
/// partition entirely whenever the volume isn't the first thing on the disk, producing a
/// file of the exact right size filled with unrelated data instead of a clean failure.
///
/// NTFS compression is explicitly out of scope for this version: compressed streams are
/// reported as <see cref="FileExtractionStatus.Unsupported"/> rather than guessed at.
/// </summary>
public static class FileExtractor
{
    private const int CopyBufferSize = 1024 * 1024;
    private const int MaxShortReadRetries = 3;

    public static FileExtractionResult Extract(
        IBlockDevice sourceDevice,
        long partitionOffset,
        int bytesPerCluster,
        ScanRecordDto record,
        string destinationPath,
        CancellationToken cancellationToken = default,
        Action<string>? onDiagnostic = null)
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

        return ExtractNonResident(sourceDevice, partitionOffset, bytesPerCluster, record, destinationPath, cancellationToken, onDiagnostic);
    }

    private static FileExtractionResult ExtractNonResident(
        IBlockDevice sourceDevice,
        long partitionOffset,
        int bytesPerCluster,
        ScanRecordDto record,
        string destinationPath,
        CancellationToken cancellationToken,
        Action<string>? onDiagnostic = null)
    {
        ulong logicalSize = record.LogicalSize;
        List<DataRunDto> runs = [.. record.DataRuns.OrderBy(r => r.VcnStart)];

        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] copyBuffer = new byte[CopyBufferSize];

        ulong written = 0;
        ulong genuinelyRecovered = 0;
        int invalidRuns = 0;
        long expectedVcn = 0;

        foreach (DataRunDto run in runs)
        {
            if (written >= logicalSize)
                break;

            cancellationToken.ThrowIfCancellationRequested();

            if (run.VcnStart < expectedVcn)
            {
                // Overlaps a VCN range already written (e.g. a duplicate/garbled run
                // from a misresolved extension record): skip it rather than risk
                // writing the same bytes twice or out of order.
                invalidRuns++;
                continue;
            }

            if (run.VcnStart > expectedVcn)
            {
                // A gap in VCN coverage -- most commonly an extension record that
                // should have contributed a run here but could not be matched (e.g.
                // because its claimed base record number collided with an unrelated
                // record elsewhere on the volume). Filling the gap with zeros keeps
                // every later run's bytes at their correct offset in the output file
                // instead of silently splicing them into the wrong position, which
                // would otherwise produce a file that is the right *size* but has
                // scrambled content from the gap onward.
                long gapClusters = run.VcnStart - expectedVcn;
                long gapBytes = gapClusters * (long)bytesPerCluster;
                ulong remainingForGap = logicalSize - written;
                long bytesForGap = Math.Min(gapBytes, (long)remainingForGap);
                if (bytesForGap > 0)
                {
                    invalidRuns++;
                    WriteZeros(output, bytesForGap);
                    written += (ulong)bytesForGap;
                }

                if (written >= logicalSize)
                    break;
            }

            expectedVcn = run.VcnStart + run.ClusterCount;

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

            long sourceOffset = partitionOffset + (run.Lcn.Value * bytesPerCluster);
            long remaining = bytesForThisRun;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunkSize = (int)Math.Min(remaining, copyBuffer.Length);

                // Retries within this exact chunk window before giving up on it: a
                // device read returning fewer bytes than requested is not necessarily
                // the end of readable data (e.g. a transient short read over USB), so
                // treating the very first short read as "nothing more to get here" would
                // silently lose data that a retry could have recovered. Only once this
                // chunk has had a fair chance is the source offset allowed to advance
                // past it -- advancing by the *requested* chunk size at that point is
                // then correct, not a silent skip, because we've confirmed (not assumed)
                // this window can't be read any further.
                int actuallyRead = 0;
                int attemptsUsed = 0;
                for (int attempt = 0; attempt < MaxShortReadRetries && actuallyRead < chunkSize; attempt++)
                {
                    attemptsUsed++;
                    int read = sourceDevice.ReadAt(sourceOffset + actuallyRead, copyBuffer.AsSpan(actuallyRead, chunkSize - actuallyRead));
                    if (read <= 0)
                        break;
                    actuallyRead += read;
                }

                if (actuallyRead < chunkSize)
                {
                    // Belt-and-suspenders: IBlockDevice implementations are required to
                    // zero-fill their own unread tail, but copyBuffer is reused across many
                    // iterations, so explicitly clearing here too means this is correct
                    // even if some future IBlockDevice implementation gets that contract
                    // wrong -- the alternative (stale bytes from a previous, unrelated
                    // chunk leaking into this file) would be a silent, hard-to-notice bug.
                    copyBuffer.AsSpan(actuallyRead, chunkSize - actuallyRead).Clear();
                    invalidRuns++; // confirmed unreadable after retries -- a real gap, not a false "Healthy"
                    onDiagnostic?.Invoke(
                        $"Leitura curta em offset {sourceOffset}: pedi {chunkSize} bytes, recebi {actuallyRead} após {attemptsUsed} tentativa(s).");
                }
                else if (onDiagnostic is not null && IsAllZero(copyBuffer.AsSpan(0, chunkSize)))
                {
                    // The device reported a full, successful read (actuallyRead == chunkSize,
                    // no short-read signal at all) but every byte came back zero. Real file
                    // content this large is essentially never genuinely all-zero outside a
                    // sparse run (which never reaches this branch), so this is the signature
                    // of a device/bridge silently returning "success" for data it could not
                    // actually retrieve -- a fundamentally different failure mode than a
                    // short read, and one no retry-the-same-call loop can detect or fix.
                    onDiagnostic(
                        $"Leitura em offset {sourceOffset} devolveu {chunkSize} bytes (tamanho pedido bate), mas todos zero -- possível erro de leitura mascarado pelo disco/controladora.");
                }

                output.Write(copyBuffer, 0, chunkSize);
                genuinelyRecovered += (ulong)actuallyRead;

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

    private static bool IsAllZero(ReadOnlySpan<byte> span) => span.IndexOfAnyExcept((byte)0) < 0;

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

    /// <summary>
    /// Extracts up to <paramref name="maxBytes"/> of a file's logical content into
    /// memory, for GUI preview purposes only -- never written to disk. Best-effort, with
    /// no pass/fail status: sparse, missing, or invalid runs read as zeros, same as
    /// <see cref="Extract"/>, since a preview just needs bytes to decode, not a verdict.
    /// </summary>
    public static byte[] ExtractPreviewBytes(IBlockDevice sourceDevice, long partitionOffset, int bytesPerCluster, ScanRecordDto record, int maxBytes, Action<string>? onDiagnostic = null)
    {
        if (!record.HasData || record.IsDataCompressed || maxBytes <= 0)
            return [];

        if (!record.IsDataNonResident)
        {
            byte[] resident = record.ResidentData ?? [];
            return resident.Length <= maxBytes ? resident : resident[..maxBytes];
        }

        int previewSize = (int)Math.Min((long)record.LogicalSize, maxBytes);
        if (previewSize <= 0)
            return [];

        using var buffer = new MemoryStream(previewSize);
        List<DataRunDto> runs = [.. record.DataRuns.OrderBy(r => r.VcnStart)];
        long expectedVcn = 0;

        foreach (DataRunDto run in runs)
        {
            if (buffer.Length >= previewSize)
                break;

            if (run.VcnStart < expectedVcn)
                continue;

            if (run.VcnStart > expectedVcn)
            {
                long gapBytes = (run.VcnStart - expectedVcn) * bytesPerCluster;
                WriteZeros(buffer, Math.Min(gapBytes, previewSize - buffer.Length));
            }

            expectedVcn = run.VcnStart + run.ClusterCount;
            if (buffer.Length >= previewSize)
                break;

            long runByteLength = run.ClusterCount * (long)bytesPerCluster;
            long bytesForThisRun = Math.Min(runByteLength, previewSize - buffer.Length);
            if (bytesForThisRun <= 0)
                continue;

            if (run.IsSparse || run.Lcn is null || run.Lcn < 0)
            {
                WriteZeros(buffer, bytesForThisRun);
                continue;
            }

            byte[] chunk = new byte[bytesForThisRun];
            long chunkSourceOffset = partitionOffset + (run.Lcn.Value * bytesPerCluster);
            int chunkActuallyRead = 0;
            for (int attempt = 0; attempt < MaxShortReadRetries && chunkActuallyRead < chunk.Length; attempt++)
            {
                int read = sourceDevice.ReadAt(chunkSourceOffset + chunkActuallyRead, chunk.AsSpan(chunkActuallyRead));
                if (read <= 0)
                    break;
                chunkActuallyRead += read;
            }

            if (chunkActuallyRead < chunk.Length)
            {
                onDiagnostic?.Invoke($"[preview] Leitura curta em offset {chunkSourceOffset}: pedi {chunk.Length} bytes, recebi {chunkActuallyRead}.");
            }
            else if (onDiagnostic is not null && IsAllZero(chunk))
            {
                onDiagnostic($"[preview] Leitura em offset {chunkSourceOffset} devolveu {chunk.Length} bytes completos, mas todos zero -- possível erro de leitura mascarado pelo disco/controladora.");
            }

            buffer.Write(chunk, 0, chunk.Length);
        }

        return buffer.ToArray();
    }
}
