namespace ServiceLib.Services;

public enum TermitusProbeKind { Tcp, Udp }

public enum TermitusProbeStatus { Ok, Slow, Fail }

/// <summary>One tested server (a location × one transport). Ms &lt;= 0 = no answer.</summary>
public sealed record TermitusProbe(string IndexId, string LocationKey, bool IsRussia, TermitusProbeKind Kind, string Technical, int Ms)
{
    public TermitusProbeStatus Status => TermitusConnCheck.StatusOf(Ms);
}

/// <summary>A suggestion under the results; Action is what its button does (null = text only).</summary>
public sealed record TermitusCheckAdvice(string Text, TermitusAdviceAction Action = TermitusAdviceAction.None);

public enum TermitusAdviceAction { None, UseTcp, UseUdp, OtherServer, Report }

/// <summary>One line of the result list, ready for the screen.</summary>
public sealed record TermitusCheckRowData(string Key, string Badge, string Title, string Protocol, TermitusProbeStatus Status, string Mark, string MsText, string Explain)
{
    public bool IsOk => Status == TermitusProbeStatus.Ok;
    public bool IsSlow => Status == TermitusProbeStatus.Slow;
    public bool IsFail => Status == TermitusProbeStatus.Fail;
}

/// <summary>
/// 0.6 «Проверка подключения»: turns the core's real-delay numbers (the same test as «Проверить скорость»)
/// into ✓ / ✗ / «медленно» with a plain-Russian reason and a suggestion. Pure logic, unit-tested; the
/// measuring itself is the existing SpeedtestService (Realping) in the view model.
/// </summary>
public static class TermitusConnCheck
{
    /// <summary>Real delay above this is «медленно» (a real-delay probe includes the TLS/handshake round trips).</summary>
    public const int SlowMs = 1000;

    public static TermitusProbeStatus StatusOf(int ms) => ms <= 0 ? TermitusProbeStatus.Fail : ms > SlowMs ? TermitusProbeStatus.Slow : TermitusProbeStatus.Ok;

    public static string ProtocolLabel(TermitusProbeKind k, TermitusLocale loc) => k == TermitusProbeKind.Udp ? loc.ProtoUdpFull : loc.ProtoTcpFull;

