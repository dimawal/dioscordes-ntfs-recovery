using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Recovery;
using Xunit;

namespace NtfsRecovery.Tests.Recovery;

public class AttributeListResolverTests
{
    private static DataAttribute NonResidentData(ushort attributeId, params DataRun[] runs) => new()
    {
        Type = NtfsAttributeType.Data,
        RawTypeCode = 0x80,
        IsNonResident = true,
        Name = null,
        AttributeId = attributeId,
        AttributeLength = 0,
        DataRuns = runs,
        RealSize = 100_000,
        AllocatedSize = 100_000,
    };

    private static AttributeListAttribute AttributeList(params AttributeListEntry[] entries) => new()
    {
        Type = NtfsAttributeType.AttributeList,
        RawTypeCode = 0x20,
        IsNonResident = false,
        Name = null,
        AttributeId = 0,
        AttributeLength = 0,
        Entries = entries,
    };

    private static MftRecord Record(uint recordNumber, ushort sequenceNumber, FileReference baseFileRecord, params NtfsAttribute[] attributes) => new()
    {
        SourceOffset = 0,
        RecordNumber = recordNumber,
        SequenceNumber = sequenceNumber,
        IsInUse = true,
        IsDirectory = false,
        BaseFileRecord = baseFileRecord,
        BytesInUse = 100,
        BytesAllocated = 1024,
        Attributes = attributes,
    };

    [Fact]
    public void Resolve_NoAttributeList_ReturnsRecordUnchanged()
    {
        MftRecord baseRecord = Record(10, 1, new FileReference(0, 0), NonResidentData(0, new DataRun(0, 5, 100, false)));

        MftRecord result = AttributeListResolver.Resolve(baseRecord, _ => null);

        Assert.Same(baseRecord, result);
    }

    [Fact]
    public void Resolve_ExtensionRecordWithValidBase_MergesDataRuns()
    {
        var baseData = NonResidentData(0, new DataRun(0, 5, 100, false));
        var attributeList = AttributeList(
            new AttributeListEntry(NtfsAttributeType.Data, 0x80, 0, 0, new FileReference(10, 1), null),
            new AttributeListEntry(NtfsAttributeType.Data, 0x80, 0, 5, new FileReference(999, 2), null));

        MftRecord baseRecord = Record(10, 1, new FileReference(0, 0), baseData, attributeList);

        var extensionData = NonResidentData(0, new DataRun(5, 3, 500, false));
        MftRecord extensionRecord = Record(999, 2, baseFileRecord: new FileReference(10, 1), extensionData);

        MftRecord resolved = AttributeListResolver.Resolve(baseRecord, n => n == 999 ? extensionRecord : null);

        DataAttribute mergedData = resolved.DataAttributes.Single(d => d.Name is null);
        Assert.Equal(2, mergedData.DataRuns!.Count);
        Assert.Equal(100, mergedData.DataRuns![0].Lcn);
        Assert.Equal(500, mergedData.DataRuns![1].Lcn);
    }

    [Fact]
    public void Resolve_ExtensionRecordWithMismatchedBaseSequence_IsIgnored()
    {
        var baseData = NonResidentData(0, new DataRun(0, 5, 100, false));
        var attributeList = AttributeList(
            new AttributeListEntry(NtfsAttributeType.Data, 0x80, 0, 5, new FileReference(999, 2), null));

        MftRecord baseRecord = Record(10, 1, new FileReference(0, 0), baseData, attributeList);

        // Extension record claims to extend record 10, but with the WRONG sequence
        // number -- as if record 10's slot had been reused since this extension was written.
        var extensionData = NonResidentData(0, new DataRun(5, 3, 500, false));
        MftRecord extensionRecord = Record(999, 2, baseFileRecord: new FileReference(10, 99), extensionData);

        MftRecord resolved = AttributeListResolver.Resolve(baseRecord, n => n == 999 ? extensionRecord : null);

        DataAttribute mergedData = resolved.DataAttributes.Single(d => d.Name is null);
        Assert.Same(baseData, mergedData); // untouched: untrusted extension was skipped
    }

    [Fact]
    public void Resolve_MissingExtensionRecord_ReturnsBaseUnchangedWithoutThrowing()
    {
        var baseData = NonResidentData(0, new DataRun(0, 5, 100, false));
        var attributeList = AttributeList(
            new AttributeListEntry(NtfsAttributeType.Data, 0x80, 0, 5, new FileReference(999, 2), null));

        MftRecord baseRecord = Record(10, 1, new FileReference(0, 0), baseData, attributeList);

        MftRecord resolved = AttributeListResolver.Resolve(baseRecord, _ => null);

        Assert.Same(baseRecord, resolved);
    }
}
