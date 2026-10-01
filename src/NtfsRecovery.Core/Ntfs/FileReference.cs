namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// NTFS file reference: a 64-bit value packing a 48-bit MFT record number and a
/// 16-bit sequence number. Used for parent directory references and base-record
/// references in $ATTRIBUTE_LIST entries.
/// </summary>
public readonly struct FileReference : IEquatable<FileReference>
{
    public ulong RecordNumber { get; }
    public ushort SequenceNumber { get; }

    public FileReference(ulong recordNumber, ushort sequenceNumber)
    {
        RecordNumber = recordNumber;
        SequenceNumber = sequenceNumber;
    }

    public static FileReference Parse(ulong raw) =>
        new(raw & 0x0000FFFFFFFFFFFFUL, (ushort)(raw >> 48));

    public ulong ToRaw() => (RecordNumber & 0x0000FFFFFFFFFFFFUL) | ((ulong)SequenceNumber << 48);

    public bool Equals(FileReference other) =>
        RecordNumber == other.RecordNumber && SequenceNumber == other.SequenceNumber;

    public override bool Equals(object? obj) => obj is FileReference other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(RecordNumber, SequenceNumber);

    public override string ToString() => $"{RecordNumber}#{SequenceNumber}";
}
