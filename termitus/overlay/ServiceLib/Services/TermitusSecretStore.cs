using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ServiceLib.Services;

/// <summary>Where the subscription link (the account secret) really lives. Implementations: macOS Keychain, Windows DPAPI.</summary>
public interface ITermitusSecretBackend
{
    string Prefix { get; }

    /// <summary>Stores <paramref name="plain"/> and returns the reference to keep in the database (null = could not, keep as before).</summary>
    string? Protect(string plain, string? currentRef);

    /// <summary>The secret behind a reference, or null (missing / not readable by this user).</summary>
    string? Unprotect(string reference);

    void Remove(string reference);
}

/// <summary>
/// 0.6.5 security review D-4: the subscription link is the account's key. It used to sit in guiNDB.db in clear text (any process
/// of the same user, and Time Machine, could read it). Now <c>SubItem.Url</c> is a property backed by a reference
/// (<c>ter-keychain:&lt;id&gt;</c> on macOS, <c>ter-dpapi:&lt;base64 blob&gt;</c> on Windows, DPAPI CurrentUser); the DB column holds only the
/// reference. Existing plain values are migrated at start and the database is vacuumed so the old plaintext pages are gone.
/// Linux / failures keep the plain value (neutral log line, nothing is lost).
/// </summary>
public static class TermitusSecretStore
{
    private const string Tag = "TermitusSecretStore";
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static ITermitusSecretBackend? _backend;
    private static bool _backendChosen;

    /// <summary>Tests can inject a backend (null = platform default).</summary>
    public static ITermitusSecretBackend? Backend
    {
        get
        {
            if (!_backendChosen)
            {
                // macOS: NO Keychain (21.10, owner's live test): the app is only ad-hoc signed, so the Keychain ACL is tied to this
                // exact build -- macOS asked for the login-keychain password again and again, and would after every update.
                // Links stay in the 0600 data folder as in 0.6.4; any ter-keychain: reference from a test build is moved back.
                _backend = OperatingSystem.IsWindows() ? new TermitusDpapiBackend() : null;
                _backendChosen = true;
            }
            return _backend;
        }
        set
        {
            _backend = value;
            _backendChosen = true;
            Cache.Clear();
        }
    }

    public static bool IsProtected(string? stored)
        => stored != null && (stored.StartsWith(TermitusKeychainBackend.RefPrefix, StringComparison.Ordinal) || stored.StartsWith(TermitusDpapiBackend.RefPrefix, StringComparison.Ordinal));

