using System.Buffers;
using NtfsRecovery.Core.IO;
using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Scan;

/// <summary>
/// Streams through a region of a block device looking for "FILE" signature candidates,
/// without assuming the $MFT is contiguous or healthy and without loading the scanned
/// region into memory at once.
///
/// Reads proceed in fixed-size blocks via a single reused buffer sized
/// <c>blockSize + mftRecordSize</c>. Each iteration keeps the trailing
/// <c>mftRecordSize</c> bytes of the previous block as a carry so that a record whose
/// header lands near a block boundary still has its full body available in one
/// contiguous span before it is parsed. A monotonically increasing watermark offset
/// guarantees each byte position is evaluated exactly once even though carried bytes
/// physically reappear in the next iteration's buffer.
///
/// Candidate positions are restricted to sector boundaries: every MFT record is laid
/// out at a multiple of the sector size, so this cuts comparisons by a factor of the
/// sector size (typically 512x) without missing any genuine record.
/// </summary>
public sealed class MftScanner
{
    private readonly IBlockDevice _device;
    private readonly int _bytesPerSector;
    private readonly int _mftRecordSize;
    private readonly int _blockSize;

    public MftScanner(IBlockDevice device, int bytesPerSector, int mftRecordSize, int blockSize = 16 * 1024 * 1024)
    {
        if (bytesPerSector <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSector));
        if (mftRecordSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(mftRecordSize));
        if (blockSize < mftRecordSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize), "blockSize must be at least one MFT record long.");

        _device = device;
        _bytesPerSector = bytesPerSector;
        _mftRecordSize = mftRecordSize;
        _blockSize = blockSize;
    }

    public ScanStatistics Scan(
        long startOffset,
        long length,
        Action<MftRecord> onValidRecord,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (startOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(startOffset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        var stats = new ScanStatistics();
        int overlap = _mftRecordSize;
        int capacity = _blockSize + overlap;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);

        try
        {
            long endOffset = startOffset + length;
            long readPos = startOffset;
            long combinedStart = startOffset;
            long watermark = startOffset;
            int carryLength = 0;

            while (readPos < endOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(_blockSize, endOffset - readPos);
                int read = _device.ReadAt(readPos, buffer.AsSpan(carryLength, toRead));
                int combinedLength = carryLength + read;

                int scanLimit = combinedLength - _mftRecordSize;
                if (scanLimit >= 0)
                {
                    for (int i = 0; i <= scanLimit; i += _bytesPerSector)
                    {
                        long absolute = combinedStart + i;
                        if (absolute < watermark)
                            continue;

                        watermark = absolute + _bytesPerSector;

                        if (buffer[i] == (byte)'F' && buffer[i + 1] == (byte)'I' &&
                            buffer[i + 2] == (byte)'L' && buffer[i + 3] == (byte)'E')
                        {
                            stats.CandidatesFound++;

                            MftCandidateValidationResult result = MftCandidateValidator.Validate(
                                buffer.AsSpan(i, _mftRecordSize), _bytesPerSector, absolute);

                            if (result.IsValid)
                            {
                                stats.ValidRecords++;
                                if (result.Record!.IsInUse)
                                    stats.InUseRecords++;
                                if (result.Record.IsDirectory)
                                    stats.Directories++;
                                else
                                    stats.Files++;

                                onValidRecord(result.Record);
                            }
                            else
                            {
                                stats.RecordRejection(result.RejectReason!.Value);
                            }
                        }
                    }
                }

                int nextCarryLength = Math.Min(overlap, combinedLength);
                if (nextCarryLength > 0)
                {
                    Buffer.BlockCopy(buffer, combinedLength - nextCarryLength, buffer, 0, nextCarryLength);
                }
                combinedStart = combinedStart + combinedLength - nextCarryLength;
                carryLength = nextCarryLength;

                readPos += read;
                progress?.Report(new ScanProgress(Math.Min(readPos - startOffset, length), length));

                if (read < toRead)
                    break; // short read: end of device/region reached
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return stats;
    }
}
