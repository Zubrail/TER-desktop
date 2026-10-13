using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>Support chat: URL building, parsing, merge/order/dedupe and the unread rule.</summary>
public class TermitusSupportTests
{
    private static TermitusSupportMessageDto Op(string id, string text = "ответ") => new(id, "operator", text, 1_790_000_000);
    private static TermitusSupportMessageDto Me(string id, string text = "вопрос") => new(id, "user", text, 1_790_000_000);

    [Test]
    public async Task SupportUrl_KeepsQueryAddsAfter()
    {
        await Assert.That(TermitusSupport.SupportUrl("https://termitus.online/sub/TOK"))
            .IsEqualTo("https://termitus.online/sub/TOK/support");
        await Assert.That(TermitusSupport.SupportUrl("https://termitus.online/sub/TOK/?x=1#frag", "42"))
            .IsEqualTo("https://termitus.online/sub/TOK/support?x=1&after=42");
        await Assert.That(TermitusSupport.SupportUrl("https://h/s", "a b")).IsEqualTo("https://h/s/support?after=a%20b");
        await Assert.That(TermitusSupport.SupportUrl("vless://x@h:1")).IsNull();
        await Assert.That(TermitusSupport.SupportUrl(null)).IsNull();
    }

    [Test]
    public async Task ParseMessages_ArrayWrappedAndJunk()
    {
        var list = TermitusSupport.ParseMessages("""
            [{"id": 7, "from": "operator", "text": "Здравствуйте", "at": 1790000000},
             {"id": "8", "from": "user", "text": "Привет", "at": "2026-10-06T10:00:00Z"},
             {"id": 9, "from": "user", "text": "ms", "at": 1790000000123},
             42, null]
            """);
        await Assert.That(list).IsNotNull();
        await Assert.That(list!.Count).IsEqualTo(3);
        await Assert.That(list[0].Id).IsEqualTo("7");
        await Assert.That(list[1].At).IsEqualTo(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        await Assert.That(list[2].At).IsEqualTo(1790000000L);

        await Assert.That(TermitusSupport.ParseMessages("""{"messages":[{"id":1,"from":"user","text":"x"}]}""")!.Count).IsEqualTo(1);
        await Assert.That(TermitusSupport.ParseMessages("[]")!.Count).IsEqualTo(0);
        await Assert.That(TermitusSupport.ParseMessages("""{"detail":"Not Found"}""")).IsNull();
        await Assert.That(TermitusSupport.ParseMessages("<html>")).IsNull();
        await Assert.That(TermitusSupport.ParseMessages(null)).IsNull();
    }

    [Test]
    public async Task ParsePostedId_NumberOrString()
    {
        await Assert.That(TermitusSupport.ParsePostedId("""{"id": 15}""")).IsEqualTo("15");
        await Assert.That(TermitusSupport.ParsePostedId("""{"id": "m-15"}""")).IsEqualTo("m-15");
        await Assert.That(TermitusSupport.ParsePostedId("{}")).IsNull();
        await Assert.That(TermitusSupport.ParsePostedId("oops")).IsNull();
    }

    [Test]
    public async Task CompareIds_NumericThenOrdinal()
    {
        await Assert.That(TermitusSupport.CompareIds("9", "10")).IsLessThan(0);
        await Assert.That(TermitusSupport.CompareIds("b", "a")).IsGreaterThan(0);
        await Assert.That(TermitusSupport.CompareIds(null, "1")).IsLessThan(0);
        await Assert.That(TermitusSupport.CompareIds(null, null)).IsEqualTo(0);
        await Assert.That(TermitusSupport.MaxId("9", "10")).IsEqualTo("10");
        await Assert.That(TermitusSupport.MaxId(null, "3")).IsEqualTo("3");
    }

    [Test]
    public async Task Merge_DedupesOrdersAndDropsJunk()
    {
        var a = TermitusSupport.Merge([], [Me("10"), Op("9"), Me("2"), new("11", "user", "  ", 1), new(null, "user", "x", 1)]);
        await Assert.That(a.Select(m => m.Id).ToArray()).IsEquivalentTo(new[] { "2", "9", "10" });
        await Assert.That(a[1].FromOperator).IsTrue();

        // Same ids again + one new: no duplicates, server copy replaces ours.
        var b = TermitusSupport.Merge(a, [Op("9", "исправленный ответ"), Op("12")]);
        await Assert.That(b.Count).IsEqualTo(4);
        await Assert.That(b.Single(m => m.Id == "9").Text).IsEqualTo("исправленный ответ");
        await Assert.That(b[^1].Id).IsEqualTo("12");
    }

    [Test]
    public async Task Merge_LocalStaysLastUntilServerCopy()
    {
        var local = new TermitusChatMessage(TermitusSupport.LocalPrefix + "1", false, "мой вопрос", 1, Local: true);
        var a = TermitusSupport.Merge([local], [Op("5")]);
        await Assert.That(a[^1].Local).IsTrue();
        await Assert.That(a.Count).IsEqualTo(2);

        // A sent message that already got its real id (Local=true) disappears when the server copy arrives.
        var sent = new TermitusChatMessage("6", false, "мой вопрос", 1, Local: true);
        var b = TermitusSupport.Merge([.. a.Where(m => !m.Local), sent], [Me("6", "мой вопрос")]);
        await Assert.That(b.Count).IsEqualTo(2);
        await Assert.That(b.All(m => !m.Local)).IsTrue();
    }

    [Test]
    public async Task Merge_KeepsNewestWithinCap()
    {
        var many = Enumerable.Range(1, TermitusSupport.MaxMessages + 20).Select(i => Me(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var m = TermitusSupport.Merge([], many);
        await Assert.That(m.Count).IsEqualTo(TermitusSupport.MaxMessages);
        await Assert.That(m[0].Id).IsEqualTo("21");
    }

    [Test]
    public async Task Unread_OnlyNewerOperatorReplies()
    {
        var chat = TermitusSupport.Merge([], [Me("1"), Op("2"), Me("3")]);
        var lastOp = TermitusSupport.LastOperatorId(chat);
        await Assert.That(lastOp).IsEqualTo("2");
        await Assert.That(TermitusSupport.HasUnread(lastOp, null)).IsTrue();   // never seen
        await Assert.That(TermitusSupport.HasUnread(lastOp, "2")).IsFalse();   // seen
        await Assert.That(TermitusSupport.HasUnread(null, null)).IsFalse();    // operator never wrote
        // My own newer message does not make anything unread.
        await Assert.That(TermitusSupport.HasUnread(TermitusSupport.LastOperatorId(TermitusSupport.Merge(chat, [Me("4")])), "2")).IsFalse();
        // A new operator reply after "seen" does (ids compare numerically: 10 > 9).
        await Assert.That(TermitusSupport.HasUnread(TermitusSupport.LastOperatorId([Op("10")]), "9")).IsTrue();
        await Assert.That(TermitusSupport.LastServerId([Me("3"), Op("12"), Me("4")])).IsEqualTo("12");
    }

    [Test]
    public async Task NormalizeOutgoing_TrimAndLimit()
    {
        await Assert.That(TermitusSupport.NormalizeOutgoing("  привет \n")).IsEqualTo("привет");
        await Assert.That(TermitusSupport.NormalizeOutgoing("   ")).IsNull();
        await Assert.That(TermitusSupport.NormalizeOutgoing(null)).IsNull();
        await Assert.That(TermitusSupport.NormalizeOutgoing(new string('a', TermitusSupport.MaxTextLength + 1))).IsNull();
        await Assert.That(JsonSerializer.Serialize(new TermitusSupportPostRequest("x"))).IsEqualTo("""{"text":"x"}""");
    }

    [Test]
    public async Task InterpretSend_FinalContract()
    {
        var ok = TermitusSupport.InterpretSend(201, """{"id": 31, "from": "user", "text": "привет", "at": "2026-10-06T10:00:00Z"}""", null);
        await Assert.That(ok.Kind).IsEqualTo(TermitusSendKind.Ok);
        await Assert.That(ok.Message!.Id).IsEqualTo("31");
        await Assert.That(TermitusSupport.InterpretSend(201, "", null).Kind).IsEqualTo(TermitusSendKind.Ok);
        await Assert.That(TermitusSupport.InterpretSend(201, "", null).Message).IsNull();
        await Assert.That(TermitusSupport.InterpretSend(422, """{"detail":"empty"}""", null).Kind).IsEqualTo(TermitusSendKind.Invalid);
        var rl = TermitusSupport.InterpretSend(429, null, TimeSpan.FromSeconds(12));
        await Assert.That(rl.Kind).IsEqualTo(TermitusSendKind.RateLimited);
        await Assert.That(rl.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(12));
        await Assert.That(TermitusSupport.InterpretSend(403, null, null).Kind).IsEqualTo(TermitusSendKind.Guest);
        await Assert.That(TermitusSupport.InterpretSend(404, null, null).Kind).IsEqualTo(TermitusSendKind.Unavailable);
        await Assert.That(TermitusSupport.InterpretSend(null, null, null).Kind).IsEqualTo(TermitusSendKind.Failed);
        await Assert.That(TermitusSupport.InterpretSend(500, null, null).Kind).IsEqualTo(TermitusSendKind.Failed);
    }

    [Test]
    public async Task Paging_FullPageMeansMore()
    {
        await Assert.That(TermitusSupport.HasMorePages(TermitusSupport.PageSize)).IsTrue();
        await Assert.That(TermitusSupport.HasMorePages(TermitusSupport.PageSize - 1)).IsFalse();
        await Assert.That(TermitusSupport.HasMorePages(0)).IsFalse();
    }
}
