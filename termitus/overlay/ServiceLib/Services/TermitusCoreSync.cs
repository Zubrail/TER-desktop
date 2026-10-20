using System.Security.Cryptography;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.5: upstream copies the bundled bin\ into the data folder only once (overwrite=false), so a core
/// (xray / sing-box / wintun / libcronet) that comes with a new TER would never replace the old copy. At startup, before
/// any core runs, files of the core folders whose content differs from the bundled one are overwritten. A locked or
/// unwritable target is skipped (neutral log line) and retried at the next start. Same code on Windows and macOS.
/// </summary>
public static class TermitusCoreSync
{
    private const string _tag = "TermitusCoreSync";

    /// <summary>Core folders inside bin\ (geo files have their own marker-based sync).</summary>
    public static readonly string[] CoreDirs = ["xray", "sing_box"];

    /// <summary>"xray/xray.exe" and "sing_box/libcronet.dll" yes; "geoip.dat", "other/x" no.</summary>
    public static bool IsCoreFile(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && CoreDirs.Contains(parts[0], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refresh decision for one file. Missing target or different size = refresh without hashing;
    /// equal size = compare sha256 (hashes are only computed then).
    /// </summary>
    public static bool NeedsRefresh(long srcSize, long? dstSize, Func<string> srcSha256, Func<string> dstSha256)
    {
        if (dstSize == null || dstSize != srcSize)
        {
            return true;
        }
        return !string.Equals(srcSha256(), dstSha256(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static void Refresh(string fromBinPath, string toBinPath)
    {
        try
        {
            if (fromBinPath == toBinPath || !Directory.Exists(fromBinPath))
            {
                return;
            }
            foreach (var src in Directory.EnumerateFiles(fromBinPath, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(fromBinPath, src);
                if (!IsCoreFile(rel))
                {
                    continue;
                }
                var dst = Path.Combine(toBinPath, rel);
                try
                {
                    var dstInfo = new FileInfo(dst);
                    if (!NeedsRefresh(new FileInfo(src).Length, dstInfo.Exists ? dstInfo.Length : null, () => Sha(src), () => Sha(dst)))
                    {
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, true);
                    Logging.SaveLog($"{_tag}: core file refreshed ({Path.GetFileName(rel)})");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logging.SaveLog($"{_tag}: could not refresh a core file ({ex.GetType().Name}), will retry at next start");
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: failed ({ex.GetType().Name})");
        }
    }
}
