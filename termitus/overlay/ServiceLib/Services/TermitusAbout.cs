namespace ServiceLib.Services;

/// <summary>
/// GPL-3.0 compliance surface (0.6): where "Исходный код" points, and the bundled license texts.
/// The URL comes from <c>ter-source-url.txt</c> next to the binary (package-ter-osx.sh copies
/// <c>termitus/SOURCE_URL</c> into it), so the owner changes ONE file at release time, not code.
/// Until then it is a clearly fake placeholder (see GPL-COMPLIANCE.md: this MUST be replaced before distribution).
/// </summary>
public static class TermitusAbout
{
    public const string PlaceholderSourceUrl = "https://github.com/REPLACE-ME/termitus-desktop";
    public const string SourceUrlFile = "ter-source-url.txt";
    public const string LicensesFile = "THIRD-PARTY-NOTICES.txt";

    public static string SourceUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, SourceUrlFile);
            if (File.Exists(path))
            {
                var url = File.ReadAllText(path).Trim();
                if (TermitusUpdateManifest.IsSafeDownloadUrl(url))
                {
                    return url;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // placeholder below
        }
        return PlaceholderSourceUrl;
    }

    public static bool IsPlaceholder(string url) => url.Contains("REPLACE-ME", StringComparison.Ordinal);

    /// <summary>The bundled notices file (LICENSE of TER + components), or null.</summary>
    public static string? LicensesPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, LicensesFile);
        return File.Exists(path) ? path : null;
    }
}
