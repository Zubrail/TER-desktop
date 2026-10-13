namespace ServiceLib.Services;

/// <summary>
/// In-place list updates for UI collections (0.4 smoothness pass). The old
/// Clear()+Add() rebuild re-created every row control (template, layout, bindings)
/// on each refresh; these helpers touch only what changed, so an unchanged list
/// raises zero CollectionChanged events and keeps its controls.
/// Pure, unit-tested.
/// </summary>
public static class TermitusCollections
{
    /// <summary>
    /// Makes <paramref name="target"/> match <paramref name="source"/> by key: existing
    /// items are moved and updated in place (<paramref name="update"/>), missing ones
    /// created, extra ones removed. Returns the number of structural changes
    /// (insert/move/remove).
    /// </summary>
    public static int Sync<TItem, TData, TKey>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TData> source,
        Func<TItem, TKey> itemKey,
        Func<TData, TKey> dataKey,
        Func<TData, TItem> create,
        Action<TItem, TData> update)
        where TKey : notnull
    {
        var changes = 0;
        var cmp = EqualityComparer<TKey>.Default;
        var wantedAt = new Dictionary<TKey, int>(cmp);
        for (var i = 0; i < source.Count; i++)
        {
            wantedAt.TryAdd(dataKey(source[i]), i);
        }
        for (var i = 0; i < source.Count; i++)
        {
            var key = dataKey(source[i]);
            if (i < target.Count && cmp.Equals(itemKey(target[i]), key))
            {
                update(target[i], source[i]);
                continue;
            }
            if (i < target.Count)
            {
                var cur = itemKey(target[i]);
                if (!wantedAt.TryGetValue(cur, out var curDest))
                {
                    // Gone from the source: remove it here, look at slot i again.
                    target.RemoveAt(i);
                    changes++;
                    i--;
                    continue;
                }
                if (curDest > i && i + 1 < target.Count && cmp.Equals(itemKey(target[i + 1]), key))
                {
                    // One item jumped down the list (e.g. the fastest row changed):
                    // move IT once instead of shifting every row below it up.
                    target.Move(i, Math.Min(curDest, target.Count - 1));
                    changes++;
                    i--;
                    continue;
                }
            }
            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (cmp.Equals(itemKey(target[j]), key))
                {
                    found = j;
                    break;
                }
            }
            if (found >= 0)
            {
                target.Move(found, i);
                update(target[i], source[i]);
            }
            else
            {
                target.Insert(i, create(source[i]));
            }
            changes++;
        }
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
            changes++;
        }
        return changes;
    }

    /// <summary>
    /// Same for immutable items (records): an item whose key matches but whose value
    /// changed is replaced in its slot. Returns the number of changes (incl. replaces).
    /// </summary>
    public static int SyncReplace<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, TKey> key)
        where TKey : notnull
    {
        var replaced = 0;
        var changes = Sync(target, source, key, key, d => d, (_, _) => { });
        for (var i = 0; i < source.Count; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(target[i], source[i]))
            {
                target[i] = source[i];
                replaced++;
            }
        }
        return changes + replaced;
    }
}
