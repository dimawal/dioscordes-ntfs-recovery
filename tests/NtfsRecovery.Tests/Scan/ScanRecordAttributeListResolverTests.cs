using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Scan;
using Xunit;

namespace NtfsRecovery.Tests.Scan;

public class ScanRecordAttributeListResolverTests
{
    private static ScanRecordDto BaseRecord(uint recordNumber, ushort sequenceNumber, List<DataRunDto> runs, List<AttributeListEntryDto> entries) => new()
    {
        RecordNumber = recordNumber,
        SequenceNumber = sequenceNumber,
        IsDataNonResident = true,
        HasData = true,
        DataRuns = runs,
        AttributeListEntries = entries,
    };

    private static ScanRecordDto ExtensionRecord(uint recordNumber, ushort sequenceNumber, ulong baseRecordNumber, ushort baseSequenceNumber, List<DataRunDto> runs) => new()
    {
        RecordNumber = recordNumber,
        SequenceNumber = sequenceNumber,
        IsExtensionRecord = true,
        BaseRecordNumber = baseRecordNumber,
        BaseSequenceNumber = baseSequenceNumber,
        IsDataNonResident = true,
        HasData = true,
        DataRuns = runs,
    };

    [Fact]
    public void Resolve_NoAttributeList_PassesRecordThrough()
    {
        var record = BaseRecord(10, 1, [new DataRunDto(0, 5, 100, false)], []);

        List<ScanRecordDto> result = ScanRecordAttributeListResolver.Resolve([record]);

        Assert.Single(result);
        Assert.Same(record, result[0]);
    }

    [Fact]
    public void Resolve_ValidExtensionRecord_MergesRunsAndDropsExtensionFromOutput()
    {
        var entries = new List<AttributeListEntryDto>
        {
            new(NtfsAttributeType.Data, 0x80, 0, 10, 1, null),
            new(NtfsAttributeType.Data, 0x80, 0, 999, 2, null),
        };
        var baseRecord = BaseRecord(10, 1, [new DataRunDto(0, 5, 100, false)], entries);
        var extension = ExtensionRecord(999, 2, baseRecordNumber: 10, baseSequenceNumber: 1, [new DataRunDto(5, 3, 500, false)]);

        List<ScanRecordDto> result = ScanRecordAttributeListResolver.Resolve([baseRecord, extension]);

        Assert.Single(result); // extension record itself is not emitted
        Assert.Equal(10u, result[0].RecordNumber);
        Assert.Equal(2, result[0].DataRuns.Count);
        Assert.Equal(100, result[0].DataRuns[0].Lcn);
        Assert.Equal(500, result[0].DataRuns[1].Lcn);
    }

    [Fact]
    public void Resolve_ExtensionWithMismatchedBaseSequence_IsIgnored()
    {
        var entries = new List<AttributeListEntryDto> { new(NtfsAttributeType.Data, 0x80, 0, 999, 2, null) };
        var baseRecord = BaseRecord(10, 1, [new DataRunDto(0, 5, 100, false)], entries);
        var extension = ExtensionRecord(999, 2, baseRecordNumber: 10, baseSequenceNumber: 99, [new DataRunDto(5, 3, 500, false)]);

        List<ScanRecordDto> result = ScanRecordAttributeListResolver.Resolve([baseRecord, extension]);

        Assert.Single(result[0].DataRuns); // merge rejected: extension's claimed base sequence doesn't match
    }

    [Fact]
    public void Resolve_MissingExtensionRecord_LeavesBaseRunsUnchanged()
    {
        var entries = new List<AttributeListEntryDto> { new(NtfsAttributeType.Data, 0x80, 0, 999, 2, null) };
        var baseRecord = BaseRecord(10, 1, [new DataRunDto(0, 5, 100, false)], entries);

        List<ScanRecordDto> result = ScanRecordAttributeListResolver.Resolve([baseRecord]);

        Assert.Single(result[0].DataRuns);
    }

    [Fact]
    public void Resolve_WorksAcrossSeparatelyPersistedBatches()
    {
        // Simulates a resumed, checkpointed scan: base and extension were found in
        // different scan segments but both ended up in the same database.
        var entries = new List<AttributeListEntryDto> { new(NtfsAttributeType.Data, 0x80, 0, 999, 2, null) };
        var batch1 = BaseRecord(10, 1, [new DataRunDto(0, 5, 100, false)], entries);
        var batch2 = ExtensionRecord(999, 2, 10, 1, [new DataRunDto(5, 3, 500, false)]);

        List<ScanRecordDto> allPersistedRecords = [batch1, batch2];
        List<ScanRecordDto> result = ScanRecordAttributeListResolver.Resolve(allPersistedRecords);

        Assert.Single(result);
        Assert.Equal(2, result[0].DataRuns.Count);
    }
}
