namespace ServiceLib.Services;

/// <summary>
/// TER default routing for Russian users, applied OFFLINE.
///
/// Upstream v2rayN ships Chinese built-in routing ("V4-绕过大陆(Whitelist)" etc.)
/// and only gets the Russia preset (runetfreedom) by downloading templates,
/// rule lists, DNS presets and geo files from github.com /
/// raw.githubusercontent.com at click time. From a Russian network without a
/// working VPN those downloads time out (seen live in TER 0.1.0: every request
/// failed with net_http_ssl_connection_failed and the Chinese whitelist stayed
/// active). So TER embeds the runetfreedom v2rayN template here verbatim
/// (https://github.com/runetfreedom/russia-v2ray-custom-routing-list, v2rayN/
/// template.json "RUv1" + all.json / all_except_ru.json / only_blocked.json)
/// and bundles the matching runetfreedom geoip.dat / geosite.dat in the .app.
///
/// Hooked into ConfigHandler.InitBuiltinRouting by apply-changes.sh, so a
/// fresh install never gets the Chinese presets. Idempotent: it only creates
/// the RU routing items (and picks the default) when no "RUv1-" item exists
/// yet, so a user's later choice of another routing is never overridden.
/// </summary>
public static class TermitusRussiaPreset
{
    private const string _tag = "TermitusRussiaPreset";

    /// <summary>Same prefix the upstream external-template importer uses (template.version).</summary>
    public const string TemplateVersion = "RUv1";

    /// <summary>Default routing: everything through the VPN except Russian sites / IPs.</summary>
    public const string DefaultRemarks = "Всё, кроме РФ";

    /// <summary>Marker shipped next to the bundled geo files (= runetfreedom release tag).</summary>
    public const string GeoVersionFile = "ter-geo.version";

    private sealed record PresetItem(string Remarks, string? DomainStrategy, string Rules);

    private static readonly PresetItem[] _items =
    [
        new("Всё", null, """
        [
          { "outboundTag": "block", "domain": ["geosite:category-ads-all"], "enabled": true, "remarks": "Блокировка рекламы" },
          { "outboundTag": "direct", "ip": ["geoip:private"], "enabled": true, "remarks": "Приватные сети напрямую" },
          { "outboundTag": "direct", "domain": ["geosite:private"], "enabled": true, "remarks": "Приватные домены напрямую" },
          { "port": "0-65535", "outboundTag": "proxy", "enabled": true, "remarks": "Остальное в прокси" }
        ]
        """),
        new(DefaultRemarks, "IPOnDemand", """
        [
          { "port": "", "outboundTag": "direct", "protocol": ["bittorrent"], "enabled": true, "remarks": "Торрент напрямую" },
          { "port": "", "outboundTag": "block", "domain": ["geosite:category-ads-all"], "enabled": true, "remarks": "Блокировка рекламы" },
          { "outboundTag": "direct", "ip": ["geoip:private"], "enabled": true, "remarks": "Приватные сети напрямую" },
          { "outboundTag": "direct", "domain": ["geosite:private"], "enabled": true, "remarks": "Приватные домены напрямую" },
          { "outboundTag": "direct", "ip": ["geoip:ru"], "domain": ["geosite:ru-available-only-inside"], "enabled": true, "remarks": "Доступные только в России напрямую" },
          { "port": "0-65535", "outboundTag": "proxy", "enabled": true, "remarks": "Остальное в прокси" }
        ]
        """),
        new("Заблокированное", "IPOnDemand", """
        [
          { "outboundTag": "direct", "protocol": ["bittorrent"], "enabled": true, "remarks": "Торрент напрямую" },
          { "outboundTag": "block", "domain": ["geosite:category-ads-all"], "enabled": true, "remarks": "Блокировка рекламы" },
          { "outboundTag": "direct", "ip": ["geoip:private"], "enabled": true, "remarks": "Приватные сети напрямую" },
          { "outboundTag": "direct", "domain": ["geosite:private"], "enabled": true, "remarks": "Приватные домены напрямую" },
          { "outboundTag": "proxy", "ip": ["1.0.0.1", "1.1.1.1", "8.8.8.8", "8.8.4.4"], "enabled": true, "remarks": "DNS в прокси" },
          { "port": "50000-65535", "network": "udp", "outboundTag": "proxy", "enabled": true, "remarks": "Дискорд (войс) в прокси" },
          { "outboundTag": "proxy", "ip": ["geoip:ru-blocked"], "enabled": true, "remarks": "Заблокированные сети в прокси" },
          { "outboundTag": "proxy", "domain": ["geosite:ru-blocked"], "enabled": true, "remarks": "Заблокированные домены в прокси" },
          { "port": "0-65535", "outboundTag": "direct", "enabled": true, "remarks": "Остальное напрямую" }
        ]
        """),
    ];

