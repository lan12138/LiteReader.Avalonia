// LiteReader · Avalonia 跨平台版 —— 代码补全候选
// 作者：hujie  创建：2026-09-18
//
// 对照 LiteReader.cpp:4377 起的补全模块（关键字补全 / 括号配对补全 / 函数补全）。
// 这里只负责「算出候选列表」这一件纯逻辑；弹出、上下选择、确认插入都在 EditorSurface 里
// （与我们自绘编辑区的整体策略一致：候选列表也是自己画的，见 EditorSurface.DrawCompletion）。
//
// 候选来源两路：
//   1) 当前语言的关键字 / 类型（SyntaxHighlighter.WordsFor）
//   2) 文档内收集到的函数名（DocumentModel.FunctionNames，标识符后紧跟 '('）
// 顺序：函数名在前 —— 文档里真实存在的名字比语言关键字更可能是用户想打的。

namespace LiteReader.Models;

/// <param name="Text">候选文本。</param>
/// <param name="IsFunction">是否是文档内的函数名。确认插入时函数会补出 ()。</param>
public readonly record struct CompletionItem(string Text, bool IsFunction);

public static class CompletionEngine
{
    /// <summary>候选上限。再多也没人翻，反而多了要画的矩形。</summary>
    public const int MaxItems = 200;

    /// <summary>
    /// 取光标处正在输入的词（前缀）。
    /// </summary>
    /// <returns>WordStart = 词的起始偏移（插入候选后要替换的区间起点）；Prefix = 已输入部分（可能为空）。</returns>
    public static (int WordStart, string Prefix) WordPrefix(DocumentModel doc, int caretOffset)
    {
        string text = doc.Text;
        if (caretOffset <= 0) return (0, string.Empty);

        int s = caretOffset;
        while (s > 0 && IsWordChar(text[s - 1])) s--;
        return (s, text.Substring(s, caretOffset - s));
    }

    /// <summary>
    /// 算出候选列表。无候选时返回空列表（调用方据此隐藏补全面板）。
    /// </summary>
    public static IReadOnlyList<CompletionItem> Suggest(DocumentModel doc, int caretOffset)
    {
        (int _, string prefix) = WordPrefix(doc, caretOffset);
        if (prefix.Length == 0) return [];

        var result = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // ---- 1) 文档内函数名 ----
        foreach (string name in doc.FunctionNames())
        {
            if (result.Count >= MaxItems) break;
            if (!StartsWith(name, prefix)) continue;
            if (name.Length == prefix.Length) continue;      // 已经完全打出来了，不必再提示
            if (seen.Add(name)) result.Add(new CompletionItem(name, true));
        }

        // ---- 2) 当前语言关键字 / 类型 ----
        foreach (string word in SyntaxHighlighter.WordsFor(doc.Language))
        {
            if (result.Count >= MaxItems) break;
            if (!StartsWith(word, prefix)) continue;
            if (word.Length == prefix.Length) continue;
            if (seen.Add(word)) result.Add(new CompletionItem(word, false));
        }

        // 只有一个候选、且与已输入内容完全一致 → 没什么可提示的
        if (result.Count == 1 && string.Equals(result[0].Text, prefix, StringComparison.Ordinal))
            return [];

        return result;
    }

    /// <summary>前缀匹配（忽略大小写），与 C++ 的 startsWithCI 同义。</summary>
    private static bool StartsWith(string s, string prefix)
        => s.Length > prefix.Length
           && s.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
