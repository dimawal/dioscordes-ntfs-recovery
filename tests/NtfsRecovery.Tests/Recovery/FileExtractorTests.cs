using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Core.Scan;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Recovery;

public class FileExtractorTests : IDisposable
{
    private readonly string _tempDir;

    public FileExtractorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ntfsrecover-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string DestPath(string name) => Path.Combine(_tempDir, name);

    private static byte[] BuildClusteredDevice(int clusterCount, int bytesPerCluster)
    {
        byte[] content = new byte[clusterCount * bytesPerCluster];
        for (int c = 0; c < clusterCount; c++)
            content.AsSpan(c * bytesPerCluster, bytesPerCluster).Fill((byte)c);
        return content;
    }

    [Fact]
    public void Extract_ResidentData_WritesBytesDirectly()
    {
        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = false,
            ResidentData = "hello world"u8.ToArray(),
        };
        string dest = DestPath("resident.bin");

        var device = new InMemoryBlockDevice([]);
        FileExtractionResult result = FileExtractor.Extract(device, 0, 512, record, dest);

        Assert.Equal(FileExtractionStatus.Healthy, result.Status);
        Assert.Equal("hello world"u8.ToArray(), File.ReadAllBytes(dest));
    }

    [Fact]
    public void Extract_NonResident_MultipleRuns_ReassemblesInOrder()
    {
        const int bytesPerCluster = 512;
        byte[] deviceContent = BuildClusteredDevice(8, bytesPerCluster);
        var device = new InMemoryBlockDevice(deviceContent);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(2 * bytesPerCluster) + 100,
            DataRuns =
            [
                new DataRunDto(VcnStart: 0, ClusterCount: 2, Lcn: 0, IsSparse: false),
                new DataRunDto(VcnStart: 2, ClusterCount: 1, Lcn: 5, IsSparse: false),
            ],
        };
        string dest = DestPath("nonresident.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.Healthy, result.Status);
        Assert.Equal(record.LogicalSize, result.RecoveredBytes);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal((int)record.LogicalSize, output.Length);
        Assert.All(output.AsSpan(0, bytesPerCluster).ToArray(), b => Assert.Equal(0, b)); // cluster 0
        Assert.All(output.AsSpan(bytesPerCluster, bytesPerCluster).ToArray(), b => Assert.Equal(1, b)); // cluster 1
        Assert.All(output.AsSpan(2 * bytesPerCluster, 100).ToArray(), b => Assert.Equal(5, b)); // cluster 5, truncated to 100 bytes
    }

    [Fact]
    public void Extract_SparseRun_FillsZerosWithoutReadingSource()
    {
        const int bytesPerCluster = 512;
        byte[] deviceContent = BuildClusteredDevice(2, bytesPerCluster);
        var device = new InMemoryBlockDevice(deviceContent);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(2 * bytesPerCluster),
            DataRuns =
            [
                new DataRunDto(VcnStart: 0, ClusterCount: 1, Lcn: null, IsSparse: true),
                new DataRunDto(VcnStart: 1, ClusterCount: 1, Lcn: 0, IsSparse: false),
            ],
        };
        string dest = DestPath("sparse.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.Healthy, result.Status);
        byte[] output = File.ReadAllBytes(dest);
        Assert.All(output.AsSpan(0, bytesPerCluster).ToArray(), b => Assert.Equal(0, b));
        Assert.All(output.AsSpan(bytesPerCluster, bytesPerCluster).ToArray(), b => Assert.Equal(0, b)); // cluster 0's fill value is also 0
    }

    [Fact]
    public void Extract_InvalidRun_ReportsCorruptRunsAndZeroFillsThatRun()
    {
        const int bytesPerCluster = 512;
        byte[] deviceContent = BuildClusteredDevice(2, bytesPerCluster);
        var device = new InMemoryBlockDevice(deviceContent);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(2 * bytesPerCluster),
            DataRuns =
            [
                new DataRunDto(VcnStart: 0, ClusterCount: 1, Lcn: -1, IsSparse: false), // malformed: negative LCN, not sparse
                new DataRunDto(VcnStart: 1, ClusterCount: 1, Lcn: 1, IsSparse: false),
            ],
        };
        string dest = DestPath("corrupt.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.CorruptRuns, result.Status);
        Assert.Equal(1, result.InvalidRunCount);
        Assert.Equal((ulong)(2 * bytesPerCluster), result.ExpectedBytes);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal((int)record.LogicalSize, output.Length); // file still has the correct total length
        Assert.All(output.AsSpan(0, bytesPerCluster).ToArray(), b => Assert.Equal(0, b)); // placeholder for the bad run
        Assert.All(output.AsSpan(bytesPerCluster, bytesPerCluster).ToArray(), b => Assert.Equal(1, b)); // good run intact
    }

    [Fact]
    public void Extract_MissingRunsBeforeEndOfFile_ReportsPartial()
    {
        const int bytesPerCluster = 512;
        byte[] deviceContent = BuildClusteredDevice(1, bytesPerCluster);
        var device = new InMemoryBlockDevice(deviceContent);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(3 * bytesPerCluster), // declares more data than the runs below cover
            DataRuns = [new DataRunDto(VcnStart: 0, ClusterCount: 1, Lcn: 0, IsSparse: false)],
        };
        string dest = DestPath("partial.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.Partial, result.Status);
        Assert.Equal((ulong)bytesPerCluster, result.RecoveredBytes);
        Assert.Equal((ulong)(3 * bytesPerCluster), result.ExpectedBytes);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal((int)record.LogicalSize, output.Length);
    }

    [Fact]
    public void Extract_TransientShortRead_RetriesAndRecoversFullData()
    {
        // Reproduces a real bug: a device read that returns fewer bytes than requested
        // (e.g. a USB mass-storage bridge capping a large transfer) was previously
        // accepted as final on the very first attempt, permanently zero-filling the rest
        // of that chunk even though a follow-up read at the correct continued offset
        // would have retrieved the real data. A good JPEG header followed by a wall of
        // zeros in an otherwise non-fragmented, single-run file is exactly what this
        // produced in practice. The fix retries within the chunk before giving up.
        const int bytesPerCluster = 4096;
        const int clusterCount = 3; // 12288 bytes, well under the 1MB chunk size: one inner-loop iteration
        byte[] content = new byte[clusterCount * bytesPerCluster];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)(i % 251); // distinguishable, non-repeating-enough pattern

        // First call to ReadAt at the run's start returns only 1000 of the 12288 requested
        // bytes; the retry (continuing from byte 1000) succeeds normally.
        var device = new FlakyBlockDevice(content, flakyOffset: 0, firstCallMaxBytes: 1000);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)content.Length,
            DataRuns = [new DataRunDto(0, clusterCount, 0, false)],
        };
        string dest = DestPath("flaky.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.Healthy, result.Status);
        Assert.Equal(0, result.InvalidRunCount);
        Assert.Equal((ulong)content.Length, result.RecoveredBytes);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal(content, output); // byte-for-byte identical, not a correct-size file with a zeroed tail
    }

    [Fact]
    public void Extract_PersistentlyUnreadableRegion_RetriesThenReportsCorruptRunsWithZeroFill()
    {
        const int bytesPerCluster = 4096;
        const int clusterCount = 3;
        byte[] content = new byte[clusterCount * bytesPerCluster];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)(i % 251);

        // Unlike the transient case above, every attempt at this offset fails -- the
        // region is genuinely unreadable, not just slow to respond on the first try.
        var device = new PersistentlyUnreadableBlockDevice(content, unreadableStart: 0, unreadableEnd: content.Length);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)content.Length,
            DataRuns = [new DataRunDto(0, clusterCount, 0, false)],
        };
        string dest = DestPath("unreadable.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.CorruptRuns, result.Status);
        Assert.Equal(0UL, result.RecoveredBytes);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal(content.Length, output.Length); // still the correct total size
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Extract_GapBetweenRuns_FillsGapWithZerosInsteadOfSplicingWrongBytes()
    {
        // Reproduces a real bug: when a fragmented file's $ATTRIBUTE_LIST merge fails to
        // find its extension record's run (e.g. due to a record-number collision), the
        // base record is left with a run list that has a hole in the middle -- VCN 0-0
        // then VCN 2-2, skipping VCN 1. Before this fix, the extractor just concatenated
        // whatever runs it had back-to-back, silently shifting every byte after the gap
        // to the wrong position: the output was the correct total size ("Healthy") but
        // scrambled from the gap onward. It must instead zero-fill the missing VCN range
        // and report CorruptRuns.
        const int bytesPerCluster = 512;
        byte[] deviceContent = BuildClusteredDevice(3, bytesPerCluster);
        var device = new InMemoryBlockDevice(deviceContent);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(3 * bytesPerCluster),
            DataRuns =
            [
                new DataRunDto(VcnStart: 0, ClusterCount: 1, Lcn: 0, IsSparse: false), // cluster 0
                // VCN 1 is missing entirely (the un-merged extension's run).
                new DataRunDto(VcnStart: 2, ClusterCount: 1, Lcn: 2, IsSparse: false), // cluster 2
            ],
        };
        string dest = DestPath("gap.bin");

        FileExtractionResult result = FileExtractor.Extract(device, 0, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.CorruptRuns, result.Status);
        Assert.Equal(1, result.InvalidRunCount);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal((int)record.LogicalSize, output.Length);
        Assert.All(output.AsSpan(0, bytesPerCluster).ToArray(), b => Assert.Equal(0, b)); // cluster 0's fill value
        Assert.All(output.AsSpan(bytesPerCluster, bytesPerCluster).ToArray(), b => Assert.Equal(0, b)); // gap: zero-filled, not cluster 2's content
        Assert.All(output.AsSpan(2 * bytesPerCluster, bytesPerCluster).ToArray(), b => Assert.Equal(2, b)); // cluster 2 lands at its correct offset
    }

    [Fact]
    public void Extract_CompressedData_IsUnsupported()
    {
        var record = new ScanRecordDto { HasData = true, IsDataCompressed = true, LogicalSize = 1000 };
        var device = new InMemoryBlockDevice([]);

        FileExtractionResult result = FileExtractor.Extract(device, 0, 512, record, DestPath("compressed.bin"));

        Assert.Equal(FileExtractionStatus.Unsupported, result.Status);
    }

    [Fact]
    public void Extract_NoDataAttribute_IsMetadataOnly()
    {
        var record = new ScanRecordDto { HasData = false };
        var device = new InMemoryBlockDevice([]);

        FileExtractionResult result = FileExtractor.Extract(device, 0, 512, record, DestPath("metadata.bin"));

        Assert.Equal(FileExtractionStatus.MetadataOnly, result.Status);
    }

    [Fact]
    public void Extract_NonZeroPartitionOffset_ReadsFromCorrectAbsoluteDiskPosition()
    {
        // Reproduces the real, confirmed root-cause bug: a $DATA run's LCN is relative to
        // the start of the NTFS *volume*, not the start of the physical disk. Omitting
        // the partition's own start offset when computing the absolute read position
        // silently reads from wherever LCN*bytesPerCluster happens to land in the raw
        // device -- which, on a disk with more than one partition, is a *different
        // partition's* data, not an error. The output is still the exact right size, so
        // nothing about the extraction status reveals this on its own; only the content
        // is wrong. This is what full, real-disk recoveries were actually hitting.
        const int bytesPerCluster = 4096;
        const long partitionOffset = 3L * bytesPerCluster; // the volume starts 3 clusters into the device

        byte[] device = new byte[6 * bytesPerCluster];
        device.AsSpan(0, (int)partitionOffset).Fill(0xAA); // an earlier, unrelated partition -- must never be read
        device.AsSpan((int)partitionOffset, 2 * bytesPerCluster).Fill(0x42); // the real volume content at volume-relative LCN 0-1

        var blockDevice = new InMemoryBlockDevice(device);
        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(2 * bytesPerCluster),
            DataRuns = [new DataRunDto(VcnStart: 0, ClusterCount: 2, Lcn: 0, IsSparse: false)], // volume-relative LCN 0
        };
        string dest = DestPath("partition_offset.bin");

        FileExtractionResult result = FileExtractor.Extract(blockDevice, partitionOffset, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.Healthy, result.Status);
        byte[] output = File.ReadAllBytes(dest);
        Assert.All(output, b => Assert.Equal(0x42, b)); // the volume's real content, never the other partition's 0xAA
    }

    [Fact]
    public void ExtractPreviewBytes_NonZeroPartitionOffset_ReadsFromCorrectAbsoluteDiskPosition()
    {
        const int bytesPerCluster = 4096;
        const long partitionOffset = 3L * bytesPerCluster;

        byte[] device = new byte[6 * bytesPerCluster];
        device.AsSpan(0, (int)partitionOffset).Fill(0xAA);
        device.AsSpan((int)partitionOffset, 2 * bytesPerCluster).Fill(0x42);

        var blockDevice = new InMemoryBlockDevice(device);
        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(2 * bytesPerCluster),
            DataRuns = [new DataRunDto(VcnStart: 0, ClusterCount: 2, Lcn: 0, IsSparse: false)],
        };

        byte[] preview = FileExtractor.ExtractPreviewBytes(blockDevice, partitionOffset, bytesPerCluster, record, maxBytes: 2 * bytesPerCluster);

        Assert.All(preview, b => Assert.Equal(0x42, b));
    }

    [Fact]
    public void ExtractPreviewBytes_ResidentData_ReturnsBytesDirectly()
    {
        var record = new ScanRecordDto { HasData = true, IsDataNonResident = false, ResidentData = "preview me"u8.ToArray() };
        var device = new InMemoryBlockDevice([]);

        byte[] preview = FileExtractor.ExtractPreviewBytes(device, 0, 512, record, maxBytes: 1024);

        Assert.Equal("preview me"u8.ToArray(), preview);
    }

    [Fact]
    public void ExtractPreviewBytes_NonResident_CapsAtMaxBytes()
    {
        const int bytesPerCluster = 512;
        byte[] deviceContent = BuildClusteredDevice(4, bytesPerCluster);
        var device = new InMemoryBlockDevice(deviceContent);

        var record = new ScanRecordDto
        {
            HasData = true,
            IsDataNonResident = true,
            LogicalSize = (ulong)(4 * bytesPerCluster),
            DataRuns = [new DataRunDto(0, 4, 0, false)],
        };

        byte[] preview = FileExtractor.ExtractPreviewBytes(device, 0, bytesPerCluster, record, maxBytes: 100);

        Assert.Equal(100, preview.Length);
        Assert.All(preview, b => Assert.Equal(0, b)); // cluster 0's fill value
    }

    [Fact]
    public void ExtractPreviewBytes_CompressedData_ReturnsEmpty()
    {
        var record = new ScanRecordDto { HasData = true, IsDataCompressed = true, LogicalSize = 1000 };
        var device = new InMemoryBlockDevice([]);

        byte[] preview = FileExtractor.ExtractPreviewBytes(device, 0, 512, record, maxBytes: 1024);

        Assert.Empty(preview);
    }
}
