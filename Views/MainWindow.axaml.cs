// LiteReader · Avalonia 跨平台版 —— 主窗口（视图层）
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 「编辑」菜单的路由：这些动作作用在「当前有键盘焦点的编辑区」上，
//      天然是视图层语义 → 直接打到 EditorSurface.Focused，不进 VM（VM 不反向持有控件）。
//   2) 窗口位置/尺寸/最大化状态接入配置持久化，并做「是否还落在某块屏幕上」的校验，
//      避免拔掉外接显示器后窗口跑到看不见的地方。
//   3) 新增 F12 跳转到定义、查找条的开合与聚焦、以及 Esc 关闭查找条。
//      F12 与 Esc 走**隧道路由**（Tunnel）而不是 KeyBinding：隧道路由在焦点控件之前触发，
//      所以无论焦点在编辑区还是查找框，行为都一样，不会被控件自己的按键处理吃掉。
//   4) 订阅 EditorSurface.StatusReported，把编辑区右键菜单的反馈（终端起没起、
//      跳转定不定义得到）转写进状态栏 —— 编辑区的 DataContext 是标签 VM，
//      拿不到主窗口 VM，只能靠这个事件把话递出来。
//   5) 窗口外框（系统标题栏 + 边框）跟随主题：Opened 时施加一次，之后随 ThemeService.Changed 重施
//      （机制与平台限制见 Services/WindowChrome.cs）。主题色与「深色标志」分开取 ——
//      前者决定底色，后者决定标题栏按钮字形的明暗，两者在 Win11 上是独立的。
//
// 这里只做几件「视图才有资格做的事」：
//   1) 把平台文件对话框能力注入给 VM（VM 不依赖 TopLevel / StorageProvider）
//   2) 文件树的交互（双击/回车打开）
//   3) 查找条的聚焦（VM 只知道「该显示」，不知道「焦点该给谁」）
//   4) 退出、关于这种纯 UI 行为
// 跨平台收益：StorageProvider 在 Windows 是 IFileDialog、Linux 是 GTK/portal 对话框、
//   macOS 是 NSOpenPanel —— 我们一行平台判断都不用写。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using LiteReader.Services;
using LiteReader.ViewModels;

namespace LiteReader.Views;

public partial class MainWindow : Window
{
    /// <summary>最近一次施加窗口外框的结果（自检直接读它，验证「主题一换外框就跟着换」这条接线）。</summary>
    internal ChromeResult ChromeState { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Opened += OnOpened;
        Closing += OnClosing;

        // 隧道路由：先于焦点控件收到按键，用于实现「全局快捷键」
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.PickFilesAsync = PickFilesAsync;
        vm.PickFolderAsync = PickFolderAsync;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        // 编辑区右键菜单的反馈（「已在命令提示符中打开」「没找到定义」）从这里进状态栏。
        // 退订在 OnClosing —— 静态事件不退订会把窗口一直挂在事件上。
        EditorSurface.StatusReported += Report;

        // 换主题时外框也要跟着换。这里订阅的是静态服务，所以退订同样放在 OnClosing。
        if (ThemeService.Current is { } themes) themes.Changed += ApplyChrome;
    }

