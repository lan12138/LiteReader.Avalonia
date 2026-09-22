// LiteReader · Avalonia 跨平台版 —— 编辑器设置（全局单例）
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：
//   1) 接入 AppConfigStore —— 字号/制表宽改动会落盘，下次启动自动恢复
//      （写盘放在事件之后，先让界面生效再存，避免磁盘慢影响手感）。
//   2) 新增 AutoComplete（代码补全总开关）。它同时管两件事：候选列表弹出、
//      以及括号/引号的自动配对插入 —— 与 LiteReader.cpp 的 g_autocomplete 一致，
//      因为这两者本来就是「一个开关控制的一整套辅助输入」。
//
// 为什么用静态单例而不是一路属性绑定：
//   字号/主题版本是「全局、唯一、低频变化」的设置。做成静态 + 事件广播后，
//   自绘控件只要在挂载时订阅、卸载时退订即可，XAML 里不用再写
//   $parent[Window].((vm:MainWindowViewModel)DataContext).XXX 这类长绑定，
//   模板层保持干净，也不容易在编译期绑定上踩坑。

namespace LiteReader.Services;

public static class EditorSettings
{
    public const double DefaultFontSize = 13.0;
    public const double MinFontSize = 8.0;
    public const double MaxFontSize = 32.0;
    public const int DefaultTabWidth = 4;
    public const bool DefaultAutoComplete = true;

    private static double _fontSize = DefaultFontSize;
    private static int _tabWidth = DefaultTabWidth;
    private static bool _autoComplete = DefaultAutoComplete;
    private static bool _loading;

    public static event Action? Changed;

    /// <summary>从配置载入（启动时调用一次）。载入期间不触发落盘。</summary>
    public static void LoadFrom(AppConfig cfg)
    {
        _loading = true;
        try
        {
            _fontSize = Math.Clamp(cfg.FontSize, MinFontSize, MaxFontSize);
            _tabWidth = Math.Clamp(cfg.TabWidth, 1, 16);
            _autoComplete = cfg.AutoComplete;
        }
        finally { _loading = false; }
    }

    public static double FontSize
    {
        get => _fontSize;
        set
        {
            double v = Math.Clamp(value, MinFontSize, MaxFontSize);
            if (Math.Abs(v - _fontSize) < 0.001) return;
            _fontSize = v;
            Changed?.Invoke();
            Persist();
        }
    }

    public static int TabWidth
    {
        get => _tabWidth;
        set
        {
            int v = Math.Clamp(value, 1, 16);
            if (v == _tabWidth) return;
            _tabWidth = v;
            Changed?.Invoke();
            Persist();
        }
    }

    /// <summary>代码补全开关：候选列表 + 括号/引号自动配对。</summary>
    public static bool AutoComplete
    {
        get => _autoComplete;
        set
        {
            if (value == _autoComplete) return;
            _autoComplete = value;
            Changed?.Invoke();
            Persist();
        }
    }

    private static void Persist()
    {
        if (_loading) return;
        AppConfigStore.Persist(c =>
        {
            c.FontSize = _fontSize;
            c.TabWidth = _tabWidth;
            c.AutoComplete = _autoComplete;
        });
    }
}

