namespace ServiceLib.Services;

/// <summary>
/// TER config defaults that differ from upstream v2rayN, applied right after
/// ConfigHandler.LoadConfig (hook added by apply-changes.sh).
///
///  - UI language: Russian. For a brand-new config the language default itself
///    is patched in LoadConfig ("ru" instead of upstream's zh/en guess). This
///    class adds a ONE-TIME migration for configs created by TER 0.1.0, which
///    got upstream's automatic "en": they are switched to "ru" once. The
///    migration id is remembered in guiConfigs/ter-migrations.txt, so a user
///    who later picks English in Settings keeps English.
/// </summary>
public static class TermitusDefaults
{
    private const string _tag = "TermitusDefaults";
    private const string MigrationsFile = "ter-migrations.txt";
    public const string DefaultLanguage = "ru";

    public static void OnConfigLoaded(Config config)
    {
        try
        {
            var path = Utils.GetConfigPath(MigrationsFile);
            var done = File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet()
                : [];
            var changed = false;

            const string langMigration = "0.1.1-ui-language-ru";
            if (!done.Contains(langMigration))
            {
                config.UiItem ??= new();
                if (config.UiItem.CurrentLanguage.IsNullOrEmpty()
                    || config.UiItem.CurrentLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    config.UiItem.CurrentLanguage = DefaultLanguage;
                }
                done.Add(langMigration);
                changed = true;
            }

            // 0.2.0: tidy the advanced (full) UI — server list and log as tabs instead
            // of a half-empty log pane, hide the "subscription group" and "IP info"
            // columns (always "import_sub" / "none" for our users).
            const string layoutMigration = "0.2.0-advanced-layout";
            if (!done.Contains(layoutMigration))
            {
                config.UiItem ??= new();
                config.UiItem.MainGirdOrientation = EGirdOrientation.Tab;
                config.UiItem.HideColumnIpInfo = true;
                string[] order = ["ConfigType", "Remarks", "Address", "Port", "Network", "StreamSecurity",
                    "SubRemarks", "DelayVal", "SpeedVal", "IpInfo", "TodayUp", "TodayDown", "TotalUp", "TotalDown"];
                config.UiItem.MainColumnItem = order
                    .Select((name, i) => new ColumnItem
                    {
                        Name = name,
                        Index = i,
                        Width = name switch
                        {
                            "SubRemarks" or "IpInfo" => -1,
                            "Remarks" => 220,
                            "Address" => 150,
                            _ => 100,
                        },
                    })
                    .ToList();
                done.Add(layoutMigration);
                changed = true;
            }

            // 0.3.0: leak-safe TUN/DNS defaults (SECURITY.md #3/#4), once — a power
            // user may switch them off again under "Для опытных".
            //  - strict_route on (upstream default, but 0.1/0.2 configs could carry false);
            //  - sniffing on: in TUN mode the sing-box "hijack-dns" rule that pulls every
            //    :53 query into its DNS module exists only when sniffing is enabled;
            //  - DNS answers resolved through the tunnel for proxied domains is the
            //    upstream design (remote DNS has detour=proxy, final=remote) — nothing to change.
            const string leakMigration = "0.3.0-leak-safe";
            if (!done.Contains(leakMigration))
            {
                ApplyLeakSafeDefaults(config);
                done.Add(leakMigration);
                changed = true;
            }

            // 0.5: live speed on the connect screen/tray popover needs upstream's statistics ticker
            // (local counters only). Always on, not user-visible.
            config.GuiItem ??= new();
            config.GuiItem.DisplayRealTimeSpeed = true;

            if (changed)
            {
                File.WriteAllLines(path, done.OrderBy(x => x));
            }
            TermitusTestHooks.ApplyToConfig(config); // no-op unless TER_TEST_INSTANCE is set
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public static void ApplyLeakSafeDefaults(Config config)
    {
        config.TunModeItem ??= new();
        config.TunModeItem.AutoRoute = true;
        config.TunModeItem.StrictRoute = true;
        if (config.Inbound is { Count: > 0 })
        {
            config.Inbound.First().SniffingEnabled = true;
        }
    }
}
