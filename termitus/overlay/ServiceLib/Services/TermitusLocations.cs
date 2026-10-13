namespace ServiceLib.Services;

/// <summary>
/// Groups imported servers into user-facing "locations" for the TER server picker.
///
/// Our subscription names every transport of a node like "🇫🇷 Франция 2" (VLESS)
/// and "🇫🇷 Франция 2 · H2" (Hysteria2). A location is the name before " · ";
/// picking a location means "use whichever of its transports answers fastest",
/// so the user never has to know what VLESS/gRPC/Hysteria2 are.
///
/// Russian locations are never chosen by "Auto" (0.5): they answer fastest from
/// inside RU (short distance) but are not a way out. The user can pick one by hand.
/// </summary>
public static class TermitusLocations
{
    public const string AutoKey = "";
    private const string ChoiceFile = "ter-location.txt";

    public sealed record Location(string Key, string Title, bool IsRussia, IReadOnlyList<string> IndexIds);

    public static string KeyOf(string? remarks)
    {
        var r = (remarks ?? string.Empty).Trim();
        var cut = r.IndexOf(" · ", StringComparison.Ordinal);
        return cut > 0 ? r[..cut].Trim() : r;
    }

    /// <summary>
    /// What the UI shows for a location key (0.4.1 freeze fix): "🇳🇱 Нидерланды 1" ->
    /// ("NL", "Нидерланды 1"). Emoji are removed from the text and the flag becomes a
    /// two-letter badge: on macOS every emoji glyph goes through Avalonia's system font
    /// fallback, which loaded the 190 MB Apple Color Emoji font again for every text
    /// layout (~200 ms of a frozen window and ~190 MB of garbage per flag, measured).
    /// Badge: the flag's ISO code, else the first letter of the name, else "·".
    /// </summary>
    public static (string Badge, string Name) DisplayParts(string? key)
    {
        var s = key ?? string.Empty;
        string? iso = null;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, s[i + 1]);
                i++;
                // Regional indicator A..Z (U+1F1E6..U+1F1FF): two of them = a flag.
                if (cp is >= 0x1F1E6 and <= 0x1F1FF && iso == null
                    && i + 2 < s.Length && char.IsHighSurrogate(s[i + 1]) && char.IsLowSurrogate(s[i + 2]))
                {
                    var cp2 = char.ConvertToUtf32(s[i + 1], s[i + 2]);
                    if (cp2 is >= 0x1F1E6 and <= 0x1F1FF)
                    {
                        iso = $"{(char)('A' + cp - 0x1F1E6)}{(char)('A' + cp2 - 0x1F1E6)}";
                        i += 2;
                    }
                }
                continue; // any astral-plane symbol (emoji) is dropped
            }
            if (IsEmojiBmp(c))
            {
                continue;
            }
            sb.Append(c);
        }
        var name = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var badge = iso ?? (name.FirstOrDefault(char.IsLetterOrDigit) is var f and not '\0' ? char.ToUpperInvariant(f).ToString() : "·");
        return (badge, name.Length > 0 ? name : (iso ?? "—"));
    }

    /// <summary>BMP code points that are emoji/presentation helpers, not text.</summary>
    private static bool IsEmojiBmp(char c)
        => c is '\uFE0F' or '\uFE0E' or '\u200D' or '\u20E3'
            || c is >= '\u2600' and <= '\u27BF'   // misc symbols, dingbats (☀ ⚡ ✅ ...)
            || c is >= '\u2B00' and <= '\u2BFF'   // arrows/stars (⭐ ⬆)
            || c is >= '\u2300' and <= '\u23FF';  // technical (⌛ ⏱)

    public static bool IsRussia(string key)
    {
        return key.Contains("🇷🇺", StringComparison.Ordinal)
            || key.Contains("Россия", StringComparison.OrdinalIgnoreCase)
            || key.Contains("Russia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Locations in subscription order (first occurrence wins).</summary>
    public static List<Location> Group(IEnumerable<(string IndexId, string? Remarks)> servers)
    {
        var order = new List<string>();
        var map = new Dictionary<string, List<string>>();
        foreach (var (id, remarks) in servers)
        {
            var key = KeyOf(remarks);
            if (key.Length == 0 || id.IsNullOrEmpty())
            {
                continue;
            }
            if (!map.TryGetValue(key, out var ids))
            {
                ids = [];
                map[key] = ids;
                order.Add(key);
            }
            ids.Add(id);
        }
        return order.Select(k => new Location(k, k, IsRussia(k), map[k])).ToList();
    }

    /// <summary>Fastest responding server of one location, or null.</summary>
    public static string? BestIn(Location loc, IReadOnlyDictionary<string, int> delays)
    {
        return loc.IndexIds
            .Where(id => delays.TryGetValue(id, out var ms) && ms > 0)
            .OrderBy(id => delays[id])
            .FirstOrDefault();
    }

    /// <summary>Best delay of a location (ms), or -1 when nothing answered / not tested.</summary>
    public static int BestDelay(Location loc, IReadOnlyDictionary<string, int> delays)
    {
        var best = BestIn(loc, delays);
        return best == null ? -1 : delays[best];
    }

    /// <summary>
    /// Server for a choice: a location key, or AutoKey = fastest foreign location,
    /// never Russia (0.5).
    /// </summary>
    public static string? Pick(string choice, IReadOnlyList<Location> locations, IReadOnlyDictionary<string, int> delays,
        Func<string, bool>? preferred)
    {
        // 0.5 protocol preference ("only TCP"/"only UDP"): used when something of that kind answers,
        // otherwise the normal pick -- a preference must never be the reason Connect fails.
        if (preferred != null)
        {
            var only = delays.Where(kv => preferred(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var first = Pick(choice, locations, only);
            if (first != null)
            {
                return first;
            }
        }
        return Pick(choice, locations, delays);
    }

    public static string? Pick(string choice, IReadOnlyList<Location> locations, IReadOnlyDictionary<string, int> delays)
    {
        if (choice != AutoKey)
        {
            var loc = locations.FirstOrDefault(l => l.Key == choice);
            return loc == null ? null : BestIn(loc, delays);
        }

        string? Fastest(IEnumerable<Location> ls) => ls
            .Select(l => BestIn(l, delays))
            .Where(id => id != null)
            .OrderBy(id => delays[id!])
            .FirstOrDefault();

        // 0.5: Auto never picks a Russian node (they are not a way out); only by hand.
        return Fastest(locations.Where(l => !l.IsRussia));
    }

    public static string LoadChoice()
    {
        try
        {
            var path = Utils.GetConfigPath(ChoiceFile);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : AutoKey;
        }
        catch
        {
            return AutoKey;
        }
    }

    /// <summary>«Выйти»: back to Auto.</summary>
    public static void ClearChoice()
    {
        try
        {
            var path = Utils.GetConfigPath(ChoiceFile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusLocations", ex);
        }
    }

    public static void SaveChoice(string choice)
    {
        try
        {
            File.WriteAllText(Utils.GetConfigPath(ChoiceFile), choice);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusLocations", ex);
        }
    }
}
