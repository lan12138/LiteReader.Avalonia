// LiteReader · Avalonia 跨平台版 —— 编辑内核模糊测试
// 作者：hujie  创建：2026-09-18
//
// 为什么要它：
//   DocumentModel 为了大文件流畅，把「每次编辑重扫全文行索引」换成了
//   「只重切受影响区间 + 尾段整体平移」。这个优化一旦有边界漏判
//   （在行首/行尾/文件末尾/CRLF 中间编辑、跨多行删除、粘贴带换行…），
//   表现是「行号错位、渲染串行」这类很难靠肉眼定位的问题。
//   所以用一个与被测实现完全独立的朴素实现（全文重新切分）做差分对拍：
//   随机编辑 → 每一步都比对文本、行数、每行内容、每行起始偏移。
//
// 修订说明（2026-09-18）：对拍维度增加**跨行着色状态**
//   （块注释 / 未闭合字符串 / 括号嵌套深度 —— 后者是彩虹括号的输入）。
//   理由：它和行索引是同一套「编辑后局部失效」的机制，出问题时同样是静默的 ——
//   不抛异常、文本完全正确，只是下游若干行颜色不对，没对拍就只能靠肉眼。
//   参照实现是「从第 0 行起逐行推进着色器」的朴素版本，完全不碰懒推进缓存。
//
// 用法：
//   LiteReader --fuzz                  ← 默认 3000 步随机编辑
//   LiteReader --fuzz 20000 --fuzz-seed 7
// 失败时返回退出码 1（可挂到 CI 上）。

using System.Text;
using LiteReader.Models;

namespace LiteReader.Diagnostics;

public static class EditFuzz
{
    public static bool Requested(string[] args) => Array.IndexOf(args, "--fuzz") >= 0;

    public static bool BenchRequested(string[] args) => Array.IndexOf(args, "--bench-edit") >= 0;

    /// <summary>
    /// 单次编辑耗时基准。这个数字是「增量行索引」是否值得存在的主要依据：
    /// 全文重扫在 19 MB / 10 万行下约 20 ms/次按键（打字会顿），增量更新要压到个位数毫秒。
    /// </summary>
    public static int RunBench()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var sb = new StringBuilder(20_000_000);
        for (int i = 0; i < 100_000; i++)
            sb.Append("    public int Method").Append(i).Append("(int a, int b) { return a + b; } // 中文注释\r\n");
        string big = sb.ToString();
        double mb = big.Length * 2 / 1024.0 / 1024.0;

        var doc = new DocumentModel();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        doc.SetText(big, dirty: false);
        sw.Stop();
        Console.WriteLine($"[bench] 载入 {mb:F1} MB（UTF-16，{doc.LineCount:N0} 行）：建索引 {sw.Elapsed.TotalMilliseconds:F1} ms");

        var rnd = new Random(7);
        var samples = new List<double>(400);
        for (int i = 0; i < 400; i++)
        {
            int at = rnd.Next(doc.CharCount);
            sw.Restart();
            doc.Insert(at, "x");
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        Console.WriteLine($"[bench] 单字符插入 ×400：中位 {samples[samples.Count / 2]:F2} ms  " +
                          $"p90 {samples[(int)(samples.Count * 0.9)]:F2} ms  最大 {samples[^1]:F2} ms");

        // 拆出「不可避免的那部分」：整串重建文本本身的纯拷贝成本。
        // 有了这个数才能判断剩余开销该不该继续优化、以及该往哪个方向优化。
        var concatSamples = new List<double>(20);
        for (int i = 0; i < 20; i++)
        {
            int at = big.Length / 2;
            sw.Restart();
            _ = string.Concat(big.AsSpan(0, at), "x".AsSpan(), big.AsSpan(at));
            sw.Stop();
            concatSamples.Add(sw.Elapsed.TotalMilliseconds);
        }
        concatSamples.Sort();
        Console.WriteLine($"[bench] 其中「整串重建文本」本身：中位 {concatSamples[concatSamples.Count / 2]:F2} ms" +
                          $"（这一步要换成 piece table / 间隙缓冲才能省掉，否则是大文件编辑的固定下限）");

        sw.Restart();
        doc.Undo();
        sw.Stop();
        Console.WriteLine($"[bench] 撤销 1 步：{sw.Elapsed.TotalMilliseconds:F2} ms");

        return 0;
    }

