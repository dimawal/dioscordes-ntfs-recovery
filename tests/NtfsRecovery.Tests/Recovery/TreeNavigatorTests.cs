using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Tests.TestSupport;
using Xunit;

namespace NtfsRecovery.Tests.Recovery;

public class TreeNavigatorTests
{
    private static RecoveryNode BuildSampleTree()
    {
        var index = new VirtualMftIndex();
        index.Add(TestMftRecordFactory.Create(5, 1, null, 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1000, 1, "Mateus Campos", 5, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(1002, 1, "SNES", 1000, 1, isDirectory: true));
        index.Add(TestMftRecordFactory.Create(2000, 1, "rom.sfc", 1002, 1));
        return NtfsTreeBuilder.Build(index).Root;
    }

    [Fact]
    public void FindByPath_NestedPath_ResolvesCorrectNode()
    {
        RecoveryNode root = BuildSampleTree();

        RecoveryNode? node = TreeNavigator.FindByPath(root, @"\Mateus Campos\SNES\rom.sfc");

        Assert.NotNull(node);
        Assert.Equal("rom.sfc", node!.Name);
        Assert.False(node.IsDirectory);
    }

    [Fact]
    public void FindByPath_CaseInsensitive_Matches()
    {
        RecoveryNode root = BuildSampleTree();

        RecoveryNode? node = TreeNavigator.FindByPath(root, @"\mateus campos\snes");

        Assert.NotNull(node);
        Assert.Equal("SNES", node!.Name);
    }

    [Fact]
    public void FindByPath_UnknownSegment_ReturnsNull()
    {
        RecoveryNode root = BuildSampleTree();

        Assert.Null(TreeNavigator.FindByPath(root, @"\Mateus Campos\DoesNotExist"));
    }

    [Fact]
    public void FindAllByName_FindsKnownDirectoryAnywhereInTree()
    {
        RecoveryNode root = BuildSampleTree();

        RecoveryNode found = TreeNavigator.FindAllByName(root, "SNES").Single();

        Assert.Equal(@"\Mateus Campos\SNES", found.FullPath);
    }

    [Fact]
    public void EnumerateFiles_ReturnsOnlyLeafFiles()
    {
        RecoveryNode root = BuildSampleTree();

        RecoveryNode[] files = TreeNavigator.EnumerateFiles(root).ToArray();

        Assert.Contains(files, f => f.Name == "rom.sfc");
        Assert.DoesNotContain(files, f => f.Name == "SNES");
    }
}
