// LiteReader · Avalonia 跨平台版 —— 开发自检（视觉回归 + 编辑冒烟）
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-19 · 窗口外框跟随主题 + 图标资源）：
//   纯逻辑层新增两节：WindowChromeSmoke（COLORREF 反读、相对亮度方向、平台能力声明）、
//   IconSmoke（图标资源能否加载/解码、Linux 图标路径与 desktop 的 Icon= 是否同源）。
//   真实控件层在 ToolsSmoke 里补了**两节**：
//     · ChromeSmoke   —— 切主题后读 MainWindow.ChromeState，验证「换主题 → 重施外框」这条订阅没断
//                        （颜色对不对由纯逻辑那节管，这节只管接线）。
//     · ShellThemeSmoke —— 验证**外壳**（窗口底/状态栏/分隔条）真的解析到主题色。
//                        这节的唯一理由是那个看代码看不出来的坑：Avalonia 资源查找里
//                        **父字典自己持有的键优先于 MergedDictionaries**，而 ThemeService 正是
//                        把主题字典塞进 Application.Resources.MergedDictionaries —— 于是只要
//                        App.axaml 里恰好也定义了同名键，主题值就被永久遮住。
//                        详见该方法的注释。
//   主题键表由 15 个扩到 18 个（新增 WindowCaptionBackground / WindowCaptionForeground / WindowBorder）。
//   注意：这两项都**不能只靠截图验**——系统标题栏与边框属于非客户区，
//   RenderTargetBitmap 抓不到，必须另外抓真实窗口（见 spikes/winshot/grab.py）。
//
// 修订说明（2026-09-18 续 · 双击分词高亮 / 右侧导航条 / 右键菜单）：
//   纯逻辑层新增两节：WordMarkerSmoke（整词命中边界 + SQL 例外）、
//   OverviewSmoke（导航条滑块的高度/位置/反算与往返一致）。
//   真实控件层在 FeatureSmoke 里补了三节：双击选中与点亮、导航条拖拽跳转、右键菜单的项序与灰显。
//   主题键表由 12 个键扩到 15 个（新增 EditorMarkBackground / EditorOverviewBackground /
//   EditorOverviewThumb）—— 漏一个键不会报错，只会「这一套主题下某个颜色不对」，所以逐键断言。
//
// 修订说明（2026-09-18）：
//   除原有的 5 套主题截图外，断言分成**两层**：
//     ① 纯逻辑层（快、稳、不依赖窗口）：跨行状态机（SyntaxSmoke）、彩虹括号与跨行嵌套深度
//        （RainbowSmoke）、括号配对（BracketMatchSmoke）、查找（FindSmoke）、
//        跳转定义（DefinitionSmoke）、代码补全（CompletionSmoke）、主题键完整性（ThemeKeySmoke）。
//     ② 真实控件层：编辑内核冒烟（EditSmokeAsync）与四项能力的闭环（FeatureSmokeAsync）——
//        前者驱动 HandleKey/HandleText 验证编辑，后者用真实 EditorSurface 验证
//        「彩虹括号亮不亮、命令改的选区回不回流、候选弹不弹得出来、确认后文本被替换成什么」。
//     ③ 工具层（ToolsSmokeAsync）：Ctrl+滚轮调字号（真实控件）、单实例/文件转发
//        （跑完整的收发回路，不只是断言两个纯函数）、终端候选清单与文件关联纯函数。
//   为什么要分层：②能测到 ①测不到的东西（逻辑对了但没人调用、算出来了但没画出来），
//   而①能测到②测不到的边界（②要跑窗口、慢，塞不进几十条边界用例）。
//
//   同时修掉几处**期望值写错**的断言（它们此前从未跑过）：
//     · FindSmoke：测试串里只有 3 处 alpha（不是 4），序号是 2（不是 3），
//       向前查找的 caret 语义是「当前命中的结束位置」，FindBackward(27) 应是 11 而不是 22。
//     · RainbowSmoke：用「插入 /*」验证深度失效是错的 —— 那会让整篇进注释态、深度反而归零，
//       断言退化成「深度变了」；改成删掉一个 {，断言深度精确降 1。
//     · DefinitionSmoke：选区断言把 "Calc(a" 当成了 "Calc"（裁剪规则是裁两端非词字符，不是取首词）。
//     · CompletionSmoke：测试文档里没有函数定义，函数补全那条路其实从没被验证过。
//     · ToolsSmoke：连消息头都没有的空流抛的是 EndOfStreamException，不是 InvalidDataException
//       —— 两者都继承 IOException 但**互不继承**，一开始按后者断言，跑出来是红的。
//
// 用途：`LiteReader --selftest <文件路径>` 会依次套用 5 套主题各截一张 PNG，
//   输出到 exe 同目录的 selftest/ 下，用于人工比对配色、字体回退、行号槽宽度等
//   容易被无声改坏的东西（尤其换主题/改字体回退链之后）。
//
// 为什么需要它：Avalonia 的渲染问题通常是「静默」的 —— 不抛异常，只是画错或画不出来。
//   单靠单元测试测不到「中文是否渲染成方块」「暗色主题下控件是不是还是白底」，
//   必须看图。这个模式就是把看图这件事自动化。
//
// 参数：
//   --selftest              只截空界面（5 套主题）
//   --selftest <path>       先打开该文件再截（最能反映真实观感）
//   --selftest-out <dir>    指定输出目录
// 退出码：全部断言通过 0，有失败 1（可直接挂 CI）。

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiteReader.Models;
using LiteReader.Services;
using LiteReader.ViewModels;
using LiteReader.Views;

namespace LiteReader.Diagnostics;

public static class SelfTest
{
    public static bool Requested(string[] args) => Array.IndexOf(args, "--selftest") >= 0;

    public static async Task<int> RunAsync(MainWindow window, MainWindowViewModel vm, string[] args)
    {
        // WinExe 在 Windows 上默认用系统 ANSI 代码页输出，中文会乱码。显式切到 UTF-8。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 无控制台时忽略 */ }

        string? file = ArgValue(args, "--selftest", skipIfStartsWithDash: true);
        string outDir = ArgValue(args, "--selftest-out") ?? Path.Combine(AppContext.BaseDirectory, "selftest");
        Directory.CreateDirectory(outDir);

        Console.WriteLine($"[selftest] 输出目录：{outDir}");
        Console.WriteLine($"[selftest] 配置文件：{AppConfigStore.FilePath}");
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            vm.OpenFile(file);
            Console.WriteLine($"[selftest] 已打开：{file}");
        }

        // 等首帧 + 布局稳定
        await Task.Delay(1200);

        foreach (ThemeDescriptor theme in ThemeService.Catalog)
        {
            vm.ApplyThemeCommand.Execute(theme.Name);
            await Task.Delay(700);              // 让 FluentTheme + 自绘控件各刷一帧
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            string safe = theme.Name.Replace(' ', '_');
            string path = Path.Combine(outDir, $"theme-{safe}.png");
            Capture(window, path);

            // 关键断言：打印「实际取到的」配色值。
            // 若主题字典没加载到（URI 写错、资源被裁剪），ThemeService.Brush 会退回兜底色，
            // 这里立刻能看出来（同时也说明图片不可信了）。
            string bg = DescribeColor("EditorBackground");
            string kw = DescribeColor("TokKeyword");
            Console.WriteLine($"[selftest] {theme.Name,-16} → {Path.GetFileName(path),-28} " +
                              $"背景={bg} 关键字={kw}");
        }

        // 状态栏数据自检：打印一次实际解析出来的行数/字符数/编码
        Console.WriteLine($"[selftest] 文档摘要：{vm.SelectedTab?.Summary ?? "(无文档)"}");

