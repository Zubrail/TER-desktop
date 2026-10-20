using System.Security.Cryptography;

namespace ServiceLib.Services;

/// <summary>
/// The Ed25519 PUBLIC key that update manifests must be signed with. Put by
/// <c>termitus/tools/sign_release.py keygen --patch-app ...</c>; the matching private key lives only
/// with the owner (never in git). While the constant is empty the updater is OFF: it refuses every
/// manifest instead of trusting an unsigned one (RELEASE: the package script refuses to build without it).
/// </summary>
public static class TermitusUpdateKey
{
    /// <summary>Base64 of the 32-byte public key; empty = not configured.</summary>
    public const string PublicKeyBase64 = "UY3t29AZHAZ1eI4EjbZvD0x7P2a5gx6XNcO+uje7sj4=";

    public static byte[]? Effective()
    {
        // Test instance only (TERM_TEST hooks): a throw-away key for the live check of the updater.
        var s = TermitusTestHooks.IsTestInstance && !TermitusTestHooks.UpdatePubKey.IsNullOrEmpty()
            ? TermitusTestHooks.UpdatePubKey
            : PublicKeyBase64;
        return TermitusUpdateManifest.TryBase64(s, 32);
    }
}

public enum TermitusUpdateStatus
{
    /// <summary>Signed, valid, newer than us, has a file for this platform.</summary>
    Available,
    UpToDate,
    /// <summary>Unsigned / bad signature / malformed / unsafe link: never offered.</summary>
    Invalid,
    /// <summary>No public key compiled in: updates are switched off.</summary>
    NoKey,
    /// <summary>No HTTP answer at all.</summary>
    Offline,
    /// <summary>The server answered, but not with a manifest (404, 5xx).</summary>
    ServerError,
}

public sealed record TermitusUpdateInfo(string Version, string NotesRu, string Url, string Sha256, string PlatformKey);

public sealed record TermitusUpdateCheck(TermitusUpdateStatus Status, TermitusUpdateInfo? Info = null, string? Reason = null);

/// <summary>
/// Update manifest <c>https://termitus.online/app/latest.json</c>. Pure parsing + verification
/// (unit-tested); the network part is in <see cref="TermitusUpdater"/>.
///
/// <code>
/// { "version": "0.6.1", "notes_ru": "...",
///   "url_mac_arm64": "https://...dmg", "url_mac_x64": "...", "url_win_x64": "...",
///   "sha256": { "mac_arm64": "hex", "mac_x64": "hex", "win_x64": "hex" },
///   "signature": "base64 Ed25519" }
/// </code>
/// The signature covers <see cref="CanonicalMessage"/>: a length-prefixed text form of every field
/// (no JSON canonicalisation ambiguity between Python and C#). Absent optional fields are "".
/// </summary>
public static class TermitusUpdateManifest
{
    public const string Magic = "TER-UPDATE-1";
    public const string DefaultUrl = "https://termitus.online/app/latest.json";
    public const int MaxManifestBytes = 64 * 1024;

    /// <summary>Platform keys, in the order they enter the signed message.</summary>
    public static readonly string[] PlatformKeys = ["mac_arm64", "mac_x64", "win_x64"];

