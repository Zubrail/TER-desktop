namespace ServiceLib.Services;

public enum TermitusTrayKind
{
    Action,
    Separator,
    Submenu,
}

/// <summary>One entry of the tray's right-click menu (pure data; the macOS side builds an NSMenu from it).</summary>
public sealed record TermitusTrayItem(string Title, TermitusTrayKind Kind, string? Id = null, bool Checked = false,
    bool Enabled = true, IReadOnlyList<TermitusTrayItem>? Children = null)
{
    public static TermitusTrayItem Sep { get; } = new(string.Empty, TermitusTrayKind.Separator);
}

/// <summary>What the menu needs to know; filled by the view model on the UI thread.</summary>
public sealed record TermitusTrayState(
    bool Connected,
    bool Connecting,
    bool HasSubscription,
    bool AutoChoice,
    IReadOnlyList<(string Key, string Title, bool Selected)> Countries,
    bool SysProxyClear,
    bool SysProxySet,
    bool SysProxyNothing,
    bool NoConnection = false);

/// <summary>
/// 0.5 tray: the right-click menu is short on purpose: Connect, Country ›, Open TER, Report a problem, Quit.
/// Everything technical (system proxy mode, copy proxy command, subscription update) hides
/// under «Для опытных ›». Pure function, unit-tested.
/// </summary>
public static class TermitusTrayMenu
{
    public const string Connect = "connect";
    public const string Open = "open";
    public const string Quit = "quit";
    public const string Report = "report";
    public const string CountryPrefix = "country:";
    public const string CountryAuto = "country:";
    public const string ProxyClear = "adv:proxy-clear";
    public const string ProxySet = "adv:proxy-set";
    public const string ProxyNothing = "adv:proxy-none";
    public const string CopyProxy = "adv:copy-proxy";
    public const string UpdateSub = "adv:sub-update";
    public const string UpdateSubViaProxy = "adv:sub-update-proxy";

    public static List<TermitusTrayItem> Build(TermitusTrayState s, TermitusLocale loc)
    {
        var items = new List<TermitusTrayItem>
        {
            new(s.NoConnection ? loc.NoConnectionClick : s.Connected ? loc.TrayDisconnect : loc.TrayConnect, TermitusTrayKind.Action, Connect,
                Enabled: s.HasSubscription && !s.Connecting),
        };

        var countries = new List<TermitusTrayItem>
        {
            new(loc.AutoTitle, TermitusTrayKind.Action, CountryAuto, Checked: s.AutoChoice),
        };
        countries.AddRange(s.Countries.Select(c => new TermitusTrayItem(c.Title, TermitusTrayKind.Action, CountryPrefix + c.Key, Checked: c.Selected && !s.AutoChoice)));
        items.Add(new(loc.TrayCountry, TermitusTrayKind.Submenu, Enabled: s.HasSubscription, Children: countries));

        items.Add(new(loc.TrayOpen, TermitusTrayKind.Action, Open));
        items.Add(new(loc.TrayReport, TermitusTrayKind.Action, Report));
        items.Add(TermitusTrayItem.Sep);
        items.Add(new(loc.TrayAdvanced, TermitusTrayKind.Submenu, Children:
        [
            new(loc.TraySysProxyClear, TermitusTrayKind.Action, ProxyClear, Checked: s.SysProxyClear),
            new(loc.TraySysProxySet, TermitusTrayKind.Action, ProxySet, Checked: s.SysProxySet),
            new(loc.TraySysProxyNothing, TermitusTrayKind.Action, ProxyNothing, Checked: s.SysProxyNothing),
            TermitusTrayItem.Sep,
            new(loc.TrayCopyProxy, TermitusTrayKind.Action, CopyProxy),
            new(loc.TrayUpdateSub, TermitusTrayKind.Action, UpdateSub, Enabled: s.HasSubscription),
            new(loc.TrayUpdateSubViaProxy, TermitusTrayKind.Action, UpdateSubViaProxy, Enabled: s.HasSubscription && s.Connected),
        ]));
        items.Add(TermitusTrayItem.Sep);
        items.Add(new(loc.TrayQuit, TermitusTrayKind.Action, Quit));
        return items;
    }

    /// <summary>Icon state of the tray: "busy" while connecting, "warn" when attention is needed, "on", else "off".</summary>
    public static string IconState(bool connecting, bool warn, bool connected) =>
        connecting ? "busy" : warn ? "warn" : connected ? "on" : "off";

    /// <summary>Tooltip text for an <see cref="IconState"/> value.</summary>
    public static string Tooltip(string iconState, TermitusLocale loc) => iconState switch
    {
        "on" => loc.TrayTooltipOn,
        "busy" => loc.TrayTooltipBusy,
        "warn" => loc.TrayTooltipWarn,
        _ => loc.TrayTooltipOff,
    };

    /// <summary>Flat dump for logs/tests: "Подключить | Страна › [Авто✓, Нидерланды] | ...".</summary>
    public static string Describe(IEnumerable<TermitusTrayItem> items) =>
        string.Join(" | ", items.Select(i => i.Kind switch
        {
            TermitusTrayKind.Separator => "-",
            TermitusTrayKind.Submenu => $"{i.Title} ›[{Describe(i.Children ?? [])}]",
            _ => i.Title + (i.Checked ? "✓" : string.Empty) + (i.Enabled ? string.Empty : "(off)"),
        }));
}
