namespace NtfsRecovery.Core.Recovery;

/// <summary>Path-based lookups over a reconstructed <see cref="RecoveryNode"/> tree, shared by the CLI and (later) the GUI.</summary>
public static class TreeNavigator
{
    public static RecoveryNode? FindByPath(RecoveryNode root, string path)
    {
        string[] segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        RecoveryNode current = root;

        foreach (string segment in segments)
        {
            RecoveryNode? next = current.Children.FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            current = next;
        }

        return current;
    }

    public static IEnumerable<RecoveryNode> FindAllByName(RecoveryNode root, string name)
    {
        if (string.Equals(root.Name, name, StringComparison.OrdinalIgnoreCase))
            yield return root;

        foreach (RecoveryNode child in root.Children)
            foreach (RecoveryNode match in FindAllByName(child, name))
                yield return match;
    }

    public static IEnumerable<RecoveryNode> EnumerateFiles(RecoveryNode node)
    {
        if (!node.IsDirectory)
        {
            yield return node;
            yield break;
        }

        foreach (RecoveryNode child in node.Children)
            foreach (RecoveryNode file in EnumerateFiles(child))
                yield return file;
    }
}
