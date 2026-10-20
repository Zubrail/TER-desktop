using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.5 security review D-1/D-2: the privileged side (root on macOS, an elevated TER on Windows) never runs a core on a
/// config that sits in a user-writable folder as it is. It takes a copy, and this sanitiser makes sure the copy cannot turn the
/// privileged core into a file-write tool: it parses the JSON (garbage = refuse), requires a non-empty inbounds and outbounds
/// list, strips log.output, xray log.access/error and external_ui*, and pins the sing-box cache file into a protected work dir.
/// The macOS supervisor does the same in JavaScript (<see cref="TermitusMacShell.ConfigValidatorJs"/>); keep both in step.
/// </summary>
public static class TermitusConfigSanitizer
{
    private static readonly string[] DroppedKeys = ["external_ui", "external_ui_download_url", "external_ui_download_detour"];

    /// <summary>Sanitised JSON text, or null when the config must be refused.</summary>
    public static string? Sanitize(string? json, string protectedWorkDir)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, null, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is not JsonObject obj
            || obj["inbounds"] is not JsonArray { Count: > 0 }
            || obj["outbounds"] is not JsonArray { Count: > 0 })
        {
            return null;
        }
        Strip(obj);
        if (obj["log"] is JsonObject log)
        {
            if (log["output"] is JsonValue o && o.TryGetValue<string>(out var os) && os.Length > 0)
            {
                log.Remove("output");
            }
            foreach (var k in new[] { "access", "error" })
            {
                if (log[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 && s != "none")
                {
                    log.Remove(k);
                }
            }
        }
        if (obj["experimental"] is JsonObject exp && exp["cache_file"] is JsonObject cache)
        {
            cache["path"] = Path.Combine(protectedWorkDir, "cache.db");
        }
        return obj.ToJsonString();
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in DroppedKeys)
                {
                    o.Remove(k);
                }
                foreach (var child in o.Select(kv => kv.Value).ToList())
                {
                    Strip(child);
                }
                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    Strip(child);
                }
                break;
        }
    }
}
