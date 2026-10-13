namespace ServiceLib.Services;

// In-app payment (TER 0.5). Contract with the backend (all under the user's own
// subscription link, like /info; the token inside the URL is the only credential):
//   GET  <sub>/plans                -> {plans:[{id,title,price_rub,days}], methods:[{id,title}], offer_url}
//   POST <sub>/pay {plan_id,method} -> {payment_id, pay_url}
//   GET  <sub>/pay/{payment_id}     -> {status: pending|paid|failed|expired}
// 404/405/501 on /plans = "not built yet": the UI says payment is in the Telegram bot.
// Nothing is logged except status codes: the URLs hold the token.

public sealed record TermitusPlan(string Id, string Title, int PriceRub, int Days);

public sealed record TermitusPayMethod(string Id, string Title);

public sealed record TermitusPlans(IReadOnlyList<TermitusPlan> Plans, IReadOnlyList<TermitusPayMethod> Methods, string? OfferUrl);

public enum TermitusPayStatus
{
    Pending,
    Paid,
    Failed,
    Expired,
}

public enum TermitusPaymentsKind
{
    Ok,
    /// <summary>Backend has no payment API yet (404/405/501) or answered nonsense: use the bot.</summary>
    Unavailable,
    /// <summary>No HTTP answer at all.</summary>
    Offline,
    /// <summary>Other error (5xx, 4xx): try again later.</summary>
    Error,
}

public sealed record TermitusPlansResult(TermitusPaymentsKind Kind, TermitusPlans? Plans = null);

public sealed record TermitusPayStartResult(TermitusPaymentsKind Kind, string? PaymentId = null, string? PayUrl = null);

public enum TermitusWaitOutcome
{
    Paid,
    Failed,
    Expired,
    /// <summary>Gave up after the maximum wait (15 min).</summary>
    TimedOut,
    Cancelled,
}

public static class TermitusPayments
{
    private const string _tag = "TermitusPayments";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    #region Pure logic (unit-tested)

