using System.Globalization;

namespace ServiceLib.Services;

// TER Account 2.0 (0.6.x): managing the subscription from the app. All calls go to
//   <subscription-url>/account/...   (same device token as /info; the token in the URL is the only credential)
//   GET devices | PATCH devices/{id} {name} | DELETE devices/{id} | POST devices/{id}/manage {on}
//   POST devices/invite {confirm_pay} | GET|PATCH referrals | GET payments | POST rotate-link
// 403 = this device is not a manager (the subscription's owner manages it). Nothing but status codes is logged:
// the URLs, the invite link and the new subscription link are secrets.

public enum TermitusAcctKind
{
    Ok,
    /// <summary>403: this device may not manage the subscription.</summary>
    NotManager,
    /// <summary>No HTTP answer at all.</summary>
    Offline,
    /// <summary>402: not enough balance.</summary>
    PaymentRequired,
    /// <summary>429: too many open invitations / requests.</summary>
    TooMany,
    /// <summary>409 / 422: the server refused with a reason (the reason is in Detail).</summary>
    Refused,
    /// <summary>Anything else (404, 5xx, nonsense).</summary>
    Error,
}

public sealed record TermitusAcctResult(TermitusAcctKind Kind, string? Body = null, string? Detail = null);

public sealed record TermitusAcctDevice(int Id, string Name, string Platform, DateTimeOffset? LastSeen, bool IsThis, bool CanManage, bool PaidSlot, bool IsActive);

public sealed record TermitusReferrals(string? Link, string? SiteLink, int Invited, decimal BonusRub, int BonusDays, string Reward, decimal Percent, int DaysPerPayment)
{
    public bool RewardIsDays => Reward == "days";
}

public sealed record TermitusPaymentItem(DateTimeOffset? Date, decimal Amount, string Currency, string Provider, string Kind, int PlanDays, string Status);

/// <summary>Answer of POST devices/invite: either "needs payment" (first call past the free limit) or the finished link.</summary>
public sealed record TermitusInvite(bool NeedsPayment, int PriceRub, decimal BalanceRub, string? JoinUrl, DateTimeOffset? ExpiresAt);

public static class TermitusAccountApi
{
    private const string _tag = "TermitusAccountApi";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    #region Pure logic (unit-tested)

    public static string? Endpoint(string? subscriptionUrl, params string[] segments)
        => TermitusPayments.Endpoint(subscriptionUrl, ["account", .. segments]);

    /// <summary>HTTP status + body -> kind (+ the server's own reason for 409/422/402/429 when it sent one).</summary>
    public static TermitusAcctResult Interpret(int? status, string? body)
    {
        if (status == null)
        {
            return new(TermitusAcctKind.Offline);
        }
        if (status is >= 200 and < 300)
        {
            return new(TermitusAcctKind.Ok, body);
        }
        var detail = DetailOf(body);
        return status switch
        {
            403 => new(TermitusAcctKind.NotManager, null, detail),
            402 => new(TermitusAcctKind.PaymentRequired, body, detail),
            429 => new(TermitusAcctKind.TooMany, null, detail),
            409 or 422 => new(TermitusAcctKind.Refused, null, detail),
            _ => new(TermitusAcctKind.Error, null, null),
        };
    }