    /// <summary>Parses TER_TEST_PROBE: "udp=-1,tcp=120,Германия=-1" (first matching rule wins).</summary>
    public static List<(string Match, int Ms)> ParseSpec(string? spec)
    {
        var list = new List<(string, int)>();
        foreach (var part in (spec ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.LastIndexOf('=');
            if (eq > 0 && int.TryParse(part[(eq + 1)..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var ms))
            {
                list.Add((part[..eq].Trim(), ms));
            }
        }
        return list;
    }

    public static int ApplySpec(IReadOnlyList<(string Match, int Ms)> rules, string locationKey, TermitusProbeKind kind, int real)
    {
        foreach (var (m, ms) in rules)
        {
            if ((m.Equals("udp", StringComparison.OrdinalIgnoreCase) && kind == TermitusProbeKind.Udp)
                || (m.Equals("tcp", StringComparison.OrdinalIgnoreCase) && kind == TermitusProbeKind.Tcp)
                || locationKey.Contains(m, StringComparison.OrdinalIgnoreCase))
            {
                return ms;
            }
        }
        return real;
    }

    private static bool Answers(IEnumerable<TermitusProbe> ps) => ps.Any(p => p.Ms > 0);

    /// <summary>Why one line is red/amber/green, in plain words.</summary>
    public static string Explain(TermitusProbe p, IReadOnlyList<TermitusProbe> all, TermitusLocale loc)
    {
        var sameLocation = all.Where(o => o.LocationKey == p.LocationKey && o.IndexId != p.IndexId).ToList();
        var otherKind = sameLocation.Where(o => o.Kind != p.Kind).ToList();
        switch (p.Status)
        {
            case TermitusProbeStatus.Ok:
                return loc.CheckOkLine;

            case TermitusProbeStatus.Slow:
                return loc.CheckSlowLine(p.Ms);

            default:
                if (p.Kind == TermitusProbeKind.Udp && Answers(otherKind))
                {
                    return loc.CheckUdpBlockedLine;
                }
                if (p.Kind == TermitusProbeKind.Tcp && Answers(otherKind))
                {
                    return loc.CheckTcpBlockedLine;
                }
                return loc.CheckDownLine;
        }
    }

    public static List<TermitusCheckRowData> Rows(IReadOnlyList<TermitusProbe> probes, TermitusLocale loc)
        => probes
            .OrderBy(p => p.IsRussia ? 1 : 0)
            .ThenBy(p => p.Status == TermitusProbeStatus.Fail ? 1 : 0)
            .ThenBy(p => p.LocationKey, StringComparer.Ordinal)
            .ThenBy(p => p.Kind)
            .Select(p =>
            {
                var (badge, name) = TermitusLocations.DisplayParts(p.LocationKey);
                return new TermitusCheckRowData(
                    p.IndexId, badge, name, ProtocolLabel(p.Kind, loc), p.Status,
                    p.Status switch { TermitusProbeStatus.Ok => "✓", TermitusProbeStatus.Slow => "~", _ => "✗" },
                    p.Ms > 0 ? loc.Ms(p.Ms) : "—",
                    Explain(p, probes, loc));
            })
            .ToList();

    /// <summary>Suggestions for the whole picture, most important first.</summary>
    public static List<TermitusCheckAdvice> Analyze(IReadOnlyList<TermitusProbe> probes, TermitusLocale loc)
    {
        var advice = new List<TermitusCheckAdvice>();
        if (probes.Count == 0)
        {
            return advice;
        }
        var tcp = probes.Where(p => p.Kind == TermitusProbeKind.Tcp).ToList();
        var udp = probes.Where(p => p.Kind == TermitusProbeKind.Udp).ToList();
        var ok = probes.Where(p => p.Ms > 0).ToList();

        if (ok.Count == 0)
        {
            advice.Add(new(loc.CheckAllDown, TermitusAdviceAction.Report));
            return advice;
        }
        if (ok.All(p => p.IsRussia) && probes.Any(p => !p.IsRussia))
        {
            advice.Add(new(loc.CheckOnlyRussia, TermitusAdviceAction.Report));
        }
        if (udp.Count > 0 && !Answers(udp) && Answers(tcp))
        {
            advice.Add(new(loc.CheckUdpCut, TermitusAdviceAction.UseTcp));
        }
        else if (tcp.Count > 0 && !Answers(tcp) && Answers(udp))
        {
            advice.Add(new(loc.CheckTcpCut, TermitusAdviceAction.UseUdp));
        }
        var failed = probes.Count(p => p.Ms <= 0);
        if (failed > 0 && advice.Count == 0)
        {
            advice.Add(new(loc.CheckSomeDown(failed, probes.Count), TermitusAdviceAction.OtherServer));
        }
        if (ok.All(p => p.Ms > SlowMs))
        {
            advice.Add(new(loc.CheckAllSlow));
        }
        if (advice.Count == 0)
        {
            advice.Add(new(loc.CheckAllGood));
        }
        return advice;
    }

    /// <summary>Compact lines for the problem report: one per server, no addresses.</summary>
    public static List<string> ReportLines(IReadOnlyList<TermitusProbe> probes)
        => probes
            .OrderBy(p => p.LocationKey, StringComparer.Ordinal).ThenBy(p => p.Kind)
            .Select(p =>
            {
                var name = TermitusLocations.DisplayParts(p.LocationKey).Name;
                var res = p.Status switch
                {
                    TermitusProbeStatus.Ok => $"ok {p.Ms} мс",
                    TermitusProbeStatus.Slow => $"медленно {p.Ms} мс",
                    _ => "нет ответа",
                };
                return $"{name} · {(p.Kind == TermitusProbeKind.Udp ? "UDP" : "TCP")} ({p.Technical}): {res}";
            })
            .ToList();

    /// <summary>"what failed / what still works" for the connection-error card.</summary>
    public static (string Failed, string Works) FailedAndWorks(IReadOnlyList<TermitusProbe> probes, TermitusLocale loc)
    {
        if (probes.Count == 0)
        {
            return (loc.ErrFailedUnknown, loc.ErrWorksUnknown);
        }
        var tcp = probes.Where(p => p.Kind == TermitusProbeKind.Tcp).ToList();
        var udp = probes.Where(p => p.Kind == TermitusProbeKind.Udp).ToList();
        var bad = new List<string>();
        var good = new List<string>();
        void Part(string name, List<TermitusProbe> ps)
        {
            if (ps.Count == 0)
            {
                return;
            }
            var okc = ps.Count(p => p.Ms > 0);
            (okc == 0 ? bad : good).Add(okc == 0 ? loc.ErrNoneAnswer(name, ps.Count) : loc.ErrSomeAnswer(name, okc, ps.Count));
        }
        Part(loc.ProtoTcpFull, tcp);
        Part(loc.ProtoUdpFull, udp);
        return (bad.Count == 0 ? loc.ErrFailedUnknown : string.Join(" ", bad), good.Count == 0 ? loc.ErrWorksNothing : string.Join(" ", good));
    }
}
