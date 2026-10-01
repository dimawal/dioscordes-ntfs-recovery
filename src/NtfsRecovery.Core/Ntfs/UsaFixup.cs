using System.Buffers.Binary;

namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// Applies the NTFS Update Sequence Array (USA) fixup to an in-memory copy of an MFT
/// record or index record. This never touches the source device: callers must pass a
/// private, already-copied buffer, and this class only mutates that buffer.
///
/// NTFS protects multi-sector structures against torn writes by storing the last two
/// bytes of every sector in the USA and replacing them, at write time, with a sequence
/// number. On read, those two bytes must be restored from the USA after verifying that
/// every sector's trailing bytes still match the stored sequence number -- a mismatch
/// signals a torn write or (for our purposes) that the "FILE" signature was a false
/// positive / the record is corrupted.
/// </summary>
public static class UsaFixup
{
    /// <summary>
    /// Attempts to apply the fixup in place. Returns false (with <paramref name="error"/>
    /// set) if the USA geometry is inconsistent with the buffer or any sector's trailing
    /// bytes do not match the expected update sequence number.
    /// </summary>
    public static bool TryApply(Span<byte> record, int bytesPerSector, ushort usaOffset, ushort usaSize, out string? error)
    {
        error = null;

        if (usaSize == 0)
        {
            error = "Update Sequence Array size is zero.";
            return false;
        }

        int usaByteLength = usaSize * 2;
        if (usaOffset < 0 || usaOffset + usaByteLength > record.Length)
        {
            error = "Update Sequence Array extends past the record buffer.";
            return false;
        }

        ushort expectedUsn = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));
        int sectorCount = usaSize - 1;

        if (sectorCount <= 0 || (long)sectorCount * bytesPerSector > record.Length)
        {
            error = "Update Sequence Array sector count does not fit within the record buffer.";
            return false;
        }

        for (int i = 0; i < sectorCount; i++)
        {
            int sectorEndOffset = (i + 1) * bytesPerSector - 2;

            ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(sectorEndOffset, 2));
            if (actual != expectedUsn)
            {
                error = $"USN mismatch at sector {i}: expected 0x{expectedUsn:X4}, found 0x{actual:X4}.";
                return false;
            }

            int usaEntryOffset = usaOffset + 2 + (i * 2);
            record.Slice(usaEntryOffset, 2).CopyTo(record.Slice(sectorEndOffset, 2));
        }

        return true;
    }
}
