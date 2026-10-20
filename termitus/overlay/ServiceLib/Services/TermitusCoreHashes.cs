using System.Security.Cryptography;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.5 security review D-1/D-2: the expected sha256 of the bundled cores, anchored in the app itself.
/// package-ter-osx.sh / package-ter-win.sh replace the <c>Raw</c> line below with the real hashes of the cores they put
/// into the package (before dotnet publish), so the pin travels inside the compiled, signed binary. Whatever the privileged
/// side runs must match this table; a hash computed at run time from a file in a user-writable folder proves nothing.
/// Format of Raw: "file-name=sha256hex;file-name=sha256hex" (file name without folders, lower-case, e.g. sing-box, xray.exe).
/// An empty / unknown entry means "refuse" (fail closed). Debug builds fall back to hashing the file (developer convenience, compiled out of Release).
/// </summary>
public static class TermitusCoreHashes
{
    // @@CORE_HASHES@@ (replaced by the package scripts; do not edit by hand)
    private const string Raw = "";

    private static readonly Lazy<Dictionary<string, string>> Table = new(() => Parse(Raw));

    /// <summary>"a=hex;b=hex" -> map (lower-case names/hashes); malformed entries are dropped.</summary>
    public static Dictionary<string, string> Parse(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0)
            {
                continue;
            }
            var name = part[..i].Trim();
            var hex = part[(i + 1)..].Trim().ToLowerInvariant();
            if (name.Length > 0 && hex.Length == 64 && hex.All(Uri.IsHexDigit))
            {
                d[name] = hex;
            }
        }
        return d;
    }

    /// <summary>Embedded expected hash for the core at <paramref name="path"/> (matched by file name); null = unknown, refuse.</summary>
    public static string? ExpectedFor(string path, string? rawOverride = null)
    {
        var table = rawOverride != null ? Parse(rawOverride) : Table.Value;
        var name = Path.GetFileName(path ?? "");
        if (table.TryGetValue(name, out var h))
        {
            return h;
        }
#if DEBUG
        if (rawOverride == null && table.Count == 0)
        {
            return FileSha256(path ?? ""); // Debug build without a package step only
        }
#endif
        return null;
    }

    /// <summary>sha256 (lower-case hex) of a file, null when it cannot be read.</summary>
    public static string? FileSha256(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True only when the file exists and its sha256 equals <paramref name="expected"/>.</summary>
    public static bool Verify(string path, string? expected)
    {
        if (expected == null || expected.Length != 64)
        {
            return false;
        }
        var actual = FileSha256(path);
        return actual != null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
