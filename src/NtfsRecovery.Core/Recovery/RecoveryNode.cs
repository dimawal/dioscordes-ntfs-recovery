namespace NtfsRecovery.Core.Recovery;

/// <summary>
/// A node in the reconstructed virtual directory tree. Synthetic nodes (the recovered
/// root, $Orphans, $NonameFiles) have <see cref="Record"/> set to null.
/// </summary>
public sealed class RecoveryNode
{
    public required string Name { get; init; }
    public VirtualMftRecord? Record { get; init; }
    public RecoveryNode? Parent { get; private set; }
    public List<RecoveryNode> Children { get; } = [];

    public bool IsDirectory => Record is null || Record.IsDirectory;

    public void AddChild(RecoveryNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public string FullPath
    {
        get
        {
            if (Parent is null)
                return "\\";

            string parentPath = Parent.FullPath;
            return parentPath == "\\" ? $"\\{Name}" : $"{parentPath}\\{Name}";
        }
    }
}
