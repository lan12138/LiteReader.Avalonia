// LiteReader · Avalonia 跨平台版 —— 文档模型
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 由「只读」升级为「可编辑」：新增 Replace / Insert / Remove，
//      并把行索引重建从「每次编辑全文重扫」改为「只重切受影响区间 + 尾段整体平移」。
//      19.3 MB / 10 万行文件下，全文重扫约 20 ms/次按键，增量更新约 1–3 ms，是打字手感的关键。
//   2) 新增操作式撤销/重做栈（对照 LiteReader.cpp 的 EditStep：记录被替换区间、删除串、插入串），
//      并对「连续单字符输入 / 连续退格」做时间窗合并，避免一次打字产生几百步撤销。
//   3) 新增跨行块注释/字符串状态机（懒推进），供 SyntaxHighlighter 做续行着色。
//
// 职责：文本持有 + 行索引 + 编码识别与回写 + 编辑与历史。
// 跨平台注意点：
//   · 换行：Windows 常见 CRLF，Linux/macOS 是 LF。行索引只认 '\n'，CR 由行长度裁掉（CRLF 折叠）。
//     编辑时保持原文件的换行风格（Load 时探测 NewLine），避免在 Linux 上编辑 Windows 文件把 CRLF 打散。
//   · 编码：UTF-8 / UTF-8 BOM / UTF-16 BOM 直接判定；其余按 UTF-8 严格解码试探，
//     失败再回退 GBK(936)。GBK 依赖 CodePagesEncodingProvider，三个平台都可用（随共享框架分发）。

using System.Text;

namespace LiteReader.Models;

/// <summary>
/// 一行「起始处」的跨行着色状态。着色器把它当输入，行扫描完再写回作为下一行的输入。
/// 之所以要它：<c>/* ... */</c>、未闭合字符串、以及**括号嵌套深度**都必须跨行延续，
/// 否则整篇注释会被逐行误判成代码，彩虹括号也会每行从第 0 色重新开始。
/// </summary>
/// <param name="InComment">该行起始处是否处于块注释中。</param>
/// <param name="InString">该行起始处未闭合字符串的引号字符；'\0' 表示不在字符串中。</param>
/// <param name="Depth">
/// 该行起始处的括号嵌套深度（彩虹括号用）。跨行是必须的 ——
/// 一个跨越多行的函数体，其内部的括号应该接着外层深度继续变色，而不是从第 0 色重来。
/// </param>
public readonly record struct LineState(bool InComment, char InString, int Depth = 0)
{
    public static readonly LineState None = new(false, '\0', 0);
}


