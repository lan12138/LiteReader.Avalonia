// LiteReader · Avalonia 跨平台版 —— 查找
// 作者：hujie  创建：2026-09-18
//
// 对照 LiteReader.cpp:3622 的 doFind()。语义照搬：
//   · 普通子串查找（不是正则），大小写可选；
//   · **循环查找** —— 向后找到文末没命中就从头再找，向前同理；
//   · 命中后把 [命中起点, 命中终点) 设为选区。
//
// 为什么单独一个文件而不是塞进 DocumentModel：
//   这两个函数是纯的（只吃 string 和参数、产出偏移），不需要文档的任何索引，
//   放在这里可以直接被自检断言驱动，也不必让 DocumentModel 再长出一块与编辑无关的 API。

namespace LiteReader.Models;

public static class TextSearch
{
    /// <summary>
    /// 从 from 起向后找。找不到时**绕回开头**再找一次（循环查找）。
    /// </summary>
    /// <returns>命中起点；都找不到返回 -1。</returns>
    public static int FindForward(string text, string query, int from, bool matchCase)
    {
        if (string.IsNullOrEmpty(query) || text.Length < query.Length) return -1;
        StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        from = Math.Clamp(from, 0, text.Length);
        int p = text.IndexOf(query, from, cmp);
        if (p < 0 && from > 0) p = text.IndexOf(query, 0, cmp);   // 绕回文首
        return p;
    }

    /// <summary>
    /// 从 caret 起向前找。先跳过当前命中（否则会在原地反复命中），找不到时绕到文末再找。
    /// </summary>
    /// <returns>命中起点；都找不到返回 -1。</returns>
    public static int FindBackward(string text, string query, int caret, bool matchCase)
    {
        if (string.IsNullOrEmpty(query) || text.Length < query.Length) return -1;
        StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // ⚠️ 这里有个必须实测才敢下结论的坑：
        //   LastIndexOf(value, startIndex, cmp) 的 startIndex 约束的是**命中的结束位置**，
        //   而不是起始位置。实测（spikes/lastindexof-probe/probe.cs）：
        //     在 "alpha Beta ALPHA beta alpha" 上
        //       LastIndexOf("alpha", 25, IgnoreCase) == 11   ← @22 那个命中结束于 26 > 25，被排除
        //       LastIndexOf("alpha", 26, IgnoreCase) == 22
        //   若按「起始位置」的直觉直接传 startIndex，向前查找会在第一个命中处来回打转。
        //   要表达「起点 <= pos」，得传 pos + len - 1。
        int pos = caret - query.Length - 1;      // 严格早于当前命中的最后一个可能起点
        int p = pos >= 0 ? LastStartingAtOrBefore(text, query, pos, cmp) : -1;
        if (p < 0) p = LastStartingAtOrBefore(text, query, text.Length - query.Length, cmp);   // 绕到文末
        return p;
    }

    /// <summary>找出全部命中里「起点 &lt;= maxStart」的最后一个。找不到返回 -1。</summary>
    private static int LastStartingAtOrBefore(string text, string query, int maxStart, StringComparison cmp)
    {
        // 把「起点上限」换算成 LastIndexOf 需要的「终点上限」
        int maxEnd = Math.Min(maxStart + query.Length - 1, text.Length - 1);
        if (maxEnd < query.Length - 1) return -1;
        return text.LastIndexOf(query, maxEnd, cmp);
    }

    /// <summary>
    /// 统计全部命中数（供查找条显示「第 n / 共 m 项」）。
    /// 不用正则，逐段 IndexOf 推进；空查询返回 0。
    /// </summary>
    public static int CountAll(string text, string query, bool matchCase)
    {
        if (string.IsNullOrEmpty(query) || text.Length < query.Length) return 0;
        StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int count = 0;
        int i = 0;
        while (i <= text.Length - query.Length)
        {
            int p = text.IndexOf(query, i, cmp);
            if (p < 0) break;
            count++;
            i = p + query.Length;        // 不重叠计数
        }
        return count;
    }

    /// <summary>
    /// 命中起点 hit 是第几个命中（1 基）—— 查找条显示「第 n / 共 m 项」用。
    /// 不是命中起点则返回 0。
    /// </summary>
    public static int OrdinalOf(string text, string query, int hit, bool matchCase)
    {
        if (hit < 0 || string.IsNullOrEmpty(query)) return 0;
        StringComparison cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int count = 0, i = 0;
        while (i <= hit)
        {
            int p = text.IndexOf(query, i, cmp);
            if (p < 0 || p > hit) break;
            count++;
            if (p == hit) return count;
            i = p + query.Length;
        }
        return 0;
    }
}