    /// <summary>The value to keep in the database for <paramref name="plain"/> (the plain value itself when no store is available).</summary>
    public static string? Conceal(string? currentStored, string? plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain))
        {
            return plain; // empty, or already a reference (e.g. copied through JSON)
        }
        var b = Backend;
        if (b == null)
        {
            return plain;
        }
        try
        {
            var current = currentStored != null && currentStored.StartsWith(b.Prefix, StringComparison.Ordinal) ? currentStored : null;
            var r = b.Protect(plain, current);
            if (r == null)
            {
                Logging.SaveLog($"{Tag}: secret store unavailable, the link stays in the database for now");
                return plain;
            }
            Cache[r] = plain;
            return r;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logging.SaveLog($"{Tag}: protect failed ({ex.GetType().Name})");
            return plain;
        }
    }

    /// <summary>The real link for a stored value (a plain stored value is returned as is).</summary>
    public static string? Reveal(string? stored)
    {
        if (!IsProtected(stored))
        {
            return stored;
        }
        if (Cache.TryGetValue(stored!, out var hit))
        {
            return hit;
        }
        var b = stored!.StartsWith(TermitusKeychainBackend.RefPrefix, StringComparison.Ordinal) && OperatingSystem.IsMacOS()
            ? LegacyKeychain
            : Backend;
        if (b == null || !stored.StartsWith(b.Prefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }
        try
        {
            var plain = b.Unprotect(stored);
            if (plain == null)
            {
                Logging.SaveLog($"{Tag}: a stored link could not be read (the user must sign in again)");
                return string.Empty;
            }
            Cache[stored] = plain;
            return plain;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logging.SaveLog($"{Tag}: unprotect failed ({ex.GetType().Name})");
            return string.Empty;
        }
    }

    /// <summary>macOS: the data folder (server list, settings, logs) is not part of Time Machine backups (`tmutil addexclusion`, sticky, best effort).</summary>
    public static void ExcludeDataFromBackups()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable(Global.LocalAppData) != "1")
        {
            return;
        }
        try
        {
            var dir = Utils.StartupPath();
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/tmutil") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("addexclusion");
            psi.ArgumentList.Add(dir);
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logging.SaveLog($"{Tag}: backup exclusion failed ({ex.GetType().Name})");
        }
    }

    /// <summary>Removes the stored secret behind a reference (sign out / deleted subscription).</summary>
    public static void Forget(string? stored)
    {
        if (!IsProtected(stored))
        {
            return;
        }
        Cache.TryRemove(stored!, out _);
        try
        {
            Backend?.Remove(stored!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logging.SaveLog($"{Tag}: remove failed ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// At start (tables exist): every subscription whose link is still plain in the database goes into the secret store; the
    /// database is then vacuumed so no plaintext page is left behind. Idempotent.
    /// </summary>
    private static TermitusKeychainBackend? _legacyKeychain;
    private static TermitusKeychainBackend LegacyKeychain => _legacyKeychain ??= new TermitusKeychainBackend();

    /// <summary>macOS: references written into the Keychain by 0.6.5 test builds go back into the database (one read each), and the
    /// Keychain item is deleted. Unreadable ones are left as they are (the user signs in again). Idempotent.</summary>
    public static void MigrateKeychainBack()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        try
        {
            var subs = SQLiteHelper.Instance.TableAsync<SubItem>().ToListAsync().GetAwaiter().GetResult();
            foreach (var s in subs)
            {
                var raw = s.UrlStored;
                if (string.IsNullOrEmpty(raw) || !raw.StartsWith(TermitusKeychainBackend.RefPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var plain = Reveal(raw);
                if (string.IsNullOrEmpty(plain))
                {
                    continue;
                }
                s.Url = plain; // no backend on macOS -> stored as the plain link
                SQLiteHelper.Instance.UpdateAsync(s).GetAwaiter().GetResult();
                try { LegacyKeychain.Remove(raw); } catch (Exception ex) when (ex is not OutOfMemoryException) { Logging.SaveLog($"{Tag}: keychain cleanup failed ({ex.GetType().Name})"); }
                Cache.TryRemove(raw, out _);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logging.SaveLog($"{Tag}: keychain move-back failed ({ex.GetType().Name})");
        }
    }

    public static void MigrateSubItems()
    {
        ExcludeDataFromBackups();
        MigrateKeychainBack();
        try
        {
            if (Backend == null)
            {
                return;
            }
            var subs = SQLiteHelper.Instance.TableAsync<SubItem>().ToListAsync().GetAwaiter().GetResult();
            var moved = 0;
            foreach (var s in subs)
            {
                var raw = s.UrlStored;
                if (string.IsNullOrEmpty(raw) || IsProtected(raw))
                {
                    continue;
                }
                s.Url = raw; // Conceal
                if (IsProtected(s.UrlStored))
                {
                    SQLiteHelper.Instance.UpdateAsync(s).GetAwaiter().GetResult();
                    moved++;
                }
            }
            if (moved > 0)
            {
                SQLiteHelper.Instance.ExecuteAsync("VACUUM").GetAwaiter().GetResult();
                Logging.SaveLog($"{Tag}: {moved} subscription link(s) moved out of the database");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logging.SaveLog($"{Tag}: migration failed ({ex.GetType().Name})");
        }
    }
}

/// <summary>Windows DPAPI (CurrentUser): the encrypted blob IS the reference; no second store.</summary>
public sealed class TermitusDpapiBackend : ITermitusSecretBackend
{
    public const string RefPrefix = "ter-dpapi:";
    public string Prefix => RefPrefix;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    private const int CryptProtectUiForbidden = 0x1;

    // application-specific entropy: another program of the same user cannot decrypt the blob by simply calling DPAPI
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("online.termitus.desktop/subscription");

    private static byte[] Run(bool protect, byte[] input)
    {
        var inH = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entH = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var din = new DataBlob { cbData = input.Length, pbData = inH.AddrOfPinnedObject() };
            var ent = new DataBlob { cbData = Entropy.Length, pbData = entH.AddrOfPinnedObject() };
            var entPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
            try
            {
                Marshal.StructureToPtr(ent, entPtr, false);
                DataBlob dout;
                var ok = protect
                    ? CryptProtectData(ref din, null, entPtr, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out dout)
                    : CryptUnprotectData(ref din, IntPtr.Zero, entPtr, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out dout);
                if (!ok)
                {
                    throw new InvalidOperationException("DPAPI call failed");
                }
                try
                {
                    var res = new byte[dout.cbData];
                    Marshal.Copy(dout.pbData, res, 0, dout.cbData);
                    return res;
                }
                finally
                {
                    LocalFree(dout.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(entPtr);
            }
        }
        finally
        {
            inH.Free();
            entH.Free();
        }
    }

    public string? Protect(string plain, string? currentRef)
        => RefPrefix + Convert.ToBase64String(Run(true, Encoding.UTF8.GetBytes(plain)));

    public string? Unprotect(string reference)
    {
        try
        {
            return Encoding.UTF8.GetString(Run(false, Convert.FromBase64String(reference[RefPrefix.Length..])));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Remove(string reference)
    {
        // nothing to delete: the blob lives in the database row
    }
}

/// <summary>macOS login Keychain, generic password item (service online.termitus.desktop, account = random id), via Security.framework.</summary>
public sealed class TermitusKeychainBackend : ITermitusSecretBackend
{
    public const string RefPrefix = "ter-keychain:";
    public const string Service = "online.termitus.desktop";
    public string Prefix => RefPrefix;

    private const string Sec = "/System/Library/Frameworks/Security.framework/Security";
    private const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;
    private const uint Utf8 = 0x08000100;

    [DllImport(Sec)] private static extern int SecItemAdd(IntPtr attrs, IntPtr result);
    [DllImport(Sec)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Sec)] private static extern int SecItemUpdate(IntPtr query, IntPtr attrsToUpdate);
    [DllImport(Sec)] private static extern int SecItemDelete(IntPtr query);

    [DllImport(Cf)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, nint capacity, IntPtr keyCb, IntPtr valCb);
    [DllImport(Cf)] private static extern void CFDictionarySetValue(IntPtr d, IntPtr key, IntPtr val);
    [DllImport(Cf)] private static extern IntPtr CFStringCreateWithBytes(IntPtr alloc, byte[] bytes, nint len, uint enc, bool isExternal);
    [DllImport(Cf)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint len);
    [DllImport(Cf)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(Cf)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(Cf)] private static extern void CFRelease(IntPtr o);

    private static IntPtr Sym(string lib, string name)
    {
        var h = NativeLibrary.Load(lib);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(h, name));
    }

    private static IntPtr Addr(string lib, string name) => NativeLibrary.GetExport(NativeLibrary.Load(lib), name);

    private static IntPtr Str(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return CFStringCreateWithBytes(IntPtr.Zero, b, b.Length, Utf8, false);
    }

    private static IntPtr NewDict()
        => CFDictionaryCreateMutable(IntPtr.Zero, 0, Addr(Cf, "kCFTypeDictionaryKeyCallBacks"), Addr(Cf, "kCFTypeDictionaryValueCallBacks"));

    private static IntPtr BaseQuery(string account, List<IntPtr> toRelease)
    {
        var d = NewDict();
        toRelease.Add(d);
        var svc = Str(Service);
        var acc = Str(account);
        toRelease.Add(svc);
        toRelease.Add(acc);
        CFDictionarySetValue(d, Sym(Sec, "kSecClass"), Sym(Sec, "kSecClassGenericPassword"));
        CFDictionarySetValue(d, Sym(Sec, "kSecAttrService"), svc);
        CFDictionarySetValue(d, Sym(Sec, "kSecAttrAccount"), acc);
        return d;
    }

    private static void ReleaseAll(List<IntPtr> l)
    {
        foreach (var p in l)
        {
            if (p != IntPtr.Zero)
            {
                CFRelease(p);
            }
        }
    }

    private static string AccountOf(string reference) => reference[RefPrefix.Length..];

    public string? Protect(string plain, string? currentRef)
    {
        var account = currentRef != null ? AccountOf(currentRef) : Guid.NewGuid().ToString("N");
        var rel = new List<IntPtr>();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            rel.Add(data);
            var add = BaseQuery(account, rel);
            CFDictionarySetValue(add, Sym(Sec, "kSecValueData"), data);
            var rc = SecItemAdd(add, IntPtr.Zero);
            if (rc == ErrSecDuplicateItem)
            {
                var q = BaseQuery(account, rel);
                var upd = NewDict();
                rel.Add(upd);
                CFDictionarySetValue(upd, Sym(Sec, "kSecValueData"), data);
                rc = SecItemUpdate(q, upd);
            }
            return rc == ErrSecSuccess ? RefPrefix + account : null;
        }
        finally
        {
            ReleaseAll(rel);
        }
    }

    public string? Unprotect(string reference)
    {
        var rel = new List<IntPtr>();
        try
        {
            var q = BaseQuery(AccountOf(reference), rel);
            CFDictionarySetValue(q, Sym(Sec, "kSecReturnData"), Sym(Cf, "kCFBooleanTrue"));
            CFDictionarySetValue(q, Sym(Sec, "kSecMatchLimit"), Sym(Sec, "kSecMatchLimitOne"));
            if (SecItemCopyMatching(q, out var res) != ErrSecSuccess || res == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                var len = (int)CFDataGetLength(res);
                var buf = new byte[len];
                Marshal.Copy(CFDataGetBytePtr(res), buf, 0, len);
                return Encoding.UTF8.GetString(buf);
            }
            finally
            {
                CFRelease(res);
            }
        }
        finally
        {
            ReleaseAll(rel);
        }
    }

    public void Remove(string reference)
    {
        var rel = new List<IntPtr>();
        try
        {
            SecItemDelete(BaseQuery(AccountOf(reference), rel));
        }
        finally
        {
            ReleaseAll(rel);
        }
    }
}
