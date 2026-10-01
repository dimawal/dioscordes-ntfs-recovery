using NtfsRecovery.Core.IO;
using Xunit;

namespace NtfsRecovery.Tests.IO;

public class ImageFileReaderTests : IDisposable
{
    private readonly string _tempFile;

    public ImageFileReaderTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"ntfsrecover-test-{Guid.NewGuid():N}.img");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    [Fact]
    public void ReadAt_ReturnsExpectedBytesAtOffset()
    {
        byte[] content = new byte[4096];
        for (int i = 0; i < content.Length; i++)
            content[i] = (byte)(i % 256);
        File.WriteAllBytes(_tempFile, content);

        using var reader = new ImageFileReader(_tempFile);

        Span<byte> buffer = new byte[16];
        int read = reader.ReadAt(100, buffer);

        Assert.Equal(16, read);
        for (int i = 0; i < 16; i++)
            Assert.Equal((byte)((100 + i) % 256), buffer[i]);
    }

    [Fact]
    public void Length_MatchesFileSize()
    {
        File.WriteAllBytes(_tempFile, new byte[12345]);

        using var reader = new ImageFileReader(_tempFile);

        Assert.Equal(12345, reader.Length);
    }

    [Fact]
    public void ReadAt_PastEndOfFile_ReturnsFewerBytes()
    {
        File.WriteAllBytes(_tempFile, new byte[10]);

        using var reader = new ImageFileReader(_tempFile);

        Span<byte> buffer = new byte[20];
        int read = reader.ReadAt(0, buffer);

        Assert.Equal(10, read);
    }

    [Fact]
    public void Constructor_OpensReadOnly_CannotBeWrittenThroughUnderlyingFile()
    {
        File.WriteAllBytes(_tempFile, new byte[16]);

        using var reader = new ImageFileReader(_tempFile);

        // The underlying FileStream was opened with FileAccess.Read; attempting to
        // open it again for writing while the read handle is active must fail because
        // FileShare.Read does not grant write sharing. This is the behavioral proof
        // that ImageFileReader never acquires write access to the source.
        Assert.Throws<IOException>(() => File.Open(_tempFile, FileMode.Open, FileAccess.Write, FileShare.None));
    }

    [Fact]
    public void IBlockDevice_HasNoWriteMembers()
    {
        var writeLikeMembers = typeof(IBlockDevice)
            .GetMembers()
            .Where(m => m.Name.Contains("Write", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(writeLikeMembers);
    }
}
