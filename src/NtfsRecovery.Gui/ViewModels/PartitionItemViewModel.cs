using NtfsRecovery.Core.Partitions;

namespace NtfsRecovery.Gui.ViewModels;

/// <summary>One row in the partition list, letting the user pick an offset instead of typing it by hand.</summary>
public sealed class PartitionItemViewModel(PartitionInfo info)
{
    public int Index { get; } = info.Index;
    public long StartOffset { get; } = info.StartOffset;
    public string TypeDescription { get; } = info.TypeDescription;
    public bool LooksLikeNtfs { get; } = info.LooksLikeNtfs;
    public string? VolumeLabel { get; } = info.VolumeLabel;

    public string SizeDisplay { get; } = FormatSize(info.LengthBytes);

    public string Display =>
        $"#{Index}  offset={StartOffset:N0}  {SizeDisplay}  {TypeDescription}" +
        (LooksLikeNtfs ? "  [parece NTFS]" : "") +
        (string.IsNullOrEmpty(VolumeLabel) ? "" : $"  \"{VolumeLabel}\"");

    private static string FormatSize(long bytes)
    {
        double gb = bytes / 1_000_000_000.0;
        return gb >= 1 ? $"{gb:N1} GB" : $"{bytes / 1_000_000.0:N1} MB";
    }

    public override string ToString() => Display;
}
