// LiteReader · Avalonia 跨平台版 —— 应用装配
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 启动时先载入配置（主题 / 字号 / 上次文件夹），再做窗口装配。
//      配置读取放在最前面，这样主题字典在窗口构造前就已合并进 Application.Resources，
//      XAML 里的 {DynamicResource EditorBackground} 第一帧就能取到正确颜色（否则会闪一下默认底色）。
//   2) 新增单实例的**接收端**：第一实例在这里起一个命名管道监听（机制见 Services/SingleInstance.cs），
//      收到路径后在 UI 线程上开标签并把窗口叫到前面来。
//      「我是不是第一实例」这个判断在 Program.Main 里已经做过（那时必须定下来要不要建窗口），
//      这里调 IsFirstInstance() 拿到的是同一个缓存结果，不会出现两次判断打架。

using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LiteReader.Diagnostics;
using LiteReader.Services;
using LiteReader.ViewModels;
using LiteReader.Views;

namespace LiteReader;

public partial class App : Application
{
    private SingleInstance.Listener? _ipc;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // 三个平台都走 ClassicDesktop 生命周期（Linux/macOS 也是同一个）
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppConfigStore.Load();

            var themes = new ThemeService();
            var vm = new MainWindowViewModel(themes);
            vm.RestoreFromConfig(AppConfigStore.Current);   // 主题 / 字号 / 上次文件夹

            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            string[] args = desktop.Args ?? [];

            // 命令行带文件参数时直接打开 —— 这是「文件关联 / 右键用 LiteReader 打开」的
            // 跨平台统一入口：Windows 走注册表关联、Linux 走 .desktop + xdg-mime、
            // macOS 走 Info.plist，但最终都是把路径当 argv 传进来。
            foreach (string f in StartupFiles.ExistingFiles(args)) vm.OpenFile(f);

            StartSingleInstanceListener(desktop, vm, window, args);

            // 开发自检：5 套主题各截一张图 + 编辑内核冒烟（见 Diagnostics/SelfTest.cs）
            // 退出码透传给 shell，可以直接挂到 CI 上做三平台冒烟。
            if (SelfTest.Requested(args))
            {
                // 自检会切主题、改字号、动窗口位置——那些是诊断行为，不是用户意图。
                // 禁掉落盘，免得跑一次 CI 就把用户真实的偏好设置冲掉。
                AppConfigStore.SuppressWrites = true;

                window.Opened += async (_, _) =>
                {
                    int code = await SelfTest.RunAsync(window, vm, args);
                    desktop.Shutdown(code);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 起单实例的接收端 —— **只有第一实例才监听**。
    /// 非第一实例（Program 里已经试过转发、只是没送出去）不再监听：同一个管道名字上挂两个
    /// 接收端，客户端会随机连到其中一个，反而更难用。这种情况就退化成「各开各的窗口」，
    /// 与 LiteReader.cpp 在找不到已有窗口时的行为一致。
    /// </summary>
    private void StartSingleInstanceListener(IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel vm, MainWindow window, string[] args)
    {
        if (StartupFiles.IsDiagnostic(args) || !SingleInstance.IsFirstInstance()) return;

        _ipc = SingleInstance.StartListener(paths =>
        {
            // 回调在管道线程上，碰 UI 必须切回 UI 线程。
            // 注意 Post 是异步投递：这里的 paths 是新建的 List，不会被复用，可以安全捕获。
            Dispatcher.UIThread.Post(() =>
            {
                foreach (string p in paths)
                    if (File.Exists(p)) vm.OpenFile(p);
                BringToFront(window);
            });
        });

        // 退出路径有两条（Exit / ShutdownRequested），两条都要收 —— 漏了那条就会留一个
        // 仍占着管道名字的后台线程，下次启动的转发会打到一个已经没人处理的端点。
        desktop.Exit += (_, _) => DisposeIpc();
        desktop.ShutdownRequested += (_, _) => DisposeIpc();
    }

    private void DisposeIpc()
    {
        _ipc?.Dispose();
        _ipc = null;
    }

    /// <summary>
    /// 把窗口叫到前面来。
    /// 能做的都做了，但**跨平台别期待太高**：Wayland 不允许客户端自己提升窗口，
    /// 由合成器决定（通常只在「用户刚点了图标」这种有输入事件时才允许）。
    /// </summary>
    private static void BringToFront(MainWindow window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
