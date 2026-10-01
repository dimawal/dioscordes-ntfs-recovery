using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Scan;

/// <summary>
/// DTO-level counterpart to <see cref="Recovery.AttributeListResolver"/>: merges a
/// fragmented file's $DATA runs from its extension records, operating purely on
/// persisted <see cref="ScanRecordDto"/> rows. Because it only needs the final set of
/// records (not the live scan's in-memory <c>MftRecord</c> graph), it can run after
/// reloading a scan database that was built incrementally across a resumed, checkpointed
/// scan -- the base and its extension records do not need to have been seen in the same
/// process run.
/// </summary>
public static class ScanRecordAttributeListResolver
{
    public static List<ScanRecordDto> Resolve(IReadOnlyList<ScanRecordDto> allRecords)
    {
        var byRecordNumber = new Dictionary<uint, ScanRecordDto>();
        foreach (ScanRecordDto record in allRecords)
            byRecordNumber[record.RecordNumber] = record; // last-wins on duplicate record numbers (v1 simplification)

        var results = new List<ScanRecordDto>();

        foreach (ScanRecordDto record in allRecords)
        {
            if (record.IsExtensionRecord)
                continue;

            if (record.AttributeListEntries.Count == 0 || !record.IsDataNonResident)
            {
                results.Add(record);
                continue;
            }

            var mergedRuns = new List<DataRunDto>(record.DataRuns);
            var visited = new HashSet<ulong>();
            bool merged = false;

            foreach (AttributeListEntryDto entry in record.AttributeListEntries)
            {
                if (entry.Type != NtfsAttributeType.Data || entry.Name is not null)
                    continue;

                if (entry.HostRecordNumber == record.RecordNumber || entry.HostRecordNumber > uint.MaxValue)
                    continue;

                if (!visited.Add(entry.HostRecordNumber))
                    continue;

                if (!byRecordNumber.TryGetValue((uint)entry.HostRecordNumber, out ScanRecordDto? extension))
                    continue;

                bool claimsThisBase =
                    extension.BaseRecordNumber == record.RecordNumber &&
                    extension.BaseSequenceNumber == record.SequenceNumber;
                if (!claimsThisBase)
                    continue;

                mergedRuns.AddRange(extension.DataRuns);
                merged = true;
            }

            if (merged)
                record.DataRuns = [.. mergedRuns.OrderBy(r => r.VcnStart)];

            results.Add(record);
        }

        return results;
    }
}
