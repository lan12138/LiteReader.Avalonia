// LiteReader · Avalonia 跨平台版 —— 双击分词高亮（整词命中收集）
// 作者：hujie  创建：2026-09-18
//
// 对照 LiteReader.cpp:947 的 collectMarks() 与 :5235 的 WM_LBUTTONDBLCLK 分支。语义照搬：
//
//   · 只收**整词**命中 —— 命中处的**前一个**字符与**后一个**字符都不能是单词字符。
//     少了这条，双击 `in` 会把 `index`、`inline`、`main` 里的 in 全部点亮一片。
//     原版注释把这条写成「避免 in 命中 index 这类子串」，是同一个意思。
//
//   · **SQL 的 BEGIN / END / CASE 是例外**：双击它们不做全文档高亮，只选中该词本身。
//     理由很实在 —— 一个几百行的存储过程里有几十个 END，全点亮等于把屏幕糊满底色，
//     反而看不出光标在哪。这里连大小写都是**区分**的（原版就是 `w == L"END"`），
//     因为 SQL 关键字惯例全大写，混进来的小写 end 多半是标识符。
//
//   · 双击落点处的「整词」边界沿用原版 wordAtOffset 的语义：**只有落在单词字符上才向右扩展**，
//     落在词尾/空白/换行时向右不扩（这是原版为「接受补全时把换行符/右括号一起吞掉」打的补丁），
//     向左仍然一直扩展。双击场景下它的表现是「点在词尾后一格双击 → 选中光标前那个词」。
//
// 为什么单独一个文件、而不是塞进 DocumentModel：
//   这条规则是**纯**的 —— 只吃 string 与语言模式、产出偏移区间，不需要文档的任何索引。
//   放在 Models 下能被自检直接驱动（Diagnostics/SelfTest.cs 的 WordMarkerSmoke），
//   也不必让 DocumentModel 为「视图高亮」这种纯显示需求长出一块 API。

namespace LiteReader.Models;

/// <summary>一段要高亮的区间，左闭右开 <c>[Start, End)</c>。</summary>
public readonly record struct MarkRange(int Start, int End)
{
    public int Length => End - Start;
}

public static class WordMarker
{
    /// <summary>
    /// 单词字符定义。必须与 EditorSurface.IsWordChar 以及原版 isWordChar 完全一致 ——
    /// 三处只要有一处不同，「双击选中的词」和「被高亮的词」就会错位。
    /// </summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// 取 offset 处的整词边界（见文件头第 3 条语义说明）。
    /// offset 越界或不是单词字符时返回 <c>(offset, offset)</c>（空区间），不会抛。
    /// </summary>
    public static (int Start, int End) WordBoundsAt(string text, int offset)
    {
        if (string.IsNullOrEmpty(text) || offset < 0 || offset >= text.Length) return (offset, offset);

        int ws = offset, we = offset;
        if (IsWordChar(text[offset]))
        {
            we = offset + 1;
            while (we < text.Length && IsWordChar(text[we])) we++;
        }
        while (ws > 0 && IsWordChar(text[ws - 1])) ws--;
        return (ws, we);
    }

    /// <summary>SQL 的块括号词：BEGIN / CASE 是开、END 是闭。双击它们时只选中、不做全文档高亮。</summary>
    public static bool IsSqlBlockWord(string word) => word is "BEGIN" or "END" or "CASE";

    /// <summary>
    /// 收集 word 在 text 中的**全部整词命中**。
    /// 空词返回空表（而不是抛异常）—— 「光标不在词上」是双击时很常见的落点。
    /// </summary>
    public static List<MarkRange> CollectAll(string text, string word)
    {
        var hits = new List<MarkRange>();
        int n = word.Length;
        if (n == 0 || text.Length < n) return hits;

        int len = text.Length;
        // 命中后 p += n 而不是 p++ —— 同一个位置不可能有两处命中，跳过去省一半比较。
        for (int p = 0; p + n <= len;)
        {
            if (string.CompareOrdinal(text, p, word, 0, n) == 0)
            {
                bool okPrev = p == 0 || !IsWordChar(text[p - 1]);
                bool okNext = p + n >= len || !IsWordChar(text[p + n]);
                if (okPrev && okNext) hits.Add(new MarkRange(p, p + n));
                p += n;
            }
            else p++;
        }
        return hits;
    }

    /// <summary>
    /// 双击语义的入口：给定文档文本、双击处的整词、以及语言模式，返回该高亮的全部区间。
    /// SQL 块括号词返回**空表**（调用方仍应正常选中该词，只是不做全文档铺色）。
    /// </summary>
    public static List<MarkRange> MarksFor(string text, string word, LanguageMode language)
    {
        if (word.Length == 0) return [];
        if (language == LanguageMode.Sql && IsSqlBlockWord(word)) return [];
        return CollectAll(text, word);
    }
}
