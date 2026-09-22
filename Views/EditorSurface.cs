// LiteReader · Avalonia 跨平台版 —— 自绘编辑器控件
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18 续 · 三项原版已有能力补齐）：
//   1) **双击分词高亮** —— 双击一个词，把全文所有整词命中都铺上一层底色
//      （对照 collectMarks）。单击空白处 / 编辑文本 / 切文档都会清掉。
//   2) **右侧滑动导航条** —— 自绘的轨道 + 滑块，滑块高度 = 可视行数 / 总行数，
//      拖动跳转、点轨道翻页（对照原版的 WS_VSCROLL + SB_THUMBTRACK）。
//      原版靠系统滚动条，我们这边编辑区是自绘的，只能自己画一个。
//   3) **鼠标右键菜单** —— 复制 / 剪切 / 粘贴 / 全选 / 在命令提示符中打开 / 跳转到定义，
//      连同灰显条件一起对照 showEditorMenu；右键时先把光标落到点击处。
//
// 修订说明（2026-09-18）：
//   由「只读查看器」升级为「可编辑编辑器」：
//     · 光标 + 选区（CaretOffset / AnchorOffset），随标签保存与恢复
//     · 键盘导航（方向键 / Home / End / PgUp / PgDn / Ctrl+方向按词）、Shift 扩选
//     · 文本输入、Enter（按文档换行风格）、Tab（对齐到制表位）、Backspace / Delete
//     · 鼠标：点击定位、拖拽选区（越界自动滚动）、双击选词、三击选行
//     · 剪贴板 Ctrl+C / X / V（粘贴时把换行统一成该文档的风格）
//     · 撤销 / 重做 Ctrl+Z / Ctrl+Y（历史在 DocumentModel 里，按文档独立）
//     · 光标闪烁、保持光标可见（垂直 + 水平自动滚动）
//
// 为什么自绘而不用 TextBox：
//   实测 Avalonia 的 TextBox 给 19.3 MB 文本赋值只花 0 ms（惰性/虚拟化），
//   但也没有任何证据表明它能流畅显示 10 万行；而编辑器需要的分段着色、
//   行号槽、当前行高亮、脏矩形增量重绘，框架文本框都不提供。
//   所以走 Control.Render(DrawingContext) 自绘 —— 与 LiteReader.cpp 的
//   「行渲染缓存 + 分段绘制」同构，只是把 GDI 换成 Skia 后端。
//
// ⚠️ 两个已知性能要点：
//   1) Avalonia 的 FormattedText 只接受 string（没有 ReadOnlySpan 重载），
//      所以每行每帧都要 Substring 分配。这里用「按行缓存布局」把分配压到
//      只在文本变化 / 主题变化 / 字号变化时发生。
//   2) 光标定位必须精确到像素，但又不能用「格子数 × 字符宽」硬算 ——
//      等宽字体里 CJK 会回退到中文字体，其字宽并不等于西文字宽的两倍，长行会累积偏移。
//      解法：把每行切成「格宽同质」的段（全窄 / 全宽 / 单个制表符），
//      段内位置用线性插值，段宽用实测宽度 —— 精确且无需逐字符测量。
//
// 数据来源：字号取 EditorSettings.FontSize，主题取 ThemeService.Current；
//   两个都在 OnAttachedToVisualTree 订阅、OnDetachedFromVisualTree 退订。

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LiteReader.Models;
using LiteReader.Services;

namespace LiteReader.Views;

public sealed class EditorSurface : Control
{
    /// <summary>
    /// 字体回退链：先等宽西文，再到各平台的中文字体，最后交给系统默认。
    /// 跨平台关键 —— Windows 有 Consolas，Linux 常见 DejaVu Sans Mono，macOS 是 Menlo；
    /// 中文字体三平台各不相同，必须都列上，否则中文会退化成方块或极难看的默认字形。
    /// </summary>
    private static readonly FontFamily MonoFont = new(
        "Consolas, Menlo, DejaVu Sans Mono, JetBrains Mono, Liberation Mono, " +
        "Microsoft YaHei, PingFang SC, Noto Sans CJK SC, Source Han Sans SC, monospace");

    private const double LineSpacingFactor = 1.35;
    private const double GutterPadding = 8;
    private const double TextLeftPadding = 6;
    private const double CaretThickness = 1.6;
    private const int MaxCacheLines = 4000;          // 行布局缓存上限
    private const int MaxCachedLineChars = 6000;     // 超长行不缓存（minified JS 之类）

    // 右侧滑动导航条。宽度是「够看清、又不夺目」折中的结果 —— 系统滚动条在 Windows 上是
    // 17 px，但那个宽度在自绘的编辑区里显得很笨重，13 px 已经能舒服地拖住。
    private const double OverviewWidth = 13;
    private const double OverviewMinThumb = 28;      // 滑块最小高度（见 Models/OverviewBar.cs 的说明）
    private const double OverviewPadding = 2;        // 轨道上下留白，让滑块不会顶到窗口边缘

    // ---------------- 依赖属性 ----------------

    public static readonly StyledProperty<DocumentModel?> DocumentProperty =
        AvaloniaProperty.Register<EditorSurface, DocumentModel?>(nameof(Document));

    public static readonly StyledProperty<int> FirstVisibleLineProperty =
        AvaloniaProperty.Register<EditorSurface, int>(nameof(FirstVisibleLine));

    public static readonly StyledProperty<double> HorizontalOffsetProperty =
        AvaloniaProperty.Register<EditorSurface, double>(nameof(HorizontalOffset));

    public static readonly StyledProperty<int> CaretOffsetProperty =
        AvaloniaProperty.Register<EditorSurface, int>(nameof(CaretOffset), 0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> AnchorOffsetProperty =
        AvaloniaProperty.Register<EditorSurface, int>(nameof(AnchorOffset), 0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<EditorSurface, bool>(nameof(IsReadOnly));

    static EditorSurface()
    {
        AffectsRender<EditorSurface>(DocumentProperty, FirstVisibleLineProperty, HorizontalOffsetProperty,
            CaretOffsetProperty, AnchorOffsetProperty, IsReadOnlyProperty);
        FocusableProperty.OverrideDefaultValue<EditorSurface>(true);
    }

    public EditorSurface()
    {
        FocusAdorner = null;
        _blink.Interval = TimeSpan.FromMilliseconds(530);
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; InvalidateVisual(); };

        ContextMenu = BuildContextMenu();
        // 隧道路由：在默认的「打开 ContextMenu」处理之前把光标安顿好、把菜单项的可否用状态刷新一遍。
        // 不设 Handled —— 菜单照常由框架打开，我们只是抢在它前面做准备。
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    public DocumentModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public int FirstVisibleLine
    {
        get => GetValue(FirstVisibleLineProperty);
        set => SetValue(FirstVisibleLineProperty, value);
    }

    public double HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, value);
    }

    /// <summary>光标所在字符偏移（全局）。</summary>
    public int CaretOffset
    {
        get => GetValue(CaretOffsetProperty);
        set => SetValue(CaretOffsetProperty, value);
    }