public sealed class DocumentModel
{
    static DocumentModel()
    {
        // 注册代码页编码（GBK/GB2312 等）。缺这一步 GetEncoding(936) 会抛异常。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // ---------------- 撤销/重做：操作式历史 ----------------
    //
    // 与「整篇快照」相比：一次按键只记「被替换区间 + 删除串 + 插入串」，
    // 大文件下内存占用与编辑规模成正比、与文件大小无关。
    private readonly record struct EditStep(int Start, string Del, string Ins);

    private enum EditKind { None, Insert, Backspace, Delete }

    private const int MaxUndoSteps = 2000;          // 超出后丢最早的步骤，避免长时间编辑后无界增长
    private const int CoalesceWindowMs = 700;       // 连续输入的合并时间窗

    private readonly List<EditStep> _undo = [];
    private readonly List<EditStep> _redo = [];
    private EditKind _lastKind = EditKind.None;
    private long _lastEditTick;
    private int _lastEditEnd;                       // 上一次单字符编辑的结束偏移（用于判断是否相邻）

    private string _text = string.Empty;

    // 行索引：容量（数组长度）与有效行数（_lineCount）分离 —— 编辑时就地更新，不再每次重新分配。
    private int[] _lineStart = new int[16];
    private int[] _lineLen = new int[16];
    private int _lineCount = 1;

    // 每行【起始】的跨行着色状态，按需推进（同 LiteReader.cpp 的 g_lineInBC/ensureLineState）
    private LineState[] _lineState = [LineState.None];
    private int _stateComputed = 1;                 // [0, _stateComputed) 区间的起始状态已就绪

    /// <summary>文档版本号：文本每变一次 +1，供渲染层判断缓存是否失效。</summary>
    public int Version { get; private set; }

    /// <summary>文本或脏标记发生变化时触发（渲染层订阅后重绘）。</summary>
    public event Action? Changed;

    public string FilePath { get; private set; } = string.Empty;
    public string FileName => string.IsNullOrEmpty(FilePath) ? "(未命名)" : Path.GetFileName(FilePath);
    public string EncodingName { get; private set; } = "UTF-8";
    public bool HasBom { get; private set; }

    /// <summary>
    /// 语言模式（按扩展名判定，见 LanguageMode.cs）。
    /// 影响两件事：SQL 的 BEGIN/END/CASE 是否当块括号；代码补全给哪一套候选词。
    /// 可写 —— 但它同时决定跨行状态怎么算，所以一旦改变必须整份作废重算（见 setter）。
    /// </summary>
    public LanguageMode Language
    {
        get => _language;
        set
        {
            if (value == _language) return;
            _language = value;
            // 语言变了，之前按旧规则推出来的块括号/深度全部作废
            ResetTransientState();
            InvalidateFunctionNames();
            Version++;
            Changed?.Invoke();
        }
    }

    private LanguageMode _language = LanguageMode.Plain;

    /// <summary>上次保存/载入时的撤销栈深度。</summary>
    private int _cleanUndoDepth;

    /// <summary>
    /// 是否有未保存的改动。
    /// 用「撤销栈深度是否回到保存点」判断，而不是一个只置位不清除的脏标记 ——
    /// 这样「改了几处又全部撤销回原样」会正确地变回干净状态（LiteReader.cpp 是粘性标记，
    /// 撤销回去标题仍然带 *，这里顺手把那个小毛病修掉了）。
    /// </summary>
    public bool IsDirty => _undo.Count != _cleanUndoDepth;

    /// <summary>本文件使用的换行风格（Load 时探测；新文件按当前平台惯例）。</summary>
    public string NewLine { get; private set; } = Environment.NewLine;

    public int LineCount => _lineCount;
    public int CharCount => _text.Length;
    public string Text => _text;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---------------- 行索引读取 ----------------

    /// <summary>取第 index 行（不含换行符）。越界返回空。</summary>
    public ReadOnlySpan<char> GetLine(int index)
        => (uint)index < (uint)_lineCount ? _text.AsSpan(_lineStart[index], _lineLen[index]) : default;

    public int LineLength(int index)
        => (uint)index < (uint)_lineCount ? _lineLen[index] : 0;

    /// <summary>第 index 行首字符在全文中的偏移。</summary>
    public int LineStartOffset(int index)
        => (uint)index < (uint)_lineCount ? _lineStart[index] : 0;

    /// <summary>第 index 行末字符（不含换行）在全文中的偏移，即 [start, end) 的 end。</summary>
    public int LineEndOffset(int index) => LineStartOffset(index) + LineLength(index);

    /// <summary>第 index 行末尾是否带换行符（最后一行通常不带）。</summary>
    public bool LineHasNewline(int index) => LineEndOffset(index) < _text.Length;

    public int LineIndexOf(int charOffset)
    {
        if (_lineCount <= 1) return 0;
        if (charOffset <= 0) return 0;
        if (charOffset >= _text.Length) return _lineCount - 1;
        int lo = 0, hi = _lineCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineStart[mid] <= charOffset) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>偏移 → 行内列号。</summary>
    public int ColumnOf(int charOffset)
        => charOffset - LineStartOffset(LineIndexOf(charOffset));

    /// <summary>行 + 行内列号 → 全文偏移（列号会被钳到该行长度内）。</summary>
    public int OffsetOf(int line, int column)
    {
        if (line < 0) return 0;
        if (line >= LineCount) return _text.Length;
        return LineStartOffset(line) + Math.Clamp(column, 0, LineLength(line));
    }

    // ---------------- 装载 / 保存 ----------------

    public void SetText(string text, bool dirty = true)
    {
        _text = text ?? string.Empty;
        RebuildLineIndex();
        ResetTransientState();
        InvalidateFunctionNames();
        _undo.Clear();
        _redo.Clear();
        _lastKind = EditKind.None;
        _cleanUndoDepth = dirty ? -1 : 0;   // -1 表示「刚载入就被视为有改动」（新建未命名文档用）
        Version++;
        Changed?.Invoke();
    }

    /// <summary>从磁盘读入并识别编码。</summary>
    public void Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (string text, string encName, bool bom) = Decode(bytes);
        FilePath = path;
        EncodingName = encName;
        HasBom = bom;
        NewLine = DetectNewLine(text);
        Language = LanguageDetect.FromPath(path);
        SetText(text, dirty: false);   // SetText 会一并清空撤销历史
    }

