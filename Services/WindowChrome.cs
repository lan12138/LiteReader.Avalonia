// LiteReader · Avalonia 跨平台版 —— 窗口外框（系统标题栏 + 边框）跟随主题
// 作者：hujie  创建：2026-09-19
//
// 对应 README §9.1 表格里「标题栏 / 外边框跟随主题」那一行。
//
// 问题：编辑器区是自绘的、颜色跟着主题走，但**窗口外框（标题栏 + 四周那圈边框）是系统画的**，
//   于是出现「One Light 浅色正文 + 纯黑标题栏/边框」这种一眼就不对的组合（截图见 DEVELOPMENT.md §7.5）。
//
// 为什么走 DWM 而不是自绘标题栏：
//   自绘要连窗口按钮、拖拽、双击最大化、八向缩放、Snap 布局一起接管，成本高且会把系统行为
//   换成自己的一份（Wayland/macOS 上还各有一套规矩）。而 Windows 11 已经从 22000 起提供
//   三个属性让程序**直接指定**标题栏底色/文字色/边框色 —— 系统把绘制和行为都留着，
//   我们只喂颜色。代价是这一项在 Linux/macOS 上做不到（见下）。
//
//   DWM 属性（dwmapi.h）：
//     DWMWA_USE_IMMERSIVE_DARK_MODE = 20   Win10 1809+（更早的 build 用 19）
//                                          注意：它管的是**标题栏按钮的字形明暗**与
//                                          系统默认底色，所以亮色主题必须传 0
//     DWMWA_BORDER_COLOR            = 34   Win11 22000+  外边框
//     DWMWA_CAPTION_COLOR           = 35   Win11 22000+  标题栏底色
//     DWMWA_TEXT_COLOR              = 36   Win11 22000+  标题文字
//   三个颜色属性的参数都是 **COLORREF（0x00BBGGRR）**，不是 Avalonia 的 #AARRGGBB。
//
// ★ 缩水处（写在文档里，不含糊）：**Linux / macOS 不做**。
//   两边的标题栏都归桌面环境 / 窗口管理器，没有「程序指定颜色」的公开接口：
//     · macOS：只有 `NSWindow.appearance`（跟随系统深浅色）与全透明标题栏两条路，
//       没有「指定十六进制颜色」的 API；硬做要 ExtendClientArea 自绘，代价见上。
//     · Linux：X11 下 WM 各自为政（Mutter/KWin/Xfwm 各有各的规则），Wayland 更是协议层
//       就不给客户端画装饰的机会 —— 由合成器/DE 主题决定，程序无从插手。
//   所以这一项在非 Windows 上是「如实不做」，而不是「忘了做」。

using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;

namespace LiteReader.Services;

/// <summary>
/// 施加窗口外框配色后的结果。<see cref="AppliedDarkMode"/> 是**传进去的意图**（用于断言方向对不对），
/// <see cref="AttributesSet"/> 是 DWM 真正接受了几项 —— 两者分开，才能区分
/// 「我们传对了」和「系统收下了」。
/// </summary>
internal readonly record struct ChromeResult(
    bool Applied,
    bool DarkMode,
    bool CustomColors,
    int AttributesSet,
    string Detail);

internal static class WindowChrome
{
    // DWM 属性号（dwmapi.h，没有托管常量可用，只能自己写）
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    /// <summary>本机是否支持「指定颜色」那三个属性（Windows 11 22000 起）。</summary>
    public static bool CustomColorSupported =>
        OperatingSystem.IsWindows() && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>本工程是否接管外框（只有 Windows 有这条接口）。</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    /// <summary>Avalonia 的 Color → Win32 的 COLORREF。<c>COLORREF</c> 是 0x00BBGGRR，**反着读**。</summary>
    public static int ToColorRef(Color c) => (c.B << 16) | (c.G << 8) | c.R;

    /// <summary>
    /// 相对亮度（0=黑，1=白）。自检拿它断言「亮色主题的标题栏必须是亮色」——
    /// 这条比「键存在」有意义得多：键存在但给的是黑底，照样是「没跟随主题」。
    /// </summary>
    public static double Luminance(Color c)
        => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 把主题色施加到窗口外框上。
    /// <paramref name="dark"/> 应当取「这个主题是不是暗色主题」—— 它决定标题栏按钮字形的明暗，
    /// 与另外三个颜色**相互独立**（Win11 上可以「浅底 + 浅字形」这种错配，必须各管各的）。
    /// </summary>
    public static ChromeResult Apply(TopLevel target, Color caption, Color foreground, Color border, bool dark)
    {
        if (!Supported)
            return new ChromeResult(false, dark, false, 0,
                "非 Windows：外框由桌面环境绘制，没有「程序指定颜色」的接口（见文件头说明）");

        nint hwnd = target.TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0)
            return new ChromeResult(false, dark, false, 0, "拿不到窗口句柄（窗口尚未创建）");

        int set = 0;

        // 深色标志：Win10 1809+ 是 20，更早的 build 用 19（老系统上两个都试，能成就行）
        if (TrySet(hwnd, DwmwaUseImmersiveDarkMode, dark ? 1 : 0) || TrySet(hwnd, 19, dark ? 1 : 0))
            set++;

        bool colors = false;
        if (CustomColorSupported)
        {
            bool c1 = TrySet(hwnd, DwmwaCaptionColor, ToColorRef(caption));
            bool c2 = TrySet(hwnd, DwmwaTextColor, ToColorRef(foreground));
            bool c3 = TrySet(hwnd, DwmwaBorderColor, ToColorRef(border));
            colors = c1 && c2 && c3;
            set += (c1 ? 1 : 0) + (c2 ? 1 : 0) + (c3 ? 1 : 0);
        }

        string detail = colors
            ? $"Win11：标题栏 {Hex(caption)} / 文字 {Hex(foreground)} / 边框 {Hex(border)}（{(dark ? "深" : "浅")}色字形），{set}/4 项生效"
            : $"系统只接受深色标志（build {Environment.OSVersion.Version.Build} 早于 22000）：{set}/1 项生效";

        return new ChromeResult(true, dark, colors, set, detail);
    }

    private static bool TrySet(nint hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) >= 0;
        }
        catch
        {
            // dwmapi 缺失 / 参数被拒都不该让主流程崩 —— 外框配色只是雕花
            return false;
        }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
