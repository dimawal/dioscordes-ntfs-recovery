using NtfsRecovery.Core.Recovery;

namespace NtfsRecovery.Gui.ViewModels;

/// <summary>Thin TreeView-binding wrapper around a <see cref="RecoveryNode"/>. Only directories are shown in the tree; files appear in the file list for the selected directory.</summary>
public sealed class TreeItemViewModel
{
    public RecoveryNode Node { get; }
    public string Name { get; }
    public List<TreeItemViewModel> Children { get; }

    public TreeItemViewModel(RecoveryNode node)
    {
        Node = node;
        Name = node.Name.Length == 0 ? "\\" : node.Name;
        Children = node.Children
            .Where(c => c.IsDirectory)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new TreeItemViewModel(c))
            .ToList();
    }

    /// <summary>
    /// WPF's default automation peer for a virtualized TreeViewItem falls back to the
    /// bound item's ToString() for its accessible Name when no explicit
    /// AutomationProperties.Name is set, so without this override screen readers (and UI
    /// automation tooling) would announce the CLR type name instead of the folder name.
    /// </summary>
    public override string ToString() => Name;
}
