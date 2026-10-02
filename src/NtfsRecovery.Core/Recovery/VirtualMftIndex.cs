using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Recovery;

/// <summary>
/// In-memory index of carved MFT records, keyed by their self-reported record number.
/// Carving a damaged volume can surface more than one record claiming the same number
/// (an old, deleted generation and a newer one reusing the slot), so every candidate is
/// kept; <see cref="ResolveBest"/> picks the most plausible single instance for linking
/// without discarding the others, matching the spec's "do not blindly trust the parent
/// record number" requirement -- sequence numbers are what actually disambiguate.
/// </summary>
public sealed class VirtualMftIndex
{
    private readonly Dictionary<uint, List<VirtualMftRecord>> _byRecordNumber = new();

    public int Count { get; private set; }

    public void Add(MftRecord record)
    {
        VirtualMftRecord? virtualRecord = VirtualMftRecord.FromMftRecord(record);
        if (virtualRecord is not null)
            AddVirtual(virtualRecord);
    }

    public void AddDto(Scan.ScanRecordDto dto) => AddVirtual(VirtualMftRecord.FromDto(dto));

    private void AddVirtual(VirtualMftRecord virtualRecord)
    {
        if (!_byRecordNumber.TryGetValue(virtualRecord.RecordNumber, out List<VirtualMftRecord>? list))
        {
            list = [];
            _byRecordNumber[virtualRecord.RecordNumber] = list;
        }

        list.Add(virtualRecord);
        Count++;
    }

    public IReadOnlyList<VirtualMftRecord> GetAllCandidates(uint recordNumber) =>
        _byRecordNumber.TryGetValue(recordNumber, out List<VirtualMftRecord>? list) ? list : [];

    public IEnumerable<uint> RecordNumbersWithDuplicates =>
        _byRecordNumber.Where(kvp => kvp.Value.Count > 1).Select(kvp => kvp.Key);

    /// <summary>
    /// Picks the best candidate for a record number: in-use beats deleted, and among
    /// equally-in-use candidates the higher sequence number (the more recent generation)
    /// wins. Returns null if the record number was never observed.
    /// </summary>
    public VirtualMftRecord? ResolveBest(ulong recordNumber)
    {
        if (recordNumber > uint.MaxValue)
            return null;

        if (!_byRecordNumber.TryGetValue((uint)recordNumber, out List<VirtualMftRecord>? candidates) || candidates.Count == 0)
            return null;

        return candidates
            .OrderByDescending(r => r.IsInUse)
            .ThenByDescending(r => r.SequenceNumber)
            .First();
    }

    /// <summary>All resolved (best-candidate) records, one per distinct record number.</summary>
    public IEnumerable<VirtualMftRecord> ResolvedRecords =>
        _byRecordNumber.Keys.Select(k => ResolveBest(k)!);

    /// <summary>
    /// Every carved candidate, with no collapsing by record number. Unlike
    /// <see cref="ResolvedRecords"/>, this preserves distinct (record number, sequence
    /// number) identities -- essential on a volume whose record numbers have been reused
    /// across unrelated filesystem layouts, where collapsing to "the single best overall
    /// candidate" for a record number can silently merge two completely unrelated files
    /// into one tree node.
    /// </summary>
    public IEnumerable<VirtualMftRecord> AllCandidates => _byRecordNumber.Values.SelectMany(list => list);

    /// <summary>
    /// Resolves the specific record a child should link to as its parent. A volume that
    /// has been reformatted/repartitioned can surface several unrelated candidates
    /// sharing the same record number (e.g. record 5 from an old filesystem layout and
    /// record 5 from the current one); <see cref="ResolveBest"/> picks a single "most
    /// plausible overall" candidate, which is not necessarily the one a given child's
    /// cached sequence number actually refers to. This searches every candidate for that
    /// record number for an exact sequence match first, and only falls back to the
    /// overall best candidate (flagging <paramref name="exactMatch"/> false) when none of
    /// them match -- matching the record number alone is still a strong signal, so this
    /// favors reconstructing structure over discarding it to $Orphans on a stale sequence.
    /// </summary>
    public VirtualMftRecord? ResolveForParentLink(ulong recordNumber, ushort expectedSequence, out bool exactMatch)
    {
        exactMatch = false;

        if (recordNumber > uint.MaxValue)
            return null;

        if (!_byRecordNumber.TryGetValue((uint)recordNumber, out List<VirtualMftRecord>? candidates) || candidates.Count == 0)
            return null;

        VirtualMftRecord? exact = candidates.FirstOrDefault(c => c.SequenceNumber == expectedSequence);
        if (exact is not null)
        {
            exactMatch = true;
            return exact;
        }

        return candidates
            .OrderByDescending(r => r.IsInUse)
            .ThenByDescending(r => r.SequenceNumber)
            .First();
    }
}
