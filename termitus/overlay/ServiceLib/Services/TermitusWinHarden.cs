using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.5 security review D-2 (Windows). TER stays a per-user install (the program folder and %LOCALAPPDATA%\TeRMiTuS are
/// writable by the user), but «Защищать весь трафик» runs TER elevated (UAC). So that «Yes» never runs something a normal process
/// of the same user could have planted:
///  - the DLL search path of this process is pinned (SetDefaultDllDirectories: System32 + the application folder only; no PATH,
///    no current directory);
///  - when TER is elevated, the cores (sing-box.exe / xray.exe) and the DLLs next to them (wintun.dll, libcronet.dll) are copied
///    into %ProgramData%\TeRMiTuS\core (owner Administrators, ACL: SYSTEM + Administrators full, Users read/execute only,
///    inheritance off) and the COPY is verified against the sha256 table compiled into the app (<see cref="TermitusCoreHashes"/>);
///    only the verified copy runs, and the generated config is also copied there and sanitised
///    (<see cref="TermitusConfigSanitizer"/>) before the elevated core reads it;
///  - a pre-existing folder that is not owned by Administrators/SYSTEM is replaced.
/// Not covered by a per-user install (needs the installer / Program Files, see SECURITY.md): the elevated TER.exe and its own DLLs still
/// live in the folder the user unpacked.
/// </summary>
public static class TermitusWinHarden
{
    private const string Tag = "TermitusWinHarden";
    private const uint LoadLibrarySearchApplicationDir = 0x00000200;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    /// <summary>First thing in Main: DLLs are loaded from System32 and the application folder only.</summary>
    public static bool PinDllSearchPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        try
        {
            return SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchApplicationDir);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Files that may sit next to a core and be loaded by it. Anything else is not copied.</summary>
    public static readonly string[] SideFiles = ["wintun.dll", "libcronet.dll"];

    /// <summary>The core exe plus the side files that exist next to it (file names only).</summary>
    public static List<string> FilesToProtect(string coreExePath)
    {
        var dir = Path.GetDirectoryName(coreExePath) ?? ".";
        var list = new List<string> { Path.GetFileName(coreExePath) };
        list.AddRange(SideFiles.Where(f => File.Exists(Path.Combine(dir, f))));
        return list;
    }

    /// <summary>
    /// Copies <paramref name="src"/> to <paramref name="dstDir"/> and keeps it only if the COPY has the expected sha256
    /// (an identical copy that is already there is left alone, it may be in use). Returns the copy's path, or null (nothing left behind).
    /// </summary>
    public static string? CopyVerified(string src, string dstDir, string? expectedSha256)
    {
        if (expectedSha256 == null || expectedSha256.Length != 64)
        {
            return null;
        }
        var dst = Path.Combine(dstDir, Path.GetFileName(src));
        try
        {
            if (File.Exists(dst) && TermitusCoreHashes.Verify(dst, expectedSha256))
            {
                return dst;
            }
            var tmp = dst + ".new";
            File.Copy(src, tmp, true);
            if (!TermitusCoreHashes.Verify(tmp, expectedSha256))
            {
                File.Delete(tmp);
                return null;
            }
            File.Move(tmp, dst, true);
            return dst;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.SaveLog($"{Tag}: could not place {Path.GetFileName(src)} ({ex.GetType().Name})");
            return null;
        }
    }

    public static string ProtectedRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TeRMiTuS", "core");

    public static string WorkDirPath() => Path.Combine(ProtectedRoot(), "work");

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>True when the elevated protections apply: Windows, administrator token, not a throw-away test instance.</summary>
    public static bool Active => OperatingSystem.IsWindows() && !TermitusTestHooks.IsTestInstance && IsElevated();

    [SupportedOSPlatform("windows")]
    private static bool OwnedBySystemOrAdmins(DirectoryInfo d)
    {
        var owner = d.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner != null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
    }

    /// <summary>Creates (or re-creates) <paramref name="dir"/> with the protected ACL described above. Needs an elevated token.</summary>
    [SupportedOSPlatform("windows")]
    public static void EnsureProtectedDir(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.Exists && !OwnedBySystemOrAdmins(info))
        {
            info.Delete(true); // planted by an ordinary user: do not trust anything in it
            info.Refresh();
        }
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inh, PropagationFlags.None, AccessControlType.Allow));
        if (!info.Exists)
        {
            info.Create(sec);
        }
        else
        {
            info.SetAccessControl(sec);
        }
    }

    /// <summary>
    /// Update staging folder (D-2/D-7): inheritance off, only the current user, SYSTEM and Administrators. The per-user AppData ACL
    /// already excludes other accounts; this makes it explicit and survives a folder created elsewhere. Best effort (logged).
    /// </summary>
    public static void RestrictToCurrentUser(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            RestrictToCurrentUserWindows(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            Logging.SaveLog($"{Tag}: could not restrict {Path.GetFileName(dir)} ({ex.GetType().Name})");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUserWindows(string dir)
    {
        var info = new DirectoryInfo(dir);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var me = WindowsIdentity.GetCurrent();
        foreach (var sid in new SecurityIdentifier[] { me.User!, new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        }
        info.SetAccessControl(sec);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureTree()
    {
        var root = ProtectedRoot();
        EnsureProtectedDir(Path.GetDirectoryName(root)!); // ...\TeRMiTuS (a user could have created it)
        EnsureProtectedDir(root);
        EnsureProtectedDir(WorkDirPath());
    }

    /// <summary>
    /// The path of the core to start: unchanged when not elevated; when elevated the verified copy in the protected folder,
    /// or null (refuse) when the hash table does not know the file or the copy does not match.
    /// </summary>
    public static string? CoreFor(string userCoreExe)
    {
        if (!Active || !OperatingSystem.IsWindows())
        {
            return userCoreExe;
        }
        try
        {
            EnsureTree();
            string? result = null;
            foreach (var name in FilesToProtect(userCoreExe))
            {
                var src = Path.Combine(Path.GetDirectoryName(userCoreExe)!, name);
                var placed = CopyVerified(src, ProtectedRoot(), TermitusCoreHashes.ExpectedFor(src));
                if (placed == null)
                {
                    Logging.SaveLog($"{Tag}: {name} does not match the checksum built into TER; not starting the core as administrator");
                    return null;
                }
                if (string.Equals(name, Path.GetFileName(userCoreExe), StringComparison.OrdinalIgnoreCase))
                {
                    result = placed;
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logging.SaveLog($"{Tag}: protected core folder failed ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>Config argument for the core: the usual one when not elevated, else a sanitised copy in the protected work folder.</summary>
    public static string ConfigArg(bool absolutePath, string configName)
    {
        var userPath = Utils.GetBinConfigPath(configName);
        if (!Active)
        {
            return absolutePath ? userPath.AppendQuotes() : configName;
        }
        var text = File.ReadAllText(userPath); // read once; what is checked is what is written
        var clean = TermitusConfigSanitizer.Sanitize(text, WorkDirPath());
        if (clean == null)
        {
            Logging.SaveLog($"{Tag}: the generated config was refused (not valid JSON or no inbounds/outbounds)");
            throw new IOException("config refused");
        }
        var dst = Path.Combine(WorkDirPath(), Path.GetFileName(configName));
        File.WriteAllText(dst, clean);
        return absolutePath ? dst.AppendQuotes() : Path.GetFileName(configName);
    }

    /// <summary>Working directory of the core: the protected work folder when elevated, else binConfigs as upstream.</summary>
    public static string WorkDir() => Active ? WorkDirPath() : Utils.GetBinConfigPath();
}
