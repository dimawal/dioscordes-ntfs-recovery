using NtfsRecovery.Core.Ntfs;
using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Recovery;

public class NtfsTreeBuilderTests
{
    [Fact]
    public void Build_HealthyRoot_AttachesKnownDirectoriesUnderRoot()
    {
        // Mirrors the real-world case this tool targets: a healthy-ish root with the
        // known top-level folders DMDE/Disk Drill also found.
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1000, 1, "Mateus Campos", 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1001, 1, "BACKUP CELULAR", 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1002, 1, "SNES", 1000, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(2000, 1, "rom.sfc", 1002, 1));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        RecoveryNode? mateusCampos = result.Root.Children.FirstOrDefault(c => c.Name == "Mateus Campos");
        Assert.NotNull(mateusCampos);

        RecoveryNode? backupCelular = result.Root.Children.FirstOrDefault(c => c.Name == "BACKUP CELULAR");
        Assert.NotNull(backupCelular);

        RecoveryNode? snes = mateusCampos!.Children.FirstOrDefault(c => c.Name == "SNES");
        Assert.NotNull(snes);
        Assert.Single(snes!.Children);
        Assert.Equal("rom.sfc", snes.Children[0].Name);

        Assert.Equal(0, result.OrphanCount);
        Assert.Equal(0, result.CycleCount);
    }

    [Fact]
    public void Build_RootHasSelfReferentialFileName_IsNotTreatedAsACycle()
    {
        // Real NTFS root records commonly carry a $FILE_NAME whose parent reference
        // points back at record 5 itself. That must not be mistaken for a cycle.
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, ".", 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1000, 1, "Mateus Campos", 5, 1, isDirectory: true));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.Equal(0, result.OrphanCount);
        Assert.Equal(0, result.CycleCount);
        Assert.Contains(result.Root.Children, c => c.Name == "Mateus Campos");
    }

    [Fact]
    public void Build_MissingRoot_CreatesRecoveredRootSynthetic()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(1000, 1, "orphaned-but-parent-exists.txt", 5, 1));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.Equal("$RecoveredRoot", result.Root.Name);
        // Root record 5 was never observed, so even a syntactically valid parent
        // reference to it cannot be confirmed -> orphan.
        Assert.Equal(1, result.OrphanCount);
    }

    [Fact]
    public void Build_SequenceNumberMismatch_NoAlternateCandidate_StillAttachesAsSoftMatch()
    {
        // Parent slot 1000 only has one carved candidate (sequence 2), but this child's
        // $FILE_NAME references sequence 1. Matching DMDE/Disk Drill behavior on real,
        // reformatted/repartitioned disks: the record number is still a strong enough
        // signal on its own, so the child attaches anyway -- just counted as a lower-
        // confidence "soft match" instead of being silently discarded to $Orphans.
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1000, 2, "ReusedDir", 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(2000, 1, "stale-child.txt", 1000, 1));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.Equal(0, result.OrphanCount);
        Assert.Equal(1, result.SoftMatchCount);

        RecoveryNode reusedDir = result.Root.Children.Single(c => c.Name == "ReusedDir");
        Assert.Contains(reusedDir.Children, c => c.Name == "stale-child.txt");
    }

    [Fact]
    public void Build_ParentHasMultipleCandidates_ExactSequenceMatchAmongThem_StillAttaches()
    {
        // Reproduces the real-world bug this fix targets: record number 5 (root) has TWO
        // carved candidates -- an unrelated leftover from a prior filesystem layout on
        // the same disk (sequence 9, in-use, so "pick the single overall best" naturally
        // prefers it) and the actual root generation this child's $FILE_NAME was written
        // against (sequence 1). Resolving the parent by "best candidate overall" and
        // requiring an exact sequence match would wrongly orphan BACKUP CELULAR even
        // though an exact match exists among root's *other* candidates.
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 9, null, 5, 9, isDirectory: true, isInUse: true));
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true, isInUse: false));
        index.Add(TestMftRecordFactory.Create(1001, 1, "BACKUP CELULAR", 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(3000, 1, "photo.jpg", 1001, 1));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.Equal(0, result.OrphanCount);
        Assert.Equal(0, result.SoftMatchCount); // exact match was found, so this is full-confidence

        RecoveryNode backupCelular = result.Root.Children.Single(c => c.Name == "BACKUP CELULAR");
        Assert.Contains(backupCelular.Children, c => c.Name == "photo.jpg");
    }

    [Fact]
    public void Build_ParentReferencesMissingRecord_PlacedInOrphans()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(2000, 1, "file.txt", 999_999, 1));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.Equal(1, result.OrphanCount);
    }

    [Fact]
    public void Build_Cycle_BreaksAndPlacesInOrphans()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true));
        // A points to B as parent, B points to A as parent: a two-node cycle,
        // disconnected from the real root.
        index.Add(TestMftRecordFactory.Create(100, 1, "A", 101, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(101, 1, "B", 100, 1, isDirectory: true));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.True(result.CycleCount >= 1);
        RecoveryNode orphansNode = result.Root.Children.Single(c => c.Name == "$Orphans");
        Assert.True(orphansNode.Children.Count >= 1);
    }

    [Fact]
    public void Build_RecordWithoutName_PlacedInNonameFiles()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(2000, 1, null, 5, 1));

        TreeBuildResult result = NtfsTreeBuilder.Build(index);

        Assert.Equal(1, result.NonameCount);
        RecoveryNode nonameNode = result.Root.Children.Single(c => c.Name == "$NonameFiles");
        Assert.Contains(nonameNode.Children, c => c.Name == "#2000");
    }
}
