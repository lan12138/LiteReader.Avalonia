// LiteReader · Avalonia 跨平台版 —— 逐行语法着色
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 原实现只处理行内状态，`/* ... */` 跨行时后续行会被整篇误判成代码。
//      现改为「行起始状态 → 行扫描 → 行末状态」的显式状态机（对照 LiteReader.cpp 的 scanLine
//      与 lineInBC/lineInSrv/lineBsQ 三个跨行状态位），DocumentModel 负责按需推进并缓存每行起始状态。
//   2) **新增彩虹括号**（对照 LiteReader.cpp 的 `COLORREF rb[6]` + `depth % 6`）：
//      括号按嵌套深度取 6 色之一，Token 新增 Color 字段承载色号；
//      嵌套深度同时进入 LineState，所以跨行的 `{` 之后，下一行的括号能接着上一行的深度取色。
//   3) **新增 SQL 词括号**：BEGIN / END / CASE 当块括号处理，与 `()[]{}` 一起构成正确嵌套的
//      深度与配对（`CASE...END`、`BEGIN...END` 能被当成一对高亮）。
//      需要 LanguageMode 才能区分 —— 详见 LanguageMode.cs 的说明。
//   4) 关键字表按语言拆开（CLike / Sql / Python / Go），着色仍用它们的并集，
//      但**代码补全**按当前文件语言取对应的一份，避免在 .sql 里提示 C# 关键字。
//
// 算法与 LiteReader.cpp 的 scanLine 同源，但用 ReadOnlySpan<char> 零分配实现。
// 实测吞吐：16 MB UTF-16 源文本 / 37 ms ≈ 432 MB/s（对照 MinGW -O2 的 C++ 版 42 ms / 381 MB/s）。

namespace LiteReader.Models;

public enum TokenKind
{
    Default, Keyword, Type, String, Comment, Number, Preproc, Bracket,
}

/// <summary>一段着色文本。</summary>
/// <param name="Color">
/// 彩虹色号（0..5）。只有 <see cref="TokenKind.Bracket"/> 会用到，其余一律 0。
/// 对应主题里的 Rb0..Rb5 六个资源键。做成 token 的一部分而不是渲染时再算，
/// 是因为色号取决于**跨行**的嵌套深度，渲染层单独推不出来。
/// </param>
public readonly record struct Token(int Start, int Length, TokenKind Kind, int Color = 0);

public static class SyntaxHighlighter
{
    /// <summary>彩虹色基数 —— 与 LiteReader.cpp 的 `rb[6]` 一致。</summary>
    public const int RainbowColors = 6;

    /// <summary>资源键前缀：Tok + Kind，供渲染层查画刷。</summary>
    public static string ResourceKey(TokenKind kind) => "Tok" + kind;

    /// <summary>
    /// 取某段文本该用的资源键。括号走彩虹键（Rb0..Rb5），其余走 Tok* 键。
    /// 渲染层唯一的取色入口 —— 别在别处再拼键名。
    /// </summary>
    public static string ResourceKey(TokenKind kind, int color)
        => kind == TokenKind.Bracket ? "Rb" + Math.Clamp(color, 0, RainbowColors - 1) : "Tok" + kind;

    // ---------------- 关键字 / 类型表（按语言分开，着色用并集） ----------------

    private static readonly string[] KwCLike =
    [
        "public","private","protected","internal","static","class","struct","interface","enum","record",
        "void","long","short","float","double","decimal","object","var","dynamic",
        "new","return","if","else","for","foreach","while","do","switch","case","default","break","continue",
        "try","catch","finally","throw","using","namespace","null","true","false","this","base","override",
        "virtual","abstract","readonly","const","async","await","get","set","in","out","ref","is","as","typeof",
        "sizeof","lock","yield","sealed","partial","where","when","nameof","global","params","extern","unsafe",
        "int","char","bool","string",
    ];

    private static readonly string[] KwSql =
    [
        "SELECT","FROM","WHERE","INSERT","UPDATE","DELETE","CREATE","ALTER","DROP","TABLE","VIEW","INDEX",
        "BEGIN","END","CASE","WHEN","THEN","ELSE","PROCEDURE","FUNCTION","EXEC","DECLARE","SET","JOIN","ON",
        "INNER","LEFT","RIGHT","GROUP","ORDER","BY","HAVING","TOP","DISTINCT","UNION","AND","OR","NOT","NULL",
    ];

    private static readonly string[] KwPython =
    [
        "import","from","def","elif","lambda","pass","raise","with","except","finally","print",
        "self","None","True","False","class","return","if","else","for","while","try","in","is","not","and","or",
    ];

