using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ServiceLib.ViewModels;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>In-place UI list updates (0.4 smoothness): unchanged lists raise no events.</summary>
public class TermitusCollectionsTests
{
    private sealed class Row(string key)
    {
        public string Key { get; } = key;
        public string Value { get; set; } = string.Empty;
    }

    private static (ObservableCollection<Row> list, List<NotifyCollectionChangedAction> events) Make(params string[] keys)
    {
        var list = new ObservableCollection<Row>(keys.Select(k => new Row(k) { Value = k }));
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);
        return (list, events);
    }

    private static int Sync(ObservableCollection<Row> list, params (string Key, string Value)[] data)
        => TermitusCollections.Sync(list, data, r => r.Key, d => d.Key, d => new Row(d.Key) { Value = d.Value }, (r, d) => r.Value = d.Value);

    [Test]
    public async Task Unchanged_NoEvents_SameInstances()
    {
        var (list, events) = Make("a", "b", "c");
        var before = list.ToList();
        var changes = Sync(list, ("a", "a"), ("b", "b"), ("c", "c"));
        await Assert.That(changes).IsEqualTo(0);
        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(list.SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task ValueChange_UpdatesInPlace()
    {
        var (list, events) = Make("a", "b");
        var b = list[1];
        Sync(list, ("a", "a"), ("b", "37 мс"));
        await Assert.That(events.Count).IsEqualTo(0);
        await Assert.That(ReferenceEquals(list[1], b)).IsTrue();
        await Assert.That(b.Value).IsEqualTo("37 мс");
    }

    [Test]
    public async Task Reorder_UsesMoves_KeepsInstances()
    {
        var (list, events) = Make("a", "b", "c", "d");
        var c = list[2];
        Sync(list, ("c", "c"), ("a", "a"), ("b", "b"), ("d", "d"));
        await Assert.That(list.Select(r => r.Key).ToArray()).IsEquivalentTo(new[] { "c", "a", "b", "d" });
        await Assert.That(ReferenceEquals(list[0], c)).IsTrue();
        await Assert.That(events.All(e => e == NotifyCollectionChangedAction.Move)).IsTrue();
        await Assert.That(events.Count).IsEqualTo(1);
    }

    [Test]
    public async Task OneRowJumpsDown_SingleMove()
    {
        var (list, events) = Make("a", "b", "c", "d", "e");
        Sync(list, ("b", "b"), ("c", "c"), ("d", "d"), ("e", "e"), ("a", "a"));
        await Assert.That(list.Select(r => r.Key).ToArray()).IsEquivalentTo(new[] { "b", "c", "d", "e", "a" });
        await Assert.That(events.Count).IsEqualTo(1);

        var (list2, events2) = Make("a", "b", "c", "d", "e");
        Sync(list2, ("b", "b"), ("c", "c"), ("a", "a"), ("d", "d"), ("e", "e"));
        await Assert.That(list2.Select(r => r.Key).ToArray()).IsEquivalentTo(new[] { "b", "c", "a", "d", "e" });
        await Assert.That(events2.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RandomShuffles_AlwaysEndEqual()
    {
        var rnd = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var keys = Enumerable.Range(0, rnd.Next(0, 12)).Select(i => "k" + i).ToArray();
            var (list, _) = Make(keys);
            var next = keys.Where(_ => rnd.Next(4) != 0).Concat(Enumerable.Range(0, rnd.Next(0, 3)).Select(i => $"n{round}-{i}"))
                .OrderBy(_ => rnd.Next()).Select(k => (k, k + "!")).ToArray();
            Sync(list, next);
            await Assert.That(list.Select(r => r.Key).SequenceEqual(next.Select(n => n.k))).IsTrue();
            await Assert.That(list.All(r => r.Value == r.Key + "!")).IsTrue();
        }
    }

    [Test]
    public async Task InsertAndRemove()
    {
        var (list, events) = Make("a", "b", "c");
        Sync(list, ("a", "a"), ("x", "x"), ("c", "c"));
        await Assert.That(list.Select(r => r.Key).ToArray()).IsEquivalentTo(new[] { "a", "x", "c" });
        await Assert.That(events.Contains(NotifyCollectionChangedAction.Add)).IsTrue();
        await Assert.That(events.Contains(NotifyCollectionChangedAction.Remove)).IsTrue();
        await Assert.That(events.Contains(NotifyCollectionChangedAction.Reset)).IsFalse();

        Sync(list);
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SyncReplace_Records_AppendOnlyForChat()
    {
        var list = new ObservableCollection<TermitusChatRow>();
        var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);

        TermitusCollections.SyncReplace(list, [new TermitusChatRow("1", "a", false, "10:00"), new TermitusChatRow("2", "b", true, "10:01")], r => r.Id);
        events.Clear();
        // New message arrives: exactly one Add, existing bubbles untouched.
        TermitusCollections.SyncReplace(list,
            [new TermitusChatRow("1", "a", false, "10:00"), new TermitusChatRow("2", "b", true, "10:01"), new TermitusChatRow("3", "c", true, "10:02")], r => r.Id);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsEqualTo(NotifyCollectionChangedAction.Add);
        events.Clear();
        // Meta changed ("sending…" -> time): one Replace.
        TermitusCollections.SyncReplace(list,
            [new TermitusChatRow("1", "a", false, "10:00"), new TermitusChatRow("2", "b", true, "10:01"), new TermitusChatRow("3", "c", true, "10:03")], r => r.Id);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsEqualTo(NotifyCollectionChangedAction.Replace);
    }
}