    /// <summary><c>https://h/subscription/T?x=1</c> + "plans" -> <c>https://h/subscription/T/plans?x=1</c>. Null if not http(s).</summary>
    public static string? Endpoint(string? subscriptionUrl, params string[] segments)
    {
        if (subscriptionUrl.IsNullOrEmpty()
            || !Uri.TryCreate(subscriptionUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }
        var b = new UriBuilder(uri) { Fragment = string.Empty };
        b.Path = b.Path.TrimEnd('/') + "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
        return b.Uri.AbsoluteUri;
    }

    /// <summary>Tolerant parse: bad plans/methods are skipped; no usable plan or method -> null (= unavailable).</summary>
    public static TermitusPlans? ParsePlans(string? json)
    {
        if (json.IsNullOrEmpty())
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var plans = new List<TermitusPlan>();
            if (root.TryGetProperty("plans", out var pa) && pa.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in pa.EnumerateArray())
                {
                    var id = Str(e, "id");
                    var title = Str(e, "title");
                    if (id.IsNullOrEmpty() || title.IsNullOrEmpty() || !e.TryGetProperty("price_rub", out var pr)
                        || !TryInt(pr, out var price) || price < 0)
                    {
                        continue;
                    }
                    var days = e.TryGetProperty("days", out var dv) && TryInt(dv, out var d) && d > 0 ? d : 0;
                    plans.Add(new TermitusPlan(id!, title!, price, days));
                }
            }
            var methods = new List<TermitusPayMethod>();
            if (root.TryGetProperty("methods", out var ma) && ma.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ma.EnumerateArray())
                {
                    var id = Str(e, "id");
                    var title = Str(e, "title");
                    if (id.IsNotEmpty() && title.IsNotEmpty())
                    {
                        methods.Add(new TermitusPayMethod(id!, title!));
                    }
                }
            }
            if (plans.Count == 0 || methods.Count == 0)
            {
                return null;
            }
            var offer = Str(root, "offer_url");
            return new TermitusPlans(plans, methods, IsHttps(offer) ? offer : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static TermitusPayStartResult InterpretStart(int? status, string? body)
    {
        if (status == null)
        {
            return new(TermitusPaymentsKind.Offline);
        }
        if (status is 404 or 405 or 501)
        {
            return new(TermitusPaymentsKind.Unavailable);
        }
        if (status is < 200 or >= 300)
        {
            return new(TermitusPaymentsKind.Error);
        }
        try
        {
            using var doc = JsonDocument.Parse(body ?? string.Empty);
            var id = Str(doc.RootElement, "payment_id");
            var url = Str(doc.RootElement, "pay_url");
            // pay_url may be a payment provider's page, but only https is ever opened.
            return id.IsNotEmpty() && IsHttps(url)
                ? new(TermitusPaymentsKind.Ok, id, url)
                : new(TermitusPaymentsKind.Error);
        }
        catch (JsonException)
        {
            return new(TermitusPaymentsKind.Error);
        }
    }

    /// <summary>null = could not tell (keep waiting). Unknown words count as pending.</summary>
    public static TermitusPayStatus? ParseStatus(int? httpStatus, string? body)
    {
        if (httpStatus is not (>= 200 and < 300))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body ?? string.Empty);
            return Str(doc.RootElement, "status")?.ToLowerInvariant() switch
            {
                "paid" => TermitusPayStatus.Paid,
                "failed" => TermitusPayStatus.Failed,
                "expired" => TermitusPayStatus.Expired,
                "pending" => TermitusPayStatus.Pending,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Polls <paramref name="fetch"/> every <paramref name="interval"/> until it reports a final
    /// state, <paramref name="maxWait"/> passes or the token is cancelled. Time comes from the sum
    /// of delays (no clock), so tests can run it instantly with a fake delay.
    /// </summary>
    public static async Task<TermitusWaitOutcome> WaitAsync(
        Func<CancellationToken, Task<TermitusPayStatus?>> fetch,
        TimeSpan interval,
        TimeSpan maxWait,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        try
        {
            while (waited <= maxWait)
            {
                var st = await fetch(ct);
                switch (st)
                {
                    case TermitusPayStatus.Paid: return TermitusWaitOutcome.Paid;
                    case TermitusPayStatus.Failed: return TermitusWaitOutcome.Failed;
                    case TermitusPayStatus.Expired: return TermitusWaitOutcome.Expired;
                }
                await delay(interval, ct);
                waited += interval;
            }
            return TermitusWaitOutcome.TimedOut;
        }
        catch (OperationCanceledException)
        {
            return TermitusWaitOutcome.Cancelled;
        }
    }

    public static string FormatPrice(int rub) => rub.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) + " ₽";

    private static bool IsHttps(string? url) =>
        url.IsNotEmpty() && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString()?.Trim(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static bool TryInt(JsonElement v, out int n)
    {
        n = 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt32(out n))
            {
                return true;
            }
            if (v.TryGetDouble(out var d) && d >= 0 && d < int.MaxValue)
            {
                n = (int)Math.Round(d);
                return true;
            }
            return false;
        }
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n);
    }

    #endregion Pure logic

    #region Network

    private static string? SubUrl(SubItem sub) => Utils.GetPunycode(sub.Url.TrimEx());

    public static async Task<TermitusPlansResult> GetPlansAsync(SubItem sub, int? socksPort, CancellationToken ct)
    {
        var url = Endpoint(SubUrl(sub), "plans");
        if (url == null)
        {
            return new(TermitusPaymentsKind.Unavailable);
        }
        var res = await TermitusHttp.GetAsync(url, socksPort, RequestTimeout, ct);
        Logging.SaveLog($"{_tag}: plans -> {res.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no answer"}");
        if (res.Status == null)
        {
            return new(TermitusPaymentsKind.Offline);
        }
        if (res.Status is 404 or 405 or 501)
        {
            return new(TermitusPaymentsKind.Unavailable);
        }
        if (!res.IsSuccess)
        {
            return new(TermitusPaymentsKind.Error);
        }
        var plans = ParsePlans(res.Body);
        return plans == null ? new(TermitusPaymentsKind.Unavailable) : new(TermitusPaymentsKind.Ok, plans);
    }

    public static async Task<TermitusPayStartResult> StartAsync(SubItem sub, string planId, string method, int? socksPort, CancellationToken ct)
    {
        var url = Endpoint(SubUrl(sub), "pay");
        if (url == null)
        {
            return new(TermitusPaymentsKind.Unavailable);
        }
        var res = await TermitusHttp.PostJsonAsync(url, new TermitusPayRequest(planId, method), socksPort, RequestTimeout, ct);
        Logging.SaveLog($"{_tag}: pay -> {res.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no answer"}");
        return InterpretStart(res.Status, res.Body);
    }

    public static async Task<TermitusPayStatus?> GetStatusAsync(SubItem sub, string paymentId, int? socksPort, CancellationToken ct)
    {
        var url = Endpoint(SubUrl(sub), "pay", paymentId);
        if (url == null)
        {
            return null;
        }
        var res = await TermitusHttp.GetAsync(url, socksPort, RequestTimeout, ct);
        return ParseStatus(res.Status, res.Body);
    }

    #endregion Network
}

public sealed record TermitusPayRequest(
    [property: JsonPropertyName("plan_id")] string PlanId,
    [property: JsonPropertyName("method")] string Method);
