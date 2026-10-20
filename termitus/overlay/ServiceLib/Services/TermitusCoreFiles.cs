namespace ServiceLib.Services;

/// <summary>
/// 0.6.5 (Windows): antivirus (Defender) may quarantine or delete a core file (xray.exe, sing-box.exe) of an unsigned program.
/// TER must say so plainly instead of silently failing or closing. This is a plain presence check of the files TER needs
/// (nothing here tries to hide or restore anything): the caller turns a non-empty answer into the
/// «Защитник Windows заблокировал файл ядра» card.
/// </summary>
public static class TermitusCoreFiles
{
    /// <summary>Names (as the user sees them in a quarantine list) of missing / empty core files; empty = all there.</summary>
    public static List<string> FindMissing(string binDir, bool needTun, Func<string, long?> sizeOf)
    {
        var missing = new List<string>();
        void Check(string rel, string name)
        {
            var size = sizeOf(Path.Combine(binDir, rel));
            if (size is null or <= 0)
            {
                missing.Add(name);
            }
        }
        Check(Path.Combine("xray", "xray.exe"), "xray.exe");
        if (needTun)
        {
            Check(Path.Combine("sing_box", "sing-box.exe"), "sing-box.exe");
        }
        return missing;
    }

    private static long? SizeOnDisk(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? fi.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Windows only; other platforms always report nothing missing.</summary>
    public static List<string> MissingOnWindows(bool needTun)
        => OperatingSystem.IsWindows() ? FindMissing(Utils.GetBinPath(""), needTun, SizeOnDisk) : [];
}
