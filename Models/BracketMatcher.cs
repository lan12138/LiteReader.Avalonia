// LiteReader · Avalonia 跨平台版 —— 括号配对
// 作者：hujie  创建：2026-09-18
//
// 对照 LiteReader.cpp:1819 的 findMatch()。规则：
//   1) 光标落在 `(` `[` `{` 上 → 向右按栈找**同类型**的闭括号；
//      落在 `)` `]` `}` 上 → 向左找**同类型**的开括号。
//      ⚠️「同类型」是必须的：只用「括号总数」配对的朴素写法在大中小括号混合嵌套时会把
//      `[` 与 `)` 配成一对（C++ 注释里就记了这个修正）。
//   2) SQL：光标落在 BEGIN / END / CASE 整词上时，按词栈配对，比 ()[]{} 是同一套语义 ——
//      这样 CASE...END、BEGIN...END 能被当成一对高亮，而不是「找不到匹配」。
//
// 纯函数、不依赖任何视图状态，便于自检直接断言（见 Diagnostics/SelfTest.cs）。

namespace LiteReader.Models;

/// <summary>一对匹配的括号。A 是较靠前的那个，B 是较靠后的那个。</summary>
/// <param name="AStart">A 的起始偏移。</param>
/// <param name="ALength">A 的长度（字符括号=1，SQL 的 BEGIN/END=词长）。</param>
/// <param name="BStart">B 的起始偏移。</param>
/// <param name="BLength">B 的长度。</param>
public readonly record struct BracketPair(int AStart, int ALength, int BStart, int BLength);

public static class BracketMatcher
{
    private const string OpenChars = "([{";
    private const string CloseChars = ")]}";

    /// <summary>
    /// 在 caretOffset 处尝试配一对括号。找到返回 true，并给出两端的偏移与长度。
    /// </summary>
    /// <param name="doc">文档。</param>
    /// <param name="caretOffset">光标偏移。可以正好落在括号字符上（这是唯一的判定条件）。</param>
    public static bool TryFind(DocumentModel doc, int caretOffset, out BracketPair pair)
    {
        pair = default;
        string text = doc.Text;
        if (caretOffset < 0 || caretOffset >= text.Length) return false;

        char c = text[caretOffset];

        int openIdx = OpenChars.IndexOf(c);
        if (openIdx >= 0)
        {
            int hit = ScanForward(text, caretOffset, OpenChars[openIdx], CloseChars[openIdx]);
            if (hit < 0) return false;
            pair = new BracketPair(caretOffset, 1, hit, 1);
            return true;
        }

        int closeIdx = CloseChars.IndexOf(c);
        if (closeIdx >= 0)
        {
            int hit = ScanBackward(text, caretOffset, OpenChars[closeIdx], CloseChars[closeIdx]);
            if (hit < 0) return false;
            pair = new BracketPair(hit, 1, caretOffset, 1);
            return true;
        }

        // ---- SQL 词括号 ----
        if (doc.Language != LanguageMode.Sql) return false;
        (int ws, int we) = WordBoundsAt(text, caretOffset);
        if (we <= ws) return false;

        ReadOnlySpan<char> word = text.AsSpan(ws, we - ws);
        bool isOpenWord = word.SequenceEqual("BEGIN") || word.SequenceEqual("CASE");
        bool isCloseWord = word.SequenceEqual("END");
        if (!isOpenWord && !isCloseWord) return false;

        if (isOpenWord)
        {
            // 自身已占一层，栈初值 1
            int stack = 1;
            int i = we;
            while (i < text.Length)
            {
                while (i < text.Length && !IsWordChar(text[i])) i++;
                if (i >= text.Length) break;
                int a = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                int kind = SqlWordKind(text, a);
                if (kind == 1) stack++;
                else if (kind == -1 && --stack == 0)
                {
                    pair = new BracketPair(ws, we - ws, a, i - a);
                    return true;
                }
            }
            return false;
        }
        else
        {
            int stack = 1;
            int i = ws - 1;
            while (i >= 0)
            {
                while (i >= 0 && !IsWordChar(text[i])) i--;
                if (i < 0) break;
                int b = i;
                while (b >= 0 && IsWordChar(text[b])) b--;
                b++;
                int kind = SqlWordKind(text, b);
                if (kind == -1) stack++;
                else if (kind == 1 && --stack == 0)
                {
                    int be = b;
                    while (be < text.Length && IsWordChar(text[be])) be++;
                    pair = new BracketPair(b, be - b, ws, we - ws);
                    return true;
                }
                i = b - 1;
            }
            return false;
        }
    }

    /// <summary>从 pos 起向右找配对的闭括号（同类型，按栈计数）。找不到返回 -1。</summary>
    private static int ScanForward(string text, int pos, char open, char close)
    {
        int stack = 1;
        for (int i = pos + 1; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == open) stack++;
            else if (ch == close && --stack == 0) return i;
        }
        return -1;
    }

    /// <summary>从 pos 起向左找配对的开括号（同类型，按栈计数）。找不到返回 -1。</summary>
    private static int ScanBackward(string text, int pos, char open, char close)
    {
        int stack = 1;
        for (int i = pos - 1; i >= 0; i--)
        {
            char ch = text[i];
            if (ch == close) stack++;
            else if (ch == open && --stack == 0) return i;
        }
        return -1;
    }

    /// <summary>该偏移所在整词（不含周围非词字符）。</summary>
    private static (int Start, int End) WordBoundsAt(string text, int offset)
    {
        if (offset < 0 || offset >= text.Length || !IsWordChar(text[offset])) return (0, 0);
        int a = offset;
        while (a > 0 && IsWordChar(text[a - 1])) a--;
        int b = offset;
        while (b < text.Length && IsWordChar(text[b])) b++;
        return (a, b);
    }

    /// <summary>该位置所在整词是不是块括号：BEGIN/CASE → 1，END → -1，其余 → 0。</summary>
    private static int SqlWordKind(string text, int pos)
    {
        (int a, int b) = WordBoundsAt(text, pos);
        if (b <= a) return 0;
        ReadOnlySpan<char> w = text.AsSpan(a, b - a);
        if (w.SequenceEqual("BEGIN") || w.SequenceEqual("CASE")) return 1;
        if (w.SequenceEqual("END")) return -1;
        return 0;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
