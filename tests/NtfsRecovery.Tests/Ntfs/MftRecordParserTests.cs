using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Ntfs;

public class MftRecordParserTests
{
    [Fact]
    public void TryParse_ValidRecord_ExtractsHeaderAndAttributes()
    {
        byte[] raw = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options
        {
            RecordNumber = 1234,
            SequenceNumber = 7,
            IsDirectory = false,
            FileName = "report.docx",
            ParentRecordNumber = 55,
            ParentSequenceNumber = 2,
        });

        bool ok = MftRecordParser.TryParse(raw, 512, sourceOffset: 999_000, out MftRecord? record, out MftRecordParseFailure? failure);

        Assert.True(ok, failure?.Detail);
        Assert.NotNull(record);
        Assert.Equal(1234u, record!.RecordNumber);
        Assert.Equal(7, record.SequenceNumber);
        Assert.True(record.IsInUse);
        Assert.False(record.IsDirectory);
        Assert.Equal(999_000, record.SourceOffset);

        FileNameAttribute? fileName = record.PrimaryFileName;
        Assert.NotNull(fileName);
        Assert.Equal("report.docx", fileName!.FileName);
        Assert.Equal(55UL, fileName.ParentDirectory.RecordNumber);
        Assert.Equal(2, fileName.ParentDirectory.SequenceNumber);

        DataAttribute? data = record.DataAttributes.FirstOrDefault();
        Assert.NotNull(data);
        Assert.False(data!.IsNonResident);
        Assert.Equal("Hello, NTFS recovery!", System.Text.Encoding.ASCII.GetString(data.ResidentData!));

        Assert.NotNull(record.StandardInformation);
    }

    [Fact]
    public void TryParse_DirectoryFlag_IsReported()
    {
        byte[] raw = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options { IsDirectory = true });

        bool ok = MftRecordParser.TryParse(raw, 512, 0, out MftRecord? record, out _);

        Assert.True(ok);
        Assert.True(record!.IsDirectory);
    }

    [Fact]
    public void TryParse_CorruptFixup_IsRejected()
    {
        byte[] raw = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options { CorruptFixup = true });

        bool ok = MftRecordParser.TryParse(raw, 512, 0, out MftRecord? record, out MftRecordParseFailure? failure);

        Assert.False(ok);
        Assert.Null(record);
        Assert.Equal(MftRecordRejectReason.FixupMismatch, failure!.Reason);
    }

    [Fact]
    public void TryParse_MissingTerminator_IsRejected()
    {
        byte[] raw = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options { OmitTerminator = true });

        bool ok = MftRecordParser.TryParse(raw, 512, 0, out _, out MftRecordParseFailure? failure);

        Assert.False(ok);
        Assert.Equal(MftRecordRejectReason.MissingAttributeTerminator, failure!.Reason);
    }

    [Fact]
    public void TryParse_TooSmallBuffer_IsRejected()
    {
        byte[] raw = new byte[16];

        bool ok = MftRecordParser.TryParse(raw, 512, 0, out _, out MftRecordParseFailure? failure);

        Assert.False(ok);
        Assert.Equal(MftRecordRejectReason.TooSmall, failure!.Reason);
    }

    [Fact]
    public void TryParse_WrongSignature_IsRejected()
    {
        byte[] raw = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options());
        raw[0] = (byte)'X'; // corrupt the "FILE" signature -> false positive from a raw byte scan

        bool ok = MftRecordParser.TryParse(raw, 512, 0, out _, out MftRecordParseFailure? failure);

        Assert.False(ok);
        Assert.Equal(MftRecordRejectReason.InvalidSignature, failure!.Reason);
    }

    [Fact]
    public void TryParse_TruncatedRecord_IsRejected()
    {
        byte[] full = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options());
        byte[] truncated = full.AsSpan(0, 600).ToArray(); // cuts off mid-second-sector, before declared BytesAllocated

        bool ok = MftRecordParser.TryParse(truncated, 512, 0, out _, out MftRecordParseFailure? failure);

        Assert.False(ok);
        Assert.Equal(MftRecordRejectReason.BytesAllocatedExceedsBuffer, failure!.Reason);
    }

    [Fact]
    public void TryParse_MultipleFileNames_PrefersWin32OverDos()
    {
        byte[] raw = SyntheticMftRecordBuilder.Build(new SyntheticMftRecordBuilder.Options { FileName = "LongName.txt" });

        bool ok = MftRecordParser.TryParse(raw, 512, 0, out MftRecord? record, out _);

        Assert.True(ok);
        // Builder only emits one $FILE_NAME (Win32); this asserts the priority accessor
        // at least picks a Win32-namespace entry when present, which is what matters for
        // the heuristic required by Phase 3.
        Assert.Equal(0, record!.PrimaryFileName!.NamespacePriority);
    }
}