    /// <summary>按原编码写回（不覆盖 BOM 判定结果）。</summary>
    public void Save(string? path = null)
    {
        string target = path ?? FilePath;
        if (string.IsNullOrEmpty(target)) throw new InvalidOperationException("未指定保存路径");

        Encoding enc = HasBom
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            : EncodingName switch
            {
                "GBK" => Encoding.GetEncoding(936),
                "UTF-16 LE" => new UnicodeEncoding(false, true),
                "UTF-16 BE" => new UnicodeEncoding(true, true),
                _ => new UTF8Encoding(false),
            };

        File.WriteAllText(target, _text, enc);
        FilePath = target;

        // 另存成别的扩展名时语言可能变了（.txt → .sql）。
        // 语言影响 BEGIN/END 是否算块括号，所以交给 Language 的 setter 去作废跨行状态。
        if (Language == LanguageMode.Plain) Language = LanguageDetect.FromPath(target);

        _cleanUndoDepth = _undo.Count;     // 当前状态即保存点
        Version++;
        Changed?.Invoke();
    }

    // ---------------- 编辑 ----------------

    public void Insert(int offset, string text) => Replace(offset, 0, text);

    public void Remove(int offset, int length) => Replace(offset, length, string.Empty);

    /// <summary>
    /// 把 [start, start+length) 替换为 insert，并压入撤销栈。
    /// 这是唯一的编辑入口 —— 插入/删除都走它，保证撤销栈与行索引永远同步。
    /// </summary>
    public void Replace(int start, int length, string insert)
    {
        insert ??= string.Empty;

        int oldLen = _text.Length;
        if (start < 0) start = 0;
        if (start > oldLen) start = oldLen;
        if (length < 0) length = 0;
        if (start + length > oldLen) length = oldLen - start;
        if (length == 0 && insert.Length == 0) return;

        RecordEdit(start, length, insert);

        // 行号必须在【改文本之前】算出来 —— LineIndexOf 依赖 _text.Length 做钳位，
        // 一旦先改了文本，纯删除场景会把偏移误钳到最后一行，行索引立刻错位。
        int oldCount = _lineCount;                                        // 恒 >= 1
        int firstLine = LineIndexOf(start);                               // start 之前的偏移没变，旧索引即可
        int lastLine = length > 0 ? LineIndexOf(start + length - 1) : firstLine;

        // ---- 文本替换：整串重建。19 MB 下 memcpy 约 2 ms，是可接受的代价；
        //      真正贵的是行索引重扫，已改为增量（见下）。----
        _text = string.Concat(_text.AsSpan(0, start), insert.AsSpan(), _text.AsSpan(start + length));
        int delta = insert.Length - length;

        // ---- 行索引增量更新 ----
        // 只有 [firstLine, lastLine] 这一段需要重新切分；
        // 该区间之后的所有行首偏移统一 +delta，一次数组平移即可，不必全文重扫。
        int regionStart = _lineStart[firstLine];
        bool hasTail = lastLine + 1 < oldCount;                            // 区间后面还有旧行
        int regionEndOld = hasTail ? _lineStart[lastLine + 1] : oldLen;     // 含 lastLine 的换行符
        int regionEndNew = regionEndOld + delta;

        ReadOnlySpan<char> region = _text.AsSpan(regionStart, regionEndNew - regionStart);

        // ⚠️ 这一段是整个索引更新里最容易错的地方，两个边界都要显式判断：
        //
        //   1) region 末尾若正好落在换行符上，则「换行之后那段内容」属于下一行，
        //      而下一行已经由 tail 原样搬运 —— 在这里再切一行就会重复计数
        //      （症状：每编辑一次行数就莫名其妙多一行）。
        //
        //   2) region 若不落在换行符上（即最后一个 '\n' 之后还有实体内容），
        //      那个「半行」在物理上与 tail 的首行是同一行 —— 必须把它俩合并，
        //      否则同样会多出一行（症状：删掉半行后行号从此错位一格）。
        bool regionEmpty = region.Length == 0;
        bool endsWithNewline = !regionEmpty && region[^1] == '\n';
        bool trailingIsPartial = !regionEmpty && !endsWithNewline;

        int newlineCount = 0;
        for (int i = 0; i < region.Length; i++)
            if (region[i] == '\n') newlineCount++;

        // 只有「没有 tail 接手」或「存在半行」时，末尾那段才自成一行
        bool keepTrailing = !hasTail || trailingIsPartial;
        int regionLines = newlineCount + (keepTrailing ? 1 : 0);

        int tailOld = lastLine + 1;
        int tailCount = oldCount - tailOld;
        // 半行与 tail 首行合并 → tail 实际只需搬 tailCount-1 条
        bool mergeTailFirst = trailingIsPartial && tailCount > 0;
        int tailKeep = tailCount - (mergeTailFirst ? 1 : 0);

        // 末尾那行的结尾 '\r' 该不该按 CRLF 折叠掉？判断标准只有一条：
        // 这个 '\r' 后面紧挨着的，是不是就是终止本行的那个 '\n'。
        //   a) 区间延伸到文本末尾 → '\r' 就是全文最后一个字符，按惯例（RefSplit / RebuildLineIndex）
        //      把行尾残留的 '\r' 折掉。
        //   b) 半行合并了 tail 的首行，且该首行是空行 → 终止符就是 tail 首行自己的换行符：
        //      · 裸 LF（'\n'）→ 区间末尾的 '\r' 与它组成 CRLF，要折；
        //      · CRLF（'\r\n'）→ 真正被折的是终止符自带的那个 '\r'，它在 tail 里已经算过，
        //        区间末尾的 '\r' 反而是行内字符，不能再折（否则这一行会莫名短一个字符）。
        //   c) 其余情况（tail 首行非空）→ '\r' 后面还有内容，是行内字符，不折。
        bool foldTrailingCr = !hasTail;
        int tailFirstLen = tailOld < oldCount ? _lineLen[tailOld] : 0;
        if (!foldTrailingCr && mergeTailFirst && tailFirstLen == 0)
            foldTrailingCr = regionEndNew >= _text.Length || _text[regionEndNew] == '\n';

        int newCount = firstLine + regionLines + tailKeep;

        // ---- 就地更新，不重新分配数组 ----
        // 刚开始这里是「每次编辑 new 两个 int[行数] 再整段拷贝」，10 万行文件下光是分配+拷贝
        // 就要 4~5 ms/次按键。改成容量与行数分离后就地搬移之后，常见的「行数不变」编辑
        // 退化成一次 tail 的 memmove（甚至完全不动），分配次数降为 0。
        EnsureLineCapacity(newCount);

        int tailDest = firstLine + regionLines;
        int tailSrc = mergeTailFirst ? tailOld + 1 : tailOld;
        if (tailKeep > 0)
        {
            // Array.Copy 允许源/目标重叠（语义同 memmove），插入与删除两个方向都安全
            Array.Copy(_lineStart, tailSrc, _lineStart, tailDest, tailKeep);
            Array.Copy(_lineLen, tailSrc, _lineLen, tailDest, tailKeep);
            for (int i = 0; i < tailKeep; i++) _lineStart[tailDest + i] += delta;
        }

        FillRegionLines(region, regionStart, _lineStart, _lineLen, firstLine, keepTrailing, foldTrailingCr);

        if (mergeTailFirst)
        {
            // region 的半行 + tail 首行 = 同一行：起始偏移已是半行的位置，
            // 把 tail 首行的长度并进来即可。
            _lineLen[tailDest - 1] += tailFirstLen;
        }

        _lineCount = newCount;

        // 脏标记不用显式置位：撤销栈深度变了，IsDirty 自然为真。

        // ---- 跨行着色状态失效：firstLine 自身的起始状态仍有效，其后的要重算 ----
        if (_stateComputed > firstLine + 1) _stateComputed = firstLine + 1;
        if (_stateComputed > LineCount) _stateComputed = LineCount;
        if (_stateComputed < 1) _stateComputed = 1;
        EnsureStateCapacity();

        Version++;
        Changed?.Invoke();
    }

