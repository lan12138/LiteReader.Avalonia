// LiteReader · Avalonia 跨平台版 —— 主窗口视图模型
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 接入配置持久化：主题、字号、上次文件夹随改动落盘，启动时恢复。
//   2) 标签切换时同步状态栏的光标位置。
//   3) **新增查找**（Ctrl+F 查找条 + F3/Shift+F3 循环查找）与**代码补全开关**。
//      查找状态放在这里而不是标签 VM 上：查找条是「整窗口一个」的，切标签后查询词不该被清掉
//      （与 LiteReader.cpp 的全局 g_hFind 一致）。命中位置则写回当前标签的光标/选区。
//   4) 新增「工具」两项：在文件所在目录打开外部终端、注册为「打开方式」。
//      两者都把平台差异推给 Services（TerminalLauncher / FileAssociation），VM 里没有一句平台判断。
//   5) 订阅 EditorSettings.Changed 同步字号镜像 —— 字号现在还能从 Ctrl+滚轮改，
//      那条路径不经过本 VM 的命令，不订阅就会显示旧值。
//
// 文件选择对话框属于「视图能力」，这里通过 PickFilesAsync / PickFolderAsync 两个委托
// 由 View 注入（MainWindow.OnLoaded 里挂），VM 不直接依赖 TopLevel / StorageProvider，
// 这样 VM 可单测、也不绑死某个平台的文件对话框实现。
// 「编辑」相关命令（撤销/复制/粘贴…）天然依赖键盘焦点，属于视图层，由 MainWindow 直接路由，
// 不进 VM —— 避免 VM 反向持有控件。查找是例外：它只需要「当前标签的光标偏移」，
// 而那份状态本来就在 DocumentTabViewModel 上，所以能在 VM 里闭环。

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteReader.Models;
using LiteReader.Services;