    public static int Run(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        int steps = ArgInt(args, "--fuzz", 3000);
        int seed = ArgInt(args, "--fuzz-seed", 20260918);

        Console.WriteLine($"[fuzz] 步数={steps}  随机种子={seed}");

        string corpus = MakeCorpus(400);
        Console.WriteLine($"[fuzz] 语料：{corpus.Length:N0} 字符 / {CountLines(corpus):N0} 行（含 CRLF / LF / 中文 / 制表符）");

        // 跨行状态那一维必须**真的被覆盖到**，否则对拍形同虚设：
        // 如果语料里每行的起始状态都是干净的，那「比对起始状态」永远成立、什么也证明不了。
        var probe = new DocumentModel();
        probe.SetText(corpus, dirty: false);
        int crossLines = 0;
        for (int i = 0; i < probe.LineCount; i++)
            if (probe.GetLineStartState(i) != LineState.None) crossLines++;
        Console.WriteLine($"[fuzz] 其中 {crossLines:N0} 行的起始状态非干净（跨行块注释 / 未闭合字符串 / 括号深度）");

        int failures = 0;
        if (crossLines == 0)
        {
            Console.WriteLine("[fuzz] ✘ 语料里没有任何跨行状态 —— 「跨行着色状态」这一维等于没测");
            failures++;
        }

        failures += FuzzEdit(corpus, steps, seed);
        failures += FuzzUndoRedo(corpus, Math.Min(steps, 400), seed + 1);

        if (failures == 0)
        {
            Console.WriteLine("[fuzz] 全部通过 ✔");
            return 0;
        }
        Console.WriteLine($"[fuzz] 失败 {failures} 项 ✘");
        return 1;
    }

    // ---------------- 对拍主体 ----------------

    private static int FuzzEdit(string corpus, int steps, int seed)
    {
        var doc = new DocumentModel();
        doc.SetText(corpus, dirty: false);

        string reference = corpus;
        var rnd = new Random(seed);
        int failures = 0;

        for (int step = 0; step < steps; step++)
        {
            (int start, int length, string insert) = RandomEdit(rnd, reference.Length);

            doc.Replace(start, length, insert);
            reference = string.Concat(reference.AsSpan(0, start), insert.AsSpan(), reference.AsSpan(start + length));

            string? err = Compare(doc, reference);
            if (err is null) continue;

            failures++;
            Console.WriteLine($"[fuzz] 第 {step} 步不一致：{err}");
            Console.WriteLine($"[fuzz]   编辑 start={start} length={length} insert={Describe(insert)}");
            if (failures >= 5) break;      // 前几个就够定位了，不要刷屏
        }

        if (failures == 0)
            Console.WriteLine($"[fuzz] 增量行索引 + 跨行着色状态 × {steps} 步：与全文重切/逐行全量推进完全一致 ✔");
        return failures;
    }

    /// <summary>随机编辑，刻意覆盖各边界：文件开头/末尾、行首/行尾、跨多行删除、带换行插入。</summary>
    private static (int Start, int Length, string Insert) RandomEdit(Random rnd, int textLen)
    {
        int start;
        int kind = rnd.Next(100);

        if (kind < 8) start = 0;                          // 文件开头
        else if (kind < 16) start = textLen;              // 文件末尾
        else start = textLen == 0 ? 0 : rnd.Next(textLen + 1);

        int length = 0;
        if (start < textLen)
        {
            int max = Math.Min(textLen - start, kind < 30 ? 4 : 40);
            length = rnd.Next(max + 1);
        }

        string insert = rnd.Next(100) switch
        {
            < 40 => ((char)('a' + rnd.Next(26))).ToString(),          // 单字符
            < 50 => "\n",                                            // LF
            < 58 => "\r\n",                                          // CRLF
            < 64 => "\t",                                            // 制表符
            < 70 => "中文块" + rnd.Next(10),                          // 多字节字符
            < 76 => "x" + "\n" + "y" + "\r\n" + "z",                 // 混合换行
            < 80 => string.Empty,                                    // 纯删除
            _ => MakeRandomChunk(rnd, rnd.Next(1, 30)),
        };

        return (start, length, insert);
    }

