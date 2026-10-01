using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Recovery;

public sealed record TreeBuildResult(RecoveryNode Root, int OrphanCount, int NonameCount, int CycleCount, int DuplicateRecordNumberCount, int SoftMatchCount);

/// <summary>
/// Reconstructs a virtual directory tree from a <see cref="VirtualMftIndex"/> using only
/// parent/child references recovered from $FILE_NAME attributes -- never a live $MFT.
///
/// MFT record 5 is conventionally the volume root, but it is never assumed to be intact:
/// if it is missing or not a directory, a synthetic "$RecoveredRoot" takes its place.
/// A child is only sent to "$Orphans" when its parent record number genuinely cannot be
/// found anywhere, or when linking it would create a cycle. A sequence-number mismatch
/// alone is not enough: on a volume that has been reformatted/repartitioned, several
/// unrelated candidates can share one record number, and the "obviously best" one is not
/// always the one a given child actually refers to (see
/// <see cref="VirtualMftIndex.ResolveForParentLink"/>). The record number is still a
/// strong identity signal on its own, so when no candidate's sequence matches exactly,
/// the child is still attached to the best available candidate rather than discarded --
/// this is tracked separately as a "soft match" (<see cref="TreeBuildResult.SoftMatchCount"/>)
/// so that lower-confidence links stay visible without losing the reconstructed structure.
/// Records with no resolvable name go under "$NonameFiles".
/// </summary>
public static class NtfsTreeBuilder
{
    private const uint RootRecordNumber = 5;

    public static TreeBuildResult Build(VirtualMftIndex index)
    {
        var orphans = new RecoveryNode { Name = "$Orphans" };
        var nonameFiles = new RecoveryNode { Name = "$NonameFiles" };

        VirtualMftRecord? rootRecord = index.ResolveBest(RootRecordNumber);
        RecoveryNode root = rootRecord is { IsDirectory: true }
            ? new RecoveryNode { Name = string.Empty, Record = rootRecord }
            : new RecoveryNode { Name = "$RecoveredRoot" };

        var nodeByRecordNumber = new Dictionary<uint, RecoveryNode> { [RootRecordNumber] = root };
        int nonameCount = 0;

        foreach (VirtualMftRecord candidate in index.ResolvedRecords)
        {
            if (candidate.RecordNumber == RootRecordNumber || candidate.IsExtensionRecord)
                continue;

            if (string.IsNullOrEmpty(candidate.Name))
            {
                nonameCount++;
                continue; // materialized directly under $NonameFiles in the second pass
            }

            nodeByRecordNumber[candidate.RecordNumber] = new RecoveryNode { Name = candidate.Name, Record = candidate };
        }

        int orphanCount = 0;
        int cycleCount = 0;
        int softMatchCount = 0;

        foreach ((uint recordNumber, RecoveryNode node) in nodeByRecordNumber)
        {
            if (recordNumber == RootRecordNumber)
                continue;

            VirtualMftRecord record = node.Record!;
            FileReference? parentRef = record.ParentRecordReference;

            if (parentRef is null)
            {
                orphans.AddChild(node);
                orphanCount++;
                continue;
            }

            VirtualMftRecord? resolvedParent = index.ResolveForParentLink(parentRef.Value.RecordNumber, parentRef.Value.SequenceNumber, out bool exactMatch);

            if (resolvedParent is null || !nodeByRecordNumber.TryGetValue((uint)parentRef.Value.RecordNumber, out RecoveryNode? parentNode))
            {
                orphans.AddChild(node);
                orphanCount++;
                continue;
            }

            if (CreatesCycle(parentNode!, recordNumber, nodeByRecordNumber))
            {
                orphans.AddChild(node);
                cycleCount++;
                continue;
            }

            if (!exactMatch)
                softMatchCount++;

            parentNode.AddChild(node);
        }

        foreach (VirtualMftRecord candidate in index.ResolvedRecords)
        {
            if (candidate.RecordNumber != RootRecordNumber && !candidate.IsExtensionRecord && string.IsNullOrEmpty(candidate.Name))
            {
                nonameFiles.AddChild(new RecoveryNode { Name = $"#{candidate.RecordNumber}", Record = candidate });
            }
        }

        root.AddChild(orphans);
        root.AddChild(nonameFiles);

        int duplicates = index.RecordNumbersWithDuplicates.Count();

        return new TreeBuildResult(root, orphanCount, nonameCount, cycleCount, duplicates, softMatchCount);
    }

    /// <summary>Walks from <paramref name="startParent"/> up through recorded parent links to see whether it ever reaches <paramref name="childRecordNumber"/> again.</summary>
    private static bool CreatesCycle(RecoveryNode startParent, uint childRecordNumber, Dictionary<uint, RecoveryNode> nodeByRecordNumber)
    {
        var visited = new HashSet<uint>();
        RecoveryNode? current = startParent;

        while (current is not null)
        {
            uint? currentNumber = current.Record?.RecordNumber;
            if (currentNumber is null)
                return false; // reached root or a synthetic node without looping back

            if (currentNumber.Value == childRecordNumber)
                return true;

            if (currentNumber.Value == RootRecordNumber)
                return false; // reached the real root; its self-referential parent is normal, not a cycle

            if (!visited.Add(currentNumber.Value))
                return true; // a cycle exists further up, independent of this child

            FileReference? parentRef = current.Record!.ParentRecordReference;
            if (parentRef is null || !nodeByRecordNumber.TryGetValue((uint)parentRef.Value.RecordNumber, out current))
                return false;
        }

        return false;
    }
}