    /// <summary>
    /// Ensures the RU routing set exists. Returns true when it created it (first
    /// run, or first start after upgrading from a build that had the Chinese set).
    /// </summary>
    public static async Task<bool> EnsureRoutingAsync(Config config)
    {
        try
        {
            var items = await AppManager.Instance.RoutingItems() ?? [];
            if (items.Any(t => t.Remarks?.StartsWith(TemplateVersion + "-") == true))
            {
                return false;
            }

            // Sources for later geo/rule updates (Settings -> update geo files):
            // the same URLs upstream's EPresetType.Russia sets.
            config.ConstItem ??= new();
            config.ConstItem.GeoSourceUrl = Global.GeoFilesSources[1];
            config.ConstItem.SrsSourceUrl = Global.SingboxRulesetSources[1];
            config.ConstItem.RouteRulesTemplateSourceUrl = Global.RoutingRulesSources[1];

            // runetfreedom simple_dns.json: Yandex DNS for direct (instead of the
            // upstream Chinese 119.29.29.29 / 223.5.5.5), Google/Cloudflare remote.
            config.SimpleDNSItem ??= ConfigHandler.InitBuiltinSimpleDNS();
            config.SimpleDNSItem.UseSystemHosts = false;
            config.SimpleDNSItem.AddCommonHosts = true;
            config.SimpleDNSItem.FakeIP = false;
            config.SimpleDNSItem.GlobalFakeIp = true;
            config.SimpleDNSItem.BlockBindingQuery = true;
            config.SimpleDNSItem.DirectDNS = "77.88.8.8";
            config.SimpleDNSItem.RemoteDNS = "8.8.8.8,https://dns.google/dns-query,1.1.1.1";
            config.SimpleDNSItem.BootstrapDNS = "77.88.8.8";

            var maxSort = items.Count;
            RoutingItem? defaultItem = null;
            var added = 0;
            foreach (var preset in _items)
            {
                var item = new RoutingItem
                {
                    Remarks = $"{TemplateVersion}-{preset.Remarks}",
                    Url = string.Empty,
                    Enabled = true,
                    DomainStrategy = preset.DomainStrategy,
                    Sort = ++maxSort,
                };
                if (await ConfigHandler.AddBatchRoutingRules(item, preset.Rules) != 0)
                {
                    Logging.SaveLog($"{_tag}: failed to add {item.Remarks}");
                    continue;
                }
                added++;
                if (preset.Remarks == DefaultRemarks)
                {
                    defaultItem = item;
                }
            }

            if (added == 0)
            {
                return false; // nothing written; retried on next start
            }
            if (defaultItem != null)
            {
                await ConfigHandler.SetDefaultRouting(config, defaultItem);
            }
            await ConfigHandler.SaveConfig(config);
            Logging.SaveLog($"{_tag}: RU routing created, default = {defaultItem?.Remarks}");
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return false;
        }
    }

    /// <summary>
    /// Upstream copies the bundled bin/ folder into the data dir only for files
    /// that do not exist yet (overwrite=false), so an upgraded TER would keep
    /// old geo files that lack the RU categories (geosite:ru-available-only-inside
    /// etc.) and Xray would refuse to start with the RU routing. When the
    /// bundled geo release marker differs from the copied one, overwrite.
    /// </summary>
    public static void SyncBundledGeoFiles(string fromBinPath, string toBinPath)
    {
        try
        {
            var srcMarker = Path.Combine(fromBinPath, GeoVersionFile);
            if (!File.Exists(srcMarker) || fromBinPath == toBinPath)
            {
                return;
            }
            var dstMarker = Path.Combine(toBinPath, GeoVersionFile);
            var srcVer = File.ReadAllText(srcMarker).Trim();
            var dstVer = File.Exists(dstMarker) ? File.ReadAllText(dstMarker).Trim() : string.Empty;
            if (srcVer == dstVer)
            {
                return;
            }
            foreach (var name in new[] { "geoip.dat", "geosite.dat" })
            {
                var src = Path.Combine(fromBinPath, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(toBinPath, name), true);
                }
            }
            File.Copy(srcMarker, dstMarker, true);
            Logging.SaveLog($"{_tag}: geo files updated to bundled {srcVer} (was '{dstVer}')");
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }
}
