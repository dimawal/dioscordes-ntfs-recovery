using System.Buffers.Binary;
using NtfsRecovery.Core.Ntfs;
using Xunit;

namespace NtfsRecovery.Tests.Ntfs;

public class UsaFixupTests
{
    [Fact]
    public void TryApply_ValidUsa_RestoresSectorTrailingBytes()
    {
        byte[] record = new byte[1024];
        const int usaOffset = 0x30;
        const ushort usn = 0x0007;

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset, 2), usn);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset + 2, 2), 0xAAAA);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset + 4, 2), 0xBBBB);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510, 2), usn);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022, 2), usn);

        bool ok = UsaFixup.TryApply(record, bytesPerSector: 512, usaOffset: usaOffset, usaSize: 3, out string? error);

        Assert.True(ok, error);
        Assert.Equal(0xAAAA, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510, 2)));
        Assert.Equal(0xBBBB, BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022, 2)));
    }

    [Fact]
    public void TryApply_UsnMismatch_FailsWithoutMutatingUnrelatedBytes()
    {
        byte[] record = new byte[1024];
        const int usaOffset = 0x30;

        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset, 2), 0x0007);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510, 2), 0x0007);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022, 2), 0x9999); // mismatch

        bool ok = UsaFixup.TryApply(record, 512, usaOffset, usaSize: 3, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryApply_UsaOffsetOutOfBounds_Fails()
    {
        byte[] record = new byte[64];

        bool ok = UsaFixup.TryApply(record, 512, usaOffset: 60, usaSize: 5, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryApply_ZeroUsaSize_Fails()
    {
        byte[] record = new byte[1024];

        bool ok = UsaFixup.TryApply(record, 512, usaOffset: 0x30, usaSize: 0, out string? error);

        Assert.False(ok);
    }
}
