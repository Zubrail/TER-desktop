namespace ServiceLib.Services;

// JSON contracts of the TER app endpoints on our backend (TER 0.4). Change field
// names HERE only. Every response field is optional/nullable: the client treats a
// missing or malformed field like a missing endpoint (feature hidden, no crash).
//
//   POST https://termitus.online/app/login/start      body TermitusLoginStartRequest  -> TermitusLoginStartResponse
//   GET  https://termitus.online/app/login/poll/{code} -> 202 pending | 200 ok (+subscription_url, once)
//                                                         | 410 expired/declined/used | 404
//   POST <subscription_url>/support                   body TermitusSupportPostRequest -> TermitusSupportPostResponse
//   GET  <subscription_url>/support?after=<id>        -> [TermitusSupportMessageDto]
//   GET  <subscription_url>/info                      -> TermitusAccountInfoDto (TermitusAccount.cs; + balance, topup_url)

public sealed record TermitusLoginStartRequest(
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("platform")] string Platform);

public sealed record TermitusLoginStartResponse(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("bot_url")] string? BotUrl,
    [property: JsonPropertyName("expires_in")] double? ExpiresIn,
    [property: JsonPropertyName("poll_interval")] double? PollInterval);

public sealed record TermitusLoginPollResponse(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("subscription_url")] string? SubscriptionUrl);

public sealed record TermitusSupportPostRequest(
    [property: JsonPropertyName("text")] string Text);

public sealed record TermitusSupportPostResponse(
    [property: JsonPropertyName("id"), JsonConverter(typeof(TermitusFlexibleIdConverter))] string? Id);

public sealed record TermitusSupportMessageDto(
    [property: JsonPropertyName("id"), JsonConverter(typeof(TermitusFlexibleIdConverter))] string? Id,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("at"), JsonConverter(typeof(TermitusFlexibleTimeConverter))] long? At);

/// <summary>Message ids may come as numbers or strings; kept as a string either way.</summary>
public sealed class TermitusFlexibleIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var l)
                    ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Timestamps as unix seconds: accepts a number (seconds, or milliseconds when it is
/// obviously ms) or an ISO-8601 string. Anything else -> null.
/// </summary>
public sealed class TermitusFlexibleTimeConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetDouble(out var d) && d > 0 && !double.IsInfinity(d))
                {
                    return d > 1e11 ? (long)(d / 1000) : (long)d;
                }
                return null;

            case JsonTokenType.String:
                var s = reader.GetString();
                if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
                {
                    return n > 100_000_000_000 ? n / 1000 : n;
                }
                return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)
                    ? dto.ToUnixTimeSeconds()
                    : null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
