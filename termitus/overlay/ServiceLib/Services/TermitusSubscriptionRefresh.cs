using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ServiceLib.Services;

/// <summary>Refresh TER subscriptions with the existing scheduler, without changing manual profiles.</summary>
public static class TermitusSubscriptionRefresh
{
    public const int IntervalMinutes = 120;
    private const string Marker = "ter-subscription-refresh-068.txt";

    public static int DefaultInterval(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.AbsolutePath.StartsWith("/subscription/", StringComparison.Ordinal))
            return 0;
        var host = uri.IdnHost;
        return host.Equals("termitus.online", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".termitus.online", StringComparison.OrdinalIgnoreCase)
            || host.Equals("razep.online", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".razep.online", StringComparison.OrdinalIgnoreCase)
            ? IntervalMinutes : 0;
    }

    public static bool ConfigureMissingInterval(SubItem item)
    {
        if (!item.Enabled || item.AutoUpdateInterval != 0 || DefaultInterval(item.Url) == 0)
            return false;
        item.AutoUpdateInterval = IntervalMinutes;
        return true;
    }

    private sealed record ScheduledState(string IndexId, string? Snapshot);
    private static readonly AsyncLocal<ScheduledState?> Scheduled = new();

    public static string? ConnectionSnapshot(ProfileItem? item)
    {
        if (item is null || item.IsComplex()) return null;
        var data = JsonNode.Parse(JsonUtils.Serialize(item))!.AsObject();
        foreach (var key in data.Select(x => x.Key).ToArray())
            if (new[] { "IndexId", "Subid", "IsSub", "Remarks" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                data.Remove(key);
        // Sensitive connection fields remain in memory only; no URL or credential is logged.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString())));
    }

    public static IDisposable BeginScheduled(ProfileItem? selected, string indexId)
    {
        var previous = Scheduled.Value;
        Scheduled.Value = new(indexId, ConnectionSnapshot(selected));
        return new RestoreScope(previous);
    }

    private sealed class RestoreScope(ScheduledState? previous) : IDisposable
    {
        public void Dispose() => Scheduled.Value = previous;
    }

    public static bool KeepScheduledConnection(string indexId, ProfileItem? current)
        => Scheduled.Value is { Snapshot: not null } state
            && state.IndexId == indexId && state.Snapshot == ConnectionSnapshot(current);

    public static async Task UpgradeExistingAsync(Config config)
    {
        var marker = Utils.GetConfigPath(Marker);
        if (File.Exists(marker)) return;
        foreach (var item in await AppManager.Instance.SubItems() ?? [])
        {
            if (ConfigureMissingInterval(item) && await ConfigHandler.AddSubItem(config, item) != 0)
                throw new InvalidOperationException("Subscription refresh migration was not saved");
        }
        // One-time migration: a later deliberate disable in advanced settings stays disabled.
        await File.WriteAllTextAsync(marker, "0.6.8");
    }
}