    private static string MakeRandomChunk(Random rnd, int len)
    {
        const string pool = "abcXY_09 \t\n\r/*\"'中";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(pool[rnd.Next(pool.Length)]);
        return sb.ToString();
    }

    // ---------------- 撤销/重做对拍 ----------------

    private static int FuzzUndoRedo(string corpus, int steps, int seed)
    {
        var doc = new DocumentModel();
        doc.SetText(corpus, dirty: false);

        var snapshots = new List<string> { corpus };
        var rnd = new Random(seed);

        for (int i = 0; i < steps; i++)
        {
            (int start, int length, string insert) = RandomEdit(rnd, doc.CharCount);
            if (length == 0 && insert.Length == 0) insert = "z";      // 避免空编辑导致快照不变
            doc.Replace(start, length, insert);
            doc.BreakUndoCoalescing();                                // 每步独立成一步历史，便于逆推
            snapshots.Add(doc.Text);
        }

        int failures = 0;
        for (int i = snapshots.Count - 1; i > 0; i--)
        {
            int caret = doc.Undo();
            if (caret < 0) { Console.WriteLine($"[fuzz] 撤销提前耗尽（期望还能撤 {i} 次）"); failures++; break; }

            string? err = Compare(doc, snapshots[i - 1]);
            if (err is null) continue;
            failures++;
            Console.WriteLine($"[fuzz] 撤销到第 {i - 1} 个快照不一致：{err}");
            if (failures >= 5) break;
        }

        // 再全部重做回去
        while (doc.Redo() >= 0) { }
        string? redoErr = Compare(doc, snapshots[^1]);
        if (redoErr is not null)
        {
            failures++;
            Console.WriteLine($"[fuzz] 全部重做后不一致：{redoErr}");
        }

        if (failures == 0)
            Console.WriteLine($"[fuzz] 撤销/重做 × {steps} 步往返：完全还原 ✔");
        return failures;
    }

    // ---------------- 比对 ----------------

    /// <summary>用「朴素实现」（全文重新切分）与文档模型对拍。一致返回 null，否则返回差异描述。</summary>
    private static string? Compare(DocumentModel doc, string reference)
    {
        if (!string.Equals(doc.Text, reference, StringComparison.Ordinal))
        {
            int at = FirstDiff(doc.Text, reference);
            return $"文本不同（首个差异偏移 {at}，长度 {doc.Text.Length} vs {reference.Length}）";
        }

        string[] lines = RefSplit(reference, out int[] starts);

        // 跨行着色状态（块注释 / 未闭合字符串 / 括号嵌套深度）也一并差分对拍。
        // 这一维很容易漏：它的失效逻辑（Replace 里把 _stateComputed 退回 firstLine+1）出问题时
        // 不抛异常、文本也完全正确，只是「下游那几行颜色不对」—— 没有对拍就只能靠肉眼。
        LineState[] refStates = RefStates(lines, doc.Language);

        if (doc.LineCount != lines.Length)
            return $"行数不同：{doc.LineCount} vs {lines.Length}";

        for (int i = 0; i < lines.Length; i++)
        {
            if (!doc.GetLine(i).SequenceEqual(lines[i]))
            {
                string nl = i > 0 ? $"上一行=\"{Trim(lines[i - 1])}\"  " : string.Empty;
                string nn = i + 1 < lines.Length ? $"  下一行=\"{Trim(lines[i + 1])}\"" : string.Empty;
                return $"第 {i} 行内容不同：\n         实际=\"{Trim(doc.GetLine(i).ToString())}\" (len={doc.LineLength(i)})\n" +
                       $"         参照=\"{Trim(lines[i])}\" (len={lines[i].Length})\n" +
                       $"         {nl}{nn}\n" +
                       $"         实际起始={doc.LineStartOffset(i)} 参照起始={starts[i]}";
            }
            if (doc.LineStartOffset(i) != starts[i])
                return $"第 {i} 行起始偏移不同：{doc.LineStartOffset(i)} vs {starts[i]}";
            if (doc.LineLength(i) != lines[i].Length)
                return $"第 {i} 行长度不同：{doc.LineLength(i)} vs {lines[i].Length}";

            LineState actual = doc.GetLineStartState(i);
            if (actual != refStates[i])
                return $"第 {i} 行起始着色状态不同：实际 {ShowState(actual)} vs 参照 {ShowState(refStates[i])}";

            // 偏移 ↔ 行列 往返
            for (int col = 0; col <= lines[i].Length; col += Math.Max(1, lines[i].Length / 3))
            {
                int off = doc.OffsetOf(i, col);
                if (off != starts[i] + col)
                    return $"OffsetOf({i},{col}) 返回 {off}，期望 {starts[i] + col}";
                if (doc.LineIndexOf(off) != i && off < doc.CharCount)
                    return $"LineIndexOf({off}) 返回 {doc.LineIndexOf(off)}，期望 {i}";
            }
        }
        return null;
    }