    /// <summary>
    /// 把 region 按 '\n' 切分成行，写入 starts/lens 的 at 起始位置。返回切出的行数。
    /// </summary>
    /// <param name="keepTrailing">
    /// 是否把最后一个 '\n' 之后的那段内容也算作一行。区间尾部若正好落在换行符上，
    /// 且后面还有旧行（tail）时，必须传 false —— 否则会凭空多出一行（详见 Replace 里的注释）。
    /// </param>
    /// <param name="foldTrailingCr">
    /// 末尾那行是否要按 CRLF 折叠掉最后一个 '\r'。只有该行确实是「整个文档的最后一行」
    /// （区间一直延伸到文本末尾）时才为 true —— 否则那个 '\r' 后面还跟着 tail 的内容，
    /// 它是行内字符而不是行尾符，折叠它会把这一行莫名削短一个字符。
    /// </param>
    private static int FillRegionLines(ReadOnlySpan<char> region, int baseOffset,
                                       int[] starts, int[] lens, int at, bool keepTrailing, bool foldTrailingCr)
    {
        int k = 0, n = region.Length, start = 0;
        for (int i = 0; i < n; i++)
        {
            if (region[i] != '\n') continue;
            int len = i - start;
            if (len > 0 && region[i - 1] == '\r') len--;      // CRLF 折叠（行内，边界安全）
            starts[at + k] = baseOffset + start;
            lens[at + k] = len;
            k++;
            start = i + 1;
        }

        if (keepTrailing)
        {
            int lastLen = n - start;
            if (foldTrailingCr && lastLen > 0 && n > 0 && region[n - 1] == '\r') lastLen--;
            starts[at + k] = baseOffset + start;
            lens[at + k] = lastLen;
            k++;
        }
        return k;
    }