    public static string? PlatformKeyOfThisMachine()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsMacOS())
        {
            return arch == System.Runtime.InteropServices.Architecture.Arm64 ? "mac_arm64"
                : arch == System.Runtime.InteropServices.Architecture.X64 ? "mac_x64" : null;
        }
        if (OperatingSystem.IsWindows() && arch == System.Runtime.InteropServices.Architecture.X64)
        {
            return "win_x64";
        }
        return null;
    }

    public static byte[]? TryBase64(string? s, int expectedLength)
    {
        if (s.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            var b = Convert.FromBase64String(s.Trim());
            return b.Length == expectedLength ? b : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The exact bytes that are signed.</summary>
    public static byte[] CanonicalMessage(string version, string notes, IReadOnlyDictionary<string, string> urls,
        IReadOnlyDictionary<string, string> sha256)
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append('\n');
        void Field(string name, string? value)
        {
            var v = value ?? string.Empty;
            sb.Append(name).Append(':').Append(Encoding.UTF8.GetByteCount(v).ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(v).Append('\n');
        }
        Field("version", version);
        Field("notes_ru", notes);
        foreach (var k in PlatformKeys)
        {
            Field("url_" + k, urls.GetValueOrDefault(k));
        }
        foreach (var k in PlatformKeys)
        {
            Field("sha256_" + k, sha256.GetValueOrDefault(k));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>"0.6.1" / "v0.6.1-beta" -> comparable numbers; null if there is no number at all.</summary>
    public static int[]? ParseVersion(string? v)
    {
        if (v.IsNullOrEmpty())
        {
            return null;
        }
        var s = v.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
        {
            s = s[..cut];
        }
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return null;
        }
        var nums = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out nums[i]))
            {
                return null;
            }
        }
        return nums;
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(current);
        if (a == null || b == null)
        {
            return false;
        }
        for (var i = 0; i < 4; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] > b[i];
            }
        }
        return false;
    }

    /// <summary>Only https links; no credentials in the URL.</summary>
    public static bool IsSafeDownloadUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttps
               // test instance only: the fake backend on this very computer speaks plain http
               || (u.Scheme == Uri.UriSchemeHttp && TermitusTestHooks.IsTestInstance && u.IsLoopback))
           && u.UserInfo.Length == 0 && !u.Host.IsNullOrEmpty();

    /// <summary>
    /// Parses and verifies a manifest. Never throws. A manifest is accepted ONLY if the signature
    /// verifies under <paramref name="publicKey"/>; "no key" is its own status.
    /// </summary>
    public static TermitusUpdateCheck Evaluate(string? json, byte[]? publicKey, string currentVersion, string? platformKey)
    {
        if (publicKey == null)
        {
            return new(TermitusUpdateStatus.NoKey);
        }
        if (json.IsNullOrEmpty() || json.Length > MaxManifestBytes)
        {
            return new(TermitusUpdateStatus.Invalid, null, "empty or too large");
        }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new(TermitusUpdateStatus.Invalid, null, "not an object");
            }
            string Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;

            var version = Str(root, "version");
            var notes = Str(root, "notes_ru");
            var sigB64 = Str(root, "signature");
            var urls = PlatformKeys.ToDictionary(k => k, k => Str(root, "url_" + k));
            var hashes = new Dictionary<string, string>();
            if (root.TryGetProperty("sha256", out var sh) && sh.ValueKind == JsonValueKind.Object)
            {
                foreach (var k in PlatformKeys)
                {
                    hashes[k] = Str(sh, k).Trim().ToLowerInvariant();
                }
            }

            var sig = TryBase64(sigB64, 64);
            if (sig == null)
            {
                return new(TermitusUpdateStatus.Invalid, null, "unsigned");
            }
            if (!TermitusEd25519.Verify(publicKey, CanonicalMessage(version, notes, urls, hashes), sig))
            {
                return new(TermitusUpdateStatus.Invalid, null, "bad signature");
            }
            if (ParseVersion(version) == null)
            {
                return new(TermitusUpdateStatus.Invalid, null, "bad version");
            }
            if (!IsNewer(version, currentVersion))
            {
                return new(TermitusUpdateStatus.UpToDate);
            }
            if (platformKey == null || urls.GetValueOrDefault(platformKey).IsNullOrEmpty())
            {
                return new(TermitusUpdateStatus.UpToDate, null, "no file for this platform");
            }
            var url = urls[platformKey];
            var hash = hashes.GetValueOrDefault(platformKey) ?? string.Empty;
            if (!IsSafeDownloadUrl(url) || !IsHex64(hash))
            {
                return new(TermitusUpdateStatus.Invalid, null, "unsafe link or missing sha256");
            }
            return new(TermitusUpdateStatus.Available, new TermitusUpdateInfo(version, notes, url, hash, platformKey));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(TermitusUpdateStatus.Invalid, null, "malformed");
        }
    }

    public static bool IsHex64(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Network + install side of the updater. What leaves the device: ONE GET of latest.json (no
/// cookies, no identifiers, generic User-Agent "TER"), and — only after the user presses «Обновить» —
/// one GET of the signed file link. Nothing else, ever (SECURITY.md, 0.6).
/// </summary>
public static class TermitusUpdater
{
    private const string _tag = "TermitusUpdater";
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    public const string GenericUserAgent = "TER";
    public const long MaxDownloadBytes = 600L * 1024 * 1024;

    public static string ManifestUrl => TermitusTestHooks.IsTestInstance && !TermitusTestHooks.UpdateUrl.IsNullOrEmpty()
        ? TermitusTestHooks.UpdateUrl!
        : TermitusUpdateManifest.DefaultUrl;

    public static async Task<TermitusUpdateCheck> CheckAsync(int? socksPort, CancellationToken ct)
    {
        var key = TermitusUpdateKey.Effective();
        if (key == null)
        {
            return new(TermitusUpdateStatus.NoKey);
        }
        var res = await TermitusHttp.SendAsync(HttpMethod.Get, ManifestUrl, null, socksPort, TimeSpan.FromSeconds(15), ct, GenericUserAgent);
        TermitusUpdateCheck r;
        if (res.Status is null)
        {
            r = new(TermitusUpdateStatus.Offline);
        }
        else if (!res.IsSuccess)
        {
            r = new(TermitusUpdateStatus.ServerError);
        }
        else
        {
            r = TermitusUpdateManifest.Evaluate(res.Body, key, Utils.GetVersionInfo(), TermitusUpdateManifest.PlatformKeyOfThisMachine());
        }
        Logging.SaveLog($"{_tag}: check -> {r.Status}{(r.Reason == null ? string.Empty : " (" + r.Reason + ")")}");
        return r;
    }

    public static string UpdatesDir()
    {
        var d = Path.Combine(Utils.GetTempPath(), "updates");
        PrivateDir(d);
        return d;
    }

    /// <summary>Removes leftovers of earlier updates (downloaded dmg, staged app, mount points).</summary>
    public static void CleanOld()
    {
        try
        {
            var d = Path.Combine(Utils.GetTempPath(), "updates");
            if (Directory.Exists(d))
            {
                Directory.Delete(d, true);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: cleanup failed ({ex.GetType().Name})");
        }
    }


    /// <summary>
    /// 0.6.5 security review D-7: a staging folder only the current user can enter (0700 on macOS/Linux; on Windows the
    /// per-user AppData ACL already excludes other accounts and <see cref="TermitusWinHarden"/> tightens it). Created if missing,
    /// the mode is enforced even when it existed.
    /// </summary>
    public static void PrivateDir(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            TermitusWinHarden.RestrictToCurrentUser(dir);
            return;
        }
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir, mode);
        }
        File.SetUnixFileMode(dir, mode);
    }

    /// <summary>
    /// Verify-then-use in ONE pass: reads <paramref name="src"/> through a single handle, writes the bytes to a new file in
    /// <paramref name="destDir"/> (created exclusively, read-only) and compares the sha256 of exactly those bytes with the signed
    /// one. What the caller mounts/installs afterwards is the sealed copy, never the path that was verified earlier.
    /// Null (and nothing left behind) on any mismatch or I/O error.
    /// </summary>
    public static string? SealFile(string src, string expectedSha256, string destDir)
    {
        string? dest = null;
        try
        {
            PrivateDir(destDir);
            dest = Path.Combine(destDir, "sealed-" + Guid.NewGuid().ToString("N") + Path.GetExtension(src));
            using var sha = SHA256.Create();
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    output.Write(buf, 0, n);
                }
            }
            sha.TransformFinalBlock([], 0, 0);
            if (!string.Equals(Convert.ToHexString(sha.Hash!), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(dest);
                return null;
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(dest, UnixFileMode.UserRead);
            }
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (dest != null)
            {
                TryDelete(dest);
            }
            return null;
        }
    }

    /// <summary>
    /// Deterministic sha256 over a staged tree: every entry's relative path, its type, the bytes of files and the target of
    /// symlinks (never followed). Recorded right after staging and compared again just before the swap starts.
    /// </summary>
    public static string? TreeHash(string root)
    {
        try
        {
            using var sha = SHA256.Create();
            void Add(string text)
            {
                var b = System.Text.Encoding.UTF8.GetBytes(text + "\n");
                sha.TransformBlock(b, 0, b.Length, null, 0);
            }
            void Walk(string dir, string rel)
            {
                var entries = new DirectoryInfo(dir).GetFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
                foreach (var e in entries)
                {
                    var r = rel.Length == 0 ? e.Name : rel + "/" + e.Name;
                    if (e.LinkTarget != null)
                    {
                        Add("L " + r + " -> " + e.LinkTarget);
                    }
                    else if (e is DirectoryInfo d)
                    {
                        Add("D " + r);
                        Walk(d.FullName, r);
                    }
                    else if (e is FileInfo f)
                    {
                        Add("F " + r + " " + f.Length);
                        using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var buf = new byte[81920];
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            sha.TransformBlock(buf, 0, n, null, 0);
                        }
                    }
                }
            }
            Walk(root, string.Empty);
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public enum DownloadKind { Ok, Offline, BadStatus, TooBig, HashMismatch, IoError }

    /// <summary>Downloads to <paramref name="destPath"/> and checks the SIGNED sha256. The file is deleted on any failure.</summary>
    public static async Task<DownloadKind> DownloadAsync(TermitusUpdateInfo info, string destPath, int? socksPort,
        Action<long, long?> progress, CancellationToken ct)
    {
        var part = destPath + ".part";
        try
        {
            File.Delete(part);
            int?[] routes = socksPort is > 0 ? [socksPort, null] : [null];
            foreach (var port in routes)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, info.Url);
                    req.Headers.UserAgent.TryParseAdd(GenericUserAgent);
                    using var resp = await TermitusHttp.ClientFor(port).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        return DownloadKind.BadStatus;
                    }
                    var total = resp.Content.Headers.ContentLength;
                    if (total > MaxDownloadBytes)
                    {
                        return DownloadKind.TooBig;
                    }
                    using var sha = SHA256.Create();
                    long done = 0;
                    await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                    await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = await src.ReadAsync(buf, ct)) > 0)
                        {
                            done += n;
                            if (done > MaxDownloadBytes)
                            {
                                return DownloadKind.TooBig;
                            }
                            sha.TransformBlock(buf, 0, n, null, 0);
                            await dst.WriteAsync(buf.AsMemory(0, n), ct);
                            progress(done, total);
                        }
                    }
                    sha.TransformFinalBlock([], 0, 0);
                    var got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                    if (!string.Equals(got, info.Sha256, StringComparison.Ordinal))
                    {
                        return DownloadKind.HashMismatch;
                    }
                    File.Move(part, destPath, true);
                    return DownloadKind.Ok;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    Logging.SaveLog($"{_tag}: download failed via {(port == null ? "direct" : "proxy")} ({ex.GetType().Name})");
                }
            }
            return DownloadKind.Offline;
        }
        catch (OperationCanceledException)
        {
            return DownloadKind.Offline;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.SaveLog($"{_tag}: download io error ({ex.GetType().Name})");
            return DownloadKind.IoError;
        }
        finally
        {
            TryDelete(part);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    #region macOS install

    /// <summary>
    /// Helper run after TER quits: waits for the old process, swaps the bundle (old one is kept as
    /// "*.old-ter-update" until the new one is in place, put back if the move fails), starts the new TER.
    /// Arguments (never interpolated into shell text): PID NEW_APP TARGET_APP OPEN(1|0).
    /// </summary>
    public const string SwapScript = """
        #!/bin/bash
        PID="$1"; NEW="$2"; TARGET="$3"; DOOPEN="$4"
        for i in $(seq 1 100); do kill -0 "$PID" 2>/dev/null || break; sleep 0.3; done
        OLD="${TARGET}.old-ter-update"
        rm -rf "$OLD"
        mv "$TARGET" "$OLD" || exit 1
        if mv "$NEW" "$TARGET"; then
          rm -rf "$OLD"
          xattr -dr com.apple.quarantine "$TARGET" 2>/dev/null
        else
          mv "$OLD" "$TARGET"
          exit 2
        fi
        [ "$DOOPEN" = "1" ] && open "$TARGET"
        exit 0
        """;

    /// <summary>The .app that is running now (…/TER.app), or null when not run from a bundle (dev build).</summary>
    public static string? CurrentBundle()
    {
        if (TermitusTestHooks.IsTestInstance && !TermitusTestHooks.UpdateTarget.IsNullOrEmpty())
        {
            return TermitusTestHooks.UpdateTarget;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        // …/TER.app/Contents/MacOS
        var app = dir.Parent?.Parent;
        return dir.Name == "MacOS" && dir.Parent?.Name == "Contents" && app != null && app.Name.EndsWith(".app", StringComparison.Ordinal)
            ? app.FullName
            : null;
    }

    public static bool IsWritableDir(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".ter-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>CFBundleIdentifier / CFBundleShortVersionString from an Info.plist (XML), or nulls.</summary>
    public static (string? Id, string? Version) ReadPlist(string plistText)
    {
        string? Val(string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(plistText, $@"<key>{key}</key>\s*<string>([^<]*)</string>");
            return m.Success ? m.Groups[1].Value : null;
        }
        return (Val("CFBundleIdentifier"), Val("CFBundleShortVersionString"));
    }

    public sealed record StagedApp(string AppPath, string Version, string? TreeHash = null);

    private static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch
            {
                // already gone
            }
            return (-1, "timeout");
        }
        return (p.ExitCode, (await stdout) + (await stderr));
    }

    /// <summary>
    /// Mounts the verified dmg read-only (no Finder window), checks that it holds TER.app with our bundle id
    /// and the announced version, copies it to a staging folder, unmounts. Null = not usable.
    /// </summary>
    public static async Task<StagedApp?> StageFromDmgAsync(string dmgPath, string expectedVersion, string bundleId, string? expectedSha256 = null)
    {
        var work = Path.GetDirectoryName(dmgPath)!;
        PrivateDir(work);
        var mount = Path.Combine(work, "mnt");
        var stage = Path.Combine(work, "stage");
        PrivateDir(mount);
        var attached = false;
        try
        {
            // D-7: what gets mounted is a sealed copy made from ONE read of the file, checked against the signed sha256 in the same pass.
            if (expectedSha256 != null)
            {
                var sealedDmg = SealFile(dmgPath, expectedSha256, Path.Combine(work, "sealed"));
                if (sealedDmg == null)
                {
                    Logging.SaveLog($"{_tag}: the downloaded dmg changed after it was verified; refusing to install it");
                    return null;
                }
                dmgPath = sealedDmg;
            }
            if (Directory.Exists(stage))
            {
                Directory.Delete(stage, true);
            }
            Directory.CreateDirectory(stage);
            var a = await RunAsync("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmgPath], TimeSpan.FromSeconds(120));
            if (a.Code != 0)
            {
                Logging.SaveLog($"{_tag}: hdiutil attach failed ({a.Code})");
                return null;
            }
            attached = true;
            var app = Directory.EnumerateDirectories(mount, "*.app").FirstOrDefault();
            if (app == null)
            {
                return null;
            }
            var plist = Path.Combine(app, "Contents", "Info.plist");
            var (id, ver) = File.Exists(plist) ? ReadPlist(File.ReadAllText(plist)) : (null, null);
            if (id != bundleId || ver != expectedVersion)
            {
                Logging.SaveLog($"{_tag}: dmg content does not match the signed update (id/version)");
                return null;
            }
            var dest = Path.Combine(stage, Path.GetFileName(app));
            var c = await RunAsync("/usr/bin/ditto", [app, dest], TimeSpan.FromSeconds(180));
            if (c.Code != 0)
            {
                Logging.SaveLog($"{_tag}: ditto failed ({c.Code})");
                return null;
            }
            // D-7: the staged bundle must still be a validly signed bundle, and its tree is pinned for the swap.
            var cs = await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", dest], TimeSpan.FromSeconds(120));
            if (cs.Code != 0)
            {
                Logging.SaveLog($"{_tag}: codesign verification of the staged app failed ({cs.Code})");
                return null;
            }
            return new StagedApp(dest, ver, TreeHash(dest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Logging.SaveLog($"{_tag}: stage failed ({ex.GetType().Name})");
            return null;
        }
        finally
        {
            if (attached)
            {
                await RunAsync("/usr/bin/hdiutil", ["detach", "-force", mount], TimeSpan.FromSeconds(60));
            }
        }
    }

    /// <summary>Starts the detached swap helper. The caller must quit TER right after.</summary>
    public static bool StartSwap(string stagedApp, string targetApp, bool openAfter, string? expectedTreeHash = null)
    {
        try
        {
            // D-7: the tree that is about to be installed must be the one that was staged (and checked).
            if (expectedTreeHash != null && !string.Equals(TreeHash(stagedApp), expectedTreeHash, StringComparison.Ordinal))
            {
                Logging.SaveLog($"{_tag}: the staged app changed before the swap; refusing");
                return false;
            }
            var script = Path.Combine(Path.GetDirectoryName(stagedApp)!, "swap.sh");
            File.WriteAllText(script, SwapScript.Replace("\r\n", "\n"));
            var psi = new ProcessStartInfo("/usr/bin/nohup") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/bin/bash", script, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), stagedApp, targetApp, openAfter ? "1" : "0" })
            {
                psi.ArgumentList.Add(a);
            }
            Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Logging.SaveLog($"{_tag}: swap start failed ({ex.GetType().Name})");
            return false;
        }
    }

    #endregion macOS install
}

/// <summary>Shows a downloaded file to the user (Finder / Explorer). Never logs the path.</summary>
public static class TermitusUpdateOpen
{
    public static void Reveal(string path)
    {
        if (TermitusTestHooks.NoOpen)
        {
            Logging.SaveLog("TermitusUpdater: reveal suppressed (test instance)");
            return;
        }
        try
        {
            ProcessStartInfo psi;
            if (OperatingSystem.IsMacOS())
            {
                // Opening the .dmg itself mounts it and shows the window with TER.app to drag.
                psi = new ProcessStartInfo("/usr/bin/open");
                psi.ArgumentList.Add(path);
            }
            else if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo("explorer.exe");
                psi.ArgumentList.Add("/select," + path);
            }
            else
            {
                psi = new ProcessStartInfo("xdg-open");
                psi.ArgumentList.Add(Path.GetDirectoryName(path)!);
            }
            using var p = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logging.SaveLog($"TermitusUpdater: reveal failed ({ex.GetType().Name})");
        }
    }
}
