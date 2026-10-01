using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Scan;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Scan;

internal sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}

public class MftScannerTests
{
    private const int RecordSize = 1024;
    private const int SectorSize = 512;

    /// <summary>
    /// Lays out four back-to-back synthetic records (A: directory @0, B: file @1024,
    /// C: fixup-corrupted @2048, D: file @3072) and scans with a block size (1536) that
    /// is not a multiple of the record size, deliberately forcing records to straddle
    /// block-read boundaries so the scanner's overlap/carry logic is actually exercised.
    /// </summary>
    private static byte[] BuildFourRecordImage()
    {
        byte[] image = new byte[RecordSize * 4];

        SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options
        {
            RecordNumber = 5,
            IsDirectory = true,
            FileName = "Mateus Campos",
            ResidentData = [],
        }).CopyTo(image.AsSpan(0, RecordSize));

        SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options
        {
            RecordNumber = 100,
            ParentRecordNumber = 5,
            FileName = "foto.jpg",
        }).CopyTo(image.AsSpan(RecordSize, RecordSize));

        SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options
        {
            RecordNumber = 200,
            CorruptFixup = true,
        }).CopyTo(image.AsSpan(RecordSize * 2, RecordSize));

        SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options
        {
            RecordNumber = 101,
            ParentRecordNumber = 5,
            FileName = "SNES",
            IsDirectory = true,
            ResidentData = [],
        }).CopyTo(image.AsSpan(RecordSize * 3, RecordSize));

        return image;
    }

    [Fact]
    public void Scan_AcrossBlockBoundaries_FindsAllValidRecordsExactlyOnce()
    {
        byte[] image = BuildFourRecordImage();
        var device = new InMemoryBlockDevice(image);
        var scanner = new MftScanner(device, SectorSize, RecordSize, blockSize: 1536);

        var found = new List<MftRecord>();
        ScanStatistics stats = scanner.Scan(0, image.Length, found.Add);

        Assert.Equal(4, stats.CandidatesFound);
        Assert.Equal(3, stats.ValidRecords);
        Assert.Equal(1, stats.Rejected);
        Assert.Equal(2, stats.Directories); // A and D
        Assert.Equal(1, stats.Files); // B
        Assert.Equal(3, found.Count);

        Assert.Equal(MftRecordRejectReason.FixupMismatch, stats.RejectReasons.Keys.Single());
        Assert.Equal(1, stats.RejectReasons[MftRecordRejectReason.FixupMismatch]);

        Assert.Contains(found, r => r.RecordNumber == 5 && r.IsDirectory);
        Assert.Contains(found, r => r.RecordNumber == 100 && !r.IsDirectory);
        Assert.Contains(found, r => r.RecordNumber == 101 && r.IsDirectory);
        Assert.DoesNotContain(found, r => r.RecordNumber == 200);
    }

    [Fact]
    public void Scan_RespectsStartOffsetAndLength()
    {
        byte[] image = BuildFourRecordImage();
        var device = new InMemoryBlockDevice(image);
        var scanner = new MftScanner(device, SectorSize, RecordSize, blockSize: 4096);

        var found = new List<MftRecord>();
        // Only scan the region containing record D (offset 3072..4096).
        scanner.Scan(RecordSize * 3, RecordSize, found.Add);

        Assert.Single(found);
        Assert.Equal(101u, found[0].RecordNumber);
    }

    [Fact]
    public void Scan_ReportsProgress()
    {
        byte[] image = BuildFourRecordImage();
        var device = new InMemoryBlockDevice(image);
        var scanner = new MftScanner(device, SectorSize, RecordSize, blockSize: 1024);

        // System.Progress<T> dispatches via the captured SynchronizationContext (or the
        // thread pool when there is none), which is asynchronous and would make this
        // assertion racy. A direct, synchronous IProgress<T> keeps the test deterministic.
        var progressReports = new List<ScanProgress>();
        var progress = new SynchronousProgress<ScanProgress>(progressReports.Add);

        scanner.Scan(0, image.Length, _ => { }, progress);

        Assert.NotEmpty(progressReports);
        Assert.Equal(image.Length, progressReports[^1].BytesScanned);
    }

    [Fact]
    public void Scan_HonorsCancellation()
    {
        byte[] image = BuildFourRecordImage();
        var device = new InMemoryBlockDevice(image);
        var scanner = new MftScanner(device, SectorSize, RecordSize, blockSize: 2048);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            scanner.Scan(0, image.Length, _ => { }, cancellationToken: cts.Token));
    }
}