namespace LiteReader.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ThemeService _themes;

    public MainWindowViewModel(ThemeService themes)
    {
        _themes = themes;
        _themes.Changed += () =>
        {
            ThemeVersion = _themes.Version;
            CurrentTheme = _themes.CurrentName;
            foreach (DocumentTabViewModel t in Tabs) t.Refresh();
        };
        CurrentTheme = _themes.CurrentName;
        ThemeVersion = _themes.Version;
        _findMatchCase = AppConfigStore.Current.FindMatchCase;

        // 字号真值在 EditorSettings（编辑区直接读它），这里只是给界面显示用的镜像。
        // 之所以要订阅：字号还能从**别的入口**改 —— 编辑区上的 Ctrl+滚轮、以及未来可能加的
        // 快捷键。那些路径不经过本 VM 的命令，不订阅的话状态栏的读数会一直停在旧值。
        EditorSettings.Changed += () => EditorFontSize = EditorSettings.FontSize;
    }

    /// <summary>由 View 注入：返回选中的文件路径，取消返回 null。</summary>
    public Func<Task<IReadOnlyList<string>?>>? PickFilesAsync { get; set; }

    /// <summary>由 View 注入：返回选中的文件夹路径，取消返回 null。</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = [];

    public IReadOnlyList<string> ThemeNames => ThemeService.Catalog.Select(t => t.Name).ToList();

    [ObservableProperty]
    private DocumentTabViewModel? _selectedTab;

    /// <summary>TreeView.SelectedItem 是 object，这里保持 object? 以避免编译期绑定做类型转换。</summary>
    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private string _currentTheme = ThemeService.DefaultThemeName;

    [ObservableProperty]
    private int _themeVersion;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>是否已载入文件夹（决定左侧面板显示树还是提示语）。</summary>
    public bool HasFolder => RootNodes.Count > 0;

    /// <summary>字号真值在 EditorSettings 里；这里只是给界面显示用的镜像。</summary>
    [ObservableProperty]
    private double _editorFontSize = EditorSettings.DefaultFontSize;

    public bool HasDocument => SelectedTab is not null;

    partial void OnSelectedTabChanged(DocumentTabViewModel? value)
    {
        OnPropertyChanged(nameof(HasDocument));
        StatusText = value is null ? "就绪" : $"{value.Document.FileName}（{value.Summary.Trim()}）";
        FindStatus = string.Empty;
    }

    // ---------------- 查找 ----------------

    /// <summary>查找条是否显示。</summary>
    [ObservableProperty]
    private bool _findVisible;

    /// <summary>查询词。边打边找 —— 改一次就跳一次，与主流编辑器的「增量查找」一致。</summary>
    [ObservableProperty]
    private string _findQuery = string.Empty;

    /// <summary>是否区分大小写。</summary>
    [ObservableProperty]
    private bool _findMatchCase;

    /// <summary>查找结果提示（「第 3 / 共 12 项」「未找到」…）。</summary>
    [ObservableProperty]
    private string _findStatus = string.Empty;

    partial void OnFindMatchCaseChanged(bool value)
    {
        AppConfigStore.Persist(c => c.FindMatchCase = value);
        DoFind(forward: true);          // 切换大小写敏感后立刻重找，否则提示数与实际不符
    }

    partial void OnFindQueryChanged(string value) => DoFind(forward: true);

    // ---------------- 代码补全开关 ----------------

    [ObservableProperty]
    private bool _autoComplete = EditorSettings.DefaultAutoComplete;

    partial void OnAutoCompleteChanged(bool value)
    {
        EditorSettings.AutoComplete = value;   // 真值在 EditorSettings，这里只是菜单项的绑定目标
    }

    // ---------------- 启动恢复 ----------------

    /// <summary>启动时按配置恢复主题、字号、补全开关与上次的文件夹。</summary>
    public void RestoreFromConfig(AppConfig cfg)
    {
        EditorSettings.LoadFrom(cfg);
        EditorFontSize = EditorSettings.FontSize;
        AutoComplete = EditorSettings.AutoComplete;
        FindMatchCase = cfg.FindMatchCase;
        _themes.Apply(cfg.Theme);

        if (!string.IsNullOrEmpty(cfg.LastFolder) && Directory.Exists(cfg.LastFolder))
            SetFolder(cfg.LastFolder);
    }

    // ---------------- 命令 ----------------

    [RelayCommand]
    private async Task OpenFilesAsync()
    {
        if (PickFilesAsync is null) return;
        IReadOnlyList<string>? files = await PickFilesAsync();
        if (files is null || files.Count == 0) return;
        foreach (string f in files) OpenFile(f);
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (PickFolderAsync is null) return;
        string? dir = await PickFolderAsync();
        if (string.IsNullOrEmpty(dir)) return;
        SetFolder(dir);
        AppConfigStore.Persist(c => c.LastFolder = dir);
    }

    /// <summary>载入文件夹到左侧文件树（打开文件夹、启动恢复都走这里）。</summary>
    public void SetFolder(string dir)
    {
        RootNodes.Clear();
        RootNodes.Add(new FileNodeViewModel(dir, true) { IsExpanded = true });
        OnPropertyChanged(nameof(HasFolder));
        StatusText = "已载入文件夹：" + dir;
    }

    [RelayCommand]
    private void Save()
    {
        if (SelectedTab is null) return;
        try
        {
            SelectedTab.Document.Save();
            SelectedTab.Refresh();
            StatusText = "已保存：" + SelectedTab.Document.FilePath;
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CloseTab(DocumentTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        SelectedTab = Tabs.Count > 0 ? Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)] : null;
        StatusText = "已关闭标签";
    }

    [RelayCommand]
    private void ApplyTheme(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _themes.Apply(name);
        AppConfigStore.Persist(c => c.Theme = _themes.CurrentName);
        StatusText = "主题：" + _themes.CurrentName;
    }

    [RelayCommand]
    private void ZoomIn() => EditorFontSize = EditorSettings.FontSize += 1;

    [RelayCommand]
    private void ZoomOut() => EditorFontSize = EditorSettings.FontSize -= 1;

    [RelayCommand]
    private void ZoomReset() => EditorFontSize = EditorSettings.FontSize = EditorSettings.DefaultFontSize;

    // ---- 工具：外部终端 / 文件关联 ----

    /// <summary>
    /// 在当前文件所在目录打开一个外部终端（对照 LiteReader.cpp 的 openTerminalHere）。
    /// 目录优先级：当前标签文件所在目录 → 上次打开的文件夹 → 用户主目录。
    /// </summary>
    [RelayCommand]
    private void OpenTerminal()
    {
        string dir = TerminalLauncher.ResolveDirectory(
            SelectedTab?.Document.FilePath, AppConfigStore.Current.LastFolder);

        StatusText = TerminalLauncher.TryOpen(dir, out string used)
            ? $"已在「{dir}」打开终端（{used}）"
            : $"打开终端失败：本平台没有找到可用的终端程序。目标目录：{dir}";
    }

    /// <summary>
    /// 注册为「打开方式」。
    /// 结果用**标签页**显示而不是弹框：三个平台的报告长度差别很大（macOS 那份是一整段要自己粘的
    /// plist 加步骤），状态栏会被截断，而弹原生警告框与本工程的观感一直不搭。
    /// 用标签页显示纯文本，恰好是这个程序最擅长的事。
    /// </summary>
    [RelayCommand]
    private async Task RegisterFileAssociationAsync()
    {
        StatusText = "正在注册为「打开方式」…";
        try
        {
            AssociationResult r = await FileAssociation.RegisterAsync();
            StatusText = r.Summary;
            if (File.Exists(r.ReportPath)) OpenFile(r.ReportPath);
        }
        catch (Exception ex)
        {
            StatusText = $"注册失败：{ex.Message}";
        }
    }

    // ---- 查找命令 ----

    /// <summary>Ctrl+F：显示查找条并把焦点交给它（聚焦由 View 完成，VM 不碰控件）。</summary>
    [RelayCommand]
    private void ToggleFind() => FindVisible = !FindVisible;

    [RelayCommand]
    private void CloseFind()
    {
        FindVisible = false;
        FindStatus = string.Empty;
    }

    [RelayCommand]
    private void FindNext() => DoFind(forward: true);

    [RelayCommand]
    private void FindPrev() => DoFind(forward: false);

    /// <summary>
    /// 执行一次查找。命中后把当前标签的光标/选区设到命中处 ——
    /// 编辑区是双向绑定到这两个属性的，所以会自动滚动过去并高亮。
    /// </summary>
    /// <returns>是否命中。</returns>
    public bool DoFind(bool forward)
    {
        DocumentTabViewModel? tab = SelectedTab;
        if (tab is null)
        {
            FindStatus = FindQuery.Length == 0 ? string.Empty : "没有打开的文档";
            return false;
        }
        if (FindQuery.Length == 0)
        {
            FindStatus = string.Empty;
            return false;
        }

        string text = tab.Document.Text;
        int hit = forward
            ? TextSearch.FindForward(text, FindQuery, tab.CaretOffset, FindMatchCase)
            : TextSearch.FindBackward(text, FindQuery, tab.CaretOffset, FindMatchCase);

        if (hit < 0)
        {
            FindStatus = "未找到";
            return false;
        }

        tab.AnchorOffset = hit;
        tab.CaretOffset = hit + FindQuery.Length;

        int total = TextSearch.CountAll(text, FindQuery, FindMatchCase);
        int ordinal = TextSearch.OrdinalOf(text, FindQuery, hit, FindMatchCase);
        FindStatus = total > 1 ? $"第 {ordinal} / 共 {total} 项" : "已找到";
        return true;
    }

    /// <summary>文件树上双击/回车打开。同一路径已打开时只切换过去，不重复开标签。</summary>
    public void OpenFile(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);

            DocumentTabViewModel? existing = Tabs.FirstOrDefault(
                t => string.Equals(t.Document.FilePath, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                SelectedTab = existing;
                return;
            }

            var tab = new DocumentTabViewModel(new DocumentModel());
            tab.CloseCommand = new RelayCommand(() => CloseTab(tab));
            tab.Load(full);
            Tabs.Add(tab);
            SelectedTab = tab;
            StatusText = $"{Path.GetFileName(full)}（{tab.Summary.Trim()}）";
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
        }
    }
}

