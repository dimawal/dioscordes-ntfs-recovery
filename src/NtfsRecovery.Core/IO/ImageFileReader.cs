namespace NtfsRecovery.Core.IO;

/// <summary>
/// Read-only access to a raw disk image file (.img, .dd, .raw, ...).
/// Opens with FileAccess.Read and FileShare.Read so the image can never be
/// mutated through this reader, regardless of caller behavior.
/// </summary>
public sealed class ImageFileReader : IBlockDevice
{
    private readonly FileStream _stream;
    private bool _disposed;

    public ImageFileReader(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Image path must not be empty.", nameof(imagePath));

        SourceDescription = imagePath;

        _stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);

        Length = _stream.Length;
    }

    public long Length { get; }

    public string SourceDescription { get; }

    public int ReadAt(long offset, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        _stream.Position = offset;

        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = _stream.Read(buffer.Slice(totalRead));
            if (read == 0)
                break;

            totalRead += read;
        }

        return totalRead;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _stream.Dispose();
        _disposed = true;
    }
}