    // ---------------- 撤销 / 重做 ----------------

    private void RecordEdit(int start, int length, string insert)
    {
        if (_suppressHistory) return;   // 撤销/重做自身走 Replace，但不能再记一步历史

        long now = Environment.TickCount64;
        EditKind kind =
            (length == 0 && insert.Length > 0) ? EditKind.Insert :
            (insert.Length == 0 && length == 1) ? EditKind.Backspace :
            EditKind.None;              // 含换行的输入、删除选区、粘贴等：一律单独成步

        // 单字符输入/退格在时间窗内且位置相邻时合并：连续敲 "abcdef" 应该是一步撤销。
        // 换行、多字符粘贴、替换选区都不合并 —— 撤销粒度要跟人的心理模型一致。
        bool canMerge =
            kind != EditKind.None
            && kind == _lastKind
            && now - _lastEditTick <= CoalesceWindowMs
            && _undo.Count > 0
            && ((kind == EditKind.Insert && insert.Length == 1 && start == _lastEditEnd)
                || (kind == EditKind.Backspace && start + 1 == _lastEditEnd));

        if (canMerge)
        {
            EditStep last = _undo[^1];
            _undo[^1] = kind == EditKind.Insert
                ? last with { Ins = last.Ins + insert }
                : last with { Start = start, Del = _text.Substring(start, 1) + last.Del };
        }
        else
        {
            _undo.Add(new EditStep(start, _text.Substring(start, length), insert));
            if (_undo.Count > MaxUndoSteps) _undo.RemoveRange(0, _undo.Count - MaxUndoSteps);
        }

        _redo.Clear();
        _lastKind = kind;
        _lastEditTick = now;
        _lastEditEnd = kind == EditKind.Backspace ? start : start + insert.Length;   // 编辑后光标落点
    }

