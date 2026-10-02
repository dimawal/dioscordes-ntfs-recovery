using NtfsRecovery.Core.Ntfs;

namespace NtfsRecovery.Core.Recovery;

public sealed record TreeBuildResult(RecoveryNode Root, int OrphanCount, int NonameCount, int CycleCount, int DuplicateRecordNumberCount, int SoftMatchCount);

/// <summary>
/// Reconstructs a virtual directory tree from a <see cref="VirtualMftIndex"/> using only
/// parent/child references recovered from $FILE_NAME attributes -- never a live $MFT.
///
/// Every carved candidate gets its own tree node, keyed by its full (record number,
/// sequence number) identity -- NOT just its record number. On a volume whose record
/// numbers have been reused across filesystem layouts (e.g. after repartitioning), two
/// completely unrelated files can share one record number; collapsing them to a single
/// "best" node per record number would silently merge one file's identity into another's
/// (a real case: record 37 carried both a "BACKUP CELULAR" directory at sequence 2 and
/// an unrelated "bootmgr.exe.mui" file at sequence 97 -- picking "the best" would attach
/// BACKUP CELULAR's own children to the bootmgr.exe.mui node instead).
///
/// MFT record 5 is conventionally the volume root, but it is never assumed to be intact:
/// if it is missing or not a directory, a synthetic "$RecoveredRoot" takes its place.
/// Because record 5 is singular by NTFS convention, any reference to record 5 (regardless
/// of which sequence number it names) resolves to that one root node.
///
/// A child is only sent to "$Orphans" when its parent identity genuinely cannot be
/// resolved to a directory node, or when linking it would create a cycle. When no
/// candidate's sequence matches a child's expectation exactly, the child still attaches
/// to the best available candidate for that record number (tracked as a "soft match")
/// rather than being discarded -- the record number alone is still a strong signal.
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

        var nodeByIdentity = new Dictionary<(uint RecordNumber, ushort SequenceNumber), RecoveryNode>();
        int nonameCount = 0;

        foreach (VirtualMftRecord candidate in index.AllCandidates)
        {
            if (candidate.IsExtensionRecord || candidate.RecordNumber == RootRecordNumber)
                continue; // root is handled as a single singular node below, regardless of its candidates' sequences

            if (string.IsNullOrEmpty(candidate.Name))
            {
                nonameCount++;
                continue; // materialized directly under $NonameFiles in the final pass
            }

            nodeByIdentity[(candidate.RecordNumber, candidate.SequenceNumber)] = new RecoveryNode { Name = candidate.Name, Record = candidate };
        }

        int orphanCount = 0;
        int cycleCount = 0;
        int softMatchCount = 0;

        foreach (((uint recordNumber, ushort sequenceNumber), RecoveryNode node) in nodeByIdentity)
        {
            VirtualMftRecord record = node.Record!;
            FileReference? parentRef = record.ParentRecordReference;

            if (parentRef is null)
            {
                orphans.AddChild(node);
                orphanCount++;
                continue;
            }

            VirtualMftRecord? resolvedParent = index.ResolveForParentLink(parentRef.Value.RecordNumber, parentRef.Value.SequenceNumber, out bool exactMatch);
            RecoveryNode? parentNode = ResolveParentNode(resolvedParent, root, nodeByIdentity);

            if (resolvedParent is null || !resolvedParent.IsDirectory || parentNode is null)
            {
                orphans.AddChild(node);
                orphanCount++;
                continue;
            }

            if (CreatesCycle(parentNode, (recordNumber, sequenceNumber), root, nodeByIdentity))
            {
                orphans.AddChild(node);
                cycleCount++;
                continue;
            }

            if (!exactMatch)
                softMatchCount++;

            parentNode.AddChild(node);
        }

        foreach (VirtualMftRecord candidate in index.AllCandidates)
        {
            if (!candidate.IsExtensionRecord && candidate.RecordNumber != RootRecordNumber && string.IsNullOrEmpty(candidate.Name))
                nonameFiles.AddChild(new RecoveryNode { Name = $"#{candidate.RecordNumber}#{candidate.SequenceNumber}", Record = candidate });
        }

        root.AddChild(orphans);
        root.AddChild(nonameFiles);

        int duplicates = index.RecordNumbersWithDuplicates.Count();

        return new TreeBuildResult(root, orphanCount, nonameCount, cycleCount, duplicates, softMatchCount);
    }

    /// <summary>Record 5 is singular: any resolved parent naming record 5 (any sequence) is the one root node.</summary>
    private static RecoveryNode? ResolveParentNode(
        VirtualMftRecord? resolvedParent, RecoveryNode root, Dictionary<(uint, ushort), RecoveryNode> nodeByIdentity)
    {
        if (resolvedParent is null)
            return null;

        if (resolvedParent.RecordNumber == RootRecordNumber)
            return root;

        return nodeByIdentity.GetValueOrDefault((resolvedParent.RecordNumber, resolvedParent.SequenceNumber));
    }

    /// <summary>Walks from <paramref name="startParent"/> up through recorded parent links to see whether it ever reaches <paramref name="childIdentity"/> again.</summary>
    private static bool CreatesCycle(
        RecoveryNode startParent, (uint RecordNumber, ushort SequenceNumber) childIdentity,
        RecoveryNode root, Dictionary<(uint, ushort), RecoveryNode> nodeByIdentity)
    {
        var visited = new HashSet<(uint, ushort)>();
        RecoveryNode? current = startParent;

        while (current is not null)
        {
            if (current == root)
                return false; // reached the singular root without looping back

            if (current.Record is null)
                return false; // reached a synthetic node (e.g. $RecoveredRoot)

            var currentIdentity = (current.Record.RecordNumber, current.Record.SequenceNumber);
            if (currentIdentity == childIdentity)
                return true;

            if (!visited.Add(currentIdentity))
                return true; // a cycle exists further up, independent of this child

            FileReference? parentRef = current.Record.ParentRecordReference;
            if (parentRef is null)
                return false;

            current = parentRef.Value.RecordNumber == RootRecordNumber
                ? root
                : nodeByIdentity.GetValueOrDefault(((uint)parentRef.Value.RecordNumber, parentRef.Value.SequenceNumber));
        }

        return false;
    }
}