    /// <summary>
    /// 朴素参照实现：从第 0 行起逐行推进着色器，得到每一行的起始状态。
    /// 它**完全不碰 DocumentModel 的懒推进缓存** —— 这正是它能当参照的前提：
    /// 被验证的是「编辑之后缓存的失效与重算对不对」，而不是着色算法本身。
    /// </summary>
    private static LineState[] RefStates(string[] lines, LanguageMode mode)
    {
        var states = new LineState[lines.Length];
        LineState st = LineState.None;
        for (int i = 0; i < lines.Length; i++)
        {
            states[i] = st;
            st = SyntaxHighlighter.ScanLine(lines[i], st, tokens: null, mode);
        }
        return states;
    }

    private static string ShowState(LineState s)
        => $"(注释={s.InComment} 字符串='{(s.InString == '\0' ? "无" : s.InString.ToString())}' 括号深度={s.Depth})";

    /// <summary>
    /// 朴素参照实现：全文重新按 '\n' 切分，行尾 CR 折叠。
    /// 刻意写得直白、不用任何增量逻辑 —— 它的价值就在于「与被测实现不同源」。
    /// </summary>
    private static string[] RefSplit(string text, out int[] starts)
    {
        var lines = new List<string>();
        var st = new List<int>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int len = i - start;
            if (len > 0 && text[i - 1] == '\r') len--;
            lines.Add(text.Substring(start, len));
            st.Add(start);
            start = i + 1;
        }
        int last = text.Length - start;
        if (last > 0 && text.Length > 0 && text[^1] == '\r') last--;
        lines.Add(text.Substring(start, last));
        st.Add(start);

        starts = st.ToArray();
        return lines.ToArray();
    }

    // ---------------- 语料与工具 ----------------

    /// <summary>构造含 CRLF / LF / 中文 / 制表符 / 转义字符的语料（覆盖各种行边界的坑）。</summary>
    private static string MakeCorpus(int lines)
    {
        var rnd = new Random(12345);
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            string body = (i % 7) switch
            {
                0 => "// 单行注释 with 中文",
                1 => "int x = " + i + ";  // trailing",
                2 => "/* 块注释开始",
                3 => "块注释结束 */ int y = 1;",
                4 => "\tif (a < b) { return \"str\\\"esc\"; }",
                5 => "中文行 —— 全角标点，测试：！？；（）",
                _ => string.Empty,
            };
            sb.Append(body);
            sb.Append(i % 3 == 0 ? "\r\n" : "\n");
        }
        _ = rnd;
        return sb.ToString();
    }

    private static int CountLines(string s)
    {
        int n = 1;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    private static int FirstDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }

    private static string Describe(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            sb.Append(c switch { '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
            if (sb.Length > 40) { sb.Append("…"); break; }
        }
        return "\"" + sb + "\" (len=" + s.Length + ")";
    }

    /// <summary>把控制字符显式转义 —— 直接打印 CR/TAB 会让控制台覆盖上一行，根本看不懂差异。</summary>
    private static string Trim(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString(),
            });
            if (sb.Length > 60) { sb.Append('…'); break; }
        }
        return sb.ToString();
    }

    private static int ArgInt(string[] args, string key, int fallback)
    {
        int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 >= args.Length) return fallback;
        return int.TryParse(args[i + 1], out int v) && v > 0 ? v : fallback;
    }
}
