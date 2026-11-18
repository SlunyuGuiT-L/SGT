using System;
using System.Collections.Generic;

namespace SGT.OmniModules;

/// <summary>
/// 称号（Title）表里的一行。只保留搜索需要的字段。
/// </summary>
internal readonly record struct TitleRecord(uint RowId, string Masculine, string Feminine, ushort Order);

/// <summary>
/// 搜索结果里的一条。同名（同显示名）的多行会合并成一条，<see cref="RowIds"/> 保留全部行号。
/// </summary>
internal sealed record TitleResult(
    uint RowId,
    string Display,
    string Other,
    string Masculine,
    string Feminine,
    ushort Order,
    uint[] RowIds)
{
    public int DuplicateCount => RowIds.Length;
}

/// <summary>
/// 纯逻辑：把已解锁的称号行整理成可搜索列表，并做关键词过滤/排序/高亮定位。
/// </summary>
internal static class TitleSearchLogic
{
    /// <summary>按角色性别取显示名，空的一侧回退到另一侧。</summary>
    internal static string NameFor(TitleRecord record, bool female)
    {
        var primary = female ? record.Feminine : record.Masculine;
        if (primary.Length > 0)
        {
            return primary;
        }

        return female ? record.Masculine : record.Feminine;
    }

    /// <summary>把已解锁行按显示名去重并排序（游戏内 Order 顺序，其次行号）。</summary>
    internal static List<TitleResult> Build(IEnumerable<TitleRecord> records, bool female)
    {
        var merged = new Dictionary<string, (TitleRecord Record, List<uint> Ids)>(StringComparer.Ordinal);
        var names = new List<string>();

        foreach (var record in records)
        {
            var display = NameFor(record, female);
            if (display.Length == 0)
            {
                continue;
            }

            if (merged.TryGetValue(display, out var entry))
            {
                entry.Ids.Add(record.RowId);
                var better = record.Order < entry.Record.Order
                    || (record.Order == entry.Record.Order && record.RowId < entry.Record.RowId);
                if (better)
                {
                    merged[display] = (record, entry.Ids);
                }

                continue;
            }

            merged[display] = (record, new List<uint> { record.RowId });
            names.Add(display);
        }

        var results = new List<TitleResult>(names.Count);
        foreach (var name in names)
        {
            var (record, ids) = merged[name];
            ids.Sort();
            results.Add(new TitleResult(
                record.RowId,
                name,
                NameFor(record, !female),
                record.Masculine,
                record.Feminine,
                record.Order,
                ids.ToArray()));
        }

        results.Sort(CompareCatalogOrder);
        return results;
    }

    /// <summary>
    /// 关键词过滤：空格分隔多个词，全部命中才算匹配（显示名、另一性别、原始两列都参与）；
    /// 单关键词时按「完全相同 → 前缀 → 包含 → 仅另一性别命中」排序。
    /// </summary>
    internal static List<TitleResult> Filter(IReadOnlyList<TitleResult> source, string query)
    {
        var results = new List<TitleResult>();
        var tokens = Tokenize(query);
        if (tokens.Length == 0)
        {
            return results;
        }

        var normalized = query.Trim();
        foreach (var item in source)
        {
            if (Matches(item, tokens))
            {
                results.Add(item);
            }
        }

        var ranked = new List<(TitleResult Item, int Rank)>(results.Count);
        foreach (var item in results)
        {
            ranked.Add((item, Rank(item, normalized, tokens)));
        }

        ranked.Sort(static (left, right) =>
        {
            var byRank = left.Rank.CompareTo(right.Rank);
            return byRank != 0 ? byRank : CompareCatalogOrder(left.Item, right.Item);
        });

        results.Clear();
        foreach (var entry in ranked)
        {
            results.Add(entry.Item);
        }

        return results;
    }

    /// <summary>空格分隔关键词，去掉空项（兼容全角空格）。</summary>
    internal static string[] Tokenize(string query)
        => string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>某段文本里所有关键词命中的区间，重叠/相邻的会合并，供高亮绘制。</summary>
    internal static List<(int Start, int Length)> FindMatches(string text, IReadOnlyList<string> tokens)
    {
        var ranges = new List<(int Start, int Length)>();
        if (text.Length == 0)
        {
            return ranges;
        }

        foreach (var token in tokens)
        {
            if (token.Length == 0)
            {
                continue;
            }

            var index = 0;
            while (index <= text.Length - token.Length)
            {
                var at = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    break;
                }

                ranges.Add((at, token.Length));
                index = at + token.Length;
            }
        }

        if (ranges.Count <= 1)
        {
            return ranges;
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        var merged = new List<(int Start, int Length)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var lastEnd = last.Start + last.Length;
                if (range.Start <= lastEnd)
                {
                    var end = Math.Max(lastEnd, range.Start + range.Length);
                    merged[^1] = (last.Start, end - last.Start);
                    continue;
                }
            }

            merged.Add(range);
        }

        return merged;
    }

    private static bool Matches(TitleResult item, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (!Contains(item.Display, token)
                && !Contains(item.Other, token)
                && !Contains(item.Masculine, token)
                && !Contains(item.Feminine, token))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string text, string token)
        => text.Length > 0 && text.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static int Rank(TitleResult item, string normalizedQuery, string[] tokens)
    {
        if (tokens.Length != 1)
        {
            return 3;
        }

        if (item.Display.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (item.Display.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return item.Display.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    private static int CompareCatalogOrder(TitleResult left, TitleResult right)
    {
        var byOrder = left.Order.CompareTo(right.Order);
        return byOrder != 0 ? byOrder : left.RowId.CompareTo(right.RowId);
    }
}
