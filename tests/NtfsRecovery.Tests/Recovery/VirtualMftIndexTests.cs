using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Recovery;

public class VirtualMftIndexTests
{
    [Fact]
    public void ResolveBest_SingleCandidate_ReturnsIt()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(100, 1, "file.txt", 5, 1));

        VirtualMftRecord? resolved = index.ResolveBest(100);

        Assert.NotNull(resolved);
        Assert.Equal("file.txt", resolved!.Name);
    }

    [Fact]
    public void ResolveBest_DuplicateRecordNumber_PrefersInUseOverDeleted()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(100, 1, "old-deleted.txt", 5, 1, isInUse: false));
        index.Add(TestMftRecordFactory.Create(100, 2, "new-current.txt", 5, 1, isInUse: true));

        VirtualMftRecord? resolved = index.ResolveBest(100);

        Assert.Equal("new-current.txt", resolved!.Name);
        Assert.Contains(100u, index.RecordNumbersWithDuplicates);
    }

    [Fact]
    public void ResolveBest_UnknownRecordNumber_ReturnsNull()
    {
        var index = new VirtualMftIndex();

        Assert.Null(index.ResolveBest(999));
    }

    [Fact]
    public void Add_RecordWithoutSelfReportedNumber_IsIgnored()
    {
        // Simulate an unidentifiable carved record (no self-reported number).
        var unidentified = new NtfsRecovery.Core.Ntfs.MftRecord
        {
            SourceOffset = 0,
            RecordNumber = null,
            SequenceNumber = 1,
            IsInUse = true,
            IsDirectory = false,
            BaseFileRecord = new NtfsRecovery.Core.Ntfs.FileReference(0, 0),
            BytesInUse = 100,
            BytesAllocated = 1024,
            Attributes = [],
        };

        var index = new VirtualMftIndex();
        index.Add(unidentified);

        Assert.Equal(0, index.Count);
    }
}