        int failures = 0;
        failures += SyntaxSmoke();
        failures += RainbowSmoke();
        failures += BracketMatchSmoke();
        failures += FindSmoke();
        failures += DefinitionSmoke();
        failures += CompletionSmoke();
        failures += WordMarkerSmoke();
        failures += OverviewSmoke();
        failures += WindowChromeSmoke();
        failures += IconSmoke(window);
        failures += ThemeKeySmoke();
        failures += await EditSmokeAsync(window, vm);
        failures += await FeatureSmokeAsync(window, vm, outDir);
        failures += await ToolsSmokeAsync(window, vm);
        failures += SelectionCapture(window, vm, outDir);
        Console.WriteLine(failures == 0
            ? "[selftest] 全部通过 ✔"
            : $"[selftest] 失败 {failures} 项 ✘");
        return failures;
    }

    // ---------------- 跨行着色状态机冒烟 ----------------

    /// <summary>
    /// 跨行块注释 / 跨行字符串的状态机断言。
    ///
    /// 为什么要单独做这一节：这个能力此前只能靠截图肉眼看（selftest/sample-syntax.cs.txt）。
    /// 但着色是「静默」的 —— 逻辑改坏了图还在，只是那几行颜色不对，肉眼很容易放过。
    /// 这里把「哪一行的起始状态应该是什么」「某一行的某段应该是什么词法类型」
    /// 变成可回归的断言，改坏了立刻红。
    /// </summary>
    private static int SyntaxSmoke()
    {
        Console.WriteLine("[selftest] --- 跨行状态机冒烟 ---");

        // 刻意把陷阱都放进来：
        //   第 2 行 = 注释在本行闭合、后面还有代码 → 必须能切回代码态（不能整行吞掉）
        //   第 5 行 = 行注释里同时出现 " 和 */    → 两者都不该生效
        //   第 3/4 行 = 未闭合字符串跨行续行
        string[] lines =
        [
            "int a = 1;",                       // 0  纯代码
            "/* 注释开始",                       // 1  开启块注释，行末仍在注释里
            "   still comment */ int b = 2;",   // 2  行内闭合，之后回到代码
            "string s = \"未闭合",               // 3  开启跨行字符串
            "还是字符串\"; int d = 4;",           // 4  闭合，之后回到代码
            "// 行注释里 \" 与 */ 都无效",         // 5  整行注释
            "int c = 3;",                       // 6  回到纯代码
        ];

        var doc = new DocumentModel();
        doc.SetText(string.Join("\n", lines), dirty: false);

        int failures = 0;

        failures += Check("第 0 行起始为干净状态", doc.GetLineStartState(0) == LineState.None);
        failures += Check("块注释开启：第 2 行起始处于注释态", doc.GetLineStartState(2).InComment);
        failures += Check("块注释闭合：第 3 行起始回到干净状态", doc.GetLineStartState(3) == LineState.None);
        failures += Check("跨行字符串开启：第 4 行起始处于字符串态", doc.GetLineStartState(4).InString == '"');
        failures += Check("跨行字符串闭合：第 5 行起始回到干净状态", doc.GetLineStartState(5) == LineState.None);
        failures += Check("行注释里的引号不开启字符串", doc.GetLineStartState(6).InString == '\0');
        failures += Check("行注释里的 */ 不结束注释", !doc.GetLineStartState(6).InComment);

        failures += Check("第 0 行 int 被识别为关键字",
            HasWord(lines, doc, 0, TokenKind.Keyword, "int"));
        failures += Check("第 2 行注释段一直延伸到 */（由起始注释态续行而来）",
            HasTokenTrimmed(lines, doc, 2, TokenKind.Comment, "still comment */"));
        failures += Check("第 2 行 */ 之后的 int 恢复为关键字",
            HasWord(lines, doc, 2, TokenKind.Keyword, "int"));
        failures += Check("第 4 行闭合引号后不再有字符串段",
            !HasToken(lines, doc, 4, TokenKind.String, "int"));
        failures += Check("第 5 行整行是注释（含行首 //）",
            HasToken(lines, doc, 5, TokenKind.Comment, "// 行注释里"));

        // 编辑必须让跨行状态失效并重算 —— 否则改掉 /* 之后下游各行颜色还是错的
        doc.Replace(doc.LineStartOffset(1), 1, "x");     // 把 /* 的斜杠换成 x
        failures += Check("编辑掉 /* 之后状态被失效并重算", !doc.GetLineStartState(2).InComment);

        return failures;
    }

    /// <summary>第 line 行里是否存在一个整词匹配 word 的 token（用于关键字这类短词，避免子串误判）。</summary>
    private static bool HasWord(string[] lines, DocumentModel doc, int line, TokenKind kind, string word)
    {
        var tokens = new List<Token>();
        SyntaxHighlighter.ScanLine(lines[line], doc.GetLineStartState(line), tokens);
        foreach (Token t in tokens)
            if (t.Kind == kind && lines[line].AsSpan(t.Start, t.Length).SequenceEqual(word))
                return true;
        return false;
    }

    /// <summary>第 line 行里是否存在一个「以 text 开头」的 token（用于注释段这类会往后延伸的段）。</summary>
    private static bool HasToken(string[] lines, DocumentModel doc, int line, TokenKind kind, string text)
    {
        var tokens = new List<Token>();
        SyntaxHighlighter.ScanLine(lines[line], doc.GetLineStartState(line), tokens);
        foreach (Token t in tokens)
            if (t.Kind == kind && lines[line].AsSpan(t.Start, t.Length).StartsWith(text))
                return true;
        return false;
    }

    /// <summary>
    /// 同 HasToken，但比较前去首尾空白。
    /// 跨行续行的注释段是从「行首」开始发的，所以它的文本会带上开头那几个缩进空格 ——
    /// 断言里带上这些空格只会让测试读起来更晦涩、也不会多验证到任何东西。
    /// </summary>
    private static bool HasTokenTrimmed(string[] lines, DocumentModel doc, int line, TokenKind kind, string text)
    {
        var tokens = new List<Token>();
        SyntaxHighlighter.ScanLine(lines[line], doc.GetLineStartState(line), tokens);
        foreach (Token t in tokens)
            if (t.Kind == kind && lines[line].AsSpan(t.Start, t.Length).Trim().SequenceEqual(text))
                return true;
        return false;
    }

    // ---------------- 彩虹括号 / 嵌套深度冒烟 ----------------

    /// <summary>
    /// 彩虹括号与跨行嵌套深度。
    ///
    /// 为什么必须断言：彩虹括号是「全靠肉眼」才看得出来的功能 ——
    /// 深度算错一位，所有括号的颜色会整体错开，但程序不报任何错、文本内容也完全正常。
    /// 而且深度是**跨行**状态（与块注释同一套懒推进机制），
    /// 一旦 EnsureState 忘了把它带上，症状是「每行都从第 0 色重新开始」，很难追。
    /// </summary>
    private static int RainbowSmoke()
    {
        Console.WriteLine("[selftest] --- 彩虹括号 / 嵌套深度冒烟 ---");

        string[] lines =
        [
            "void Outer() {",        // 0
            "    if (a) {",          // 1
            "        Call(x[0]);",   // 2
            "    }",                 // 3
            "}",                     // 4
        ];

        var doc = new DocumentModel();
        doc.SetText(string.Join("\n", lines), dirty: false);

        int failures = 0;

        // 逐行起始深度：这是「跨行」的关键证据 —— 每一层的开括号都要在后续行里继续生效
        failures += Check("第 1 行起始深度 = 1", doc.GetLineStartState(1).Depth == 1);
        failures += Check("第 2 行起始深度 = 2", doc.GetLineStartState(2).Depth == 2);
        failures += Check("第 3 行起始深度 = 2", doc.GetLineStartState(3).Depth == 2);
        failures += Check("第 4 行起始深度 = 1", doc.GetLineStartState(4).Depth == 1);

        // 第 2 行 `        Call(x[0]);` 的括号列： ( =12  [ =14  ] =16  ) =17
        // 期望色号 2 / 3 / 3 / 2 —— 一行之内就能看出「深浅交替」
        failures += Check("第 2 行 ( 取第 2 色", BracketColorAt(doc, lines, 2, 12) == 2);
        failures += Check("第 2 行 [ 取第 3 色", BracketColorAt(doc, lines, 2, 14) == 3);
        failures += Check("第 2 行 ] 取第 3 色", BracketColorAt(doc, lines, 2, 16) == 3);
        failures += Check("第 2 行 ) 取第 2 色", BracketColorAt(doc, lines, 2, 17) == 2);

        // 闭括号取「减一后」的深度色 —— 这才保证一对括号同色
        failures += Check("第 3 行 } 取第 1 色（与其配对的 { 同色）", BracketColorAt(doc, lines, 3, 4) == 1);
        failures += Check("第 4 行 } 取第 0 色（与首行的 { 同色）", BracketColorAt(doc, lines, 4, 0) == 0);

        // 相邻同色括号被合并成一段是允许的（视觉一致），但不同色绝不能合并
        var tokens = new List<Token>();
        SyntaxHighlighter.ScanLine("{{}}", LineState.None, tokens);
        var bracketColors = tokens.Where(t => t.Kind == TokenKind.Bracket).Select(t => t.Color).ToList();
        failures += Check("{{}} 的括号色号序列是 0,1,1,0",
            bracketColors.Count == 3 && bracketColors[0] == 0 && bracketColors[1] == 1 && bracketColors[2] == 0);

        // 编辑之后深度必须失效重算 —— 否则改掉一个 { 之后，下游所有行的彩虹色会整体错位。
        //（这里不能用「插入 /*」来验证：那会让整个文档进注释态、深度反而归零，
        //  断言就成了「深度变了」而不是「深度对」，改坏了也看不出来。）
        // 删掉第 0 行末尾的 {，它原本把第 1 行起的括号都压深了一层：
        //   正确的话第 1 行起始深度应从 1 降到 0、第 2 行从 2 到 1。
        int brace0 = lines[0].IndexOf('{');
        doc.Replace(brace0, 1, string.Empty);
        failures += Check("删掉第 0 行的 { 后：第 1 行起始深度降为 0", doc.GetLineStartState(1).Depth == 0);
        failures += Check("删掉第 0 行的 { 后：第 2 行起始深度降为 1", doc.GetLineStartState(2).Depth == 1);

        return failures;
    }

    /// <summary>取第 line 行第 col 个字符若是括号，返回它的彩虹色号；不是括号返回 -1。</summary>
    private static int BracketColorAt(DocumentModel doc, string[] lines, int line, int col)
    {
        var tokens = new List<Token>();
        SyntaxHighlighter.ScanLine(lines[line], doc.GetLineStartState(line), tokens, doc.Language);
        foreach (Token t in tokens)
            if (t.Kind == TokenKind.Bracket && col >= t.Start && col < t.Start + t.Length) return t.Color;
        return -1;
    }

    /// <summary>同 BracketColorAt，但行文本直接取自文档（用于对真实打开的文件断言）。</summary>
    private static int BracketColorAtOffset(DocumentModel doc, int offset)
    {
        int line = doc.LineIndexOf(offset);
        var tokens = new List<Token>();
        SyntaxHighlighter.ScanLine(doc.GetLine(line), doc.GetLineStartState(line), tokens, doc.Language);

        int col = offset - doc.LineStartOffset(line);
        foreach (Token t in tokens)
            if (t.Kind == TokenKind.Bracket && col >= t.Start && col < t.Start + t.Length) return t.Color;
        return -1;
    }

    // ---------------- 括号配对冒烟 ----------------

    private static int BracketMatchSmoke()
    {
        Console.WriteLine("[selftest] --- 括号配对冒烟 ---");
        int failures = 0;

        // 用 IndexOf 现算下标，避免手数位置数错
        const string code = "int f(int a, int b) { return a + b; }";
        int lp = code.IndexOf('('), rp = code.IndexOf(')');
        int lb = code.IndexOf('{'), rb = code.IndexOf('}');

        var doc = new DocumentModel();
        doc.SetText(code, dirty: false);

        failures += Check("光标在 ( 上向右配到对应的 )",
            BracketMatcher.TryFind(doc, lp, out BracketPair p1) && p1.AStart == lp && p1.BStart == rp);
        failures += Check("光标在 ) 上向左配到对应的 (",
            BracketMatcher.TryFind(doc, rp, out BracketPair p2) && p2.AStart == lp && p2.BStart == rp);
        failures += Check("光标在 { 上配到 }",
            BracketMatcher.TryFind(doc, lb, out BracketPair p3) && p3.AStart == lb && p3.BStart == rb);
        failures += Check("光标不在括号上时返回 false", !BracketMatcher.TryFind(doc, 0, out _));

        // 同类型配对：混合嵌套时不能把 [ 与 ) 配成一对（C++ 版专门修过这个）
        const string mixed = "(a[b)c]";
        var mix = new DocumentModel();
        mix.SetText(mixed, dirty: false);
        int mlp = mixed.IndexOf('('), mlb = mixed.IndexOf('[');
        int mrp = mixed.IndexOf(')'), mrb = mixed.IndexOf(']');

        failures += Check("混合嵌套： ( 配到自己的 )，不被 [ 干扰",
            BracketMatcher.TryFind(mix, mlp, out BracketPair m1) && m1.BStart == mrp);
        failures += Check("混合嵌套： [ 配到自己的 ]",
            BracketMatcher.TryFind(mix, mlb, out BracketPair m2) && m2.BStart == mrb);
        failures += Check("混合嵌套：两个配对互不相同的另一端", m1.BStart != m2.BStart);

        // SQL 的 BEGIN / END 当块括号
        const string sqlText = "BEGIN\n  BEGIN\n    SELECT 1;\n  END\nEND";
        var sql = new DocumentModel();
        sql.SetText(sqlText, dirty: false);
        sql.Language = LanguageMode.Sql;

        int outerBegin = sqlText.IndexOf("BEGIN", StringComparison.Ordinal);
        int innerBegin = sqlText.IndexOf("BEGIN", outerBegin + 1, StringComparison.Ordinal);
        int innerEnd = sqlText.IndexOf("END", StringComparison.Ordinal);
        int outerEnd = sqlText.LastIndexOf("END", StringComparison.Ordinal);

        failures += Check("SQL：外层 BEGIN 配到最外层的 END",
            BracketMatcher.TryFind(sql, outerBegin, out BracketPair s1) && s1.BStart == outerEnd);
        failures += Check("SQL：内层 BEGIN 配到内层的 END",
            BracketMatcher.TryFind(sql, innerBegin, out BracketPair s2) && s2.BStart == innerEnd);
        failures += Check("SQL：END 能反向配到 BEGIN",
            BracketMatcher.TryFind(sql, innerEnd, out BracketPair s3) && s3.AStart == innerBegin);
        failures += Check("SQL：BEGIN 整词长度被完整高亮", s1.ALength == 5 && s1.BLength == 3);

        // 同一份文本，语言不是 SQL 时不该把 BEGIN/END 词当块括号
        var plain = new DocumentModel();
        plain.SetText(sqlText, dirty: false);
        failures += Check("非 SQL 语言下 BEGIN/END 不参与块括号配对",
            !BracketMatcher.TryFind(plain, outerBegin, out _));

        return failures;
    }

    // ---------------- 查找冒烟 ----------------

    private static int FindSmoke()
    {
        Console.WriteLine("[selftest] --- 查找冒烟 ---");

        //         0123456789012345678901234567
        //         0         1         2
        const string text = "alpha Beta ALPHA beta alpha";
        // 忽略大小写 3 处：alpha@0 / ALPHA@11 / alpha@22；区分大小写只有 2 处（0 与 22）
        int failures = 0;

        failures += Check("向后查找命中第一处", TextSearch.FindForward(text, "alpha", 0, matchCase: false) == 0);
        failures += Check("忽略大小写时命中的是中间那处 ALPHA",
            TextSearch.FindForward(text, "alpha", 1, matchCase: false) == 11);
        failures += Check("区分大小写时跳过 ALPHA，命中末尾的小写 alpha",
            TextSearch.FindForward(text, "alpha", 1, matchCase: true) == 22);
        failures += Check("到文末未命中则绕回文首",
            TextSearch.FindForward(text, "alpha", 23, matchCase: false) == 0);

        // 向前查找的 caret 语义：传的是「当前命中的结束位置」——
        // DoFind 命中后把光标放在 hit + 查询词长度，F3 / Shift+F3 再从这个位置出发。
        // ⚠️ 这里踩过一个坑：LastIndexOf 的 startIndex 约束的是**命中的结束位置**而不是起始位置
        //   （实测见 spikes/lastindexof-probe/probe.cs）。按「起始位置」直觉传参，
        //   向前查找会在第一个命中处永久打转。
        failures += Check("向前：从文末跳过当前命中，回到上一处",
            TextSearch.FindBackward(text, "alpha", 27, matchCase: false) == 11);
        failures += Check("向前：跳过当前命中，回到再前一处",
            TextSearch.FindBackward(text, "alpha", 16, matchCase: false) == 0);
        failures += Check("向前：从文首往前会绕到文末最后一处",
            TextSearch.FindBackward(text, "alpha", 0, matchCase: false) == 22);

        failures += Check("忽略大小写共 3 处", TextSearch.CountAll(text, "alpha", false) == 3);
        failures += Check("区分大小写共 2 处", TextSearch.CountAll(text, "alpha", true) == 2);
        failures += Check("「第 n / 共 m 项」的序号正确（ALPHA@11 是第 2 个）",
            TextSearch.OrdinalOf(text, "alpha", 11, false) == 2);
        failures += Check("查不到返回 -1", TextSearch.FindForward(text, "zzz", 0, false) == -1);
        failures += Check("空查询返回 -1", TextSearch.FindForward(text, "", 0, false) == -1);

        return failures;
    }

    // ---------------- 跳转定义冒烟 ----------------

    private static int DefinitionSmoke()
    {
        Console.WriteLine("[selftest] --- 跳转定义冒烟 ---");

        const string src =
            "        int total = Calc(a, b);\n" +      // 0  调用处（当前行，应被降权）
            "        return total;\n" +                 // 1
            "    }\n" +                                 // 2
            "    public int Calc(int a, int b)\n" +     // 3  真正的定义：public + int + 后跟 (
            "    {\n" +                                 // 4
            "        return a + b;\n" +                 // 5
            "    }\n";                                  // 6

        var doc = new DocumentModel();
        doc.SetText(src, dirty: false);

        int callSite = src.IndexOf("Calc", StringComparison.Ordinal);
        int defSite = src.IndexOf("Calc", callSite + 1, StringComparison.Ordinal);

        int failures = 0;
        failures += Check("取光标下的整词", DefinitionFinder.TargetName(doc, -1, -1, callSite) == "Calc");
        // 选区两端若夹着运算符/空格，取的是其中的整词 —— 注意不是「选区里的第一个词」，
        // 而是「裁掉两端非词字符后的整段」，所以选区只能略大于目标词。
        failures += Check("取选区里的整词（两端杂质被裁掉）",
            DefinitionFinder.TargetName(doc, callSite - 2, callSite + 4, 0) == "Calc");
        // anchor == caret（无选区）时要回退到「光标下的整词」—— 查找/跳转走的正是这条路
        failures += Check("空选区回退到光标下的整词",
            DefinitionFinder.TargetName(doc, callSite, callSite, callSite) == "Calc");

        int best = DefinitionFinder.FindBest(doc, "Calc", callSite);
        failures += Check("跳到定义处而不是调用处的当前行", best == defSite);
        failures += Check("找不到返回 -1", DefinitionFinder.FindBest(doc, "NoSuchName", 0) == -1);

        // 打分区分度：两处调用 + 一处定义，三者「后跟 (」的条件完全一样，
        // 唯一的差别就是定义处前面有声明关键字（int）。这条断言专门守住
        // 「前置声明关键字 +5」真的生效 —— 若 PrevWord 退回「不跳空白」的写法，
        // 定义处会掉到与第二处调用同分（4:4），按「严格大于」的取优规则会选中前面那处调用。
        const string twoSrc = "Calc(a);\nCalc(b);\nint Calc(int x) { return x; }\n";
        var two = new DocumentModel();
        two.SetText(twoSrc, dirty: false);

        int callA = twoSrc.IndexOf("Calc", StringComparison.Ordinal);
        int declAt = twoSrc.LastIndexOf("Calc", StringComparison.Ordinal);
        failures += Check("前置声明关键字参与打分（压过同分的另一处调用）",
            DefinitionFinder.FindBest(two, "Calc", callA) == declAt);

        // 只应命中「整词」：内嵌在别的标识符里不算
        var embedded = new DocumentModel();
        embedded.SetText("ReCalc(x); Calc(y);", dirty: false);
        int embeddedHit = DefinitionFinder.FindBest(embedded, "Calc", 0);
        failures += Check("不做子串匹配（ReCalc 里的 Calc 不算命中）",
            embeddedHit >= 0 && embedded.Text.AsSpan(embeddedHit, 4).SequenceEqual("Calc")
            && embeddedHit != 2);

        return failures;
    }

    // ---------------- 代码补全冒烟 ----------------

    private static int CompletionSmoke()
    {
        Console.WriteLine("[selftest] --- 代码补全冒烟 ---");

        const string src =
            "public int Calculate(int a)\n" +
            "{\n" +
            "    return Compute(a);\n" +
            "}\n" +
            "int Compute(int x) { return x; }\n" +
            "void Run() { if (true) { } }\n";

        var doc = new DocumentModel();
        doc.SetText(src, dirty: false);
        doc.Language = LanguageMode.CLike;

        int failures = 0;

        // 函数名索引：标识符后紧跟 '(' 才算，且要排除 if( / for( 这类关键字
        IReadOnlyList<string> funcs = doc.FunctionNames();
        failures += Check("收集到函数名 Calculate", funcs.Contains("Calculate"));
        failures += Check("收集到函数名 Compute", funcs.Contains("Compute"));
        failures += Check("收集到函数名 Run", funcs.Contains("Run"));
        failures += Check("不把关键字 if( 当成函数名", !funcs.Contains("if"));

        // 前缀匹配
        int typeAt = doc.CharCount;
        var items = CompletionEngine.Suggest(doc, typeAt);
        failures += Check("光标在文末（空白处）时没有候选", items.Count == 0);

        // 模拟真实场景：函数已经写在文件里，用户正在打它的前几个字母。
        // 函数名索引必须能在文件里找到它 —— 如果测试文档里根本没有定义，
        // 候选就只剩关键字一条来源，这里会「通过」，但函数补全那条路从没被验证过。
        var doc2 = new DocumentModel();
        doc2.SetText("int Calculate(int a) { return a; }\nCalc", dirty: false);
        doc2.Language = LanguageMode.CLike;
        var items2 = CompletionEngine.Suggest(doc2, doc2.CharCount);
        failures += Check("前缀 Calc 能提示到 Calculate（函数，带 () 标志）",
            items2.Any(i => i.Text == "Calculate" && i.IsFunction));

        var doc3 = new DocumentModel();
        doc3.SetText("pub", dirty: false);
        doc3.Language = LanguageMode.CLike;
        var items3 = CompletionEngine.Suggest(doc3, 3);
        failures += Check("前缀 pub 能提示 public 关键字",
            items3.Any(i => i.Text == "public" && !i.IsFunction));

        // 语言切换后候选源也切换：SQL 不该提示 C# 关键字，反之亦然
        var sqlDoc = new DocumentModel();
        sqlDoc.SetText("SEL", dirty: false);
        sqlDoc.Language = LanguageMode.Sql;
        var sqlItems = CompletionEngine.Suggest(sqlDoc, 3);
        failures += Check("SQL 文件提示 SELECT", sqlItems.Any(i => i.Text == "SELECT"));
        failures += Check("SQL 文件不提示 C# 的 public", !sqlItems.Any(i => i.Text == "public"));

        // 已经完整打出来的词不再提示自己
        var exact = new DocumentModel();
        exact.SetText("return", dirty: false);
        exact.Language = LanguageMode.CLike;
        failures += Check("完整打出的词不再自我提示",
            !CompletionEngine.Suggest(exact, 6).Any(i => i.Text == "return"));

        return failures;
    }

    // ---------------- 双击分词高亮（整词命中） ----------------

    /// <summary>
    /// 双击分词高亮：整词命中的边界 + SQL 例外。
    ///
    /// 为什么必须断言而不是靠看图：这条功能错了**完全不报错** ——
    /// 双击 `in` 顺手把 `index`、`inline` 里的 in 也点亮，看上去只是「底色多了一点」；
    /// 而 SQL 里双击 END 少了例外，一屏会被几十个 END 的底色糊满。
    /// 两种错都只能盯着图发现，所以这里把语义钉死。
    /// </summary>
    private static int WordMarkerSmoke()
    {
        Console.WriteLine("[selftest] --- 双击分词高亮冒烟 ---");
        int failures = 0;

        //         0123456789012345678901234
        const string text = "int index = in + 1; in++;";
        // 「in」按整词只有 2 处（@12、@20）；`int` 里的 in 与 `index` 里的 in 都不算
        List<MarkRange> hits = WordMarker.CollectAll(text, "in");
        failures += Check("只收整词命中（int / index 里的 in 不算）",
            hits.Count == 2 && hits[0].Start == 12 && hits[1].Start == 20);
        failures += Check("命中区间是左闭右开的整词",
            hits.Count == 2 && hits[0].Length == 2 && hits[0].End == 14);

        // 单字符词也必须按整词边界认。这里故意放一正一反两条：
        //   "a b a c a" 里 a 有 3 处（都是独立词）；
        //   "aaaaaaaa" 里 a 有 **0** 处 —— 整串是一个词，其中任何单个 a 都不是「整词」。
        // 第二条正是「整词」这个定义的价值所在，写成 count == 8 是错的（第一版就写错了）。
        List<MarkRange> single = WordMarker.CollectAll("a b a c a", "a");
        failures += Check("单字符词按整词边界计数（a b a c a 里有 3 处）",
            single.Count == 3 && single[0].Start == 0 && single[2].Start == 8);
        failures += Check("连成一串的同一字符不算整词命中（aaaaaaaa 里 a 有 0 处）",
            WordMarker.CollectAll("aaaaaaaa", "a").Count == 0);

        failures += Check("空标记词 → 空表（不抛异常）", WordMarker.CollectAll(text, string.Empty).Count == 0);
        failures += Check("文本里没有该词 → 空表", WordMarker.CollectAll(text, "zzz").Count == 0);
        failures += Check("下划线算单词字符（_in 里的 in 不算命中）",
            WordMarker.CollectAll("_in in", "in").Count == 1);
        failures += Check("词在文首 / 文末时都能命中",
            WordMarker.CollectAll("in", "in").Count == 1
            && WordMarker.CollectAll("x in", "in").Count == 1);

        // ---- SQL 的 BEGIN / END / CASE 例外 ----
        failures += Check("SQL 里双击 END → 不做全文档高亮（否则一屏糊满底色）",
            WordMarker.MarksFor("BEGIN x END", "END", LanguageMode.Sql).Count == 0);
        failures += Check("SQL 里 BEGIN / CASE 同样例外",
            WordMarker.MarksFor("BEGIN x BEGIN", "BEGIN", LanguageMode.Sql).Count == 0
            && WordMarker.MarksFor("CASE x END", "CASE", LanguageMode.Sql).Count == 0);
        failures += Check("SQL 例外区分大小写（小写 end 不算块括号词）",
            WordMarker.MarksFor("end x end", "end", LanguageMode.Sql).Count == 2);
        failures += Check("非 SQL 语言下 END 照常全文档高亮",
            WordMarker.MarksFor("END x END", "END", LanguageMode.CLike).Count == 2);
        failures += Check("SQL 里双击普通词照常高亮",
            WordMarker.MarksFor("select a from x where a", "a", LanguageMode.Sql).Count == 2);

        // ---- 落点 → 整词边界（沿用原版 wordAtOffset 的语义） ----
        (int w1, int e1) = WordMarker.WordBoundsAt(text, 6);
        failures += Check("落在词中间 → 取到整个词", text.AsSpan(w1, e1 - w1).SequenceEqual("index"));
        (int w2, int e2) = WordMarker.WordBoundsAt(text, 0);
        failures += Check("落在词首 → 取到整个词", w2 == 0 && e2 == 3);
        (int w3, int e3) = WordMarker.WordBoundsAt(text, 3);
        failures += Check("落在词尾后的空白 → 向左扩展但不向右吞（原版防吞换行的补丁）",
            w3 == 0 && e3 == 3);
        failures += Check("越界偏移 → 空区间（不抛异常）",
            WordMarker.WordBoundsAt(text, -1) == (-1, -1)
            && WordMarker.WordBoundsAt(text, text.Length) == (text.Length, text.Length));

        return failures;
    }

    // ---------------- 右侧滑动导航条（几何） ----------------

    /// <summary>
    /// 导航条的几何换算。为什么必须断言：滑块高度/位置算错的症状是
    /// 「能滚但滑块不跟手」或者「拖到底却到不了文末」—— 都是要拿鼠标较劲才发现，
    /// 而且顺着看代码很容易觉得「比例算对了就对了」，其实夹取、分母取谁都有讲究。
    /// </summary>
    private static int OverviewSmoke()
    {
        Console.WriteLine("[selftest] --- 右侧导航条几何冒烟 ---");
        int failures = 0;

        failures += Check("总行数 ≤ 可视行数 → 不可滚（不画滑块）",
            !OverviewBar.IsScrollable(10, 40) && !OverviewBar.IsScrollable(40, 40));
        failures += Check("总行数 > 可视行数 → 可滚", OverviewBar.IsScrollable(41, 40));
        failures += Check("空文档不算可滚", !OverviewBar.IsScrollable(0, 40));

        // 高度：按「可视 / 总行数」的比例（这一组刻意选得比最小高度大，量的才是比例本身）
        double proportional = OverviewBar.ThumbHeight(1000, 10000, 1000, 28);
        failures += Check("滑块高度 = 轨道高 × 可视占比", Math.Abs(proportional - 100) < 0.001);
        double tiny = OverviewBar.ThumbHeight(1000, 1_000_000, 10, 28);
        failures += Check("比例极小时被最小高度托住（否则细得拖不住）", Math.Abs(tiny - 28) < 0.001);
        double over = OverviewBar.ThumbHeight(100, 50, 100, 28);
        failures += Check("比例超过 1 时被夹在轨道高（滑块不会溢出轨道）", Math.Abs(over - 100) < 0.001);

        // 位置：首行 → 贴顶；最后可滚位 → 贴底
        const double track = 200;
        double thumbH = OverviewBar.ThumbHeight(track, 1000, 100, 28);
        failures += Check("首行在文首 → 滑块贴顶",
            Math.Abs(OverviewBar.ThumbTop(track, 1000, 100, thumbH, 0)) < 0.001);
        failures += Check("首行到最后可滚位 → 滑块贴底（底下不留空档）",
            Math.Abs(OverviewBar.ThumbTop(track, 1000, 100, thumbH, 900) - (track - thumbH)) < 0.001);

        int back = OverviewBar.LineFromThumbTop(
            OverviewBar.ThumbTop(track, 1000, 100, thumbH, 450), track, 1000, 100, thumbH);
        failures += Check($"位置 ↔ 行号 互为逆运算（450 → {back}）", back == 450);
        failures += Check("拖到轨道顶端 → 文首",
            OverviewBar.LineFromThumbTop(0, track, 1000, 100, thumbH) == 0);
        failures += Check("拖到轨道底端 → 最后可滚位",
            OverviewBar.LineFromThumbTop(track - thumbH, track, 1000, 100, thumbH) == 900);
        failures += Check("超出轨道范围被夹住（不会算出负行或越界行）",
            OverviewBar.LineFromThumbTop(-50, track, 1000, 100, thumbH) == 0
            && OverviewBar.LineFromThumbTop(track + 50, track, 1000, 100, thumbH) == 900);
        failures += Check("轨道高 ≤ 滑块高时返回 0（不除以零）",
            OverviewBar.LineFromThumbTop(10, 100, 1000, 100, 100) == 0);

        // 滑块真的表达「可见比例」而不是常数：同一份文档，视口越高滑块越长
        double shortView = OverviewBar.ThumbHeight(1000, 10000, 1000, 28);
        double tallView = OverviewBar.ThumbHeight(1000, 10000, 4000, 28);
        failures += Check("视口越高滑块越长（= 可见占比，不是常数）", tallView > shortView);

        return failures;
    }

    // ---------------- 主题键完整性 ----------------

    /// <summary>
    /// 断言 5 套主题都提供了彩虹括号 / 配对 / 补全需要的键。
    ///
    /// 这类「漏了一个键」的错误不会有任何报错 —— ThemeService.Brush 会安静地返回兜底色
    /// （见 DescribeColor 的 Magenta 探测法），表现只是「这一套主题下括号颜色不对」。
    /// 所以逐主题、逐键断言一遍。
    /// </summary>
    // ---------------- 窗口外框跟随主题（纯逻辑） ----------------

    /// <summary>
    /// 「外框跟随主题」这件事的**语义**断言 —— 而不是「主题键存在」断言。
    /// 键存在但给的是黑底，照样等于没跟随主题，所以这里算的是**相对亮度方向**：
    ///   亮色主题的标题栏必须亮、暗色主题的必须暗；标题文字与底色要拉得开、边框与底色要能分辨。
    /// 这三条全是「看着不对但跑得通」的那一类，只能靠数值钉住。
    /// </summary>
    private static int WindowChromeSmoke()
    {
        Console.WriteLine("[selftest] --- 窗口外框配色冒烟 ---");

        int failures = 0;

        // COLORREF 是 0x00BBGGRR，**反着读**。写反了不会报错，只会红蓝互换 —— 值得单独钉一条。
        failures += Check("COLORREF 反读 BGR（#282C34 → 0x00342C28）",
            WindowChrome.ToColorRef(Color.FromRgb(0x28, 0x2C, 0x34)) == 0x00342C28);
        failures += Check("纯黑 → 0x00000000", WindowChrome.ToColorRef(Colors.Black) == 0);
        failures += Check("纯白 → 0x00FFFFFF", WindowChrome.ToColorRef(Colors.White) == 0x00FFFFFF);

        failures += Check("相对亮度：黑 = 0", Math.Abs(WindowChrome.Luminance(Colors.Black)) < 1e-9);
        failures += Check("相对亮度：白 = 1", Math.Abs(WindowChrome.Luminance(Colors.White) - 1.0) < 1e-9);

        ThemeService? themes = ThemeService.Current;
        if (themes is null)
        {
            Console.WriteLine("[selftest]   ✘ 没有主题服务");
            return failures + 1;
        }

        foreach (ThemeDescriptor theme in ThemeService.Catalog)
        {
            themes.Apply(theme.Name);

            double cap = WindowChrome.Luminance(themes.ColorOf("WindowCaptionBackground", "#21252B"));
            double fg = WindowChrome.Luminance(themes.ColorOf("WindowCaptionForeground", "#ABB2BF"));
            double border = WindowChrome.Luminance(themes.ColorOf("WindowBorder", "#3E4451"));

            failures += Check(
                $"{theme.Name}：标题栏方向正确（{(theme.IsLight ? "亮色主题 → 亮标题栏" : "暗色主题 → 暗标题栏")}）",
                theme.IsLight ? cap > 0.60 : cap < 0.35);
            failures += Check($"{theme.Name}：标题文字与底色拉得开（Δ={Math.Abs(fg - cap):0.00}）",
                Math.Abs(fg - cap) > 0.35);
            failures += Check($"{theme.Name}：外边框与标题栏底色可分辨（Δ={Math.Abs(border - cap):0.000}）",
                Math.Abs(border - cap) > 0.005);
        }

        themes.Apply(ThemeService.DefaultThemeName);

        // 平台能力声明：**两个方向都要断言**。
        // 只断言「Windows 上支持」的话，非 Windows 平台永远绿 —— 等于没有守住「不要假装支持」。
        if (OperatingSystem.IsWindows())
        {
            failures += Check("Windows：接管窗口外框", WindowChrome.Supported);
            bool expectColors = Environment.OSVersion.Version.Build >= 22000;
            failures += Check($"本机 build {Environment.OSVersion.Version.Build} 的三色支持与版本判断一致",
                WindowChrome.CustomColorSupported == expectColors);
        }
        else
        {
            failures += Check("非 Windows：如实声明不接管外框（不假装支持）", !WindowChrome.Supported);
            failures += Check("非 Windows：不声称支持指定标题栏颜色", !WindowChrome.CustomColorSupported);
        }

        Console.WriteLine($"[selftest]   平台：{(WindowChrome.Supported ? "Windows" : "非 Windows（外框归桌面环境）")}，" +
                          $"三色={WindowChrome.CustomColorSupported}，build={Environment.OSVersion.Version.Build}");
        return failures;
    }

    // ---------------- 图标资源 ----------------

    /// <summary>
    /// 图标有两个**互相独立**的失败点，两个都要断言：
    ///   ① 资源没进程序集 / XAML 里的路径写错 → <c>Window.Icon</c> 为 null
    ///      （窗口、任务栏、Alt+Tab 全是默认空白图标）。XAML 编译期**不校验**资源是否存在，
    ///      所以这个错误只可能在运行期暴露 —— 正是自检该管的事。
    ///   ② Linux 侧 desktop 的 <c>Icon=</c> 与落地文件名对不上 → 文件关联成功但图标空白。
    ///      这一条在本机（Windows）永远走不到，只能靠纯函数断言保护。
    /// </summary>
    private static int IconSmoke(MainWindow window)
    {
        Console.WriteLine("[selftest] --- 图标资源冒烟 ---");

        int failures = 0;

        WindowIcon? icon = window.Icon;
        failures += Check("窗口图标已从资源加载（XAML 的 /Assets/icon.png 解析成功）", icon is not null);

        int w = 0, h = 0;
        if (icon is not null)
        {
            // WindowIcon 没有公开的尺寸属性，量它的办法是「存成 PNG 再读回来」
            using var ms = new MemoryStream();
            icon.Save(ms);
            ms.Position = 0;
            var bmp = new Bitmap(ms);
            w = bmp.PixelSize.Width;
            h = bmp.PixelSize.Height;
        }
        failures += Check($"图标是 256×256（实际 {w}×{h}）", w == 256 && h == 256);

        // 资源本身也要能直接读 —— Linux 装图标走的就是这条路（FileAssociation.InstallLinuxIconAsync），
        // 它依赖的是「资源编进了程序集」，而不是「exe 旁边有个文件」。
        bool assetOk = false;
        string note;
        try
        {
            using Stream? s = Avalonia.Platform.AssetLoader.Open(new Uri(FileAssociation.IconAssetUri));
            assetOk = s is not null && s.Length > 0;
            note = s is null ? "取不到" : $"{s.Length} B";
        }
        catch (Exception ex)
        {
            note = ex.GetType().Name;
        }
        failures += Check($"图标资源可直接读取（{note}）", assetOk);

        // Linux 纯函数：hicolor/256x256/apps/ 是图标主题规范里**必需**的那一档兜底
        string iconPath = FileAssociation.LinuxIconPath().Replace('\\', '/');
        failures += Check("Linux 图标落在 icons/hicolor/256x256/apps/ 下",
            iconPath.EndsWith($"icons/hicolor/256x256/apps/{FileAssociation.IconName}.png", StringComparison.Ordinal));
        failures += Check("desktop 的 Icon= 与图标文件名同源（否则菜单里是空白图标）",
            FileAssociation.LinuxDesktopEntry("/usr/bin/lite-reader")
                .Contains($"Icon={FileAssociation.IconName}", StringComparison.Ordinal)
            && FileAssociation.IconName == Path.GetFileNameWithoutExtension(iconPath));

        return failures;
    }

    private static int ThemeKeySmoke()
    {
        Console.WriteLine("[selftest] --- 主题键完整性 ---");

        ThemeService? themes = ThemeService.Current;
        if (themes is null)
        {
            Console.WriteLine("[selftest]   ✘ 没有主题服务");
            return 1;
        }

        string[] keys =
        [
            "Rb0", "Rb1", "Rb2", "Rb3", "Rb4", "Rb5",
            "EditorMatchBackground", "EditorMarkBackground",
            "EditorOverviewBackground", "EditorOverviewThumb",
            "WindowCaptionBackground", "WindowCaptionForeground", "WindowBorder",
            "CompletionBackground", "CompletionBorder", "CompletionForeground",
            "CompletionSelectionBackground", "CompletionSelectionForeground",
        ];

        int failures = 0;
        foreach (ThemeDescriptor theme in ThemeService.Catalog)
        {
            themes.Apply(theme.Name);

            var missing = new List<string>();
            foreach (string key in keys)
            {
                // 用 Magenta 当探测色：一旦返回它，就说明这个键根本没取到
                Avalonia.Media.IBrush b = themes.Brush(key, Avalonia.Media.Colors.Magenta);
                if (b is Avalonia.Media.ISolidColorBrush s && s.Color == Avalonia.Media.Colors.Magenta)
                    missing.Add(key);
            }

            // 六个彩虹色必须互不相同，否则彩虹就退化成单色了
            var seen = new HashSet<uint>();
            for (int i = 0; i < 6; i++)
            {
                Avalonia.Media.IBrush b = themes.Brush("Rb" + i, Avalonia.Media.Colors.Magenta);
                if (b is Avalonia.Media.ISolidColorBrush s) seen.Add(s.Color.ToUInt32());
            }

            failures += Check($"{theme.Name}：{keys.Length} 个键齐全", missing.Count == 0);
            if (missing.Count > 0)
                Console.WriteLine($"[selftest]     缺键：{string.Join(", ", missing)}");
            failures += Check($"{theme.Name}：6 个彩虹色互不相同", seen.Count == 6);
        }

        themes.Apply(ThemeService.DefaultThemeName);
        return failures;
    }

    // ---------------- 编辑内核冒烟：驱动真实控件 ----------------

    private static async Task<int> EditSmokeAsync(MainWindow window, MainWindowViewModel vm)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        EditorSurface? editor = window.GetVisualDescendants().OfType<EditorSurface>().FirstOrDefault();
        if (editor?.Document is null)
        {
            Console.WriteLine("[selftest] 编辑冒烟：跳过（没有打开文档，故没有编辑区）");
            return 0;
        }

        Console.WriteLine("[selftest] --- 编辑内核冒烟 ---");
        int failures = 0;

        var doc = editor.Document;
        string original = doc.Text;
        int probeLine = Math.Min(2, doc.LineCount - 1);
        int probeAt = doc.LineStartOffset(probeLine);

        editor.Focus();
        editor.CaretOffset = probeAt;
        editor.AnchorOffset = probeAt;

        const string typed = "★中a";

        // 1) 输入文本
        foreach (char c in typed) editor.HandleText(c.ToString());
        failures += Check("输入后文本包含所输内容", doc.Text.Contains(typed, StringComparison.Ordinal));
        failures += Check("输入后置脏标记", doc.IsDirty);
        failures += Check("输入后光标落在插入串之后", editor.CaretOffset == probeAt + typed.Length);
        failures += Check("输入后行数不变", doc.LineCount == CountLines(original));

        // 2) 退格逐个删掉
        for (int i = 0; i < typed.Length; i++) editor.HandleKey(Key.Back, KeyModifiers.None);
        failures += Check("退格后文本还原", string.Equals(doc.Text, original, StringComparison.Ordinal));

        // 3) 撤销回到初始（此时历史里还有那几步输入/退格）
        while (doc.CanUndo) editor.Undo();
        failures += Check("全部撤销后文本等于原文", string.Equals(doc.Text, original, StringComparison.Ordinal));

        // 4) 重做再撤销，验证双向一致
        int redone = 0;
        while (doc.CanRedo) { editor.Redo(); redone++; }
        failures += Check($"重做 {redone} 步后能全部撤销回来", redone > 0 && doc.CanUndo);
        while (doc.CanUndo) editor.Undo();
        failures += Check("再次全部撤销后文本等于原文", string.Equals(doc.Text, original, StringComparison.Ordinal));

        // 5) 回车：行数 +1，且插入的是该文档的换行风格
        int linesBefore = doc.LineCount;
        editor.CaretOffset = doc.LineStartOffset(probeLine);
        editor.AnchorOffset = editor.CaretOffset;
        editor.HandleKey(Key.Enter, KeyModifiers.None);
        failures += Check("回车后行数 +1", doc.LineCount == linesBefore + 1);
        failures += Check("回车插入的是文档换行风格",
            doc.GetLine(probeLine).Length == 0 && doc.LineIndexOf(editor.CaretOffset) == probeLine + 1);
        editor.Undo();
        failures += Check("撤销回车后行数复原", doc.LineCount == linesBefore);

        // 6) 全选 + 导航
        editor.HandleKey(Key.A, KeyModifiers.Control);
        failures += Check("Ctrl+A 全选", editor.AnchorOffset == 0 && editor.CaretOffset == doc.CharCount);

        editor.CaretOffset = 0;
        editor.AnchorOffset = 0;
        editor.HandleKey(Key.End, KeyModifiers.Control);
        failures += Check("Ctrl+End 跳到文末", editor.CaretOffset == doc.CharCount);

        editor.CaretOffset = 0;
        editor.AnchorOffset = 0;
        editor.HandleKey(Key.Down, KeyModifiers.None);
        failures += Check("方向键下移一行", doc.LineIndexOf(editor.CaretOffset) == Math.Min(1, doc.LineCount - 1));

        // 收尾：把文档与光标还原到初始状态，避免影响后面的截图
        while (doc.CanUndo) editor.Undo();
        editor.CaretOffset = 0;
        editor.AnchorOffset = 0;
        ResetTabViewState(vm);

        return failures;
    }

    /// <summary>把选中标签的滚动/光标状态复位（冒烟测试有可能把视图滚到别处）。</summary>
    private static void ResetTabViewState(MainWindowViewModel vm)
    {
        DocumentTabViewModel? tab = vm.SelectedTab;
        if (tab is null) return;
        tab.FirstVisibleLine = 0;
        tab.HorizontalOffset = 0;
    }

    // ---------------- 四项能力的真实控件闭环 ----------------

    /// <summary>
    /// 前面那些套件都是对纯逻辑（SyntaxHighlighter / BracketMatcher / TextSearch /
    /// DefinitionFinder / CompletionEngine）的直接断言 —— 快、稳，但**证明不了这些能力被接到了界面上**：
    /// 配对算对了却没人调用它、候选算出来了却没画出来、查找改了 VM 却回流不到编辑区，
    /// 这些情形在纯逻辑断言下全是绿的。
    ///
    /// 这一节补上那一环：造一个临时 `.cs` 文件、用真实的 <see cref="EditorSurface"/> 走一遍
    /// 「光标落上去 → 括号配对亮起、颜色按深度分开」「打字 → 自动配对 / 候选弹出 → 确认 → 文本被替换」
    /// 「命令进来 → 选区落到该去的位置」。
    ///
    /// **为什么必须另造 .cs、不能直接用自检样本 sample-syntax.cs.txt**：
    ///   语言按**扩展名**判定，`.txt` 落到 Plain —— 补全没有关键字词表、括号也不自动配对。
    ///   拿它在 Plain 文件上测这三项，等于什么都没测（还会一路绿）。
    ///
    /// 退出前会关掉临时标签并把选中标签还原，不留痕迹。
    /// </summary>
    private static async Task<int> FeatureSmokeAsync(MainWindow window, MainWindowViewModel vm, string outDir)
    {
        DocumentTabViewModel? original = vm.SelectedTab;

        // 样本刻意踩到几个点：括号在函数体内（考跨行深度）、嵌套括号（考彩虹深浅）、
        // 定义与调用分离（考跳转打分）、行末半截标识符（补全触发点）、一处空行（自动配对落点）。
        //
        // ⚠️ 后面还要接一段**填充行**：右侧导航条那节需要「总行数 > 可视行数」才成立 ——
        //   11 行的样本在 800 px 高的窗口里一屏就看完了，导航条按设计根本不会显示。
        //   填充行只是注释，不含 Calculate/Compute/括号，不影响前面任何一条基于偏移的断言
        //   （前 11 行的偏移一个都没变，只是文档变长了）。
        const string head =
            "// LiteReader 自检样本（自动生成，可删）\n" +   // 0
            "public int Calculate(int a)\n" +               // 1  定义
            "{\n" +                                         // 2
            "    return a + 1;\n" +                         // 3
            "}\n" +                                         // 4
            "\n" +                                          // 5  空行
            "void Run()\n" +                                // 6
            "{\n" +                                         // 7
            "    int v = Compute(Calculate(2));\n" +        // 8  嵌套括号 + 跳转起点
            "    Cal\n" +                                   // 9  补全触发点
            "}\n";                                          // 10

        var srcBuilder = new System.Text.StringBuilder(head);
        for (int i = 1; i <= 400; i++)
            srcBuilder.Append("// filler ").Append(i.ToString("D4", CultureInfo.InvariantCulture)).Append('\n');
        string src = srcBuilder.ToString();

        // ⚠️ 样本**不能**写进 outDir：默认的 --selftest-out 就是工程内的 selftest/ 目录，
        //   而 SDK 默认 glob `**/*.cs` —— 一个 .cs 落在工程目录里会被直接编进程序集，
        //   下一次构建立刻 CS0106 / CS1002（开发时就真这么踩了一次）。
        //   系统临时目录既保住了 .cs 扩展名（上面说的语言判定需要它），又绝不会被任何构建捡走。
        string tempDir = Path.Combine(Path.GetTempPath(), "LiteReader-selftest");
        Directory.CreateDirectory(tempDir);
        string samplePath = Path.Combine(tempDir, "feature-sample.cs");
        File.WriteAllText(samplePath, src);   // WriteAllText 默认 UTF-8 无 BOM，正是严格 UTF-8 分支要走的路

        vm.OpenFile(samplePath);
        DocumentTabViewModel? tab = vm.SelectedTab;
        if (tab is null
            || !string.Equals(tab.Document.FilePath, Path.GetFullPath(samplePath), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[selftest] 能力闭环冒烟：跳过（临时样本未能打开）");
            return 0;
        }

        // TabControl 是在切到该标签之后才实例化内容控件的 —— 要等一次布局 + 一次派发
        await Task.Delay(500);
        await PumpAsync();

        // 按 Document 取控件：切过标签之后窗口里可能同时存在多个 EditorSurface，
        // 直接 First() 有可能拿到上一个标签的那一个
        EditorSurface? editor = window.GetVisualDescendants().OfType<EditorSurface>()
            .FirstOrDefault(e => ReferenceEquals(e.Document, tab.Document));
        if (editor is null)
        {
            Console.WriteLine("[selftest] 能力闭环冒烟：跳过（编辑区尚未实例化）");
            LeaveTempTab(vm, tab, original);
            return 0;
        }

        Console.WriteLine("[selftest] --- 能力闭环冒烟（真实控件） ---");
        Console.WriteLine($"[selftest] 临时样本：{samplePath}");
        int failures = 0;
        DocumentModel doc = tab.Document;

        // 这两项都可能被用户的 config.json 关掉，自检不该受其影响
        bool savedAutoComplete = EditorSettings.AutoComplete;
        bool savedMatchCase = vm.FindMatchCase;
        EditorSettings.AutoComplete = true;
        vm.FindMatchCase = false;
        vm.CloseFindCommand.Execute(null);

        editor.Focus();

        // 所有下标现算，不手数位置
        int defName = src.IndexOf("Calculate", StringComparison.Ordinal);
        int computeAt = src.IndexOf("Compute", StringComparison.Ordinal);
        int callName = src.IndexOf("Calculate", computeAt, StringComparison.Ordinal);
        int outerOpen = src.IndexOf('(', computeAt);
        int innerOpen = src.IndexOf('(', callName);
        int innerClose = src.IndexOf(')', innerOpen);
        int outerClose = src.IndexOf(')', innerClose + 1);
        int blankLine = doc.LineStartOffset(5);
        // ⚠️ 用 LineEndOffset 而不是「行首偏移 + 3」：后者落在 "    Cal" 的第 4 个空格上，
        //   于是输入 c 之后前缀变成 "c"、候选变成全部 c 开头的关键字 ——
        //   断言还能通过（Calculate 也以 c 开头且排第一），但测的根本不是这个功能。
        int calEnd = doc.LineEndOffset(9);               // "    Cal" 的行末
        int nameLen = "Calculate".Length;

        // ---- ① 彩虹括号：配对 + 跨行深度带来的色号 ----
        editor.AnchorOffset = outerOpen;
        editor.CaretOffset = outerOpen;
        failures += Check("光标在 ( 上 → 配到自己的 )",
            editor.CurrentMatch is { } m1 && m1.AStart == outerOpen && m1.BStart == outerClose);

        editor.CaretOffset = innerOpen;
        failures += Check("光标在内层 ( 上 → 配到内层的 )",
            editor.CurrentMatch is { } m2 && m2.AStart == innerOpen && m2.BStart == innerClose);

        editor.CaretOffset = outerClose;
        failures += Check("光标在闭括号上 → 反向配回开括号",
            editor.CurrentMatch is { } m3 && m3.AStart == outerOpen && m3.BStart == outerClose);

        editor.CaretOffset = blankLine;
        failures += Check("光标不在括号上 → 没有配对（不会留着上一处的高亮）",
            editor.CurrentMatch is null);

        // 屏幕上的颜色：直接问着色器这一行实际吐出的色号，等于「肉眼会看到什么」
        int cOuterOpen = BracketColorAtOffset(doc, outerOpen);
        int cOuterClose = BracketColorAtOffset(doc, outerClose);
        int cInnerOpen = BracketColorAtOffset(doc, innerOpen);
        int cInnerClose = BracketColorAtOffset(doc, innerClose);
        failures += Check($"配对两端同色（外层 {cOuterOpen}/{cOuterClose}、内层 {cInnerOpen}/{cInnerClose}）",
            cOuterOpen == cOuterClose && cInnerOpen == cInnerClose);
        failures += Check("内外层深浅不同（彩虹成立）", cOuterOpen != cInnerOpen);
        failures += Check("行 8 的深度是第 7 行的 { 跨行带下来的（第 1 / 第 2 色）",
            cOuterOpen == 1 && cInnerOpen == 2);

        // 存一张图：色号算对 ≠ 画到了屏幕上（取色键写错、主题字典缺键，都只有看图才发现）
        editor.CaretOffset = outerOpen;
        tab.FirstVisibleLine = 0;
        tab.HorizontalOffset = 0;
        await PumpAsync();
        string featureShot = Path.Combine(outDir, "features.png");
        Capture(window, featureShot);
        Console.WriteLine($"[selftest] 彩虹括号 + 配对高亮 → {Path.GetFileName(featureShot)}");

        // 再存一张放大字号的：默认 13px 下相邻两个深度色的差别在两三百像素宽的缩略图里
        // 几乎看不出来，而「彩虹到底成不成立」正是这一节最需要让人一眼看到的事。
        double savedFontSize = EditorSettings.FontSize;
        EditorSettings.FontSize = 20;
        await PumpAsync();
        string zoomShot = Path.Combine(outDir, "features-rainbow-zoom.png");
        Capture(window, zoomShot);
        EditorSettings.FontSize = savedFontSize;
        await PumpAsync();
        Console.WriteLine($"[selftest] 彩虹括号（放大字号，便于肉眼核对深浅）→ {Path.GetFileName(zoomShot)}");

        // ---- ② 查找：命令 → VM → 编辑区 的闭环 ----
        int total = TextSearch.CountAll(doc.Text, "Calculate", false);   // 定义 + 调用 = 2 处
        tab.AnchorOffset = 0;
        tab.CaretOffset = 0;
        editor.AnchorOffset = 0;
        editor.CaretOffset = 0;
        vm.FindQuery = "Calculate";        // 改查询词本身就会触发一次向下查找（增量查找）
        await PumpAsync();
        failures += Check("向下查找命中定义处",
            tab.AnchorOffset == defName && tab.CaretOffset == defName + nameLen);
        failures += Check($"查找条显示「第 1 / 共 {total} 项」",
            vm.FindStatus.Contains($"/ 共 {total} 项", StringComparison.Ordinal));
        failures += Check("命中位置回流到了编辑区的光标", editor.CaretOffset == defName + nameLen);

        vm.FindNextCommand.Execute(null);
        await PumpAsync();
        failures += Check("查找下一个 → 跳到调用处", tab.AnchorOffset == callName);

        vm.FindPrevCommand.Execute(null);
        await PumpAsync();
        failures += Check("查找上一个 → 回到定义处（不是原地打转）", tab.AnchorOffset == defName);

        vm.FindMatchCase = true;
        failures += Check("切换区分大小写后依然命中", vm.DoFind(forward: true));
        vm.FindMatchCase = false;

        vm.CloseFindCommand.Execute(null);
        failures += Check("关闭查找条", !vm.FindVisible && vm.FindStatus.Length == 0);

        // ---- ③ 自动配对：只在「有语言」的文件里生效 ----
        int before = doc.CharCount;
        editor.AnchorOffset = blankLine;
        editor.CaretOffset = blankLine;
        failures += Check("非纯文本语言下输入 ( 会自动补出 ()", editor.HandleText("("));
        failures += Check("补出的确实是一对括号",
            doc.CharCount == before + 2 && doc.Text[blankLine] == '(' && doc.Text[blankLine + 1] == ')');
        failures += Check("光标落在括号中间", editor.CaretOffset == blankLine + 1);

        failures += Check("紧接着输入 ) 被消费（不重复插入）", editor.HandleText(")"));
        failures += Check("文本长度不变（跳过了已有的闭括号）", doc.CharCount == before + 2);
        failures += Check("光标越过闭括号", editor.CaretOffset == blankLine + 2);

        editor.Undo();
        failures += Check("撤销后自动补出的内容消失、文本逐字复原",
            doc.CharCount == before && string.Equals(doc.Text, src, StringComparison.Ordinal));

        // ---- ④ 跳转定义 ----
        editor.AnchorOffset = callName;
        editor.CaretOffset = callName;
        failures += Check("光标下的标识符被正确取出", editor.WordAtCaret() == "Calculate");
        failures += Check("跳转定义命中（调用处 → 定义处）", editor.GoToDefinition());
        failures += Check("跳转后选中了目标词",
            editor.AnchorOffset == defName && editor.CaretOffset == defName + nameLen);
        failures += Check("选区内容正好是目标标识符",
            doc.Text.Substring(editor.AnchorOffset, editor.CaretOffset - editor.AnchorOffset) == "Calculate");

        editor.AnchorOffset = 0;
        editor.CaretOffset = 0;            // 第 0 行行首是注释里的 '/'，不是标识符
        failures += Check("光标不在标识符上时不乱跳",
            editor.WordAtCaret().Length == 0 && !editor.GoToDefinition());

        // ---- ⑤ 双击分词高亮：命中全部出现位置 ----
        //
        // 为什么非要在真实控件上再测一遍（纯逻辑那节已经断言过整词语义）：
        //   「算出了 2 处命中」和「真的画到了那两行上」是两件事 ——
        //   区间是按行分桶后由渲染层取的，分桶的行号算错、或者标记被后面画的背景盖掉，
        //   纯逻辑层全都是绿的。
        editor.ClearMarksForTest();
        editor.AnchorOffset = 0;
        editor.CaretOffset = 0;
        editor.DoubleClickWord(defName + 2);              // 落在 Calculate 中间
        failures += Check("双击选中整个标识符",
            editor.AnchorOffset == defName && editor.CaretOffset == defName + nameLen);
        failures += Check("双击后标记词就是它", editor.MarkWord == "Calculate");
        failures += Check($"全文 {editor.MarkCount} 处 Calculate 都被点亮（定义 + 调用）",
            editor.MarkCount == 2);
        failures += Check("命中被分到了正确的两行上（第 2 行定义、第 9 行调用）",
            editor.MarksOfLine(1).Count == 1 && editor.MarksOfLine(8).Count == 1);
        failures += Check("命中的偏移正是那两处",
            editor.MarksOfLine(1)[0].Start == defName && editor.MarksOfLine(8)[0].Start == callName);

        // 双击标点/空白：取不到整词，退化成「选相邻一个字符」，并且**不该**点亮任何东西
        editor.ClearMarksForTest();
        editor.DoubleClickWord(blankLine);                // 第 5 行是个空行
        failures += Check("双击空白处不点亮任何词（否则会把空白/标点当词铺满全文）",
            editor.MarkCount == 0 && editor.MarkWord.Length == 0);

        editor.DoubleClickWord(defName + 2);
        failures += Check("（前提）先把标记点亮", editor.MarkCount == 2);
        editor.HandleText("x");                            // 编辑 → 偏移全失效，必须自动清除
        failures += Check("编辑文本后分词高亮自动清除（否则会点亮一堆错位的词）",
            editor.MarkCount == 0 && editor.MarkWord.Length == 0);
        editor.Undo();
        failures += Check("撤销后文本逐字复原", string.Equals(doc.Text, src, StringComparison.Ordinal));

        editor.DoubleClickWord(defName + 2);
        editor.ClearMarksForTest();                        // 等价于「单击正文」那条路径
        failures += Check("单击正文清掉分词高亮", editor.MarkCount == 0);

        // 存一张图 —— 底色错色（比如主题少了 EditorMarkBackground 键 → 落到兜底色）
        // 只有看图才发现
        editor.DoubleClickWord(defName + 2);
        editor.FirstVisibleLine = 0;
        await PumpAsync();
        string markShot = Path.Combine(outDir, "marks.png");
        Capture(window, markShot);
        Console.WriteLine($"[selftest] 双击分词高亮 → {Path.GetFileName(markShot)}（{editor.MarkCount} 处命中）");
        editor.ClearMarksForTest();

        // ---- ⑥ 代码补全：打字 → 弹候选 → 确认 → 文本被替换 ----
        editor.AnchorOffset = calEnd;
        editor.CaretOffset = calEnd;
        failures += Check("还没打字时没有候选", editor.CompletionItems.Count == 0);

        failures += Check("继续输入 c 被接受", editor.HandleText("c"));
        // 先钉住「光标在哪、前缀是什么」—— 光标落偏一格的话下面几条断言会以各种方式蒙过去，
        // 而这一条会立刻红
        failures += Check("待补全的前缀正是 Calc（不是被空格截断的片段）",
            CompletionEngine.WordPrefix(doc, editor.CaretOffset).Prefix == "Calc");

        // 拷一份：CompletionItems 返回的是控件内部的活列表，AcceptCompletion 会把它清空
        IReadOnlyList<CompletionItem> items = [.. editor.CompletionItems];
        failures += Check("打字后弹出候选列表", items.Count > 0);
        failures += Check("这个前缀下只应有 Calculate 一个候选（数量本身就是断言）",
            items.Count == 1);
        failures += Check("候选里含文档内的函数 Calculate（标为函数）",
            items.Any(i => i.Text == "Calculate" && i.IsFunction));
        failures += Check("函数候选排在关键字之前（第一个就是它）",
            items.Count > 0 && items[0].Text == "Calculate");

        // 候选面板是自绘的，「弹出来了」和「画出来了」是两件事，只能看图
        await PumpAsync();
        string compShot = Path.Combine(outDir, "completion.png");
        Capture(window, compShot);
        Console.WriteLine($"[selftest] 补全候选面板 → {Path.GetFileName(compShot)}（{items.Count} 个候选）");

        int wordStart = CompletionEngine.WordPrefix(doc, editor.CaretOffset).WordStart;
        editor.AcceptCompletion();
        failures += Check("确认函数候选后补出 ()",
            wordStart + nameLen + 2 <= doc.CharCount
            && doc.Text.Substring(wordStart, nameLen + 2) == "Calculate()");
        failures += Check("光标落在 () 之间", editor.CaretOffset == wordStart + nameLen + 1);
        failures += Check("确认后候选面板收起", editor.CompletionItems.Count == 0);

        // ---- ⑦ 右侧滑动导航条：几何 + 拖拽跳转 ----
        //
        // 这一节能测到纯逻辑层测不到的一半：OverviewSmoke 只断言「像素 ↔ 行号」的换算，
        // 而「控件真的把导航条摆在了右边缘」「拖它真的改了 FirstVisibleLine」只能在这里验证。
        editor.FirstVisibleLine = 0;
        await PumpAsync();

        EditorSurface.OverviewMetrics geo = editor.OverviewGeometry();
        int totalLines = doc.LineCount;
        int visibleLines = editor.VisibleLineCount;
        failures += Check($"文档（{totalLines} 行）比视口（{visibleLines} 行）长 → 导航条生效", geo.Valid);
        failures += Check($"导航条贴在控件右边缘（x={geo.TrackX:0.#} / 宽 {editor.Bounds.Width:0.#}）",
            geo.Valid && geo.TrackX > editor.Bounds.Width - 20 && geo.TrackX < editor.Bounds.Width);
        failures += Check("滑块高度不超过轨道、且不小于 0（受最小高度保护）",
            geo.Valid && geo.ThumbHeight > 0 && geo.ThumbHeight <= geo.TrackHeight);
        failures += Check("首行在文首 → 滑块贴顶",
            geo.Valid && Math.Abs(geo.ThumbTop - geo.TrackY) < 0.001);

        editor.BeginOverviewDragForTest(geo.ThumbTop + geo.ThumbHeight / 2);
        failures += Check("按下滑块 → 进入拖拽态", editor.IsOverviewDragging);

        editor.HandleOverviewDrag(geo.TrackY + geo.TrackHeight);
        failures += Check($"拖到最底 → 首行落到最后可滚位（{editor.FirstVisibleLine} / {totalLines - visibleLines}）",
            editor.FirstVisibleLine == Math.Max(0, totalLines - visibleLines));

        editor.HandleOverviewDrag(geo.TrackY);
        failures += Check("拖回最顶 → 首行归 0", editor.FirstVisibleLine == 0);

        double midTop = geo.TrackY + (geo.TrackHeight - geo.ThumbHeight) / 2;
        editor.HandleOverviewDrag(midTop + geo.ThumbHeight / 2);
        int expectedMid = Math.Max(0, totalLines - visibleLines) / 2;
        failures += Check($"拖到轨道正中 → 首行约在中间（{editor.FirstVisibleLine} ≈ {expectedMid}）",
            Math.Abs(editor.FirstVisibleLine - expectedMid) <= 1);

        editor.EndOverviewDragForTest();
        failures += Check("松开 → 退出拖拽态（否则之后滚轮一动就会跳回拖拽位置）",
            !editor.IsOverviewDragging);

        // 松开后继续调 HandleOverviewDrag 不该再动首行 —— 防止「拖完手指离开还在跟手」
        int afterRelease = editor.FirstVisibleLine;
        editor.HandleOverviewDrag(geo.TrackY + geo.TrackHeight);
        failures += Check("松开后再拖动无效（拖拽态被正确清掉）", editor.FirstVisibleLine == afterRelease);

        editor.FirstVisibleLine = 0;
        await PumpAsync();
        string navShot = Path.Combine(outDir, "overview.png");
        Capture(window, navShot);
        Console.WriteLine($"[selftest] 右侧导航条 → {Path.GetFileName(navShot)}" +
                          $"（{totalLines} 行 / 可视 {visibleLines} 行，滑块 {geo.ThumbHeight:0.#} px）");

        // ---- ⑧ 右键菜单：项序 + 灰显条件 ----
        //
        // 灰显条件全靠运行时状态，写错了不会报错 —— 只会让用户点到一个不该能点的菜单项。
        // 项序也一并钉住：它是照 showEditorMenu 逐条对齐的，顺手改顺序就等于改了肌肉记忆。
        editor.FirstVisibleLine = 0;
        failures += Check("菜单项与 showEditorMenu 同序：复制/剪切/粘贴/全选/在命令提示符中打开/跳转到定义",
            editor.ContextMenuHeaders().SequenceEqual(
                ["复制", "剪切", "粘贴", "全选", "在命令提示符中打开", "跳转到定义"],
                StringComparer.Ordinal));

        // 无选区 + 光标在标识符上
        editor.CaretOffset = callName + 1;
        editor.AnchorOffset = editor.CaretOffset;
        editor.ContextRequestForTest(editor.PointOfOffsetForTest(callName + 1));
        failures += Check("无选区 → 复制/剪切灰掉",
            !editor.ContextMenuEnabled("复制") && !editor.ContextMenuEnabled("剪切"));
        failures += Check("光标在标识符上 → 跳转到定义可用", editor.ContextMenuEnabled("跳转到定义"));

        // 有选区，右键落在选区内 → 保留选区（这是原版 placeCaretForContext 的关键分支）
        editor.SelectRange(defName, defName + nameLen);
        editor.ContextRequestForTest(editor.PointOfOffsetForTest(defName + 1));
        failures += Check("有选区 → 复制/剪切可用",
            editor.ContextMenuEnabled("复制") && editor.ContextMenuEnabled("剪切"));
        failures += Check("右键落在选区内 → 选区被保留（不会被这一下点没）",
            editor.AnchorOffset == defName && editor.CaretOffset == defName + nameLen);
        failures += Check("选区里的整词也可作为跳转目标", editor.ContextMenuEnabled("跳转到定义"));

        // 右键落在选区外 → 光标移到点击处、选区清掉
        int far = doc.LineStartOffset(3);
        editor.ContextRequestForTest(editor.PointOfOffsetForTest(far));
        failures += Check("右键落在选区外 → 光标移到点击处并清掉选区",
            !editor.HasSelectionNow && editor.CaretOffset == far);

        // 光标落在注释的 '/' 上（不是标识符）→ 跳转灰掉
        editor.CaretOffset = 0;
        editor.AnchorOffset = 0;
        editor.ContextRequestForTest(editor.PointOfOffsetForTest(0));
        failures += Check("光标不在标识符上 → 跳转到定义灰掉", !editor.ContextMenuEnabled("跳转到定义"));

        // ---- 收尾：还原设置、关掉临时标签、把选中标签放回去 ----
        EditorSettings.AutoComplete = savedAutoComplete;
        vm.FindMatchCase = savedMatchCase;
        vm.FindQuery = string.Empty;
        vm.CloseFindCommand.Execute(null);

        LeaveTempTab(vm, tab, original);
        await PumpAsync();

        return failures;
    }

    /// <summary>关掉自检用的临时标签，并把选中标签还原到自检前的那一个。</summary>
    private static void LeaveTempTab(MainWindowViewModel vm, DocumentTabViewModel temp, DocumentTabViewModel? original)
    {
        vm.CloseTabCommand.Execute(temp);
        if (original is not null && vm.Tabs.Contains(original)) vm.SelectedTab = original;
    }

    /// <summary>把 UI 线程上排队的派发工作跑完（属性变更 → 绑定回流 → 重排）。</summary>
    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(60);
    }

    private static int Check(string name, bool ok)
    {
        Console.WriteLine($"[selftest]   {(ok ? "✔" : "✘")} {name}");
        return ok ? 0 : 1;
    }

    // ---------------- 工具层：Ctrl+滚轮 / 单实例 / 终端 / 文件关联 ----------------

    /// <summary>
    /// 「工具」这一批能力的冒烟。三块各自的重点不一样：
    ///
    /// ① **Ctrl+滚轮调字号** —— 必须打**真实控件**。这段逻辑住在 EditorSurface 里，与它
    ///    共用状态的还有光标可见性、行高缓存、状态栏的字号镜像；纯逻辑测不到
    ///    「字号改了但状态栏还停在旧值」这种接线问题。顺带钉住一条容易写错的优先级：
    ///    Ctrl 分支必须排在 Shift 之前，否则 Ctrl+Shift+滚轮 会被 Shift 抢去横向滚动。
    ///
    /// ② **单实例 + 文件转发** —— 这一块的价值几乎全在「机制本身」上：互斥体判重、
    ///    管道收发、消息编解码。所以这里连**完整的收发回路**都跑一遍（起监听 → 发 → 收到），
    ///    而不只是断言两个纯函数。特别要钉住三条容易悄悄退化的性质：
    ///      · 「句柄挂住」这件事真的生效（互斥体被 GC 掉 = 单实例保护凭空失效）；
    ///      · 空列表能正确往返（= 「只把窗口叫到前面来」这条路，不是「打开 0 个文件」的意外）；
    ///      · 消息头异常会抛异常，而不是被当成空消息静默通过。
    ///
    /// ③ **终端 / 文件关联** —— 这两块**不能真跑**（会去起终端、写注册表），所以只断言
    ///    纯函数与清单结构。这仍然有意义：`xdg-mime` 与 macOS 那两段分支在本机永远走不到，
    ///    结构断言是它们唯一的保护。文件关联那节还专门钉住了「不抢默认关联」这个设计决策 ——
    ///    哪天有人顺手把扩展名注册成默认关联，这条会立刻红。
    /// </summary>
    private static async Task<int> ToolsSmokeAsync(MainWindow window, MainWindowViewModel vm)
    {
        await PumpAsync();

        DocumentTabViewModel? tab = vm.SelectedTab;
        EditorSurface? editor = tab is null
            ? null
            : window.GetVisualDescendants().OfType<EditorSurface>()
                .FirstOrDefault(e => ReferenceEquals(e.Document, tab.Document));

        int failures = 0;
        failures += WheelSmoke(editor, vm);
        failures += ChromeSmoke(window, vm);
        failures += await ShellThemeSmokeAsync(window, vm);
        failures += await SingleInstanceSmokeAsync();
        failures += TerminalSmoke();
        failures += FileAssociationSmoke();
        return failures;
    }

    // ---- ①b 窗口外框：真实窗口 + 「换主题就重施」这条接线 ----

    /// <summary>
    /// 纯逻辑那节验的是<em>颜色对不对</em>，这节验的是<em>接线通不通</em>：
    /// 主题一换，MainWindow 有没有真的把新的三个颜色重新喂给系统。
    /// 做法就是切主题后读 <see cref="MainWindow.ChromeState"/> —— 它只在 ApplyChrome 里被写，
    /// 所以「值跟着主题变」这件事本身就证明了订阅没断。
    /// </summary>
    private static int ChromeSmoke(MainWindow window, MainWindowViewModel vm)
    {
        Console.WriteLine("[selftest] --- 窗口外框：真实窗口接线 ---");

        int failures = 0;

        foreach (ThemeDescriptor theme in ThemeService.Catalog)
        {
            vm.ApplyThemeCommand.Execute(theme.Name);
            ChromeResult r = window.ChromeState;

            if (OperatingSystem.IsWindows())
                failures += Check($"{theme.Name}：已施加到真实窗口（{r.AttributesSet} 项生效）",
                    r.Applied && r.AttributesSet >= 1);
            else
                failures += Check($"{theme.Name}：非 Windows 如实不做（不假装成功）", !r.Applied);

            // 深色标志与三个颜色是**独立**的两件事：它管标题栏按钮字形的明暗。
            // 方向搞反了会出现「浅底 + 浅字形」，肉眼上就是「最小化/关闭按钮看不见」。
            failures += Check($"{theme.Name}：字形明暗取自主题（{(theme.IsLight ? "浅色标题栏 → 深色字形" : "深色标题栏 → 浅色字形")}）",
                r.DarkMode == !theme.IsLight);
        }

        vm.ApplyThemeCommand.Execute(ThemeService.DefaultThemeName);
        Console.WriteLine($"[selftest] 外框（{ThemeService.DefaultThemeName}）：{window.ChromeState.Detail}");
        return failures;
    }

    // ---- ①c 外壳跟随主题：钉住「App.axaml 遮蔽合并字典」那个坑 ----

    /// <summary>
    /// 这节存在的唯一理由是那个**看代码看不出来**的坑：
    /// Avalonia 的资源查找里，**父字典自己持有的键优先于它的 MergedDictionaries**。
    /// ThemeService 把主题字典塞进 <c>Application.Resources.MergedDictionaries</c>，
    /// 于是只要 App.axaml 里恰好也定义了同名键（原本确实定义了 EditorBackground 等 14 个，
    /// 理由是「设计期与首帧不缺资源」），主题值就会被**永久遮住** ——
    /// <c>{DynamicResource EditorBackground}</c> 恒等于 App.axaml 里的那个默认色 #282C34。
    ///
    /// 为什么能藏很久：编辑器正文是**自绘**的，它直接调 <c>ThemeService.Brush</c> 取色，
    /// 根本不走资源系统，所以「正文颜色」一直是对的；只有外壳（窗口底色、状态栏、
    /// 分隔条、查找条）走 DynamicResource，它们**默默停在默认色上**。
    /// 等系统标题栏按主题上色之后，「浅色外框 + 一整块深色内容」的割裂才把它暴露出来。
    ///
    /// ★ 必须**对 5 套主题逐一验**，不能只验默认主题：原本的默认值 #282C34 恰好就是
    ///   One Dark Pro 的底色 —— 只验默认主题的话，这个 bug 会全绿通过。
    /// </summary>
    private static async Task<int> ShellThemeSmokeAsync(MainWindow window, MainWindowViewModel vm)
    {
        Console.WriteLine("[selftest] --- 外壳跟随主题（App.axaml 不再遮蔽合并字典） ---");

        if (ThemeService.Current is not { } themes)
        {
            Console.WriteLine("[selftest]   ✘ 没有主题服务");
            return 1;
        }

        // 兜底色故意用洋红 —— 一旦打印出这个颜色就说明「字典整个没取到」。
        // 注意：期望值必须**在 Apply 之后**取，ColorOf 读的是当前已装入的字典。
        const string Probe = "#FF00FF";
        Color wantBg = Colors.Magenta;
        Color wantGutter = Colors.Magenta;

        int failures = 0;

        // 用 Named<T> 而不是 x:Name 生成的字段：后者可见性随生成器版本变，
        // 而且「名字写错」在这里会退化成 null 引用而不是一条红色断言。
        // 所以先钉一条「外壳控件都在」，后面才敢直接读它们的 Background。
        Border? statusBar = Named<Border>(window, "StatusBar");
        GridSplitter? splitter = Named<GridSplitter>(window, "ColumnSplitter");
        Panel? treeHost = Named<Panel>(window, "TreeHost");
        TabControl? tabsHost = Named<TabControl>(window, "TabsHost");
        failures += Check("外壳控件都在（StatusBar / ColumnSplitter / TreeHost / TabsHost 的 x:Name 没被改掉）",
            statusBar is not null && splitter is not null && treeHost is not null && tabsHost is not null);
        if (statusBar is null || splitter is null || treeHost is null || tabsHost is null)
        {
            // 这条一红，后面所有断言都会「静默不跑」（方法提前 return），
            // 所以必须把现场打出来：可视树里到底有哪些带名字的控件。
            string names = string.Join(", ", window.GetVisualDescendants().OfType<Control>()
                .Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)));
            Console.WriteLine($"[selftest]   可视树里带名字的控件：{names}");
            return failures;
        }

        foreach (ThemeDescriptor theme in ThemeService.Catalog)
        {
            vm.ApplyThemeCommand.Execute(theme.Name);
            await PumpAsync();

            // 期望值取自 ThemeService（绕过资源系统，直接查字典）：
            // 这正是「编辑器正文」看到的那个值，外壳必须与它一致。
            wantBg = themes.ColorOf("EditorBackground", Probe);
            wantGutter = themes.ColorOf("EditorGutterBackground", Probe);

            Color windowBg = SolidColor(window.Background);
            Color statusBg = SolidColor(statusBar.Background);
            Color splitterBg = SolidColor(splitter.Background);

            failures += Check($"{theme.Name}：窗口底色 = 主题 EditorBackground（{Hex(windowBg)} ← {Hex(wantBg)}）",
                windowBg == wantBg);
            failures += Check($"{theme.Name}：状态栏底色 = 主题 EditorGutterBackground（{Hex(statusBg)} ← {Hex(wantGutter)}）",
                statusBg == wantGutter);
            failures += Check($"{theme.Name}：分隔条底色 = 主题 EditorGutterBackground（{Hex(splitterBg)}）",
                splitterBg == wantGutter);

            // 左侧栏与标签栏**不能自带写死的底色**：它们本来就是「让窗口底色透出来」。
            // 一旦有人给它们写死一个颜色，主题就被局部盖住了 —— 而这正是本轮要根治的那类 bug
            // （原来的 App.axaml 就是因为定义了默认色，把整个外壳钉死在 #282C34 上）。
            failures += Check($"{theme.Name}：左侧栏没有写死底色（让窗口底色透出）",
                treeHost.Background is null);

            // 标签栏**故意不在这里断言**：实测 <c>TabControl.Background</c> 在 5 套主题下恒为 #FFFFFF
            // （Fluent 的默认值），但它的模板根本**不绘制**这个属性 ——
            // 真实截图里标签栏画的就是主题底色（selftest/theme-*.png 里那条就是 #282C34 / #FAFAFA / …）。
            // 拿一个不参与绘制的属性去断言，只会得到一条**永远红的假警报**，
            // 而假警报比没有断言更糟：它会让人开始无视红色。
            // 所以这里只打印留痕，像素结论交由 spikes/winshot/probe_tabs.py 在截图上验。
            Console.WriteLine($"[selftest]   · 标签栏：Background={DescribeBrush(tabsHost.Background)}（不参与绘制，仅供对照）");

            // 外壳还有一半不是我们自己的键：菜单 / TreeView / TabControl 的默认底色来自
            // FluentTheme，它只认 RequestedThemeVariant。ThemeService.Apply 里那句赋值没了，
            // One Light 下左侧文件树会继续是深色 —— 而那不经过任何主题键，上面三条抓不到。
            ThemeVariant? wantVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
            failures += Check($"{theme.Name}：Fluent 控件的 ThemeVariant 方向正确（{wantVariant}）",
                Application.Current?.RequestedThemeVariant == wantVariant);

            Console.WriteLine($"[selftest]   · 外壳：窗口 {Hex(windowBg)} / 状态栏 {Hex(statusBg)} / 分隔条 {Hex(splitterBg)}");
        }

        vm.ApplyThemeCommand.Execute(ThemeService.DefaultThemeName);
        return failures;
    }

    /// <summary>把 IBrush 掏成颜色；拿不到（null 或非纯色）时回一个不可能撞上的洋红。</summary>
    private static Color SolidColor(IBrush? brush)
        => brush is ISolidColorBrush s ? s.Color : Colors.Magenta;

    /// <summary>
    /// 按名字找控件，两条路都走：
    /// ① <c>FindControl</c> —— 走名称作用域（XAML 里 `x:Name` 的正规通道）；
    /// ② 可视树遍历 —— 兜底，按 <c>Control.Name</c> 线性找。
    /// 两条路都留着，是因为**它们失败的原因完全不同**：前者在名称作用域没建立时失效，
    /// 后者在控件还没进可视树（虚拟化、折叠、模板未实例化）时失效。
    /// 外壳控件两种情形都可能碰上，只留一条会给以后留一颗哑雷。
    /// </summary>
    private static T? Named<T>(MainWindow window, string name) where T : Control
        => window.FindControl<T>(name)
           ?? window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>给日志看的画刷描述：纯色给十六进制，null 给「透明」，其余给类型名。</summary>
    private static string DescribeBrush(IBrush? brush)
        => brush is ISolidColorBrush s ? Hex(s.Color) : brush is null ? "透明" : brush.GetType().Name;

    // ---- ① Ctrl + 滚轮 ----

    private static int WheelSmoke(EditorSurface? editor, MainWindowViewModel vm)
    {
        Console.WriteLine("[selftest] --- Ctrl+滚轮调字号 / 滚轮修饰键冒烟 ---");
        int failures = 0;

        if (editor is null)
        {
            Console.WriteLine("[selftest]   跳过（没有找到编辑区）");
            return 0;
        }

        double saved = EditorSettings.FontSize;
        try
        {
            // 固定一个起点，断言才有确定值
            EditorSettings.FontSize = 13.0;

            failures += Check("Ctrl+滚轮上滚 → 字号 +1（并消费掉这次滚轮）",
                editor.HandleWheel(+1, KeyModifiers.Control) && Math.Abs(EditorSettings.FontSize - 14.0) < 0.001);

            failures += Check("Ctrl+滚轮下滚 → 字号 -1",
                editor.HandleWheel(-1, KeyModifiers.Control) && Math.Abs(EditorSettings.FontSize - 13.0) < 0.001);

            // 状态栏读的是 VM 上的镜像；不订阅 EditorSettings.Changed 的话它会一直停在旧值
            failures += Check("Ctrl+滚轮后 VM 的字号镜像同步（否则状态栏停在旧值）",
                Math.Abs(vm.EditorFontSize - EditorSettings.FontSize) < 0.001);

            EditorSettings.FontSize = EditorSettings.MaxFontSize;
            editor.HandleWheel(+1, KeyModifiers.Control);
            failures += Check($"Ctrl+滚轮在 MaxFontSize({EditorSettings.MaxFontSize}) 处被夹住",
                Math.Abs(EditorSettings.FontSize - EditorSettings.MaxFontSize) < 0.001);

            EditorSettings.FontSize = EditorSettings.MinFontSize;
            editor.HandleWheel(-1, KeyModifiers.Control);
            failures += Check($"Ctrl+滚轮在 MinFontSize({EditorSettings.MinFontSize}) 处被夹住",
                Math.Abs(EditorSettings.FontSize - EditorSettings.MinFontSize) < 0.001);

            // 不按修饰键时绝不能改字号 —— 这是最容易回归的一条
            EditorSettings.FontSize = 13.0;
            bool plain = editor.HandleWheel(-1, KeyModifiers.None);
            failures += Check("普通滚轮只滚动、不动字号",
                plain && Math.Abs(EditorSettings.FontSize - 13.0) < 0.001);

            // Ctrl+Shift+滚轮 同时满足两个分支 → 必须归 Ctrl（调字号），不能被 Shift 抢走
            EditorSettings.FontSize = 13.0;
            editor.HandleWheel(+1, KeyModifiers.Control | KeyModifiers.Shift);
            failures += Check("Ctrl+Shift+滚轮 归 Ctrl（调字号），不被 Shift 抢去横向滚动",
                Math.Abs(EditorSettings.FontSize - 14.0) < 0.001);

            // Shift+滚轮：改的是水平偏移，且不动字号
            EditorSettings.FontSize = 13.0;
            editor.HorizontalOffset = 0;
            bool shifted = editor.HandleWheel(-1, KeyModifiers.Shift);
            failures += Check("Shift+滚轮改水平偏移（不是字号）",
                shifted && editor.HorizontalOffset > 0 && Math.Abs(EditorSettings.FontSize - 13.0) < 0.001);
            editor.HorizontalOffset = 0;

            failures += Check("滚轮增量为 0 时不消费（不产生无意义的空操作）",
                !editor.HandleWheel(0, KeyModifiers.Control) && !editor.HandleWheel(0, KeyModifiers.None));
        }
        finally
        {
            // 字号是全局的、而且后面还有截图要拍 —— 必须还原
            EditorSettings.FontSize = saved;
            editor.HorizontalOffset = 0;
        }

        return failures;
    }

    // ---- ② 单实例 + 文件转发 ----

    private static async Task<int> SingleInstanceSmokeAsync()
    {
        Console.WriteLine("[selftest] --- 单实例 / 文件转发冒烟 ---");
        int failures = 0;

        // 每次用全新名字，避免和「真在跑着的那个 LiteReader」互相干扰
        string tag = Guid.NewGuid().ToString("N")[..10];
        string mutexName = $"LiteReader.SelfTest.{tag}";
        string pipeName = $"LiteReader.SelfTest.{tag}";

        // 判重
        failures += Check("全新名字 → 探测为「没被占用」", !SingleInstance.ProbeNameTaken(mutexName));
        failures += Check("首次判定 → 本进程是第一实例", SingleInstance.IsFirstInstance(mutexName));
        failures += Check("同名字重复判定 → 仍取同一答案（走缓存，不重复建对象）",
            SingleInstance.IsFirstInstance(mutexName));
        // 这条钉的是「句柄必须被静态列表挂住」：一旦被 GC 回收，名字就没了，
        // 单实例保护会**凭空失效**而且完全不报错。
        failures += Check("持有中：该名字确实已被占用（句柄没被 GC 掉）",
            SingleInstance.ProbeNameTaken(mutexName));

        // 别人先创建过 → 判定为「不是第一实例」
        string taken = mutexName + ".taken";
        using (var holder = new Mutex(initiallyOwned: false, taken, out bool created))
        {
            failures += Check("外部创建该名字成功（前提成立）", created);
            failures += Check("名字已被别人创建 → 判定为「不是第一实例」",
                !SingleInstance.IsFirstInstance(taken));
        }

        // 消息编解码：故意塞进空格 / 中文 / 制表符 / 换行 —— 换行正是「不能用按行分隔」的理由
        string[] sample =
        [
            @"C:\Users\hujie\我的 文档\带 空格.cs",
            "a\tb.txt",
            "换\n行.md",
            "/home/u/源码/主程序.cpp",
        ];
        using (var ms = new MemoryStream())
        {
            SingleInstance.WritePayload(ms, sample);
            ms.Position = 0;
            List<string> back = SingleInstance.ReadPayload(ms);
            failures += Check("消息编解码往返一致（含空格 / 中文 / 制表符 / 换行）",
                back.SequenceEqual(sample, StringComparer.Ordinal));
        }

        using (var ms = new MemoryStream())
        {
            SingleInstance.WritePayload(ms, []);
            ms.Position = 0;
            failures += Check("空列表往返成功（= 「只把窗口叫到前面来」这条路）",
                SingleInstance.ReadPayload(ms).Count == 0);
        }

        // 消息头异常必须抛，不能被当成「打开 0 个文件」静默通过
        using (var bad = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]))
        {
            failures += Check("消息头超范围 → 抛 InvalidDataException（不静默当成空消息）",
                Throws<InvalidDataException>(() => SingleInstance.ReadPayload(bad)));
        }
        using (var empty = new MemoryStream())
        {
            // 空流抛的是 EndOfStreamException（BinaryReader.ReadInt32 读不满 4 字节），
            // 不是 InvalidDataException —— 两者都继承自 IOException，但**互不继承**，
            // 所以这里必须分开断言。对接收端来说两种都属于「这条连接不可信，等下一批」，
            // 由 Listener 的 catch 兜住。
            failures += Check("连消息头都没有 → 抛 EndOfStreamException（不静默当成空消息）",
                Throws<EndOfStreamException>(() => SingleInstance.ReadPayload(empty)));
        }

        // 完整的收发回路：起监听 → 发 → 收到
        var received = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var listener = SingleInstance.StartListener(p => received.TrySetResult(p), pipeName);
        try
        {
            string[] sent = [@"C:\中文 目录\a.cs", "/tmp/b.txt"];
            bool ok = await Task.Run(() => SingleInstance.TrySend(sent, pipeName, 3000));
            failures += Check("TrySend 连上监听端（第一实例还没起来时应靠重试兜住）", ok);

            Task done = await Task.WhenAny(received.Task, Task.Delay(3000));
            IReadOnlyList<string>? got = done == received.Task ? await received.Task : null;
            failures += Check("监听端收到的就是发出去的那一批（内容与顺序都一致）",
                got is not null && got.SequenceEqual(sent, StringComparer.Ordinal));

            // 连发两次都要能收到 —— 验证「一个连接一批消息、读完接着等下一条」的循环没写坏
            received = new TaskCompletionSource<IReadOnlyList<string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool ok2 = await Task.Run(() => SingleInstance.TrySend(["第二次.cs"], pipeName, 3000));
            Task done2 = await Task.WhenAny(received.Task, Task.Delay(3000));
            failures += Check("同一监听端能连续接收第二批（连接循环没写坏）",
                ok2 && done2 == received.Task && (await received.Task).SequenceEqual(new[] { "第二次.cs" }));
        }
        finally
        {
            listener.Dispose();
        }

        // 监听端关闭后必须索性返回 false，而不是把启动流程无限拖住
        bool after = await Task.Run(() => SingleInstance.TrySend(["x.cs"], pipeName, 400));
        failures += Check("监听端已释放 → TrySend 返回 false 且有界（不会把启动拖住）", !after);

        return failures;
    }

    // ---- ③ 终端 ----

    private static int TerminalSmoke()
    {
        Console.WriteLine("[selftest] --- 外部终端的候选清单冒烟 ---");
        int failures = 0;

        foreach (TerminalPlatform p in new[]
                 { TerminalPlatform.Windows, TerminalPlatform.Linux, TerminalPlatform.MacOS })
        {
            IReadOnlyList<TerminalCandidate> list = TerminalLauncher.CandidatesFor(p);
            string label = p switch
            {
                TerminalPlatform.Windows => "Windows",
                TerminalPlatform.MacOS => "macOS",
                _ => "Linux",
            };

            failures += Check($"{label}：候选非空", list.Count > 0);
            failures += Check($"{label}：没有重复的可执行名（重复的那个永远轮不到）",
                list.Select(c => c.Exe).Distinct(StringComparer.Ordinal).Count() == list.Count);
            failures += Check($"{label}：带目录参数的候选，格式串里都有 {{0}} 占位",
                list.Where(c => c.DirArgFormat is not null)
                    .All(c => c.DirArgFormat!.Contains("{0}", StringComparison.Ordinal)));
            failures += Check($"{label}：占位后面没有多余的格式化花括号（string.Format 会抛）",
                list.Where(c => c.DirArgFormat is not null)
                    .All(c => CountChar(c.DirArgFormat!, '{') == CountChar(c.DirArgFormat!, '}')));
        }

        // 与原版逐字对齐的顺序 —— 这条只在 Windows 上有意义，但正是我们在跑的平台
        failures += Check("Windows 候选次序与原版一致：wt.exe → powershell.exe → cmd.exe",
            TerminalLauncher.CandidatesFor(TerminalPlatform.Windows)
                .Select(c => c.Exe).SequenceEqual(["wt.exe", "powershell.exe", "cmd.exe"], StringComparer.Ordinal));
        failures += Check("当前平台的候选就是 CandidatesFor(当前平台)（没有分叉出第二份清单）",
            TerminalLauncher.Candidates().Select(c => c.Exe)
                .SequenceEqual(TerminalLauncher.CandidatesFor(TerminalLauncher.CurrentPlatform())
                    .Select(c => c.Exe), StringComparer.Ordinal));

        // 目录决策：文件所在目录 → 上次文件夹 → 主目录
        string dir = Path.GetTempPath();
        string file = Path.Combine(dir, "lite-reader-probe.cs");
        failures += Check("有当前文件 → 用文件所在目录（终端才有意义）",
            string.Equals(TerminalLauncher.ResolveDirectory(file, null), Path.GetDirectoryName(file),
                StringComparison.OrdinalIgnoreCase));
        failures += Check("没有当前文件 → 回落到上次打开的文件夹",
            string.Equals(TerminalLauncher.ResolveDirectory(null, dir), dir, StringComparison.OrdinalIgnoreCase));
        failures += Check("文件与文件夹都不存在 → 仍返回一个真实存在的目录（不会返回空串）",
            Directory.Exists(TerminalLauncher.ResolveDirectory(
                Path.Combine(dir, "不存在", "x.cs"), Path.Combine(dir, "也不存在"))));

        return failures;
    }

    // ---- ③ 文件关联 ----------------

    private static int FileAssociationSmoke()
    {
        Console.WriteLine("[selftest] --- 文件关联（纯函数）冒烟 ---");
        int failures = 0;

        failures += Check($"扩展名表非空（当前 {FileAssociation.Extensions.Length} 项）",
            FileAssociation.Extensions.Length >= 20);
        failures += Check("扩展名表：全部以 . 开头且已小写",
            FileAssociation.Extensions.All(e => e.StartsWith('.') && e == e.ToLowerInvariant()));
        failures += Check("扩展名表：无重复（重复注册同一处，说明表本身写错了）",
            FileAssociation.Extensions.Distinct(StringComparer.Ordinal).Count() == FileAssociation.Extensions.Length);

        const string exe = @"C:\Program Files\LiteReader\LiteReader.exe";
        failures += Check("Windows 命令行：exe 与 %1 都加引号（否则带空格的路径会被拆成两个参数）",
            FileAssociation.WindowsOpenCommand(exe) == $"\"{exe}\" \"%1\"");
        failures += Check("Windows 注册项写在 HKCU\\Software\\Classes 下（不是 HKCR → 不需要管理员）",
            FileAssociation.WindowsAppKey.StartsWith(@"Software\Classes\", StringComparison.Ordinal));
        failures += Check("每个扩展名都有对应的 OpenWithList 键路径",
            FileAssociation.Extensions.All(e =>
                FileAssociation.WindowsOpenWithKey(e).EndsWith(
                    @"\OpenWithList\LiteReader.exe", StringComparison.Ordinal)));
        // 设计决策：只进「打开方式」列表，绝不抢默认关联（否则会顶掉用户的 VS Code / Notepad++）
        failures += Check("没有任何扩展名被注册成默认关联（只加进「打开方式」）",
            FileAssociation.Extensions.All(e =>
                FileAssociation.WindowsOpenWithKey(e) != $@"Software\Classes\{e}"));
        // 与 C++ 单文件版**同名**（都是 Applications\LiteReader.exe）→ 后注册的会覆盖前一个。
        // 这本身合理（同一程序只该有一个条目），但报告里必须写明覆盖了谁，不能悄悄改。
        failures += Check("注册键名与 C++ 单文件版一致 → 会覆盖它（报告里会写明原值）",
            FileAssociation.WindowsAppKey.EndsWith(@"\Applications\LiteReader.exe", StringComparison.Ordinal));

        string entry = FileAssociation.LinuxDesktopEntry(exe);
        string[] lines = entry.Split('\n');
        failures += Check("desktop 文件：有 [Desktop Entry] 段头",
            lines.Contains("[Desktop Entry]", StringComparer.Ordinal));
        failures += Check("desktop 文件：Exec 用 %F（= 支持一次传多个文件，与命令行一致）",
            lines.Any(l => l == $"Exec=\"{exe}\" %F"));
        string? mime = lines.FirstOrDefault(l => l.StartsWith("MimeType=", StringComparison.Ordinal));
        failures += Check("desktop 文件：MimeType 存在且以分号结尾（XDG 规定的写法）",
            mime is not null && mime.EndsWith(';'));
        failures += Check("desktop 文件：每行都从第 0 列开始（缩进会让 desktop 解析器认不出来）",
            lines.All(l => l.Length == 0 || !char.IsWhiteSpace(l[0])));
        failures += Check("desktop 文件：换行只用 LF（\\r 会让部分解析器把整行当成非法值）",
            !entry.Contains('\r'));
        failures += Check("desktop 文件名与 xdg-mime 用的是同一个 ID",
            FileAssociation.DesktopId.EndsWith(".desktop", StringComparison.Ordinal)
            && FileAssociation.LinuxDesktopPath().EndsWith(FileAssociation.DesktopId, StringComparison.Ordinal));

        failures += Check("可执行文件路径不是空串（单文件发布下 Assembly.Location 会是空串，这里用的是 ProcessPath）",
            FileAssociation.ExecutablePath().Length > 0);

        return failures;
    }

    private static int CountChar(string s, char c)
    {
        int n = 0;
        foreach (char x in s) if (x == c) n++;
        return n;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch { return false; }
    }

    // ---------------- 选区/光标渲染截图 ----------------
    private static int SelectionCapture(MainWindow window, MainWindowViewModel vm, string outDir)
    {
        DocumentTabViewModel? tab = vm.SelectedTab;
        if (tab is null) return 0;

        var doc = tab.Document;
        int a = doc.LineStartOffset(Math.Min(7, doc.LineCount - 1));
        int b = doc.LineEndOffset(Math.Min(12, doc.LineCount - 1));
        tab.AnchorOffset = a;
        tab.CaretOffset = b;
        tab.FirstVisibleLine = 0;

        vm.ApplyThemeCommand.Execute(ThemeService.DefaultThemeName);
        Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).Wait();

        string path = Path.Combine(outDir, "selection.png");
        Capture(window, path);
        Console.WriteLine($"[selftest] 选区渲染 → {Path.GetFileName(path)}（第 8–13 行被选中）");
        return 0;
    }

    // ---------------- 工具 ----------------

    private static int CountLines(string s)
    {
        int n = 1;
        foreach (char c in s) if (c == '\n') n++;
        return n;
    }

    private static void Capture(Window window, string path)
    {
        int w = (int)Math.Ceiling(window.Bounds.Width);
        int h = (int)Math.Ceiling(window.Bounds.Height);
        if (w <= 0 || h <= 0) { Console.WriteLine("[selftest] 尺寸异常，跳过"); return; }

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);
        using FileStream fs = File.Create(path);
        rtb.Save(fs);
    }

    /// <summary>把当前主题里某个键的真实取值描述出来（用于抓「退回兜底色」这种情况）。</summary>
    private static string DescribeColor(string key)
    {
        if (ThemeService.Current is not { } themes) return "(无主题服务)";
        // 故意传一个不可能撞上的兜底色，这样一旦返回它就知道字典没取到
        Avalonia.Media.IBrush b = themes.Brush(key, Avalonia.Media.Colors.Magenta);
        return b is Avalonia.Media.ISolidColorBrush s ? s.Color.ToString() : b.GetType().Name;
    }

    /// <summary>取 --key 后面的第一个值；找不到返回 null。</summary>
    private static string? ArgValue(string[] args, string key, bool skipIfStartsWithDash = false)
    {
        int i = Array.IndexOf(args, key);
        if (i < 0 || i + 1 >= args.Length) return null;
        string v = args[i + 1];
        if (skipIfStartsWithDash && v.StartsWith('-')) return null;
        return v;
    }
}
