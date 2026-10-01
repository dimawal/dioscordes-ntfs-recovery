using NtfsRecovery.Core.Recovery;
using NtfsRecovery.Core.Scan;

namespace NtfsRecovery.Cli;

internal sealed record LoadedTree(TreeBuildResult TreeResult, ScanGeometry? Geometry, IReadOnlyList<ScanRecordDto> Records);

/// <summary>Shared "load a persisted scan and rebuild the tree" step used by tree/list/info/extract-record/recover.</summary>
internal static class TreeLoader
{
    public static bool TryLoad(string scanDbPath, out LoadedTree? result, out string? error)
    {
        result = null;
        error = null;

        if (!File.Exists(scanDbPath))
        {
            error = $"scan database not found: {scanDbPath}";
            return false;
        }

        using ScanStore store = ScanStore.OpenOrCreate(scanDbPath);
        List<ScanRecordDto> rawRecords = store.LoadAllRecords();
        ScanGeometry? geometry = store.TryLoadGeometry();

        List<ScanRecordDto> resolved = ScanRecordAttributeListResolver.Resolve(rawRecords);

        var index = new VirtualMftIndex();
        foreach (ScanRecordDto dto in resolved)
            index.AddDto(dto);

        TreeBuildResult treeResult = NtfsTreeBuilder.Build(index);
        result = new LoadedTree(treeResult, geometry, resolved);
        return true;
    }
}
