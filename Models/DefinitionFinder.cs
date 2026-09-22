// LiteReader · Avalonia 跨平台版 —— 跳转定义
// 作者：hujie  创建：2026-09-18
//
// 对照 LiteReader.cpp:4270 起的 gotoDefinition / scoreDefinition。
//
// ⚠️ 先说清楚这是什么：**单文件内的启发式跳转，不做语义解析、不跨文件**。
//   它按「哪个位置长得最像定义」给全文的整词命中打分，取分最高的那个跳过去。
//   这是纯文本扫描的取舍 —— 换来的是零依赖、任何语言都能用（包括没有 LSP 的语言）。
//
// 打分规则（越高越像「定义」，照搬 C++）：
//   · 基础分 1（任意整词命中）
//   · 后跟 '('            +3   —— 函数式定义
//   · 前面是修饰/类型关键字  +5   —— public / void / int / def / class / function / var / create …
//   · 前面是首字母大写的词   +2   —— 疑似类型名
//   · 命中位置在当前行       -1   —— 偏向跳到别处的定义，避免在原地打转
//
// 大小写：找标识符用**精确匹配**（C++ 的 compare 是精确的），关键字比较用忽略大小写。
//
// 修订说明（2026-09-18）：修掉一处**移植缺陷**（C++ 原版就有的）——
//   prevWordAt() 在整词命中处永远返回空串，使上面两条「前置词」规则成为死代码。
//   详见 PrevWord 的注释；本轮把「前置声明关键字」这条打通后，
//   定义处与调用处的分差才真正拉开（9 : 4 而不是 4 : 3）。

namespace LiteReader.Models;

public static class DefinitionFinder
{
    /// <summary>「这里像是声明」的前置关键字。对照 LiteReader.cpp 的 defKw[]。</summary>
    private static readonly HashSet<string> DeclKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "def", "class", "function", "func", "public", "private", "protected",
        "static", "void", "int", "long", "short", "char", "float",
        "double", "bool", "boolean", "string", "unsigned", "signed", "const",
        "let", "var", "create", "procedure", "table", "trigger", "view",
        "index", "struct", "enum", "interface", "final", "abstract", "virtual",
        "override", "new", "friend", "inline", "operator", "namespace", "module",
        "sub", "proc", "fn", "type", "local", "global", "dim",
        "set", "property",
    };

    /// <summary>
    /// 在文档内找 name 的「定义」位置。
    /// </summary>
    /// <param name="doc">文档。</param>
    /// <param name="name">目标标识符。</param>
    /// <param name="caretOffset">当前光标偏移（用于给当前行降权）。</param>
    /// <returns>最佳位置偏移；找不到返回 -1。</returns>
    public static int FindBest(DocumentModel doc, string name, int caretOffset)
    {
        if (string.IsNullOrEmpty(name)) return -1;

        int n = doc.LineCount;
        int nameLen = name.Length;
        int caretLine = doc.LineIndexOf(caretOffset);
        int bestOff = -1, bestScore = 0;

        for (int l = 0; l < n; l++)
        {
            ReadOnlySpan<char> line = doc.GetLine(l);
            if (line.Length < nameLen) continue;

            int lineStart = doc.LineStartOffset(l);
            int p = 0;
            while (p <= line.Length - nameLen)
            {
                int rel = line[p..].IndexOf(name, StringComparison.Ordinal);
                if (rel < 0) break;
                int at = p + rel;

                // 必须是「整词」命中：前后都不是词字符
                bool okPrev = at == 0 || !IsWordChar(line[at - 1]);
                bool okNext = at + nameLen >= line.Length || !IsWordChar(line[at + nameLen]);
                if (okPrev && okNext)
                {
                    int sc = Score(doc, lineStart + at, nameLen);
                    if (l == caretLine) sc -= 1;
                    if (sc > bestScore)
                    {
                        bestScore = sc;
                        bestOff = lineStart + at;
                    }
                }
                p = at + nameLen;      // 不重叠地推进
            }
        }

        return bestScore > 0 ? bestOff : -1;
    }

    /// <summary>给「name 出现在 pos 处」这件事打分。</summary>
    private static int Score(DocumentModel doc, int pos, int len)
    {
        int score = 1;
        string text = doc.Text;

        if (NextNonSpace(text, pos + len) == '(') score += 3;

        string prev = PrevWord(text, pos);
        if (DeclKeywords.Contains(prev)) score += 5;

        if (prev.Length >= 2 && char.IsUpper(prev[0])) score += 2;

        return score;
    }

    /// <summary>
    /// 取 off 之前、**跳过空白后**的整词（不含 off 本身）。
    ///
    /// ⚠️ 这里与 LiteReader.cpp 的 <c>prevWordAt()</c> 有一处**有意的行为修正**，不是笔误：
    ///   原版从 <c>off-1</c> 直接往回退。但 off 是「整词命中」的起点，它前面那个字符
    ///   必然不是词字符（否则整词判定就不成立），所以 <c>while (isWordChar(...))</c> 一次都不执行、
    ///   返回值恒为空串 ——「前置声明关键字 +5」「前置首字母大写 +2」两条规则
    ///   在 C++ 版里其实是**死代码**，打分退化成「只看后面跟不跟 '('」，
    ///   于是 <c>public int Calc(</c> 与调用处 <c>Calc(</c> 几乎同分，跳转质量全靠运气。
    ///   这里先跳过空白再取词，两条规则才真正生效（定义处 1+3+5=9 分 vs 调用处 1+3=4 分）。
    ///
    /// 跳过的是 <see cref="char.IsWhiteSpace"/>，所以跨行也成立（<c>public int\n    Calc(</c>）。
    /// </summary>
    private static string PrevWord(string text, int off)
    {
        int p = off - 1;
        while (p >= 0 && char.IsWhiteSpace(text[p])) p--;
        int end = p + 1;                                  // 前一个词的结束位置（不含）
        while (p >= 0 && IsWordChar(text[p])) p--;
        return text.Substring(p + 1, end - (p + 1));
    }

    /// <summary>取 off 之后第一个非空白字符（用于判断后面是否紧跟 '('）。</summary>
    private static char NextNonSpace(string text, int off)
    {
        int e = off;
        while (e < text.Length && (text[e] == ' ' || text[e] == '\t')) e++;
        return e < text.Length ? text[e] : '\0';
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// 取「跳转目标标识符」：优先用选区的首词，否则用光标下的整词。
    /// 对照 LiteReader.cpp 的 selectedOrWordAtCaret()。
    /// </summary>
    public static string TargetName(DocumentModel doc, int selStart, int selEnd, int caretOffset)
    {
        string text = doc.Text;

        if (selStart >= 0 && selEnd > selStart)
        {
            ReadOnlySpan<char> sel = text.AsSpan(selStart, selEnd - selStart);
            int a = 0, b = sel.Length;
            while (a < b && !IsWordChar(sel[a])) a++;
            while (b > a && !IsWordChar(sel[b - 1])) b--;
            return a < b ? sel[a..b].ToString() : string.Empty;
        }

        if (caretOffset < 0 || caretOffset >= text.Length) return string.Empty;
        if (!IsWordChar(text[caretOffset])) return string.Empty;
        int s = caretOffset, e = caretOffset;
        while (s > 0 && IsWordChar(text[s - 1])) s--;
        while (e < text.Length && IsWordChar(text[e])) e++;
        return text.Substring(s, e - s);
    }
}