    /// <summary>
    /// 撤销：取最近一步，删掉它插入的串、填回它删除的串，并压入重做栈。
    /// </summary>
    /// <returns>撤销后光标应处的偏移；没有可撤销内容时返回 -1。</returns>
    public int Undo()
    {
        if (_undo.Count == 0) return -1;
        EditStep s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        ApplyStep(s.Start, s.Ins.Length, s.Del);
        _redo.Add(s);
        _lastKind = EditKind.None;
        return s.Start + s.Del.Length;
    }

    /// <summary>重做：反向操作。</summary>
    /// <returns>重做后光标应处的偏移；没有可重做内容时返回 -1。</returns>
    public int Redo()
    {
        if (_redo.Count == 0) return -1;
        EditStep s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        ApplyStep(s.Start, s.Del.Length, s.Ins);
        _undo.Add(s);
        _lastKind = EditKind.None;
        return s.Start + s.Ins.Length;
    }

    /// <summary>撤销/重做共用的内核：替换但不记历史（历史由调用方搬运）。</summary>
    private void ApplyStep(int start, int length, string insert)
    {
        // 直接复用 Replace 的索引逻辑，但要绕开历史记录 → 临时标记
        bool saved = _suppressHistory;
        _suppressHistory = true;
        try { Replace(start, length, insert); }
        finally { _suppressHistory = saved; }
    }

    private bool _suppressHistory;

    /// <summary>
    /// 仅供自检使用：打断相邻编辑的合并窗口，让每一步都独立成一步历史。
    /// 撤销/重做的差分对拍需要「编辑步数 == 历史步数」这个前提，否则连续单字符输入
    /// 会被合并成一步，对拍逻辑无法逆推快照。
    /// </summary>
    internal void BreakUndoCoalescing() => _lastKind = EditKind.None;

    // ---------------- 跨行着色状态 ----------------

    /// <summary>取第 line 行【起始】的跨行着色状态，按需向前推进（懒计算）。</summary>
    public LineState GetLineStartState(int line)
    {
        if (line < 0) line = 0;
        if (line >= LineCount) line = Math.Max(0, LineCount - 1);
        EnsureState(line);
        return _lineState[line];
    }

    private void EnsureState(int line)
    {
        if (_stateComputed > line) return;
        EnsureStateCapacity();
        int m = _stateComputed - 1;
        LineState st = _lineState[m];
        while (m < line)
        {
            st = SyntaxHighlighter.ScanLine(GetLine(m), st, tokens: null, Language);
            m++;
            _lineState[m] = st;
            _stateComputed = m + 1;
        }
    }

    private void EnsureStateCapacity()
    {
        int need = Math.Max(1, LineCount);
        if (_lineState.Length >= need) return;
        Array.Resize(ref _lineState, Math.Max(need, _lineState.Length * 2));
    }

    private void ResetTransientState()
    {
        EnsureStateCapacity();
        _lineState[0] = LineState.None;
        _stateComputed = 1;
    }

    // ---------------- 函数名索引（供代码补全） ----------------

    /// <summary>扫描上限 —— 对照 LiteReader.cpp 的 2 MB 上限，超大文件不拖慢输入。</summary>
    private const int FuncScanCapChars = 2 * 1024 * 1024;

    /// <summary>收集数量上限 —— 候选列表再长也没人翻，2000 条足够，且限制了内存。</summary>
    private const int FuncNamesMax = 2000;

    /// <summary>
    /// 重建节流窗口。编辑会不断改变 Version，若每次按键都重扫 2 MB，打字会明显发顿；
    /// 函数名列表天然是「有点过时也无所谓」的东西，所以沿用旧表直到距上次重建超过这个时间。
    /// </summary>
    private const int FuncRebuildThrottleMs = 400;

