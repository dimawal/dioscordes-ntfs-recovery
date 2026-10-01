using NtfsRecovery.Core.Ntfs;
using Xunit;

namespace NtfsRecovery.Tests.Ntfs;

public class DataRunParserTests
{
    [Fact]
    public void Parse_SingleRun_PositiveOffset()
    {
        // header 0x21: offset size=2, length size=1; length=0x10 (16 clusters); offset=0x1234
        byte[] runList = { 0x21, 0x10, 0x34, 0x12, 0x00 };

        var runs = DataRunParser.Parse(runList);

        Assert.Single(runs);
        Assert.Equal(0, runs[0].VcnStart);
        Assert.Equal(16, runs[0].ClusterCount);
        Assert.Equal(0x1234, runs[0].Lcn);
        Assert.False(runs[0].IsSparse);
    }

    [Fact]
    public void Parse_MultipleRuns_SecondRunOffsetIsRelativeToFirst()
    {
        // Run 1: length=5, offset=+100 -> LCN 100
        // Run 2: length=3, offset=+50 -> LCN 150 (relative to previous LCN)
        byte[] runList =
        {
            0x11, 0x05, 100,
            0x11, 0x03, 50,
            0x00,
        };

        var runs = DataRunParser.Parse(runList);

        Assert.Equal(2, runs.Count);
        Assert.Equal(100, runs[0].Lcn);
        Assert.Equal(0, runs[0].VcnStart);
        Assert.Equal(150, runs[1].Lcn);
        Assert.Equal(5, runs[1].VcnStart);
    }

    [Fact]
    public void Parse_NegativeOffset_MovesLcnBackward()
    {
        // Run 1: length=10 (1 byte), offset=+1000 (2 bytes) -> header low nibble=1, high nibble=2 -> 0x21
        // Run 2: length=4 (1 byte), offset=-200 (2 bytes, two's complement) -> header 0x21
        byte[] run1 = { 0x21, 0x0A, 0xE8, 0x03 };
        byte[] run2 = { 0x21, 0x04, 0x38, 0xFF };
        byte[] runList = run1.Concat(run2).Concat(new byte[] { 0x00 }).ToArray();

        var runs = DataRunParser.Parse(runList);

        Assert.Equal(2, runs.Count);
        Assert.Equal(1000, runs[0].Lcn);
        Assert.Equal(800, runs[1].Lcn); // 1000 + (-200)
    }

    [Fact]
    public void Parse_SparseRun_HasNullLcn()
    {
        // Run1: header 0x11 -> lengthSize=1, offsetSize=1 ; length=10 ; offset=50 (1 byte)
        // Run2 (sparse): header 0x02 -> lengthSize=2, offsetSize=0 ; length=300 (2 bytes LE)
        byte[] correct =
        {
            0x11, 10, 50,
            0x02, 0x2C, 0x01, // 300 = 0x012C
            0x00,
        };

        var runs = DataRunParser.Parse(correct);

        Assert.Equal(2, runs.Count);
        Assert.False(runs[0].IsSparse);
        Assert.Equal(50, runs[0].Lcn);

        Assert.True(runs[1].IsSparse);
        Assert.Null(runs[1].Lcn);
        Assert.Equal(300, runs[1].ClusterCount);
        Assert.Equal(10, runs[1].VcnStart);
    }

    [Fact]
    public void Parse_EmptyRunList_ReturnsNoRuns()
    {
        byte[] runList = { 0x00 };

        var runs = DataRunParser.Parse(runList);

        Assert.Empty(runs);
    }

    [Fact]
    public void Parse_TruncatedHeader_Throws()
    {
        // Declares offsetSize=4, lengthSize=4 but provides no data at all.
        byte[] runList = { 0x44 };

        Assert.Throws<InvalidDataException>(() => DataRunParser.Parse(runList));
    }

    [Fact]
    public void Parse_ZeroLengthSize_Throws()
    {
        byte[] runList = { 0x10, 0x00 }; // lengthSize = 0 is invalid

        Assert.Throws<InvalidDataException>(() => DataRunParser.Parse(runList));
    }
}