    private static string? DetailOf(string? body)
    {
        if (body.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var d = Str(doc.RootElement, "detail");
            return d is { Length: > 0 and <= 300 } ? d : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Backend datetimes are naive UTC ("2026-10-13T10:00:00"): no offset means UTC.</summary>
    public static DateTimeOffset? ParseTime(string? s)
        => s.IsNotEmpty() && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static List<TermitusAcctDevice>? ParseDevices(string? json)
    {
        if (!TryRoot(json, out var root) || !root.TryGetProperty("devices", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<TermitusAcctDevice>();
        foreach (var e in arr.EnumerateArray())
        {
            if (!TryInt(e, "id", out var id))
            {
                continue;
            }
            var name = Str(e, "name");
            var platform = Str(e, "platform") ?? string.Empty;
            list.Add(new TermitusAcctDevice(id, name.IsNullOrEmpty() ? PlatformLabel(platform, false) : name, platform,
                ParseTime(Str(e, "last_seen")), Bool(e, "is_this_device"), Bool(e, "can_manage"), Bool(e, "paid_slot"),
                !e.TryGetProperty("is_active", out _) || Bool(e, "is_active")));
        }
        return list;
    }

    public static TermitusReferrals? ParseReferrals(string? json)
    {
        if (!TryRoot(json, out var root))
        {
            return null;
        }
        var reward = Str(root, "reward") == "days" ? "days" : "money";
        var link = Str(root, "link");
        var site = Str(root, "site_link");
        return new TermitusReferrals(IsHttps(link) ? link : null, IsHttps(site) ? site : null,
            Int(root, "invited_count"), Dec(root, "bonus_rub"), Int(root, "bonus_days"), reward,
            Dec(root, "bonus_percent"), Int(root, "bonus_days_per_payment"));
    }

    public static List<TermitusPaymentItem>? ParsePayments(string? json)
    {
        if (!TryRoot(json, out var root) || !root.TryGetProperty("payments", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<TermitusPaymentItem>();
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            list.Add(new TermitusPaymentItem(ParseTime(Str(e, "date")), Dec(e, "amount"), Str(e, "currency") ?? "RUB",
                Str(e, "provider") ?? string.Empty, Str(e, "kind") ?? "purchase", Int(e, "plan_days"), Str(e, "status") ?? "paid"));
        }
        return list;
    }

    /// <summary>Invite answer. A join link is only accepted when it is our https link; otherwise null (= error).</summary>
    public static TermitusInvite? ParseInvite(string? json)
    {
        if (!TryRoot(json, out var root))
        {
            return null;
        }
        var price = Int(root, "price_rub");
        if (Bool(root, "needs_payment"))
        {
            return new TermitusInvite(true, price, Dec(root, "balance_rub"), null, null);
        }
        var url = Str(root, "join_url");
        return TermitusAccount.IsSafeRenewUrl(url)
            ? new TermitusInvite(false, price, 0, url, ParseTime(Str(root, "expires_at")))
            : null;
    }

    /// <summary>The 402 body carries the price and balance too.</summary>
    public static TermitusInvite? ParseNoFunds(string? json)
        => TryRoot(json, out var root) ? new TermitusInvite(true, Int(root, "price_rub"), Dec(root, "balance_rub"), null, null) : null;

    /// <summary>New shared subscription link from rotate-link; only our https link.</summary>
    public static string? ParseRotated(string? json)
    {
        var url = TryRoot(json, out var root) ? Str(root, "subscription_url") : null;
        return TermitusAccount.IsSafeRenewUrl(url) ? url : null;
    }

    public static string PlatformLabel(string? platform, bool en)
    {
        var p = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return p switch
        {
            "" => en ? "Device" : "Устройство",
            "macos" or "mac" or "osx" or "darwin" => "macOS",
            "windows" or "win" => "Windows",
            "linux" => "Linux",
            "android" => "Android",
            "ios" or "iphone" or "ipad" => "iOS",
            "router" => en ? "Router" : "Роутер",
            "tv" or "androidtv" => "TV",
            _ => char.ToUpperInvariant(p[0]) + p[1..],
        };
    }

    /// <summary>"только что", "5 мин назад", "3 ч назад", "вчера", "3 дня назад", "12 сентября" / English twins.</summary>
    public static string RelativeTime(DateTimeOffset? at, DateTimeOffset now, bool en)
    {
        if (at is not { } t)
        {
            return en ? "never connected" : "ещё не подключалось";
        }
        var d = now - t;
        if (d < TimeSpan.FromMinutes(1))
        {
            return en ? "just now" : "только что";
        }
        if (d < TimeSpan.FromHours(1))
        {
            var m = (int)d.TotalMinutes;
            return en ? $"{m} min ago" : $"{m} мин назад";
        }
        if (d < TimeSpan.FromHours(24))
        {
            var h = (int)d.TotalHours;
            return en ? $"{h} h ago" : $"{h} ч назад";
        }
        var days = (int)Math.Floor((now.ToLocalTime().Date - t.ToLocalTime().Date).TotalDays);
        if (days <= 1)
        {
            return en ? "yesterday" : "вчера";
        }
        if (days < 7)
        {
            return en ? $"{days} days ago" : $"{days} {TermitusAccount.RuPlural(days, "день", "дня", "дней")} назад";
        }
        return TermitusAccount.FormatDate(t, now, en);
    }

    /// <summary>What a payment row says it was for: «Подписка 30 дн.», «Пополнение», «Устройство», «Подарок».</summary>
    public static string PaymentWhat(string kind, int planDays, string provider, bool en)
    {
        var k = (kind ?? string.Empty).ToLowerInvariant();
        if (k.Contains("gift"))
        {
            return en ? "Gift" : "Подарок";
        }
        if (k.Contains("device"))
        {
            return en ? "Device" : "Устройство";
        }
        if (k.Contains("topup") || k.Contains("balance") || (planDays <= 0 && !k.Contains("purchase")))
        {
            return en ? "Top-up" : "Пополнение";
        }
        if (planDays <= 0)
        {
            return en ? "Top-up" : "Пополнение";
        }
        return en ? $"Subscription {planDays} d." : $"Подписка {planDays} дн.";
    }

    /// <summary>Word for a payment that is not simply paid; null = paid (nothing to add).</summary>
    public static string? PaymentStatusWord(string status, bool en) => (status ?? string.Empty).ToLowerInvariant() switch
    {
        "pending" => en ? "waiting for payment" : "ждёт оплаты",
        "failed" => en ? "failed" : "не прошла",
        "expired" => en ? "expired" : "истекла",
        _ => null,
    };

    /// <summary>"1 990 ₽" for RUB, otherwise "12.5 USDT".</summary>
    public static string PaymentAmount(decimal amount, string currency, bool en)
    {
        var c = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (c is "" or "RUB" or "RUR" or "₽")
        {
            return TermitusAccount.FormatMoney(amount, en);
        }
        var whole = decimal.Truncate(amount) == amount;
        return $"{amount.ToString(whole ? "0" : "0.##", en ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU"))} {c}";
    }

    /// <summary>One line under the payment: "13 октября · Карта / СБП" (+ " · не прошла").</summary>
    public static string PaymentCaption(TermitusPaymentItem p, DateTimeOffset now, bool en)
    {
        var parts = new List<string>();
        if (p.Date is { } d)
        {
            parts.Add(TermitusAccount.FormatDate(d, now, en));
        }
        if (p.Provider.IsNotEmpty())
        {
            parts.Add(p.Provider);
        }
        if (PaymentStatusWord(p.Status, en) is { } w)
        {
            parts.Add(w);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>«15% или 4 дня с каждой оплаты и пополнения друга». Percent without a trailing ".0".</summary>
    public static string ReferralRule(decimal percent, int days, bool en)
    {
        var pct = percent.ToString("0.##", CultureInfo.InvariantCulture);
        return en
            ? $"{pct}% or {days} {(days == 1 ? "day" : "days")} from every payment and top-up of your friend"
            : $"{pct}% или {days} {TermitusAccount.RuPlural(days, "день", "дня", "дней")} с каждой оплаты и пополнения друга";
    }

    #endregion Pure logic

    #region Network

    private static string? SubUrl(SubItem sub) => Utils.GetPunycode(sub.Url.TrimEx());

    private static async Task<TermitusAcctResult> CallAsync(SubItem sub, HttpMethod method, string[] segments, object? body, int? socksPort, CancellationToken ct)
    {
        var url = Endpoint(SubUrl(sub), segments);
        if (url == null)
        {
            return new(TermitusAcctKind.Error);
        }
        var res = await TermitusHttp.SendAsync(method, url, body == null ? null : JsonSerializer.Serialize(body), socksPort, RequestTimeout, ct);
        Logging.SaveLog($"{_tag}: {method.Method} {segments[0]}{(segments.Length > 1 ? "/…" : string.Empty)} -> {res.Status?.ToString(CultureInfo.InvariantCulture) ?? "no answer"}");
        return Interpret(res.Status, res.Body);
    }

    public static Task<TermitusAcctResult> GetDevicesAsync(SubItem sub, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Get, ["devices"], null, port, ct);

    public static Task<TermitusAcctResult> RenameAsync(SubItem sub, int id, string name, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Patch, ["devices", id.ToString(CultureInfo.InvariantCulture)], new { name }, port, ct);

    public static Task<TermitusAcctResult> RemoveAsync(SubItem sub, int id, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Delete, ["devices", id.ToString(CultureInfo.InvariantCulture)], null, port, ct);

    public static Task<TermitusAcctResult> SetManageAsync(SubItem sub, int id, bool on, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Post, ["devices", id.ToString(CultureInfo.InvariantCulture), "manage"], new { on }, port, ct);

    public static Task<TermitusAcctResult> InviteAsync(SubItem sub, bool confirmPay, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Post, ["devices", "invite"], new { confirm_pay = confirmPay }, port, ct);

    public static Task<TermitusAcctResult> GetReferralsAsync(SubItem sub, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Get, ["referrals"], null, port, ct);

    public static Task<TermitusAcctResult> SetRewardAsync(SubItem sub, string reward, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Patch, ["referrals"], new { reward }, port, ct);

    public static Task<TermitusAcctResult> GetPaymentsAsync(SubItem sub, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Get, ["payments"], null, port, ct);

    public static Task<TermitusAcctResult> RotateLinkAsync(SubItem sub, int? port, CancellationToken ct)
        => CallAsync(sub, HttpMethod.Post, ["rotate-link"], null, port, ct);

    #endregion Network

    #region JSON helpers

    private static bool TryRoot(string? json, out JsonElement root)
    {
        root = default;
        if (json.IsNullOrEmpty())
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString()?.Trim(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static decimal Dec(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return 0;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static int Int(JsonElement e, string name) => (int)Math.Clamp(Math.Round(Dec(e, name)), 0, int.MaxValue);

    private static bool TryInt(JsonElement e, string name, out int n)
    {
        n = 0;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        return v.TryGetInt32(out n);
    }

    private static bool IsHttps(string? url) =>
        url.IsNotEmpty() && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    #endregion JSON helpers
}
