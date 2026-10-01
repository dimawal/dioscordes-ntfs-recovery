using NtfsRecovery.Core.Recovery;

namespace NtfsRecovery.Gui.ViewModels;

/// <summary>One row of the file/folder DataGrid for the currently selected directory.</summary>
public sealed class FileListItemViewModel(RecoveryNode node)
{
    public RecoveryNode Node { get; } = node;
    public string Name { get; } = node.Name;
    public string Type { get; } = node.IsDirectory ? "Pasta" : "Arquivo";
    public ulong Size { get; } = node.Record?.Dto.LogicalSize ?? 0;
    public string Record { get; } = node.Record?.RecordNumber.ToString() ?? "-";
    public string State { get; } = node.Record is null ? "synthetic" : node.Record.IsInUse ? "in-use" : "deleted";
    public int Runs { get; } = node.Record?.Dto.DataRuns.Count ?? 0;

    /// <summary>Keeps UI Automation / screen readers from announcing the CLR type name for a selected row.</summary>
    public override string ToString() => Name;
}
