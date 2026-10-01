using NtfsRecovery.Core.Scan;
using Xunit;

namespace NtfsRecovery.Tests.Scan;

public class ScanStoreTests : IDisposable
{
    private readonly string _dbPath;

    public ScanStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ntfsrecover-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void InsertAndLoadRecords_RoundTripsAllFields()
    {
        var record = new ScanRecordDto
        {
            RecordNumber = 1234,
            SequenceNumber = 7,
            IsInUse = true,
            IsDirectory = false,
            SourceOffset = 999_000,
            ParentRecordNumber = 5,
            ParentSequenceNumber = 1,
            Name = "report.docx",
            AlternateNames = ["REPORT~1.DOC"],
            IsDataNonResident = true,
            LogicalSize = 4096,
            DataRuns = [new DataRunDto(0, 1, 100, false)],
        };

        using (ScanStore store = ScanStore.OpenOrCreate(_dbPath))
        {
            store.InsertRecords([record]);
        }

        using (ScanStore reopened = ScanStore.OpenOrCreate(_dbPath))
        {
            List<ScanRecordDto> loaded = reopened.LoadAllRecords();

            Assert.Single(loaded);
            Assert.Equal(1234u, loaded[0].RecordNumber);
            Assert.Equal("report.docx", loaded[0].Name);
            Assert.Equal(5UL, loaded[0].ParentRecordNumber);
            Assert.Single(loaded[0].DataRuns);
            Assert.Equal(100, loaded[0].DataRuns[0].Lcn);
        }
    }

    [Fact]
    public void SaveAndLoadCheckpoint_RoundTrips()
    {
        using ScanStore store = ScanStore.OpenOrCreate(_dbPath);

        Assert.Null(store.TryLoadCheckpoint());

        store.SaveCheckpoint(new ScanCheckpoint(1000, 2_000_000, 500_000));
        ScanCheckpoint? loaded = store.TryLoadCheckpoint();

        Assert.NotNull(loaded);
        Assert.Equal(1000, loaded!.ScanStartOffset);
        Assert.Equal(2_000_000, loaded.ScanEndOffset);
        Assert.Equal(500_000, loaded.LastScannedOffset);

        store.SaveCheckpoint(new ScanCheckpoint(1000, 2_000_000, 1_500_000));
        Assert.Equal(1_500_000, store.TryLoadCheckpoint()!.LastScannedOffset);
    }
}
