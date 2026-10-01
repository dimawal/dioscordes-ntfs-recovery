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
        FileExtractionResult result = FileExtractor.Extract(device, 512, record, dest);

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

        FileExtractionResult result = FileExtractor.Extract(device, bytesPerCluster, record, dest);

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

        FileExtractionResult result = FileExtractor.Extract(device, bytesPerCluster, record, dest);

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

        FileExtractionResult result = FileExtractor.Extract(device, bytesPerCluster, record, dest);

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

        FileExtractionResult result = FileExtractor.Extract(device, bytesPerCluster, record, dest);

        Assert.Equal(FileExtractionStatus.Partial, result.Status);
        Assert.Equal((ulong)bytesPerCluster, result.RecoveredBytes);
        Assert.Equal((ulong)(3 * bytesPerCluster), result.ExpectedBytes);

        byte[] output = File.ReadAllBytes(dest);
        Assert.Equal((int)record.LogicalSize, output.Length);
    }

    [Fact]
    public void Extract_CompressedData_IsUnsupported()
    {
        var record = new ScanRecordDto { HasData = true, IsDataCompressed = true, LogicalSize = 1000 };
        var device = new InMemoryBlockDevice([]);

        FileExtractionResult result = FileExtractor.Extract(device, 512, record, DestPath("compressed.bin"));

        Assert.Equal(FileExtractionStatus.Unsupported, result.Status);
    }

    [Fact]
    public void Extract_NoDataAttribute_IsMetadataOnly()
    {
        var record = new ScanRecordDto { HasData = false };
        var device = new InMemoryBlockDevice([]);

        FileExtractionResult result = FileExtractor.Extract(device, 512, record, DestPath("metadata.bin"));

        Assert.Equal(FileExtractionStatus.MetadataOnly, result.Status);
    }
}