    /// <summary>选区锚点。与 CaretOffset 共同定义选区 [min, max)。</summary>
    public int AnchorOffset
    {
        get => GetValue(AnchorOffsetProperty);
        set => SetValue(AnchorOffsetProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>
    /// 当前拥有键盘焦点的编辑区。菜单里的「撤销/复制/粘贴」这类命令天然依赖焦点，
    /// 属于视图层状态 —— 用一个静态引用路由，比让 VM 反向持有控件干净。
    /// </summary>
    public static EditorSurface? Focused { get; private set; }

    /// <summary>
    /// 右键菜单里的动作有时需要一句反馈（终端没找到、跳转没找到定义…）。
    /// 编辑区拿不到主窗口的视图模型（它的 DataContext 是**标签** VM，不是主窗口 VM），
    /// 所以用事件把话递出去，由 MainWindow 订阅后写进状态栏。
    ///
    /// 刻意做成静态的：与 <see cref="Focused"/> 同一套思路 —— 全应用只有一个主窗口，
    /// 订阅一次就够，省掉每个标签控件的接线与退订（标签是会随开关反复重建的）。
    /// </summary>
    public static event Action<string>? StatusReported;

    // ---------------- 布局缓存 ----------------

    private sealed class LineLayout
    {
        public FormattedText[] Parts = [];
        public double[] X = [];
        public double[] W = [];
        public int[] ColStart = [];
        public int[] ColLen = [];
        public int[] CellsPerChar = [];   // 1=窄 2=宽 -1=制表符（段内不定长）
        public double Width;
        public int Cells;
    }

    private readonly record struct Run(int Start, int Len, TokenKind Kind, bool IsTab, int Color);

    private readonly List<Token> _tokens = new(64);
    private readonly List<Run> _runs = new(64);
    private readonly Dictionary<int, LineLayout> _lineCache = new();

    private int _cacheDocVersion = -1;
    private int _cacheThemeVersion = -1;
    private double _cacheFontSize = -1;
    private int _cacheTabWidth = -1;
    private double _cacheGutterWidth = -1;

    private double _charW;        // 实测的「窄字符」格宽
    private double _cacheCharWFontSize = -1;
    private double _sentinelW;    // 哨兵字符 'M' 的宽度（见 AdvanceWidth）

    private readonly DispatcherTimer _blink = new();
    private bool _caretOn = true;
    private bool _dragging;
    private int _desiredCol = -1;   // 上下移动时保持的「目标列」

    // ---------------- 括号配对高亮（惰性重算） ----------------
    //
    // 光标每动一次、或文档每改一次都要重配一次括号。全文档扫描（十几个字符）本身很便宜，
    // 但没必要在「只是滚动」时也重算 —— 所以用一个 (光标, 文档版本) 做记忆键。
    private BracketPair? _match;
    private int _matchCaret = -1;
    private int _matchDocVersion = -1;

    // ---------------- 代码补全（自绘候选列表） ----------------
    //
    // 为什么不像 LiteReader.cpp 那样弹一个独立窗口：Avalonia 里做「跟随光标的独立弹窗」
    // 需要 PlacementTarget + 偏移绑定，还要把光标像素坐标从控件里导出去。
    // 而我们的编辑区本来就是自绘的、已经有 XOfCol/LineHeight 这套坐标换算，
    // 直接在自己的 Render 里画矩形+文字反而更简单，也不需要任何 XAML 接线。
    private const int CompMaxVisible = 12;
    private const double CompItemHeight = 18;
    private const double CompPadding = 4;

    private readonly List<CompletionItem> _compItems = [];
    private int _compIndex;
    private int _compWordStart = -1;
    private int _compCaret = -1;
    private int _compDocVersion = -1;

    private bool CompletionVisible => _compItems.Count > 0;

    /// <summary>当前显示的候选窗口在控件内的矩形（用于命中测试；未显示时为 null）。</summary>
    private Rect? _compRect;

    // ---------------- 双击分词高亮（对照 LiteReader.cpp 的 collectMarks） ----------------
    //
    // 原版用两个东西表达这件事：g_markRanges（区间表，供绘制）+ g_markFlag（逐字符标记，
    // 供「这一行需不需要重画」的判断）。我们只需要表达「画哪些区间」，所以只要前者。
    //
    // ⚠️ 为什么按**行**分桶（Dictionary<int, List<MarkRange>>）而不是一个平的区间表：
    //   大文件里双击一个常见词（比如 `int`）能有几万个命中，渲染时每帧遍历几万条再逐条判断
    //   「在不在可视区」——而每次判断都要做一次 LineIndexOf 的二分查找。几万 × 二分 = 每帧几十万次操作，
    //   滚动立刻掉帧。按行分桶之后，Render 只查可视的那几十行，代价与命中总数无关。
    private string _markWord = string.Empty;
    private readonly Dictionary<int, List<MarkRange>> _marksByLine = [];
    private int _markCount;

    /// <summary>
    /// 记下「收集标记时那份文本的实例」。文本被换过（任何真实编辑）它就不再相等，
    /// 用于区分「真编辑」与「Save / 切语言这类只抬版本号的动作」（见 OnDocumentChanged）。
    /// </summary>
    private string? _markTextRef;

    // ---------------- 右侧滑动导航条 ----------------
    private bool _overviewDragging;
    private double _overviewGrabOffset;   // 按下点相对滑块顶端的偏移，拖动时保持手感

    // ---------------- 右键菜单 ----------------
    private MenuItem _miCut = null!;
    private MenuItem _miCopy = null!;
    private MenuItem _miPaste = null!;
    private MenuItem _miGoToDef = null!;

    // ---------------- 基础度量 ----------------

    public double LineHeight => Math.Ceiling(EditorSettings.FontSize * LineSpacingFactor);

    public int VisibleLineCount => Math.Max(1, (int)(Bounds.Height / Math.Max(1, LineHeight)));

    private double CharWidth
    {
        get
        {
            double fs = EditorSettings.FontSize;
            if (Math.Abs(fs - _cacheCharWFontSize) > 0.001 || _charW <= 0)
            {
                // 量 16 个 '0' 取平均，比单字符测量更稳（避开 hinting 造成的 ±1 px 抖动）
                var probe = new FormattedText("0000000000000000", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(MonoFont), fs, Brushes.Black);
                _charW = probe.Width / 16.0;
                _cacheCharWFontSize = fs;

                var one = new FormattedText("M", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(MonoFont), fs, Brushes.Black);
                _sentinelW = one.Width;
            }
            return _charW;
        }
    }

    /// <summary>
    /// 取一段文本的真实推进宽度（排版前进量）。
    ///
    /// ⚠️ 直接读 <see cref="FormattedText.Width"/> 在这里是不可靠的：Skia 会把**行尾空白**从宽度里裁掉，
    /// 所以「单独的空格段」「整段是空格的缩进」「制表符展开出来的空格」量出来都是 0。
    /// 后果不是"少画一个空格"这么轻 —— 段的 x 坐标是累加出来的，一段量成 0 会让后面所有段整体左移，
    /// 于是 <c>public static class</c> 被渲染成 <c>publicstaticclass</c>，行首缩进整段消失。
    /// 这是个纯静默错误：不抛异常、不报错，只能靠看图发现（见 Diagnostics/SelfTest.cs）。
    ///
    /// 修法：在末尾补一个哨兵字符再扣除它的宽度 —— 空白就不再处于行尾，宽度自然被算进去。
    /// 只对「以空白结尾」的段走这条慢路径，绝大多数段仍是一次测量。
    /// </summary>
    private double AdvanceWidth(string display, FormattedText own)
    {
        if (display.Length == 0 || !char.IsWhiteSpace(display[^1])) return own.Width;

        var withSentinel = new FormattedText(display + "M", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(MonoFont), EditorSettings.FontSize, Brushes.Black);
        return Math.Max(0, withSentinel.Width - _sentinelW);
    }

    private double GutterWidth
    {
        get
        {
            int digits = Math.Max(3, (Document?.LineCount ?? 1).ToString(CultureInfo.InvariantCulture).Length);
            return Math.Ceiling(digits * EditorSettings.FontSize * 0.62) + GutterPadding * 2;
        }
    }

    /// <summary>正文区左边缘（屏幕坐标，已含水平滚动）。</summary>
    private double TextOriginX => GutterWidth + TextLeftPadding - HorizontalOffset;

    /// <summary>
    /// 正文区可视宽度。**已扣掉右侧导航条** —— 不扣的话光标在长行末尾时
    /// EnsureCaretVisible 会把光标带到导航条底下，看不见光标在哪。
    /// </summary>
    private double TextViewportWidth
        => Math.Max(0, Bounds.Width - GutterWidth - TextLeftPadding - OverviewWidth);

    private int SelStart => Math.Min(CaretOffset, AnchorOffset);
    private int SelEnd => Math.Max(CaretOffset, AnchorOffset);
    private bool HasSelection => SelStart != SelEnd;

    // ---------------- 生命周期 ----------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EditorSettings.Changed += OnGlobalSettingChanged;
        if (ThemeService.Current is { } t) t.Changed += OnGlobalSettingChanged;
        SubscribeDocument(Document);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EditorSettings.Changed -= OnGlobalSettingChanged;
        if (ThemeService.Current is { } t) t.Changed -= OnGlobalSettingChanged;
        SubscribeDocument(null);
        _blink.Stop();
        if (ReferenceEquals(Focused, this)) Focused = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            SubscribeDocument(change.GetOldValue<DocumentModel?>());
            SubscribeDocument(change.GetNewValue<DocumentModel?>());
            InvalidateLineCache();
            ClampScrollToDocument();
            ClearMarks();                    // 换文档 → 上一个文档的分词标记不能留到新文档上
            InvalidateVisual();
        }
        else if (change.Property == CaretOffsetProperty)
        {
            _desiredCol = -1;
            EnsureCaretVisible();
            // 光标离开原来的词 → 候选列表已无意义。主动收起来，避免它挂在屏幕上不动。
            HideCompletion();
            // 括号配对也在光标变化时就重算，而不是留到 Render ——
            // 这样自检能直接读到配对结果，不必先想办法催一帧渲染出来。
            UpdateBracketMatch();
        }
    }

    private void SubscribeDocument(DocumentModel? doc)
    {
        if (doc is null) return;
        doc.Changed -= OnDocumentChanged;
        doc.Changed += OnDocumentChanged;
    }

    private void OnDocumentChanged()
    {
        InvalidateLineCache();
        ClampScrollToDocument();

        // 文本真的变了就把分词标记清掉（对照原版：编辑时 g_markWord.clear()）——
        // 标记是按**偏移**记的，文本一动偏移就全错位，不清会点亮一堆无关的词。
        //
        // ⚠️ 判据用「字符串实例是否还是原来那个」而不是 Version：
        //   Save() 与 Language 赋值都会抬 Version 并发 Changed，但文本一个字都没动。
        //   按 Version 判的话，按一下 Ctrl+S 就会把双击高亮抹掉，看着像是程序抽风。
        //   Replace 里是 string.Concat 重建整串，所以任何真实编辑都必然换实例。
        if (!ReferenceEquals(Document?.Text, _markTextRef)) ClearMarks();

        InvalidateVisual();
    }

