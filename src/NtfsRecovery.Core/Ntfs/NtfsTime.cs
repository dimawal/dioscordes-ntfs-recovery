namespace NtfsRecovery.Core.Ntfs;

/// <summary>
/// NTFS timestamps are Win32 FILETIME values (100ns intervals since 1601-01-01 UTC),
/// which happens to be the same epoch .NET uses internally. During carving, garbage or
/// partially-overwritten bytes frequently decode to out-of-range values, so conversion
/// must never throw.
/// </summary>
public static class NtfsTime
{
    public static DateTime? TryToDateTimeUtc(ulong fileTime)
    {
        if (fileTime == 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc((long)fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
