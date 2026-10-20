namespace ServiceLib.Services;

/// <summary>
/// 0.6.5: the window title. Upstream shows «TER 0.6.5 - Запущено без прав администратора» on Windows, which only scares a normal
/// user. In the simple interface the title is just «TER»; the version and the admin / non-admin note stay in the advanced interface.
/// </summary>
public static class TermitusWinTitle
{
    public static string Compose(bool advanced, bool windows, bool admin, string appName, string version, string asAdmin, string notAsAdmin)
        => advanced && windows ? $"{version} - {(admin ? asAdmin : notAsAdmin)}" : appName;

    public static string For(bool advanced)
        => Compose(advanced, Utils.IsWindows(), Utils.IsWindows() && Utils.IsAdministrator(), Global.AppName, Utils.GetVersion(), ResUI.RunAsAdmin, ResUI.NotRunAsAdmin);
}