    /// <summary>
    /// 查找条一显示就把焦点交给输入框 —— 否则用户按了 Ctrl+F 还得再点一下输入框才能打字。
    /// 「什么时候该聚焦」是视图的事，所以放在这里而不是 VM 里。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.FindVisible)) return;
        if (DataContext is MainWindowViewModel { FindVisible: true }) FindBox.Focus();
    }

    // ---------------- 全局快捷键（隧道路由） ----------------

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.F12:
                GoToDefinition(vm);
                e.Handled = true;
                break;

            case Key.Escape when vm.FindVisible:
                vm.CloseFindCommand.Execute(null);
                EditorSurface.Focused?.Focus();
                e.Handled = true;
                break;
        }
    }

    // ---------------- 窗口几何持久化 ----------------

    private void OnOpened(object? sender, EventArgs e)
    {
        RestoreGeometry(AppConfigStore.Current);
        ApplyChrome();          // 窗口已创建、句柄有了，这时候才谈得上改系统外框
    }

    // ---------------- 窗口外框跟随主题 ----------------

    /// <summary>
    /// 把当前主题的三个「外壳色」喂给系统窗口外框。
    /// 平台支持情况、以及为什么 Linux/macOS 不做，见 <see cref="WindowChrome"/> 的文件头。
    /// </summary>
    private void ApplyChrome()
    {
        ThemeService? themes = ThemeService.Current;
        if (themes is null) return;

        ThemeDescriptor desc = ThemeService.Descriptor(themes.CurrentName);
        ChromeState = WindowChrome.Apply(this,
            caption: themes.ColorOf("WindowCaptionBackground", "#21252B"),
            foreground: themes.ColorOf("WindowCaptionForeground", "#ABB2BF"),
            border: themes.ColorOf("WindowBorder", "#3E4451"),
            dark: !desc.IsLight);

        if (!ChromeState.Applied) return;

        // 状态栏提一句就够了，不打断操作；失败的原因（拿不到句柄等）一般只出现在自检里。
        if (!ChromeState.CustomColors)
            Report($"窗口外框：{ChromeState.Detail}");
    }

    private void RestoreGeometry(AppConfig cfg)
    {
        if (cfg.WindowWidth >= MinWidth) Width = cfg.WindowWidth;
        if (cfg.WindowHeight >= MinHeight) Height = cfg.WindowHeight;

        if (!double.IsNaN(cfg.WindowX) && !double.IsNaN(cfg.WindowY))
        {
            var p = new PixelPoint((int)cfg.WindowX, (int)cfg.WindowY);
            if (IsOnAnyScreen(p)) Position = p;
        }

        if (cfg.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>位置校验：窗口左上角至少要有 40 px 落在某块屏幕的工作区内。</summary>
    private bool IsOnAnyScreen(PixelPoint p)
    {
        foreach (Screen s in Screens.All)
        {
            PixelRect r = s.WorkingArea;
            if (p.X >= r.X - 40 && p.X < r.Right - 40 && p.Y >= r.Y - 10 && p.Y < r.Bottom - 10)
                return true;
        }
        return false;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 两个静态事件都必须退订：它们活在进程级对象上，不退订就会把窗口（及其整棵可视树）留住。
        EditorSurface.StatusReported -= Report;
        if (ThemeService.Current is { } themes) themes.Changed -= ApplyChrome;

        AppConfigStore.Persist(c =>
        {
            c.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                c.WindowWidth = Width;
                c.WindowHeight = Height;
                c.WindowX = Position.X;
                c.WindowY = Position.Y;
            }
        });
    }

    // ---------------- 文件对话框（Avalonia 统一抽象，三平台共用） ----------------

    private async Task<IReadOnlyList<string>?> PickFilesAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "打开文件",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("代码与文本")
                    {
                        Patterns = ["*.cs", "*.cpp", "*.cc", "*.c", "*.h", "*.hpp", "*.py", "*.js", "*.ts",
                                    "*.java", "*.go", "*.rs", "*.sql", "*.json", "*.xml", "*.yml", "*.yaml",
                                    "*.md", "*.txt", "*.log", "*.ini", "*.sh", "*.ps1", "*.bat"],
                    },
                    FilePickerFileTypes.All,
                ],
            });

        return files.Count == 0 ? null : files.Select(f => f.Path.LocalPath).ToList();
    }

    private async Task<string?> PickFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "打开文件夹", AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    // ---------------- 文件树交互 ----------------

    /// <summary>双击：目录展开/折叠，文件打开。</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (FolderTree.SelectedItem is not FileNodeViewModel node) return;

        if (node.IsDirectory)
            node.IsExpanded = !node.IsExpanded;
        else
            vm.OpenFile(node.FullPath);
    }

    /// <summary>回车：与双击同义（照顾键盘操作）。</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (FolderTree.SelectedItem is not FileNodeViewModel node) return;
        if (!node.IsDirectory) vm.OpenFile(node.FullPath);
        e.Handled = true;
    }

    // ---------------- 编辑菜单路由 ----------------

    private void OnUndoClick(object? sender, RoutedEventArgs e) => Invoke("撤销", ed => ed.Undo());

    private void OnRedoClick(object? sender, RoutedEventArgs e) => Invoke("重做", ed => ed.Redo());

    private void OnCutClick(object? sender, RoutedEventArgs e) => Invoke("剪切", ed => ed.Cut());

    private void OnCopyClick(object? sender, RoutedEventArgs e) => Invoke("复制", ed => ed.Copy());

    private void OnPasteClick(object? sender, RoutedEventArgs e) => Invoke("粘贴", ed => ed.Paste());

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => Invoke("全选", ed => ed.SelectAll());

    // ---------------- 查找条 ----------------

    /// <summary>查找框里的按键：回车找下一个、Shift+回车找上一个、Esc 关掉（F3 已由 KeyBinding 处理）。</summary>
    private void OnFindBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                vm.DoFind(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
                break;

            case Key.Escape:
                vm.CloseFindCommand.Execute(null);
                EditorSurface.Focused?.Focus();     // 关掉查找条后把焦点还给编辑区
                e.Handled = true;
                break;
        }
    }

    // ---------------- 跳转到定义 ----------------

    private void OnGoToDefinitionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) GoToDefinition(vm);
    }

    private void GoToDefinition(MainWindowViewModel vm)
    {
        EditorSurface? ed = EditorSurface.Focused;
        if (ed is null)
        {
            Report("跳转到定义：请先点击编辑区获得焦点");
            return;
        }

        string name = ed.WordAtCaret();
        if (name.Length == 0)
        {
            Report("跳转到定义：光标不在标识符上");
            return;
        }

        Report(ed.GoToDefinition()
            ? $"跳转到定义：{name}"
            : $"未找到 {name} 的定义（本功能是单文件内的启发式查找，不做语义解析）");
    }

    private void Invoke(string name, Action<EditorSurface> action)
    {
        EditorSurface? ed = EditorSurface.Focused;
        if (ed is null)
        {
            Report($"{name}：请先点击编辑区获得焦点");
            return;
        }
        action(ed);
        Report(name);
    }

    private void Report(string text)
    {
        if (DataContext is MainWindowViewModel vm) vm.StatusText = text;
    }

    // ---------------- 纯 UI 行为 ----------------

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.StatusText = "LiteReader · Avalonia 跨平台版 · .NET 10 —— 代码着色 / 多主题 / 可编辑 / 长文本" +
                            $"   配置文件：{AppConfigStore.FilePath}";
    }
}
