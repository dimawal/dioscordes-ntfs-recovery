namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// Parses an NTFS non-resident attribute run list into <see cref="DataRun"/> entries.
///
/// Run list format: a sequence of runs, each starting with a header byte whose low
/// nibble gives the byte length of the following (unsigned) cluster-count field and
/// whose high nibble gives the byte length of the following (signed) LCN-offset field.
/// The LCN offset is relative to the previous run's LCN (not absolute), can be negative
/// (sparse files reusing freed space before the previous run), and is omitted entirely
/// (offset field length 0) for sparse runs. The list ends with a single 0x00 byte.
/// </summary>
public static class DataRunParser
{
    public static List<DataRun> Parse(ReadOnlySpan<byte> runList)
    {
        var runs = new List<DataRun>();
        long vcn = 0;
        long lcn = 0;
        int pos = 0;

        while (pos < runList.Length)
        {
            byte header = runList[pos];
            if (header == 0)
                break; // terminator

            int lengthFieldSize = header & 0x0F;
            int offsetFieldSize = (header >> 4) & 0x0F;
            pos++;

            if (lengthFieldSize == 0)
                throw new InvalidDataException("Data run has a zero-length cluster-count field.");

            if (pos + lengthFieldSize + offsetFieldSize > runList.Length)
                throw new InvalidDataException("Data run header declares fields that exceed the run list buffer.");

            long clusterCount = ReadUnsignedLittleEndian(runList.Slice(pos, lengthFieldSize));
            pos += lengthFieldSize;

            bool isSparse = offsetFieldSize == 0;
            long? absoluteLcn = null;

            if (!isSparse)
            {
                long offsetDelta = ReadSignedLittleEndian(runList.Slice(pos, offsetFieldSize));
                pos += offsetFieldSize;
                lcn += offsetDelta;
                absoluteLcn = lcn;
            }

            runs.Add(new DataRun(vcn, clusterCount, absoluteLcn, isSparse));
            vcn += clusterCount;
        }

        return runs;
    }

    private static long ReadUnsignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);
        return value;
    }

    private static long ReadSignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (int i = 0; i < bytes.Length; i++)
            value |= (long)bytes[i] << (8 * i);

        bool isNegative = bytes.Length > 0 && (bytes[^1] & 0x80) != 0;
        if (isNegative && bytes.Length < 8)
            value |= -1L << (8 * bytes.Length);

        return value;
    }
}