    private string[] _funcNames = [];
    private int _funcNamesVersion = -1;
    private long _funcNamesTick;

    private void InvalidateFunctionNames()
    {
        _funcNames = [];
        _funcNamesVersion = -1;
    }

    /// <summary>
    /// 文档内的函数名列表：标识符后紧跟 '('（忽略空白），去重、排除关键字。
    /// 按 Version 缓存 + 时间节流，编辑期间不会每按键重扫一次。
    /// </summary>
    public IReadOnlyList<string> FunctionNames()
    {
        if (_funcNamesVersion == Version) return _funcNames;

        long now = Environment.TickCount64;
        if (_funcNames.Length > 0 && now - _funcNamesTick < FuncRebuildThrottleMs)
            return _funcNames;                 // 太频繁：先沿用旧表

        _funcNames = ScanFunctionNames();
        _funcNamesVersion = Version;
        _funcNamesTick = now;
        return _funcNames;
    }

    private string[] ScanFunctionNames()
    {
        int cap = Math.Min(_text.Length, FuncScanCapChars);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();

        int i = 0;
        while (i < cap)
        {
            char c = _text[i];
            if (!(char.IsLetter(c) || c == '_')) { i++; continue; }

            int s = i;
            while (i < cap && (char.IsLetterOrDigit(_text[i]) || _text[i] == '_')) i++;
            int e = i;

            int j = e;
            while (j < cap && char.IsWhiteSpace(_text[j])) j++;
            if (j >= cap || _text[j] != '(') continue;

            ReadOnlySpan<char> name = _text.AsSpan(s, e - s);
            if (SyntaxHighlighter.IsKeywordWord(name)) continue;   // if( / for( 之类不是函数
            if (seen.Add(_text.Substring(s, e - s)) && names.Count < FuncNamesMax)
                names.Add(_text.Substring(s, e - s));
        }
        return [.. names];
    }

    /// <summary>保证行索引数组能放下 need 行（按 2 倍增长，避免逐行 Append 式的反复扩容）。</summary>
    private void EnsureLineCapacity(int need)
    {
        if (_lineStart.Length >= need) return;
        int cap = Math.Max(need, _lineStart.Length * 2);
        Array.Resize(ref _lineStart, cap);
        Array.Resize(ref _lineLen, cap);
    }

    private void RebuildLineIndex()
    {
        int n = _text.Length;
        // 先数行数，避免逐行扩容
        int lines = 1;
        for (int i = 0; i < n; i++)
            if (_text[i] == '\n') lines++;

        EnsureLineCapacity(lines);
        int l = 0, start = 0;
        for (int i = 0; i < n && l < lines; i++)
        {
            if (_text[i] != '\n') continue;
            int len = i - start;
            if (len > 0 && _text[i - 1] == '\r') len--;   // CRLF 折叠
            _lineStart[l] = start;
            _lineLen[l] = len;
            l++;
            start = i + 1;
        }
        if (l < lines)
        {
            int len = n - start;
            if (len > 0 && n > 0 && _text[n - 1] == '\r') len--;
            _lineStart[l] = start;
            _lineLen[l] = len;
            l++;
        }
        _lineCount = Math.Max(1, l);
        if (_lineCount > _lineStart.Length) EnsureLineCapacity(_lineCount);
    }

    // ---------------- 编码 ----------------

    /// <summary>探测换行风格：先出现的那个说了算；都没有则按平台惯例。</summary>
    public static string DetectNewLine(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            return i > 0 && text[i - 1] == '\r' ? "\r\n" : "\n";
        }
        return Environment.NewLine;
    }

    /// <summary>字节 → 文本。顺序：BOM &gt; 严格 UTF-8 &gt; GBK。</summary>
    public static (string Text, string EncodingName, bool HasBom) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8", true);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 LE", true);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16 BE", true);

        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), "UTF-8", false);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.GetEncoding(936).GetString(bytes), "GBK", false);
        }
    }
}