    private void OnGlobalSettingChanged()
    {
        InvalidateLineCache();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void InvalidateLineCache()
    {
        _lineCache.Clear();
        _cacheDocVersion = -1;
        _cacheThemeVersion = -1;
    }

    private void ClampScrollToDocument()
    {
        int max = Math.Max(0, (Document?.LineCount ?? 1) - 1);
        if (FirstVisibleLine > max) FirstVisibleLine = max;
        if (FirstVisibleLine < 0) FirstVisibleLine = 0;
        if (HorizontalOffset < 0) HorizontalOffset = 0;
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        Focused = this;
        _caretOn = true;
        _blink.Start();
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _blink.Stop();
        _caretOn = false;
        if (ReferenceEquals(Focused, this)) Focused = null;
        InvalidateVisual();
    }

    // ---------------- 坐标换算 ----------------

    private LineLayout LayoutOf(int line)
    {
        ThemeService? themes = ThemeService.Current;
        double fontSize = EditorSettings.FontSize;
        DocumentModel? doc = Document;
        double gutterW = GutterWidth;

        int docVer = doc?.Version ?? -1;
        int themeVer = themes?.Version ?? -1;
        if (docVer != _cacheDocVersion || themeVer != _cacheThemeVersion
            || Math.Abs(fontSize - _cacheFontSize) > 0.001
            || EditorSettings.TabWidth != _cacheTabWidth
            || Math.Abs(gutterW - _cacheGutterWidth) > 0.5)
        {
            _lineCache.Clear();
            _cacheDocVersion = docVer;
            _cacheThemeVersion = themeVer;
            _cacheFontSize = fontSize;
            _cacheTabWidth = EditorSettings.TabWidth;
            _cacheGutterWidth = gutterW;
        }

        if (_lineCache.TryGetValue(line, out LineLayout? cached)) return cached;
        if (doc is null) return new LineLayout();

        LineLayout layout = BuildLayout(doc, line, themes, fontSize);
        if (doc.LineLength(line) <= MaxCachedLineChars)
        {
            if (_lineCache.Count >= MaxCacheLines) _lineCache.Clear();
            _lineCache[line] = layout;
        }
        return layout;
    }

    private LineLayout BuildLayout(DocumentModel doc, int line, ThemeService? themes, double fontSize)
    {
        ReadOnlySpan<char> text = doc.GetLine(line);

        _tokens.Clear();
        SyntaxHighlighter.ScanLine(text, doc.GetLineStartState(line), _tokens, doc.Language);

        // ---- 把 token 再切成「格宽同质」的段 ----
        // 段保留 token 的色号，括号才能按嵌套深度取彩虹色。
        _runs.Clear();
        foreach (Token t in _tokens)
        {
            int end = Math.Min(t.Start + t.Length, text.Length);
            int i = t.Start;
            while (i < end)
            {
                if (text[i] == '\t')
                {
                    _runs.Add(new Run(i, 1, t.Kind, true, t.Color));
                    i++;
                    continue;
                }
                bool wide = IsWideChar(text[i]);
                int j = i + 1;
                while (j < end && text[j] != '\t' && IsWideChar(text[j]) == wide) j++;
                _runs.Add(new Run(i, j - i, t.Kind, false, t.Color));
                i = j;
            }
        }

        // ---- 生成 FormattedText 并累计位置 ----
        int count = _runs.Count;
        var lay = new LineLayout
        {
            Parts = new FormattedText[count],
            X = new double[count],
            W = new double[count],
            ColStart = new int[count],
            ColLen = new int[count],
            CellsPerChar = new int[count],
        };

        double x = 0;
        int cells = 0;
        for (int k = 0; k < count; k++)
        {
            Run r = _runs[k];
            string display;
            int cellsPerChar;
            if (r.IsTab)
            {
                int spaces = Math.Max(1, EditorSettings.TabWidth - (cells % EditorSettings.TabWidth));
                display = new string(' ', spaces);
                cellsPerChar = -1;
                cells += spaces;
            }
            else
            {
                display = text.Slice(r.Start, r.Len).ToString();
                cellsPerChar = IsWideChar(text[r.Start]) ? 2 : 1;
                cells += cellsPerChar * r.Len;
            }

            string key = r.Kind == TokenKind.Default
                ? "EditorForeground"
                : SyntaxHighlighter.ResourceKey(r.Kind, r.Color);
            IBrush brush = Res(themes, key, BrushFallback(r.Kind));

            var ft = new FormattedText(display, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(MonoFont), fontSize, brush);

            lay.Parts[k] = ft;
            lay.X[k] = x;
            lay.W[k] = AdvanceWidth(display, ft);
            lay.ColStart[k] = r.Start;
            lay.ColLen[k] = r.Len;
            lay.CellsPerChar[k] = cellsPerChar;
            x += lay.W[k];
        }

        lay.Width = x;
        lay.Cells = cells;
        return lay;
    }

    /// <summary>
    /// 主题里查不到键时的兜底色。括号有 6 个彩虹键，任何一个缺失都不该让程序崩掉，
    /// 落到正文色即可（虽然会丢掉彩虹，但内容还是能看）。
    /// </summary>
    private static string BrushFallback(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "#C678DD",
        TokenKind.Type => "#E5C07B",
        TokenKind.String => "#98C379",
        TokenKind.Number => "#D19A66",
        TokenKind.Comment => "#5C6370",
        TokenKind.Preproc => "#56B6C2",
        _ => "#ABB2BF",
    };

    /// <summary>行内列号 → 行内 x（相对正文起点）。</summary>
    private static double XOfCol(LineLayout lay, int col)
    {
        if (lay.ColStart.Length == 0) return 0;
        if (col <= 0) return lay.X[0];
        for (int i = 0; i < lay.ColStart.Length; i++)
        {
            int cs = lay.ColStart[i], cl = lay.ColLen[i];
            if (col <= cs) return lay.X[i];
            if (col <= cs + cl)
            {
                if (lay.CellsPerChar[i] < 0) return col <= cs ? lay.X[i] : lay.X[i] + lay.W[i];
                double perChar = lay.W[i] / cl;     // 段内同质 → 线性可插值
                return lay.X[i] + (col - cs) * perChar;
            }
        }
        return lay.Width;
    }

    /// <summary>行内 x → 行内列号（取字符中点分界，与鼠标直觉一致）。</summary>
    private static int ColOfX(LineLayout lay, double x)
    {
        if (lay.ColStart.Length == 0) return 0;
        if (x <= lay.X[0]) return lay.ColStart[0];
        for (int i = 0; i < lay.ColStart.Length; i++)
        {
            int cs = lay.ColStart[i], cl = lay.ColLen[i];
            double x0 = lay.X[i], x1 = x0 + lay.W[i];
            if (x > x1 && i + 1 < lay.ColStart.Length) continue;
            if (cl <= 0) return cs;
            if (x >= x1) return cs + cl;
            if (lay.CellsPerChar[i] < 0) return x - x0 >= lay.W[i] * 0.5 ? cs + 1 : cs;
            double perChar = lay.W[i] / cl;
            return cs + Math.Clamp((int)Math.Floor((x - x0) / perChar + 0.5), 0, cl);
        }
        return lay.ColStart[^1] + lay.ColLen[^1];
    }

    private Point PointFromOffset(int offset)
    {
        DocumentModel? doc = Document;
        if (doc is null || doc.LineCount == 0) return new Point(TextOriginX, 0);
        int line = doc.LineIndexOf(offset);
        int col = offset - doc.LineStartOffset(line);
        LineLayout lay = LayoutOf(line);
        double y = (line - FirstVisibleLine) * LineHeight;
        return new Point(TextOriginX + XOfCol(lay, col), y);
    }

    private int OffsetFromPoint(Point p)
    {
        DocumentModel? doc = Document;
        if (doc is null || doc.LineCount == 0) return 0;
        int line = FirstVisibleLine + (int)Math.Floor(p.Y / Math.Max(1, LineHeight));
        line = Math.Clamp(line, 0, doc.LineCount - 1);
        LineLayout lay = LayoutOf(line);
        int col = ColOfX(lay, p.X - TextOriginX);
        return doc.OffsetOf(line, col);
    }

    // ---------------- 滚动 / 保证光标可见 ----------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (HandleWheel(e.Delta.Y, e.KeyModifiers)) e.Handled = true;
    }

    /// <summary>
    /// 滚轮处理内核。抽出来的理由与 <see cref="HandleKey"/> / <see cref="HandleText"/> 一样：
    /// <c>PointerWheelEventArgs</c> 没法在自检里伪造（得先凑一个 IPointer 实现），
    /// 而这里真正要测的是「修饰键 + 方向 → 干什么」这段映射，不是 Avalonia 的指针派发。
    /// </summary>
    /// <returns>是否消费了这次滚轮。</returns>
    internal bool HandleWheel(double deltaY, KeyModifiers mods)
    {
        if (Math.Abs(deltaY) < 0.0001) return false;

        // Ctrl/Cmd + 滚轮 = 调字号（每格 ±1 pt，范围见 EditorSettings）。
        // 对照 LiteReader.cpp 的 WM_MOUSEWHEEL 分支：那边是 g_fontSize ± 1 后夹在 9~28；
        // 本工程的范围收在 EditorSettings.Min/MaxFontSize（8~32）—— 比原版略宽，
        // 因为这里字号同时要伺候高 DPI 屏和投影仪，两端各多留一点。
        //
        // 为什么放在最前面而不是跟 Shift 并列：Ctrl+Shift+滚轮 会同时满足两个条件，
        // 而「按住 Ctrl 想调字号」是明确的意图，不该被 Shift 抢去横向滚动。
        if (IsCommand(mods))
        {
            EditorSettings.FontSize = EditorSettings.FontSize + (deltaY > 0 ? 1 : -1);
            // 字号一变行高就变，光标可能已经被挤出可视区 —— 原版这里也是先 updateCaretPos()
            EnsureCaretVisible();
            InvalidateVisual();
            return true;
        }

        // 没有文档时（占位提示页）只允许调字号，滚动交给上面的分支处理完了
        if (Document is null) return false;

        if (mods.HasFlag(KeyModifiers.Shift))
        {
            HorizontalOffset = Math.Max(0, HorizontalOffset - deltaY * CharWidth * 3);
        }
        else
        {
            int max = Math.Max(0, Document.LineCount - 1);
            FirstVisibleLine = Math.Clamp(FirstVisibleLine - (int)Math.Round(deltaY * 3), 0, max);
        }
        return true;
    }

    /// <summary>把光标移进可视区（垂直 + 水平），编辑后必须调，否则打字会把光标推出屏幕。</summary>
    private void EnsureCaretVisible()
    {
        DocumentModel? doc = Document;
        if (doc is null || doc.LineCount == 0) return;

        int line = doc.LineIndexOf(CaretOffset);
        int visible = VisibleLineCount;
        if (line < FirstVisibleLine) FirstVisibleLine = line;
        else if (line >= FirstVisibleLine + visible) FirstVisibleLine = line - visible + 1;

        double x = XOfCol(LayoutOf(line), CaretOffset - doc.LineStartOffset(line));
        double vw = TextViewportWidth;
        if (vw > 0)
        {
            if (x < HorizontalOffset) HorizontalOffset = Math.Max(0, x - CharWidth);
            else if (x > HorizontalOffset + vw) HorizontalOffset = x - vw + CharWidth;
        }
    }

    // ---------------- 键盘 ----------------

