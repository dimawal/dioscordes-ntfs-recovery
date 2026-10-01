using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Recovery;

/// <summary>
/// Merges a heavily fragmented file's $DATA runs back together when its run list didn't
/// fit in one MFT record and spilled into extension records referenced by
/// $ATTRIBUTE_LIST. Only the unnamed primary data stream is merged (consistent with the
/// rest of this engine's v1 scope, which does not track alternate data streams).
///
/// Every extension record is validated against the base record it claims to extend
/// (both record number and sequence number) before anything from it is trusted -- a
/// reference is not enough; the carved extension record must actually assert the same
/// base identity, otherwise it is silently skipped rather than corrupting the merge.
/// </summary>
public static class AttributeListResolver
{
    public static MftRecord Resolve(MftRecord baseRecord, Func<ulong, MftRecord?> lookupByRecordNumber)
    {
        AttributeListAttribute? attributeList = baseRecord.AttributeList;
        if (attributeList is null)
            return baseRecord;

        DataAttribute? baseData = baseRecord.DataAttributes.FirstOrDefault(d => d.Name is null);
        if (baseData is null || !baseData.IsNonResident)
            return baseRecord; // nothing to extend: resident data and no-data records are already complete

        var mergedRuns = new List<DataRun>(baseData.DataRuns ?? []);
        var visitedExtensionRecords = new HashSet<ulong>();
        bool merged = false;

        foreach (AttributeListEntry entry in attributeList.Entries)
        {
            if (entry.Type != NtfsAttributeType.Data || entry.Name is not null)
                continue;

            ulong hostRecordNumber = entry.BaseFileReference.RecordNumber;
            if (hostRecordNumber == baseRecord.RecordNumber)
                continue; // this instance is the one already parsed into baseRecord

            if (!visitedExtensionRecords.Add(hostRecordNumber))
                continue; // already processed; guards against repeated/cyclic entries

            MftRecord? extensionRecord = lookupByRecordNumber(hostRecordNumber);
            if (extensionRecord is null)
                continue; // extension record not found during carving; merge what we have

            bool claimsThisBase =
                extensionRecord.BaseFileRecord.RecordNumber == baseRecord.RecordNumber &&
                extensionRecord.BaseFileRecord.SequenceNumber == baseRecord.SequenceNumber;
            if (!claimsThisBase)
                continue; // extension record doesn't actually assert this base identity; don't trust it

            DataAttribute? extensionData = extensionRecord.DataAttributes
                .FirstOrDefault(d => d.Name is null && d.AttributeId == entry.AttributeId);
            if (extensionData?.DataRuns is null)
                continue;

            mergedRuns.AddRange(extensionData.DataRuns);
            merged = true;
        }

        if (!merged)
            return baseRecord;

        var mergedDataAttribute = DataAttribute.ParseNonResident(
            new AttributeCommon(baseData.Type, baseData.RawTypeCode, true, baseData.Name, baseData.AttributeId, baseData.AttributeLength),
            mergedRuns.OrderBy(r => r.VcnStart).ToList(),
            baseData.AllocatedSize,
            baseData.RealSize,
            baseData.InitializedSize,
            baseData.CompressionUnit);

        List<NtfsAttribute> newAttributes = [.. baseRecord.Attributes.Where(a => !ReferenceEquals(a, baseData)), mergedDataAttribute];

        return new MftRecord
        {
            SourceOffset = baseRecord.SourceOffset,
            RecordNumber = baseRecord.RecordNumber,
            SequenceNumber = baseRecord.SequenceNumber,
            IsInUse = baseRecord.IsInUse,
            IsDirectory = baseRecord.IsDirectory,
            BaseFileRecord = baseRecord.BaseFileRecord,
            BytesInUse = baseRecord.BytesInUse,
            BytesAllocated = baseRecord.BytesAllocated,
            Attributes = newAttributes,
        };
    }
}
