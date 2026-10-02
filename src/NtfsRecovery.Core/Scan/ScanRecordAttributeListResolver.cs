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
        // Keyed by (RecordNumber, SequenceNumber), not just RecordNumber: a volume whose
        // record numbers have been reused (e.g. after repartitioning) can have several
        // unrelated records sharing one number, and collapsing them to "whichever record
        // was seen last" can silently pick the wrong one as an extension record -- the
        // real extension record then never gets merged in, and the base record's
        // DataRuns are left with an undetected gap (see MftRecordParser/FileExtractor's
        // VCN-contiguity check for how that gap is now caught instead of silently
        // producing a corrupted file).
        var byIdentity = new Dictionary<(uint RecordNumber, ushort SequenceNumber), ScanRecordDto>();
        foreach (ScanRecordDto record in allRecords)
            byIdentity[(record.RecordNumber, record.SequenceNumber)] = record;

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

                if (!byIdentity.TryGetValue(((uint)entry.HostRecordNumber, entry.HostSequenceNumber), out ScanRecordDto? extension))
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
