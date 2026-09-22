// LiteReader · Avalonia 跨平台版 —— 右侧滑动导航条的几何换算
// 作者：hujie  创建：2026-09-18
//
// 对照 LiteReader.cpp 的 WS_VSCROLL 系统滚动条（:5991 的窗口样式、:4761 的 WM_VSCROLL）。
// 原版直接把窗口样式设成 WS_VSCROLL，滑块高度、按比例定位、拖动跳转
// （SB_THUMBTRACK 的 HIWORD(wp)）全部由 Windows 负责。
//
// 我们不能再这么干，两条原因：
//   1) 编辑区是**自绘**的（Control.Render），控件树里根本没有 ScrollViewer，
//      给它挂一个 ScrollBar 反而要额外同步 Offset↔FirstVisibleLine 两套状态；
//   2) 系统滚动条的观感与亮/暗主题无关 —— 在 5 套主题（含 One Light 与极致黑）下
//      它都是同一个灰，跟自绘的编辑区完全不搭。
//
// 所以自己画一个：**轨道 + 滑块**，滑块高度正比于「可视行数 / 总行数」、
// 位置正比于 FirstVisibleLine —— 这正是「当前页面占总页面多少」的直观表达。
//
// 这里只有几何，没有一行视图代码：纯函数、可被自检直接断言
// （见 Diagnostics/SelfTest.cs 的 OverviewSmoke）。坐标约定：
//   · 全部是**控件局部坐标**，Y 向下；
//   · track 参数指轨道高度，thumbTop 参数指**相对轨道顶端**的偏移（0 = 最上面）。
//
// ⚠️ 为什么滑块高度要夹一个下限（minThumb）：
//   10 万行的文件、可视 40 行 → 滑块只有轨道的 0.04%，在 800 px 高的窗口里是 0.3 px ——
//   既看不见也拖不住。这是所有编辑器都必须处理的事（VS Code 的 minimap 用的是另一套方案，
//   系统滚动条则直接把最小值定死）。

namespace LiteReader.Models;

public static class OverviewBar
{
    /// <summary>是否值得显示滑块。总行数 ≤ 可视行数时没有任何可滚的余地，画出来只会误导。</summary>
    public static bool IsScrollable(int totalLines, int visibleLines)
        => totalLines > 0 && visibleLines > 0 && totalLines > visibleLines;

    /// <summary>
    /// 滑块高度 = 轨道高 × 可视占比，并夹在 [min(minThumb, track), track] 之间。
    /// 上界必须是 track 而不是 minThumb —— 文档很短（但仍可滚，比如多一行）时
    /// 按比例算出来的高度可能已经接近 track，不该被 minThumb 的反向夹取拉长。
    /// </summary>
    public static double ThumbHeight(double track, int totalLines, int visibleLines, double minThumb)
    {
        if (track <= 0 || totalLines <= 0 || visibleLines <= 0) return 0;
        double ratio = visibleLines / (double)totalLines;
        return Math.Clamp(track * ratio, Math.Min(minThumb, track), track);
    }

    /// <summary>
    /// 滑块顶端相对轨道顶端的偏移。
    /// firstLine = 0 → 0；firstLine 到「最后可滚位」（totalLines - visibleLines）→ track - thumbH。
    /// ⚠️ 分母是 totalLines - visibleLines，不是 totalLines - 1：滚动条表达的是
    ///   「首行还能往下走几行」，走到最后一行时滑块该正好贴底。
    /// </summary>
    public static double ThumbTop(double track, int totalLines, int visibleLines, double thumbH, int firstLine)
    {
        double travel = track - thumbH;
        if (travel <= 0) return 0;
        int maxFirst = Math.Max(1, totalLines - visibleLines);
        double t = Math.Clamp(firstLine / (double)maxFirst, 0, 1);
        return t * travel;
    }

    /// <summary>
    /// 反向换算：滑块顶端偏移 → FirstVisibleLine。拖拽跳转就靠这个。
    /// 与 <see cref="ThumbTop"/> 必须互为逆运算 —— SelfTest 里有一条往返断言钉住这一点。
    /// </summary>
    public static int LineFromThumbTop(double thumbTop, double track, int totalLines, int visibleLines, double thumbH)
    {
        double travel = track - thumbH;
        if (travel <= 0) return 0;
        int maxFirst = Math.Max(0, totalLines - visibleLines);
        double t = Math.Clamp(thumbTop / travel, 0, 1);
        return (int)Math.Round(t * maxFirst);
    }
}