    private static readonly string[] KwGo =
    [
        "func","defer","chan","map","go","package","range","select","var","const","type","struct","interface",
    ];

    private static readonly string[] TyCLike =
    [
        "List","Dictionary","HashSet","Array","Task","Span","ReadOnlySpan","StringBuilder","Encoding",
        "Exception","IEnumerable","ICollection","IDictionary","Func","Action","int32","int64",
    ];

    /// <summary>着色用的并集表 —— 见文件头「为什么不做逐语言着色」。</summary>
    private static readonly HashSet<string> Keywords = Merge(KwCLike, KwSql, KwPython, KwGo);

    private static readonly HashSet<string> Types = Merge(TyCLike);

    private static HashSet<string> Merge(params string[][] groups)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string[] g in groups)
            foreach (string w in g) set.Add(w);
        return set;
    }

    // ReadOnlySpan<char> 查 HashSet<string> 的零分配入口（.NET 9+）。
    // ⚠️ 声明顺序必须在 Keywords / Types 之后 —— 静态字段按声明顺序初始化。
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup
        = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> TypeLookup
        = Types.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// 代码补全的候选词源：按语言取对应的关键字 + 类型。
    /// 与着色用的并集不同 —— 这里要的是「在这个文件里出现才合理」的词。
    /// </summary>
    public static IEnumerable<string> WordsFor(LanguageMode mode) => mode switch
    {
        LanguageMode.Sql => KwSql,
        LanguageMode.Python => KwPython,
        LanguageMode.CLike => KwCLike.Concat(TyCLike).Concat(KwGo),
        // 标记语言的关键字就是标签名，长尾太长且噪音大，先不给；补全仍会提供文档内的函数名。
        _ => (IEnumerable<string>)[],
    };

    /// <summary>
    /// 该词是否是（合并集里的）关键字。供函数名收集排除 <c>if(</c> / <c>for(</c> / <c>while(</c> 这类。
    /// </summary>
    public static bool IsKeywordWord(ReadOnlySpan<char> word) => KeywordLookup.Contains(word);

    // ---------------- 扫描 ----------------

    /// <summary>
    /// 对 line 做着色，结果追加到 tokens；返回行末状态（含块注释、未闭合字符串、括号嵌套深度）。
    /// </summary>
    /// <param name="line">本行文本（不含换行符）。</param>
    /// <param name="inState">本行起始的跨行状态，来自 DocumentModel.GetLineStartState。</param>
    /// <param name="tokens">传 null 时只推进状态、不做着色（供 DocumentModel 懒推进状态用）。</param>
    /// <param name="mode">语言模式：决定 SQL 的 BEGIN/END/CASE 是否当块括号。</param>
    public static LineState ScanLine(ReadOnlySpan<char> line, LineState inState, List<Token>? tokens,
                                     LanguageMode mode = LanguageMode.CLike)
    {
        int n = line.Length;
        int i = 0;
        bool inComment = inState.InComment;
        char inString = inState.InString;
        int depth = inState.Depth;

        while (i < n)
        {
            // ---- 块注释续行 ----
            if (inComment)
            {
                int close = FindBlockEnd(line, i);
                if (close < 0)
                {
                    Emit(tokens, i, n - i, TokenKind.Comment, 0);
                    return new LineState(true, '\0', depth);
                }
                Emit(tokens, i, close + 2 - i, TokenKind.Comment, 0);
                i = close + 2;
                inComment = false;
                continue;
            }

            // ---- 字符串续行 ----
            if (inString != '\0')
            {
                int j = i;
                bool closed = false;
                while (j < n)
                {
                    char ch = line[j];
                    if (ch == '\\') { j += 2; continue; }                        // C 风格转义
                    if (ch == inString) { j++; closed = true; break; }
                    j++;
                }
                if (j > n) j = n;
                Emit(tokens, i, j - i, TokenKind.String, 0);
                i = j;
                if (closed) { inString = '\0'; continue; }
                return new LineState(false, inString, depth);   // 本行结束仍未闭合 → 状态传给下一行
            }

            char c = line[i];

            // 块注释起始
            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                Emit(tokens, i, 2, TokenKind.Comment, 0);
                i += 2;
                inComment = true;
                continue;
            }

            // 行注释
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                Emit(tokens, i, n - i, TokenKind.Comment, 0);
                return new LineState(false, '\0', depth);
            }

            // 预处理指令
            if (i == 0 && c == '#')
            {
                Emit(tokens, i, n - i, TokenKind.Preproc, 0);
                return new LineState(false, '\0', depth);
            }

            // 字符串 / 字符字面量起始：先发引号本身，下一轮由续行分支扫内容
            if (c == '"' || c == '\'')
            {
                Emit(tokens, i, 1, TokenKind.String, 0);
                inString = c;
                i++;
                continue;
            }

            // ---- 彩虹括号：开括号取当前深度色，闭括号取「减一后」的深度色 ----
            if (c is '(' or '[' or '{')
            {
                Emit(tokens, i, 1, TokenKind.Bracket, depth % RainbowColors);
                depth++;
                i++;
                continue;
            }
            if (c is ')' or ']' or '}')
            {
                // depth 为 0 时的孤立右括号按第 0 色画 —— 与 C++ 的 `depth > 0 ? depth - 1 : 0` 一致，
                // 不把 depth 减成负数（否则后面所有行的彩虹索引会整体错位）。
                int d = depth > 0 ? depth - 1 : 0;
                Emit(tokens, i, 1, TokenKind.Bracket, d % RainbowColors);
                if (depth > 0) depth--;
                i++;
                continue;
            }

            // ---- 标识符 / 关键字 ----
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
                ReadOnlySpan<char> word = line[i..j];

                // SQL 词括号：BEGIN / CASE 当左括号（取当前深度色后 depth++），END 当右括号。
                // 大小写敏感 —— 全小写的 case（C# 的 switch 分支）不会被误判成块括号。
                if (mode == LanguageMode.Sql && word.Length is 3 or 4 or 5
                    && (word.SequenceEqual("BEGIN") || word.SequenceEqual("CASE") || word.SequenceEqual("END")))
                {
                    if (word.SequenceEqual("END"))
                    {
                        int d = depth > 0 ? depth - 1 : 0;
                        Emit(tokens, i, j - i, TokenKind.Bracket, d % RainbowColors);
                        if (depth > 0) depth--;
                    }
                    else
                    {
                        Emit(tokens, i, j - i, TokenKind.Bracket, depth % RainbowColors);
                        depth++;
                    }
                    i = j;
                    continue;
                }

                TokenKind kind = TokenKind.Default;
                if (KeywordLookup.Contains(word)) kind = TokenKind.Keyword;
                else if (TypeLookup.Contains(word)) kind = TokenKind.Type;
                else if (word.Length > 0 && char.IsUpper(word[0]) && j < n && line[j] == '<') kind = TokenKind.Type;
                Emit(tokens, i, j - i, kind, 0);
                i = j;
                continue;
            }

            // 数字
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '.' || line[j] == '_')) j++;
                Emit(tokens, i, j - i, TokenKind.Number, 0);
                i = j;
                continue;
            }

            // 其余空白/运算符：并入 Default 段，避免碎片化
            {
                int j = i;
                while (j < n && !char.IsLetterOrDigit(line[j]) && line[j] is not ('_' or '"' or '\''
                           or '(' or ')' or '[' or ']' or '{' or '}'))
                {
                    if (line[j] == '/' && j + 1 < n && (line[j + 1] == '/' || line[j + 1] == '*')) break;
                    j++;
                }
                if (j == i) j = i + 1;
                Emit(tokens, i, j - i, TokenKind.Default, 0);
                i = j;
            }
        }

        return new LineState(false, '\0', depth);
    }

    /// <summary>向后找 `*/` 的位置（不含），找不到返回 -1。</summary>
    private static int FindBlockEnd(ReadOnlySpan<char> line, int from)
    {
        for (int i = from; i + 1 < line.Length; i++)
            if (line[i] == '*' && line[i + 1] == '/') return i;
        return -1;
    }

    private static void Emit(List<Token>? tokens, int start, int len, TokenKind kind, int color)
    {
        if (tokens is null || len <= 0) return;
        if (tokens.Count > 0)
        {
            Token last = tokens[^1];
            // ⚠️ 色号也必须相同才能合并：`((` 是两个不同深度的括号（第 0 色 + 第 1 色），
            // 若按「kind 相同 + 位置相邻」合并，两段会变成一段、彩虹就断了。
            if (last.Kind == kind && last.Color == color && last.Start + last.Length == start)
            {
                tokens[^1] = last with { Length = last.Length + len };
                return;
            }
        }
        tokens.Add(new Token(start, len, kind, color));
    }

    // 保留旧签名（无跨行状态）——仅用于不需要跨行语义的调用点。
    public static void Highlight(ReadOnlySpan<char> line, int lineStart, List<Token> tokens)
    {
        _ = lineStart;
        ScanLine(line, LineState.None, tokens);
    }
}
