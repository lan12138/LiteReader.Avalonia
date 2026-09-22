// LiteReader · Avalonia 跨平台版 —— 入口
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 新增 --fuzz 模式 —— 不需要窗口，在 Avalonia 起来之前就跑完退出，
//      这样挂到 CI 上做「编辑内核差分对拍」不会因为无显示环境而失败（headless 也能跑）。
//   2) 新增单实例转发：已在运行时不另开进程，把命令行文件发过去后自己退出。
//      **这一段必须放在 Avalonia 之前** —— 它要在「建窗口」这件事发生之前就决定
//      「这次到底要不要建窗口」，放进 App 里已经晚了（窗口已经建出来了）。

using System;
using Avalonia;
using LiteReader.Diagnostics;
using LiteReader.Services;

namespace LiteReader;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 纯逻辑自检：不创建窗口、不需要显示环境
        if (EditFuzz.Requested(args)) return EditFuzz.Run(args);
        if (EditFuzz.BenchRequested(args)) return EditFuzz.RunBench();

        // ---- 单实例 + 文件转发（机制见 Services/SingleInstance.cs）----
        // 诊断模式跳过：见 Services/StartupFiles.cs 的说明。
        if (!StartupFiles.IsDiagnostic(args) && !SingleInstance.IsFirstInstance())
        {
            // 已有实例：把文件递过去（空列表 = 只把它的窗口叫到前面来），然后自己退出。
            if (SingleInstance.TrySend(StartupFiles.ExistingFiles(args))) return 0;

            // 递不过去 —— 名字还挂着但对端已经不响应（进程被杀、管道名字残留）。
            // 这时**继续按新实例启动**，而不是静默退出：双击图标却什么都没发生，
            // 比多开一个窗口糟糕得多。代价是这种情况不再有单实例保护，可以接受。
            // （对照 LiteReader.cpp：FindWindow 轮询拿不到窗口时也是继续往下建自己的窗口。）
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()      // Windows / Linux(X11,Wayland) / macOS 自动选择后端
            .WithInterFont()          // 自带一套西文字体，避免各平台默认字体差异
            .LogToTrace();
}