    private static bool IsCommand(KeyModifiers m)
        => m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta);   // macOS 用 ⌘

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }

    /// <summary>
    /// 键盘处理内核。之所以从 OnKeyDown 里抽出来，是为了让自检能直接驱动同一段逻辑
    /// （见 Diagnostics/SelfTest.cs）—— 伪造平台 KeyEvent 既繁琐又脆弱，而这里真正要测的是
    /// 「按键 → 编辑动作」这段映射，不是 Avalonia 的事件派发。
    /// </summary>
    internal bool HandleKey(Key key, KeyModifiers mods)
    {
        DocumentModel? doc = Document;
        if (doc is null) return false;

        bool cmd = IsCommand(mods);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        int line = doc.LineIndexOf(CaretOffset);
        int col = CaretOffset - doc.LineStartOffset(line);

        // ---- 候选列表可见时，↑/↓/Enter/Tab/Esc 先归它 ----
        // 不这样做的后果：打完 `pub` 想看候选，按 ↓ 却先把光标移走、列表当场消失。
        if (CompletionVisible && !cmd && !shift)
        {
            switch (key)
            {
                case Key.Down:
                    _compIndex = Math.Min(_compIndex + 1, Math.Min(_compItems.Count, CompMaxVisible) - 1);
                    InvalidateVisual();
                    return true;
                case Key.Up:
                    _compIndex = Math.Max(_compIndex - 1, 0);
                    InvalidateVisual();
                    return true;
                case Key.Enter or Key.Return or Key.Tab:
                    if (!IsReadOnly) AcceptCompletion();
                    return true;
                case Key.Escape:
                    HideCompletion();
                    return true;
            }
        }

        switch (key)
        {
            case Key.Left:
                HideCompletion();
                if (cmd) MoveCaret(PrevWordBoundary(doc, CaretOffset), shift);
                else if (!shift && HasSelection) MoveCaret(SelStart, false);
                else MoveCaret(Math.Max(0, CaretOffset - 1), shift);
                break;

            case Key.Right:
                HideCompletion();
                if (cmd) MoveCaret(NextWordBoundary(doc, CaretOffset), shift);
                else if (!shift && HasSelection) MoveCaret(SelEnd, false);
                else MoveCaret(Math.Min(doc.CharCount, CaretOffset + 1), shift);
                break;

            case Key.Up:
                HideCompletion();
                MoveVertical(-1, shift);
                break;
            case Key.Down:
                HideCompletion();
                MoveVertical(+1, shift);
                break;

            case Key.Home:
                HideCompletion();
                MoveCaret(cmd ? 0 : doc.LineStartOffset(line), shift);
                break;
            case Key.End:
                HideCompletion();
                MoveCaret(cmd ? doc.CharCount : doc.LineEndOffset(line), shift);
                break;

            case Key.PageUp:
                HideCompletion();
                MoveCaret(doc.OffsetOf(Math.Max(0, line - VisibleLineCount), col), shift);
                break;
            case Key.PageDown:
                HideCompletion();
                MoveCaret(doc.OffsetOf(Math.Min(doc.LineCount - 1, line + VisibleLineCount), col), shift);
                break;

            case Key.Back:
                if (IsReadOnly) break;
                if (HasSelection) ReplaceSelection(string.Empty);
                else if (CaretOffset > 0)
                {
                    int n = cmd ? PrevWordBoundary(doc, CaretOffset) : CaretOffset - 1;
                    ReplaceRange(n, CaretOffset - n, string.Empty);
                }
                UpdateCompletion();
                break;

            case Key.Delete:
                if (IsReadOnly) break;
                if (HasSelection) ReplaceSelection(string.Empty);
                else if (CaretOffset < doc.CharCount)
                {
                    int n = cmd ? NextWordBoundary(doc, CaretOffset) : CaretOffset + 1;
                    ReplaceRange(CaretOffset, n - CaretOffset, string.Empty);
                }
                UpdateCompletion();
                break;

            case Key.Enter:
                if (IsReadOnly) break;
                if (cmd) break;                     // Ctrl+Enter 留给外面（例如未来的“运行”）
                ReplaceSelection(doc.NewLine);
                HideCompletion();
                break;

            case Key.Tab:
                if (IsReadOnly) break;
                {
                    int spaces = EditorSettings.TabWidth - (col % EditorSettings.TabWidth);
                    ReplaceSelection(new string(' ', spaces));
                }
                HideCompletion();
                break;

            case Key.A when cmd:
                HideCompletion();
                CaretOffset = doc.CharCount;
                AnchorOffset = 0;
                break;

            case Key.C when cmd:
                _ = CopyAsync(cut: false);
                break;
            case Key.X when cmd:
                if (!IsReadOnly) _ = CopyAsync(cut: true);
                break;
            case Key.V when cmd:
                if (!IsReadOnly) _ = PasteAsync();
                break;

            case Key.Z when cmd && shift:
            case Key.Y when cmd:
                HideCompletion();
                Redo();
                break;
            case Key.Z when cmd:
                HideCompletion();
                Undo();
                break;

            default:
                return false;
        }

        return true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (HandleText(e.Text)) e.Handled = true;
    }

    /// <summary>文本输入内核（同样抽出来供自检驱动）。返回是否消费了这次输入。</summary>
    internal bool HandleText(string? text)
    {
        if (IsReadOnly) return false;
        if (string.IsNullOrEmpty(text)) return false;
        // 控制字符（含 Enter/Tab/Esc）由 HandleKey 处理，这里只收可见文本
        foreach (char c in text)
            if (char.IsControl(c) && c != '\t') return false;

        string t = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // ---- 括号/引号自动配对（对照 LiteReader.cpp 的 WM_CHAR 分支）----
        // 只在「有语言」的文件里生效：在 .txt / .log 里打一个引号就自动补出两个引号，
        // 对写散文和看日志的人是纯干扰，而按语言区分不需要增加任何开关。
        DocumentModel? doc = Document;
        if (EditorSettings.AutoComplete && doc is not null && doc.Language != LanguageMode.Plain
            && t.Length == 1)
        {
            char ch = t[0];

            // 尖括号只在「前面紧跟标识符」时配对（Vector<），免得 a < b 被补成 a < b>
            bool pairOpen = IsPairOpen(ch)
                && !(ch == '<' && !(CaretOffset > 0 && IsWordChar(doc.Text[CaretOffset - 1])));

            if (pairOpen)
            {
                char close = PairClose(ch);
                if (HasSelection)
                {
                    string sel = doc.Text.Substring(SelStart, SelEnd - SelStart);
                    ReplaceRange(SelStart, SelEnd - SelStart, string.Concat(ch, sel, close));
                }
                else
                {
                    ReplaceRange(CaretOffset, 0, string.Concat(ch, close));
                    SetCaret(CaretOffset - 1);      // 光标置于配对中间
                }
                UpdateCompletion();
                return true;
            }

            // 输入的正是已有的闭括号 → 跳过它，而不是插成重复的一个
            if (IsPairClose(ch) && !HasSelection
                && CaretOffset < doc.CharCount && doc.Text[CaretOffset] == ch)
            {
                SetCaret(CaretOffset + 1);
                UpdateCompletion();
                return true;
            }
        }

        ReplaceSelection(t);
        UpdateCompletion();
        return true;
    }

    private static bool IsPairOpen(char c)
        => c is '(' or '{' or '[' or '<' or '"' or '\'';

    private static bool IsPairClose(char c)
        => c is ')' or '}' or ']' or '>' or '"' or '\'';

    private static char PairClose(char c) => c switch
    {
        '(' => ')',
        '{' => '}',
        '[' => ']',
        '<' => '>',
        _ => c,          // 引号自配对
    };

    private void MoveCaret(int offset, bool extendSelection)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;
        offset = Math.Clamp(offset, 0, doc.CharCount);
        CaretOffset = offset;
        if (!extendSelection) AnchorOffset = offset;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    private void MoveVertical(int deltaLines, bool extendSelection)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;

        int line = doc.LineIndexOf(CaretOffset);
        int col = _desiredCol >= 0 ? _desiredCol : CaretOffset - doc.LineStartOffset(line);
        int target = Math.Clamp(line + deltaLines, 0, doc.LineCount - 1);
        int offset = doc.OffsetOf(target, col);

        _desiredCol = col;                  // 上下移动时保持目标列，短行不会把列号吃掉
        CaretOffset = offset;
        if (!extendSelection) AnchorOffset = offset;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    private int PrevWordBoundary(DocumentModel doc, int offset)
    {
        string s = doc.Text;
        int i = offset;
        while (i > 0 && !IsWordChar(s[i - 1])) i--;
        while (i > 0 && IsWordChar(s[i - 1])) i--;
        return i;
    }

    private int NextWordBoundary(DocumentModel doc, int offset)
    {
        string s = doc.Text;
        int i = offset;
        while (i < s.Length && !IsWordChar(s[i])) i++;
        while (i < s.Length && IsWordChar(s[i])) i++;
        return i;
    }

    // ---------------- 编辑 ----------------

    private void ReplaceRange(int start, int length, string insert)
    {
        DocumentModel? doc = Document;
        if (doc is null || IsReadOnly) return;

        doc.Replace(start, length, insert);
        int caret = start + insert.Length;
        AnchorOffset = caret;
        CaretOffset = caret;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    /// <summary>替换当前选区（无选区时等价于在光标处插入）。</summary>
    private void ReplaceSelection(string insert)
    {
        if (HasSelection) ReplaceRange(SelStart, SelEnd - SelStart, insert);
        else ReplaceRange(CaretOffset, 0, insert);
    }

    public void Undo()
    {
        DocumentModel? doc = Document;
        if (doc is null || IsReadOnly) return;
        int caret = doc.Undo();
        if (caret < 0) return;
        AnchorOffset = caret;
        CaretOffset = caret;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    public void Redo()
    {
        DocumentModel? doc = Document;
        if (doc is null || IsReadOnly) return;
        int caret = doc.Redo();
        if (caret < 0) return;
        AnchorOffset = caret;
        CaretOffset = caret;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    // ---- 供菜单调用的公开入口（键盘快捷键在 OnKeyDown 里直接处理） ----

    public void Copy() => _ = CopyAsync(cut: false);

    public void Cut() => _ = CopyAsync(cut: true);

    public void Paste() => _ = PasteAsync();

    public void SelectAll()
    {
        DocumentModel? doc = Document;
        if (doc is null) return;
        AnchorOffset = 0;
        CaretOffset = doc.CharCount;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    public bool HasSelectionNow => HasSelection;

    // ---------------- 剪贴板 ----------------

    private async Task CopyAsync(bool cut)
    {
        DocumentModel? doc = Document;
        if (doc is null || !HasSelection) return;
        string s = doc.Text.Substring(SelStart, SelEnd - SelStart);
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is not null) await clip.SetTextAsync(s);
        }
        catch { /* 剪贴板被别的进程占用等情况：静默忽略，不打断编辑 */ }

        if (cut) ReplaceSelection(string.Empty);
    }

    private async Task PasteAsync()
    {
        DocumentModel? doc = Document;
        if (doc is null || IsReadOnly) return;
        string? t;
        try
        {
            var clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is null) return;
            // TryGetTextAsync 会先探数据格式再读，剪贴板里没有文本时返回 null 而不抛异常
            t = await clip.TryGetTextAsync();
        }
        catch { return; }

        if (string.IsNullOrEmpty(t)) return;
        // 粘贴的换行统一成该文档的风格：在 Linux 上编辑 Windows 文件不会把 CRLF 打散
        t = t.Replace("\r\n", "\n").Replace('\r', '\n');
        if (doc.NewLine != "\n") t = t.Replace("\n", doc.NewLine);
        ReplaceSelection(t);
    }

    // ---------------- 鼠标 ----------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        DocumentModel? doc = Document;
        if (doc is null) return;

        Focus();
        PointerPoint pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;

        // 点在候选列表上 → 选中并确认（优先级最高，否则会被下面当成点击正文）
        int compHit = CompletionHitTest(pt.Position);
        if (compHit >= 0)
        {
            _compIndex = compHit;
            AcceptCompletion();
            e.Handled = true;
            return;
        }

        // 右侧导航条排第二：点/拖它只能滚动，不能顺手把光标挪到别的行上 ——
        // 那个行为很难向用户解释（「我明明拖的是滚动条，光标怎么跑下面去了」）。
        if (TryStartOverviewDrag(pt.Position, e.Pointer))
        {
            e.Handled = true;
            return;
        }

        int offset = OffsetFromPoint(pt.Position);

        if (e.ClickCount == 2)
        {
            DoubleClickWord(offset);
        }
        else if (e.ClickCount >= 3)
        {
            TripleClickLine(offset);
        }
        else
        {
            // 单击正文：清掉上一次双击留下的分词高亮（对照原版 WM_LBUTTONDOWN 里的清理）
            ClearMarks();

            AnchorOffset = offset;
            CaretOffset = offset;
            _dragging = true;
            e.Pointer.Capture(this);

            // Ctrl/Cmd + 单击 = 跳转到定义（对照 LiteReader.cpp 的「跳转到定义」菜单/快捷键）
            if (IsCommand(e.KeyModifiers))
            {
                _dragging = false;
                e.Pointer.Capture(null);
                GoToDefinition();
            }
            else
            {
                HideCompletion();
            }
        }

        _desiredCol = -1;
        EnsureCaretVisible();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// 双击选词 + 分词高亮（对照 LiteReader.cpp:5235 的 WM_LBUTTONDBLCLK）。
    /// 抽成 internal 是为了让自检能驱动同一段逻辑 —— <c>ClickCount</c> 是平台给的，
    /// 在自检里没有构造入口，而这里真正要验证的是「落点 → 选中哪一段 + 点亮哪些区间」。
    /// </summary>
    internal void DoubleClickWord(int offset)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;

        string text = doc.Text;
        (int a, int b) = WordMarker.WordBoundsAt(text, offset);
        bool gotWord = b > a;

        // 落在空白/标点上（取不到整词）时退化成「选中相邻的一个字符」——
        // 这是本工程对原版的**有意偏离**：原版此时什么都不选，用户会觉得双击没反应。
        if (!gotWord)
        {
            a = Math.Max(0, offset - 1);
            b = Math.Min(text.Length, offset + 1);
        }

        AnchorOffset = a;
        CaretOffset = b;

        // 只有真的命中一个整词时才点亮：退化分支选中的是标点/空白，铺色毫无意义。
        // 两端都校验一次 IsWordChar —— WordBoundsAt 向左是无条件扩展的，
        // 但退化分支是我们自己造的区间，不能假定它也是个词。
        bool isWord = gotWord
            && WordMarker.IsWordChar(text[a])
            && WordMarker.IsWordChar(text[b - 1]);
        UpdateMarks(isWord ? text.Substring(a, b - a) : string.Empty);

        _dragging = false;
        HideCompletion();
        InvalidateVisual();
    }

    /// <summary>三击选整行（含行尾换行符，与主流编辑器一致）。</summary>
    private void TripleClickLine(int offset)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;
        int line = doc.LineIndexOf(offset);
        AnchorOffset = doc.LineStartOffset(line);
        CaretOffset = doc.LineHasNewline(line) ? doc.LineEndOffset(line) + 1 : doc.LineEndOffset(line);
        _dragging = false;
        HideCompletion();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // 导航条拖拽优先：它和「拖拽选字」是两件互斥的事（按下时就分过流了，
        // 这里再判一次是因为 _dragging 可能因为边界情况没被清掉）
        if (_overviewDragging)
        {
            HandleOverviewDrag(e.GetPosition(this).Y);
            e.Handled = true;
            return;
        }

        if (!_dragging) return;
        DocumentModel? doc = Document;
        if (doc is null) return;

        Point p = e.GetPosition(this);
        double lineH = Math.Max(1, LineHeight);

        // 拖出上下边界时自动滚动（按住往下拖可以连续选很多行）
        if (p.Y < 0 && FirstVisibleLine > 0) FirstVisibleLine--;
        else if (p.Y > Bounds.Height) FirstVisibleLine = Math.Min(doc.LineCount - 1, FirstVisibleLine + 1);

        // 拖出左右边界时水平滚动
        if (p.X < GutterWidth && HorizontalOffset > 0) HorizontalOffset = Math.Max(0, HorizontalOffset - CharWidth);
        else if (p.X > Bounds.Width) HorizontalOffset += CharWidth;

        CaretOffset = OffsetFromPoint(new Point(p.X, Math.Clamp(p.Y, 0, Math.Max(0, Bounds.Height - lineH))));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_overviewDragging)
        {
            _overviewDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    // ---------------- 括号配对高亮 ----------------

    /// <summary>
    /// 重配光标处的括号（惰性：只有光标或文档变了才真算）。
    /// </summary>
    private void UpdateBracketMatch()
    {
        DocumentModel? doc = Document;
        if (doc is null)
        {
            _match = null;
            return;
        }
        if (_matchCaret == CaretOffset && _matchDocVersion == doc.Version) return;

        _matchCaret = CaretOffset;
        _matchDocVersion = doc.Version;
        _match = BracketMatcher.TryFind(doc, CaretOffset, out BracketPair p) ? p : null;
    }

    /// <summary>
    /// 仅供自检读取的当前括号配对结果（读取时按需重算）。null = 光标处没有可配对的括号。
    /// 之所以要暴露它：配对高亮画在背景层，是**纯视觉**的东西，
    /// 「算出来了但没画」和「压根没算出来」在截图里长得一样，只有断言能分辨。
    /// </summary>
    internal BracketPair? CurrentMatch
    {
        get { UpdateBracketMatch(); return _match; }
    }

    // ---------------- 双击分词高亮 ----------------

    /// <summary>
    /// 设置标记词并收集它的全部整词命中（空词 = 只清除）。语义见 Models/WordMarker.cs。
    /// </summary>
    private void UpdateMarks(string word)
    {
        ClearMarks();
        DocumentModel? doc = Document;
        _markTextRef = doc?.Text;
        if (doc is null || word.Length == 0) return;

        _markWord = word;
        foreach (MarkRange r in WordMarker.MarksFor(doc.Text, word, doc.Language))
        {
            int line = doc.LineIndexOf(r.Start);
            if (!_marksByLine.TryGetValue(line, out List<MarkRange>? bucket))
            {
                bucket = [];
                _marksByLine[line] = bucket;
            }
            bucket.Add(r);
            _markCount++;
        }
        InvalidateVisual();
    }

    private void ClearMarks()
    {
        if (_markCount == 0 && _markWord.Length == 0) return;
        _markWord = string.Empty;
        _marksByLine.Clear();
        _markCount = 0;
        _markTextRef = null;
        InvalidateVisual();
    }

    /// <summary>
    /// 仅供自检：当前标记词（空串 = 没有标记）。双击高亮是**纯视觉**产物，
    /// 「算出来了但没画」和「压根没算」在截图里长得一样，只有断言能分辨。
    /// </summary>
    internal string MarkWord => _markWord;

    /// <summary>仅供自检：当前被点亮的命中总数。</summary>
    internal int MarkCount => _markCount;

    /// <summary>仅供自检：某个可见行上的全部标记区间。</summary>
    internal IReadOnlyList<MarkRange> MarksOfLine(int line)
        => _marksByLine.TryGetValue(line, out List<MarkRange>? b) ? b : [];

    /// <summary>仅供自检：等价于「单击正文」那条清掉分词的路径。</summary>
    internal void ClearMarksForTest() => ClearMarks();

    // ---------------- 右侧滑动导航条 ----------------

    /// <summary>导航条几何（控件局部坐标，Y 向下）。不可滚 / 尺寸不足时 <c>Valid = false</c>。</summary>
    internal readonly record struct OverviewMetrics(
        double TrackX, double TrackY, double TrackHeight, double ThumbHeight, double ThumbTop, bool Valid);

    internal OverviewMetrics OverviewGeometry()
    {
        DocumentModel? doc = Document;
        double w = Bounds.Width, h = Bounds.Height;
        if (doc is null || w <= 0 || h <= 0 || w < OverviewWidth * 3) return default;

        double track = h - OverviewPadding * 2;
        int total = doc.LineCount;
        int visible = VisibleLineCount;
        if (!OverviewBar.IsScrollable(total, visible) || track <= 0) return default;

        double thumbH = OverviewBar.ThumbHeight(track, total, visible, OverviewMinThumb);
        double thumbTop = OverviewPadding + OverviewBar.ThumbTop(track, total, visible, thumbH, FirstVisibleLine);
        return new OverviewMetrics(w - OverviewWidth, OverviewPadding, track, thumbH, thumbTop, true);
    }

    /// <summary>
    /// 尝试开始导航条拖拽：命中导航条则跳转/翻页、捕获指针并返回 true。
    /// 压在滑块上 → 保持「按下点与滑块顶端」的相对位置；点在轨道空白处 → 翻一屏
    /// （对照系统滚动条点轨道的原生行为）。
    /// </summary>
    private bool TryStartOverviewDrag(Point p, IPointer pointer)
    {
        OverviewMetrics g = OverviewGeometry();
        if (!g.Valid) return false;
        if (p.X < g.TrackX || p.Y < g.TrackY || p.Y > g.TrackY + g.TrackHeight) return false;

        double rel = p.Y - g.ThumbTop;
        if (rel >= 0 && rel <= g.ThumbHeight)
        {
            _overviewGrabOffset = rel;
        }
        else
        {
            _overviewGrabOffset = g.ThumbHeight / 2;
            PageOverview(p.Y < g.ThumbTop ? -1 : +1);
        }

        _overviewDragging = true;
        _dragging = false;                   // 与「拖拽选字」互斥
        pointer.Capture(this);
        HideCompletion();
        InvalidateVisual();
        return true;
    }

    /// <summary>按一屏翻页（导航条点轨道空白处 / 未来的 PgUp 复用点）。</summary>
    private void PageOverview(int direction)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;
        int max = Math.Max(0, doc.LineCount - 1);
        FirstVisibleLine = Math.Clamp(FirstVisibleLine + direction * VisibleLineCount, 0, max);
    }

    /// <summary>
    /// 导航条拖拽内核。抽出来供自检驱动的理由与 HandleKey / HandleWheel 完全一样：
    /// <c>PointerEventArgs</c> 在自检里没法伪造（得先凑一个 IPointer / PointerPoint 实现），
    /// 而这里真正要验证的是「像素 → 首行」这段映射，不是 Avalonia 的指针派发。
    /// </summary>
    internal void HandleOverviewDrag(double y)
    {
        DocumentModel? doc = Document;
        if (doc is null || !_overviewDragging) return;

        OverviewMetrics g = OverviewGeometry();
        if (!g.Valid) return;

        double thumbTopRel = y - _overviewGrabOffset - g.TrackY;
        int line = OverviewBar.LineFromThumbTop(thumbTopRel, g.TrackHeight, doc.LineCount,
            VisibleLineCount, g.ThumbHeight);
        FirstVisibleLine = Math.Clamp(line, 0, Math.Max(0, doc.LineCount - 1));
        InvalidateVisual();
    }

    /// <summary>仅供自检：模拟「在 y 处按住滑块」（自检没有真指针，只能借这个入口）。</summary>
    internal void BeginOverviewDragForTest(double y)
    {
        OverviewMetrics g = OverviewGeometry();
        if (!g.Valid) return;
        _overviewGrabOffset = Math.Clamp(y - g.ThumbTop, 0, g.ThumbHeight);
        _overviewDragging = true;
    }

    /// <summary>仅供自检：模拟松开（结束拖拽）。</summary>
    internal void EndOverviewDragForTest() => _overviewDragging = false;

    /// <summary>仅供自检：是否正在拖导航条。</summary>
    internal bool IsOverviewDragging => _overviewDragging;

    // ---------------- 右键菜单（对照 LiteReader.cpp:4149 的 showEditorMenu） ----------------
    //
    // 为什么不用 XAML 里的 ContextMenu 资源：
    //   有三项的**可用性**取决于运行时状态（有没有选区 → 复制/剪切；剪贴板里有没有文本 → 粘贴；
    //   光标或选区里有没有标识符 → 跳转到定义），而「右键时先把光标落到点击处」这条原版行为
    //   更是只能在代码里做。全部放在控件自身之后，菜单跟着控件走，切标签不会串。

    private ContextMenu BuildContextMenu()
    {
        _miCopy = new MenuItem { Header = "复制", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        _miCut = new MenuItem { Header = "剪切", InputGesture = new KeyGesture(Key.X, KeyModifiers.Control) };
        _miPaste = new MenuItem { Header = "粘贴", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control) };
        var miSelectAll = new MenuItem { Header = "全选", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        var miTerminal = new MenuItem { Header = "在命令提示符中打开" };
        _miGoToDef = new MenuItem { Header = "跳转到定义", InputGesture = new KeyGesture(Key.F12) };

        _miCopy.Click += (_, _) => Copy();
        _miCut.Click += (_, _) => Cut();
        _miPaste.Click += (_, _) => Paste();
        miSelectAll.Click += (_, _) => SelectAll();
        miTerminal.Click += (_, _) => OpenTerminalHere();
        _miGoToDef.Click += (_, _) => GoToDefinitionWithReport();

        // 分组顺序与原版 showEditorMenu 逐条对齐：剪贴板三件 → 分隔 → 全选 → 分隔 →
        // 在命令提示符中打开 → 分隔 → 跳转到定义
        var menu = new ContextMenu();
        menu.Items.Add(_miCopy);
        menu.Items.Add(_miCut);
        menu.Items.Add(_miPaste);
        menu.Items.Add(new Separator());
        menu.Items.Add(miSelectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(miTerminal);
        menu.Items.Add(new Separator());
        menu.Items.Add(_miGoToDef);
        return menu;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        Focus();        // 右键也把焦点给编辑区，否则菜单里的动作会落到别的控件上

        if (e.TryGetPosition(this, out Point p)) PlaceCaretForContext(p);

        SyncMenuEnabled();
        // 剪贴板那条是异步的，只能补一次刷新（见 SyncPasteEnabledAsync 的说明）
        _ = SyncPasteEnabledAsync();
    }

    /// <summary>
    /// 右键时把光标落到点击处（对照 LiteReader.cpp:4037 的 placeCaretForContext）。
    /// 唯一的例外：落点已在**现有选区内**时保留选区不动 ——
    /// 否则用户好不容易选了一段文字、想右键复制，选区会被这一下点没了。
    /// </summary>
    private void PlaceCaretForContext(Point p)
    {
        DocumentModel? doc = Document;
        if (doc is null || p.X < GutterWidth) return;      // 点行号槽：不动光标

        int offset = OffsetFromPoint(p);
        if (HasSelection && offset >= SelStart && offset < SelEnd) return;

        AnchorOffset = offset;
        CaretOffset = offset;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    /// <summary>
    /// 刷新菜单项的可否用状态。三条对照原版 showEditorMenu：
    /// 无选区 → 复制/剪切灰掉；取不到标识符 → 跳转到定义灰掉。
    /// </summary>
    private void SyncMenuEnabled()
    {
        bool hasSel = HasSelection;
        _miCopy.IsEnabled = hasSel;
        _miCut.IsEnabled = hasSel && !IsReadOnly;
        _miGoToDef.IsEnabled = TargetName().Length > 0;
    }

    /// <summary>
    /// 剪贴板里没有文本时把「粘贴」灰掉（对照原版的 IsClipboardFormatAvailable(CF_UNICODETEXT)）。
    ///
    /// ⚠️ Avalonia 的剪贴板 API 全是异步的，所以这条只能在菜单弹出之后补一次刷新。
    ///   绝不能为了「菜单弹出时就是对的」去 .Result / .Wait() 同步阻塞 ——
    ///   那会在 UI 线程上等自己，是教科书式的死锁。
    /// </summary>
    private async Task SyncPasteEnabledAsync()
    {
        bool has = false;
        try
        {
            IClipboard? clip = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clip is not null) has = !string.IsNullOrEmpty(await clip.TryGetTextAsync());
        }
        catch { has = false; }      // 剪贴板被别的进程独占：当作没有，不让菜单项开着天窗
        _miPaste.IsEnabled = has && !IsReadOnly;
    }

    /// <summary>当前「跳转目标」标识符：优先取选区里的整词，否则取光标下的整词。</summary>
    private string TargetName()
    {
        DocumentModel? doc = Document;
        if (doc is null) return string.Empty;
        return DefinitionFinder.TargetName(doc,
            HasSelection ? SelStart : -1,
            HasSelection ? SelEnd : -1,
            CaretOffset);
    }

    /// <summary>与 MainWindow 的 F12 同语义，但把结果写进状态栏（编辑区自己拿不到主窗口 VM）。</summary>
    private void GoToDefinitionWithReport()
    {
        string name = TargetName();
        if (name.Length == 0)
        {
            StatusReported?.Invoke("跳转到定义：光标不在标识符上");
            return;
        }
        StatusReported?.Invoke(GoToDefinition()
            ? $"跳转到定义：{name}"
            : $"未找到 {name} 的定义（本功能是单文件内的启发式查找，不做语义解析）");
    }

    /// <summary>当前文件所在目录打开外部终端。目录决策与「工具」菜单共用 TerminalLauncher
    /// （文件所在目录 → 上次打开的文件夹 → 用户主目录）。
    /// </summary>
    private void OpenTerminalHere()
    {
        string dir = TerminalLauncher.ResolveDirectory(Document?.FilePath, AppConfigStore.Current.LastFolder);
        bool ok = TerminalLauncher.TryOpen(dir, out string used);
        StatusReported?.Invoke(ok
            ? $"已在「{dir}」打开终端（{used}）"
            : $"打开终端失败：本平台没有找到可用的终端程序。目标目录：{dir}");
    }

    // ---- 仅供自检的入口：ContextRequested 事件在自检里伪造不出来 ----

    /// <summary>驱动一次「右键请求」：安顿光标 + 刷新菜单项状态（不含异步的剪贴板检查）。</summary>
    internal void ContextRequestForTest(Point p)
    {
        PlaceCaretForContext(p);
        SyncMenuEnabled();
    }

    /// <summary>菜单项标题（按显示顺序）。</summary>
    internal IReadOnlyList<string> ContextMenuHeaders()
        => ContextMenu is null
            ? []
            : ContextMenu.Items.OfType<MenuItem>()
                .Select(i => i.Header?.ToString() ?? string.Empty)
                .ToList();

    /// <summary>某个菜单项当前是否可用（按标题查）。</summary>
    internal bool ContextMenuEnabled(string header)
    {
        if (ContextMenu is null) return false;
        MenuItem? mi = ContextMenu.Items.OfType<MenuItem>()
            .FirstOrDefault(i => string.Equals(i.Header?.ToString(), header, StringComparison.Ordinal));
        return mi?.IsEnabled ?? false;
    }

    // ---------------- 代码补全 ----------------

    private void HideCompletion()
    {
        if (_compItems.Count == 0) return;
        _compItems.Clear();
        _compIndex = 0;
        _compWordStart = -1;
        _compCaret = -1;
        _compDocVersion = -1;
        _compRect = null;
        InvalidateVisual();
    }

    /// <summary>
    /// 重建候选列表。光标或文档变了才重算；同一位置不重复算（打字时每次输入都会调到这里）。
    /// </summary>
    private void UpdateCompletion()
    {
        DocumentModel? doc = Document;
        if (doc is null || IsReadOnly || !EditorSettings.AutoComplete || HasSelection)
        {
            HideCompletion();
            return;
        }
        if (_compCaret == CaretOffset && _compDocVersion == doc.Version && CompletionVisible) return;

        IReadOnlyList<CompletionItem> items = CompletionEngine.Suggest(doc, CaretOffset);
        (int wordStart, _) = CompletionEngine.WordPrefix(doc, CaretOffset);

        _compItems.Clear();
        _compItems.AddRange(items);
        _compIndex = 0;
        _compWordStart = wordStart;
        _compCaret = CaretOffset;
        _compDocVersion = doc.Version;
        InvalidateVisual();
    }

    /// <summary>
    /// 确认当前高亮候选。函数会补出 <c>()</c> 并把光标放进括号里（对照 LiteReader.cpp 的 acceptCompletion）。
    /// </summary>
    internal void AcceptCompletion()
    {
        DocumentModel? doc = Document;
        if (doc is null || !CompletionVisible || _compWordStart < 0) return;

        CompletionItem item = _compItems[Math.Clamp(_compIndex, 0, _compItems.Count - 1)];
        int ws = _compWordStart;
        int we = Math.Clamp(CaretOffset, ws, doc.CharCount);

        int caret;
        if (item.IsFunction)
        {
            char nxt = we < doc.CharCount ? doc.Text[we] : '\0';
            if (nxt == '(')
            {
                // 名字后面已经有 '(' 了：只替换名字，光标进到括号内，不再补一对
                doc.Replace(ws, we - ws, item.Text);
                caret = ws + item.Text.Length;
                if (caret + 1 < doc.CharCount && doc.Text[caret] == '(' && doc.Text[caret + 1] == ')') caret++;
                else if (caret < doc.CharCount && doc.Text[caret] == '(') caret++;
            }
            else
            {
                doc.Replace(ws, we - ws, item.Text + "()");
                caret = ws + item.Text.Length + 1;      // 光标落在 () 之间
            }
        }
        else
        {
            doc.Replace(ws, we - ws, item.Text);
            caret = ws + item.Text.Length;
        }

        HideCompletion();
        SetCaret(caret);
    }

    /// <summary>光标是否正落在候选窗口上；是则返回被点中的序号，否则 -1。</summary>
    private int CompletionHitTest(Point p)
    {
        if (!CompletionVisible || _compRect is not { } rect) return -1;
        if (!rect.Contains(p)) return -1;
        int vis = Math.Min(_compItems.Count, CompMaxVisible);
        int idx = (int)((p.Y - rect.Y - CompPadding / 2) / CompItemHeight);
        return idx >= 0 && idx < vis ? idx : -1;
    }

    /// <summary>
    /// 仅供自检读取的当前补全候选（空列表 = 面板未弹出）。
    /// 候选列表同样是自绘的纯视觉产物，只能靠断言确认「打字真的弹出了候选」，
    /// 以及「确认后文本真的被替换成了什么」。
    /// </summary>
    internal IReadOnlyList<CompletionItem> CompletionItems => _compItems;

    /// <summary>仅供自检：当前高亮的候选序号。</summary>
    internal int CompletionIndex => _compIndex;

    // ---------------- 光标定位的公开入口 ----------------

    /// <summary>把光标放到 offset（同时清掉选区），并保证可见。</summary>
    private void SetCaret(int offset)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;
        offset = Math.Clamp(offset, 0, doc.CharCount);
        AnchorOffset = offset;
        CaretOffset = offset;
        EnsureCaretVisible();
        InvalidateVisual();
    }

    /// <summary>选中 [start, end) 并把光标移到末尾 —— 供查找与跳转定义使用。</summary>
    public void SelectRange(int start, int end)
    {
        DocumentModel? doc = Document;
        if (doc is null) return;
        AnchorOffset = Math.Clamp(start, 0, doc.CharCount);
        CaretOffset = Math.Clamp(end, 0, doc.CharCount);
        EnsureCaretVisible();
        InvalidateVisual();
    }

    /// <summary>仅供自检：偏移 → 控件内坐标（自检没法像控件那样量出文本宽度）。</summary>
    internal Point PointOfOffsetForTest(int offset) => PointFromOffset(offset);

    /// <summary>光标处的标识符（不在词上返回空串）。有选区时优先取选区里的整词。</summary>
    public string WordAtCaret(int? offset = null)
    {
        DocumentModel? doc = Document;
        if (doc is null) return string.Empty;
        return offset is null
            ? TargetName()
            : DefinitionFinder.TargetName(doc, -1, -1, offset.Value);
    }

    /// <summary>
    /// 跳转到标识符的定义处（单文件启发式，见 DefinitionFinder）。
    /// </summary>
    /// <returns>找到并跳转了返回 true；没找到返回 false（调用方可据此提示用户）。</returns>
    public bool GoToDefinition()
    {
        DocumentModel? doc = Document;
        if (doc is null) return false;

        string name = DefinitionFinder.TargetName(
            doc,
            HasSelection ? SelStart : -1,
            HasSelection ? SelEnd : -1,
            CaretOffset);
        if (name.Length == 0) return false;

        int hit = DefinitionFinder.FindBest(doc, name, CaretOffset);
        if (hit < 0) return false;

        SelectRange(hit, hit + name.Length);
        return true;
    }

    // ---------------- 渲染 ----------------

    private static IBrush Res(ThemeService? themes, string key, string hex)
        => themes?.Brush(key, Color.Parse(hex)) ?? new SolidColorBrush(Color.Parse(hex));

    /// <summary>等宽字体下 CJK/全角占 2 格。与 LiteReader.cpp 的 isWideChar 同一张表。</summary>
    private static bool IsWideChar(char c)
    {
        if (c == 0) return false;
        if (c >= 0x1100 && c <= 0x115F) return true;
        if (c >= 0x2E80 && c <= 0x303E) return true;
        if (c >= 0x3041 && c <= 0x33FF) return true;
        if (c >= 0x3400 && c <= 0x4DBF) return true;
        if (c >= 0x4E00 && c <= 0x9FFF) return true;
        if (c >= 0xA000 && c <= 0xA4CF) return true;
        if (c >= 0xAC00 && c <= 0xD7A3) return true;
        if (c >= 0xF900 && c <= 0xFAFF) return true;
        if (c >= 0xFE30 && c <= 0xFE4F) return true;
        if (c >= 0xFF00 && c <= 0xFFEF) return true;
        if (c >= 0x20000 && c <= 0x2FA1F) return true;
        return false;
    }

    public override void Render(DrawingContext ctx)
    {
        ThemeService? themes = ThemeService.Current;
        double fontSize = EditorSettings.FontSize;

        IBrush bg = Res(themes, "EditorBackground", "#282C34");
        IBrush fg = Res(themes, "EditorForeground", "#ABB2BF");
        IBrush gutterBg = Res(themes, "EditorGutterBackground", "#21252B");
        IBrush gutterFg = Res(themes, "EditorGutterForeground", "#5C6370");
        IBrush curLine = Res(themes, "EditorCurrentLine", "#2C313A");
        IBrush selBg = Res(themes, "EditorSelection", "#3E4451");
        IBrush markBg = Res(themes, "EditorMarkBackground", "#1A4046");

        double w = Bounds.Width, h = Bounds.Height;
        ctx.FillRectangle(bg, new Rect(0, 0, w, h));

        DocumentModel? doc = Document;
        double gutterW = GutterWidth;
        ctx.FillRectangle(gutterBg, new Rect(0, 0, gutterW, h));

        if (doc is null || doc.LineCount == 0)
        {
            DrawPlaceholder(ctx, fg, gutterW, fontSize);
            return;
        }

        double lineH = LineHeight;
        int first = Math.Clamp(FirstVisibleLine, 0, Math.Max(0, doc.LineCount - 1));
        int last = Math.Min(doc.LineCount, first + VisibleLineCount + 1);

        int caretLine = doc.LineIndexOf(CaretOffset);
        int selA = SelStart, selB = SelEnd;

        // ---- 第一遍：背景层（当前行 → 分词标记 → 选区），都必须在文字下面 ----
        for (int line = first; line < last; line++)
        {
            double y = (line - first) * lineH;

            if (line == caretLine)
                ctx.FillRectangle(curLine, new Rect(gutterW, y, Math.Max(0, w - gutterW), lineH));

            // 双击分词高亮：压在当前行之上、选区之下。
            // 这个层次顺序对照原版 drawLine 里单次取色的优先级 mt > sel > mk
            // （配对 > 选区 > 分词标记）—— 后画的盖住先画的，所以标记必须排在选区之前。
            DrawLineMarks(ctx, markBg, line, y, lineH, doc);

            if (selA == selB) continue;

            int ls = doc.LineStartOffset(line);
            int len = doc.LineLength(line);
            int from = Math.Clamp(selA - ls, 0, len);
            int to = Math.Clamp(selB - ls, 0, len);
            bool includesNewline = selA <= doc.LineEndOffset(line) && selB > doc.LineEndOffset(line) && doc.LineHasNewline(line);
            if (from >= to && !includesNewline) continue;

            LineLayout lay = LayoutOf(line);
            double x1 = XOfCol(lay, from);
            double x2 = includesNewline ? Math.Max(XOfCol(lay, len), TextViewportWidth) : XOfCol(lay, to);
            if (x2 <= x1) continue;

            using (ctx.PushClip(new Rect(gutterW, 0, Math.Max(0, w - gutterW), h)))
                ctx.FillRectangle(selBg, new Rect(TextOriginX + x1, y, x2 - x1, lineH));
        }

        // ---- 括号配对高亮（背景层，在文字下面才不挡字） ----
        UpdateBracketMatch();
        if (_match is { } mp)
        {
            IBrush matchBg = Res(themes, "EditorMatchBackground", "#3A3A3A");
            using (ctx.PushClip(new Rect(gutterW, 0, Math.Max(0, w - gutterW), h)))
            {
                DrawMatchBox(ctx, matchBg, mp.AStart, mp.ALength, first, lineH, doc);
                DrawMatchBox(ctx, matchBg, mp.BStart, mp.BLength, first, lineH, doc);
            }
        }

        // ---- 第二遍：行号 + 正文 ----
        using (ctx.PushClip(new Rect(0, 0, Math.Max(0, gutterW), h)))
        {
            for (int line = first; line < last; line++)
            {
                double y = (line - first) * lineH;
                var no = new FormattedText((line + 1).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(MonoFont), fontSize, gutterFg);
                ctx.DrawText(no, new Point(gutterW - GutterPadding - no.Width, y));
            }
        }

        // 正文的右边界要停在导航条左边：文字跑到导航条底下去不仅看不清，
        // 还会让「横向滚动到底」变成一个看不见的边界。
        double textRight = Math.Max(0, w - OverviewWidth);

        using (ctx.PushClip(new Rect(gutterW, 0, Math.Max(0, textRight - gutterW), h)))
        {
            for (int line = first; line < last; line++)
            {
                double y = (line - first) * lineH;
                LineLayout lay = LayoutOf(line);
                for (int k = 0; k < lay.Parts.Length; k++)
                {
                    double px = TextOriginX + lay.X[k];
                    if (px > textRight) break;               // 超右边界就不再画后面的段
                    ctx.DrawText(lay.Parts[k], new Point(px, y));
                }
            }
        }

        // ---- 光标 ----
        if (_caretOn && IsFocused && !HasSelection)
        {
            int col = CaretOffset - doc.LineStartOffset(caretLine);
            LineLayout lay = LayoutOf(caretLine);
            double x = TextOriginX + XOfCol(lay, col);
            double y = (caretLine - first) * lineH;
            using (ctx.PushClip(new Rect(gutterW, 0, Math.Max(0, textRight - gutterW), h)))
                ctx.FillRectangle(fg, new Rect(x, y, CaretThickness, lineH));
        }

        // ---- 右侧滑动导航条 ----
        DrawOverview(ctx, themes);

        // ---- 代码补全候选列表（最上层） ----
        DrawCompletion(ctx, themes, fontSize, lineH, first, w, h, caretLine, doc);
    }

    /// <summary>
    /// 把落在 line 上的分词标记画成底色块。
    /// 区间按行分桶（见 _marksByLine 的说明），所以这里只查一次字典、
    /// 只遍历「这一行上的命中」，代价与全文命中总数无关。
    /// </summary>
    private void DrawLineMarks(DrawingContext ctx, IBrush brush, int line, double y, double lineH, DocumentModel doc)
    {
        if (_markCount == 0) return;
        if (!_marksByLine.TryGetValue(line, out List<MarkRange>? bucket)) return;

        int ls = doc.LineStartOffset(line);
        int lineLen = doc.LineLength(line);
        LineLayout lay = LayoutOf(line);
        double textRight = Bounds.Width;

        using (ctx.PushClip(new Rect(GutterWidth, 0, Math.Max(0, textRight - GutterWidth), Bounds.Height)))
        {
            foreach (MarkRange r in bucket)
            {
                int from = Math.Clamp(r.Start - ls, 0, lineLen);
                int to = Math.Clamp(r.End - ls, 0, lineLen);
                if (to <= from) continue;

                double x1 = TextOriginX + XOfCol(lay, from);
                double x2 = TextOriginX + XOfCol(lay, to);
                if (x2 <= x1) x2 = x1 + CharWidth * (to - from);   // 段边界对齐时的兜底
                if (x1 > textRight) break;                          // 命中按起点升序，后面只会更靠右

                ctx.FillRectangle(brush, new Rect(x1, y, x2 - x1, lineH));
            }
        }
    }

    /// <summary>
    /// 画右侧滑动导航条：一条轨道 + 一个滑块。滑块高度 = 可视行数 / 总行数，
    /// 所以它同时表达了「当前页面占总页面多少」和「还能往哪滚」。
    /// 圆角而不是直角 —— 在 13 px 的宽度下直角看着像控件被截断了。
    /// </summary>
    private void DrawOverview(DrawingContext ctx, ThemeService? themes)
    {
        OverviewMetrics g = OverviewGeometry();
        if (!g.Valid) return;

        IBrush track = Res(themes, "EditorOverviewBackground", "#21252B");
        IBrush thumb = Res(themes, "EditorOverviewThumb", "#4B5263");
        double radius = OverviewWidth / 2;

        ctx.DrawRectangle(track, null, new Rect(g.TrackX, g.TrackY, OverviewWidth, g.TrackHeight), radius, radius);
        ctx.DrawRectangle(thumb, null, new Rect(g.TrackX, g.ThumbTop, OverviewWidth, g.ThumbHeight), radius, radius);
    }

    /// <summary>
    /// 把一个括号字符画成高亮块。配对的两端可能跨行，所以逐端单独算坐标。
    /// </summary>
    private void DrawMatchBox(DrawingContext ctx, IBrush brush, int start, int len,
                             int firstLine, double lineH, DocumentModel doc)
    {
        if (len <= 0) return;
        int line = doc.LineIndexOf(start);
        if (line < firstLine || line >= firstLine + VisibleLineCount + 1) return;   // 不在可视区，省下布局

        int col = start - doc.LineStartOffset(line);
        LineLayout lay = LayoutOf(line);
        double x1 = TextOriginX + XOfCol(lay, col);
        double x2 = TextOriginX + XOfCol(lay, Math.Min(col + len, doc.LineLength(line)));
        if (x2 <= x1) x2 = x1 + CharWidth * len;      // 段边界对齐时的兜底，至少给一个字符宽

        double y = (line - firstLine) * lineH;
        // 左右各外扩 1 px：括号字符本身的字形左右留白较多，不加外扩看起来像没选中
        ctx.FillRectangle(brush, new Rect(x1 - 1, y, x2 - x1 + 2, lineH));
    }

    /// <summary>
    /// 自绘候选列表：贴在光标下方，超出视口就往上/往左挪。
    /// 对照 LiteReader.cpp 的 showCompletion（那边是独立弹窗，这里是画在自己身上）。
    /// </summary>
    private void DrawCompletion(DrawingContext ctx, ThemeService? themes, double fontSize,
                               double lineH, int firstLine, double w, double h,
                               int caretLine, DocumentModel doc)
    {
        if (!CompletionVisible)
        {
            _compRect = null;
            return;
        }

        int vis = Math.Min(_compItems.Count, CompMaxVisible);

        // 只量可见的那几条：候选最多 200 个，全量测量在每帧都做是浪费
        var parts = new FormattedText[vis];
        IBrush itemFg = Res(themes, "CompletionForeground", "#D4D4D4");
        double maxW = 0;
        for (int i = 0; i < vis; i++)
        {
            string label = _compItems[i].IsFunction ? _compItems[i].Text + "  ƒ" : _compItems[i].Text;
            parts[i] = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(MonoFont), fontSize, itemFg);
            maxW = Math.Max(maxW, parts[i].Width);
        }

        double boxW = Math.Max(140, maxW + 22);
        double boxH = vis * CompItemHeight + CompPadding * 2;

        int col = CaretOffset - doc.LineStartOffset(caretLine);
        double cx = TextOriginX + XOfCol(LayoutOf(caretLine), col);
        double top = (caretLine - firstLine) * lineH;

        double bx = Math.Min(cx, Math.Max(0, w - OverviewWidth - boxW));
        double by = top + lineH;
        if (by + boxH > h) by = Math.Max(0, top - boxH);      // 下方放不下就翻到光标上方
        var box = new Rect(bx, by, boxW, boxH);
        _compRect = box;                                      // 记下来供命中测试用

        IBrush popupBg = Res(themes, "CompletionBackground", "#21252B");
        IBrush border = Res(themes, "CompletionBorder", "#3E4451");
        IBrush selItemBg = Res(themes, "CompletionSelectionBackground", "#3E4451");
        IBrush selItemFg = Res(themes, "CompletionSelectionForeground", "#FFFFFF");

        ctx.FillRectangle(popupBg, box);
        ctx.DrawRectangle(null, new Pen(border, 1), box);

        for (int i = 0; i < vis; i++)
        {
            double iy = by + CompPadding / 2 + i * CompItemHeight;
            var row = new Rect(bx + 1, iy, boxW - 2, CompItemHeight);
            if (i == _compIndex)
            {
                ctx.FillRectangle(selItemBg, row);
                string label = _compItems[i].IsFunction ? _compItems[i].Text + "  ƒ" : _compItems[i].Text;
                ctx.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(MonoFont), fontSize, selItemFg), new Point(bx + 8, iy));
            }
            else
            {
                ctx.DrawText(parts[i], new Point(bx + 8, iy));
            }
        }

        // 候选多于可见条数时，在右下角标出总数，用户才知道还有内容
        if (_compItems.Count > vis)
        {
            IBrush hintFg = Res(themes, "CompletionForeground", "#D4D4D4");
            var hint = new FormattedText($"{_compIndex + 1}/{_compItems.Count}", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(MonoFont), fontSize - 1, hintFg);
            ctx.DrawText(hint, new Point(bx + boxW - hint.Width - 6, by + boxH - hint.Height - 2));
        }
    }

    private void DrawPlaceholder(DrawingContext ctx, IBrush fg, double gutterW, double fontSize)
    {
        var hint = new FormattedText(
            "未打开文件 —— 文件 → 打开文件（Ctrl+O）",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(MonoFont), fontSize + 1, fg);
        ctx.DrawText(hint, new Point(gutterW + 16, 24));
    }
}
