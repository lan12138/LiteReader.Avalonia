// LiteReader · Avalonia 跨平台版 —— 主题服务
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：主题字典由「按 URI 反射加载」改为「new 编译期类型」，
//   消除 AvaloniaXamlLoader.Load(Uri) 的 IL2026 裁剪告警（详见 Themes/ThemeDictionaries.cs）。
//
// 5 套配色放在 Themes/*.axaml（每个是一份 ResourceDictionary，键名一致）。
// 切换时把当前字典从 Application.Resources.MergedDictionaries 里换掉，
// 再把 RequestedThemeVariant 设成 Light/Dark，让 FluentTheme 的控件外壳也跟着变。
// 渲染层（EditorSurface）订阅 Changed 重绘，并靠 Version 判断缓存失效。

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using LiteReader.Themes;

namespace LiteReader.Services;

/// <param name="Name">显示名（也是持久化用的键，改名会让旧配置回落到默认主题）。</param>
/// <param name="IsLight">亮色主题 —— 决定 FluentTheme 的 ThemeVariant 与外壳配色方向。</param>
/// <param name="Factory">创建该主题字典。用委托而非 URI：裁剪友好（见 Themes/ThemeDictionaries.cs）。</param>
public sealed record ThemeDescriptor(string Name, bool IsLight, Func<ResourceDictionary> Factory);

public sealed class ThemeService
{
    public const string DefaultThemeName = "One Dark Pro";

    public static readonly IReadOnlyList<ThemeDescriptor> Catalog =
    [
        new("One Dark Pro",  IsLight: false, () => new OneDarkPro()),
        new("One Light",     IsLight: true,  () => new OneLight()),
        new("VS Code Dark",  IsLight: false, () => new VsCodeDark()),
        new("IntelliJ IDEA", IsLight: false, () => new IntelliJIdea()),
        new("极致黑 AMOLED",  IsLight: false, () => new Amoled()),
    ];

    public static ThemeDescriptor Descriptor(string name)
        => Catalog.FirstOrDefault(t => t.Name == name) ?? Catalog[0];

    /// <summary>当前服务实例。全应用只有一个，渲染层直接取用，省掉一路属性绑定。</summary>
    public static ThemeService? Current { get; private set; }

    private ResourceDictionary? _current;

    public ThemeService() => Current = this;

    public string CurrentName { get; private set; } = DefaultThemeName;

    /// <summary>主题版本号：每次切换 +1，渲染层据此丢弃缓存。</summary>
    public int Version { get; private set; }

    public event Action? Changed;

    public void Apply(string name)
    {
        ThemeDescriptor desc = Descriptor(name);
        Application app = Application.Current
            ?? throw new InvalidOperationException("Application 尚未初始化");

        ResourceDictionary dict = desc.Factory();

        if (_current is not null)
            app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Insert(0, dict);
        _current = dict;

        app.RequestedThemeVariant = desc.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

        CurrentName = desc.Name;
        Version++;
        Changed?.Invoke();
    }

    /// <summary>
    /// 渲染层取色：直接从当前主题字典里查，绕过 Application 的资源查找语义，
    /// 行为确定、可预测；查不到时给一个明显的兜底色（方便一眼看出漏了哪个键）。
    /// </summary>
    public IBrush Brush(string key, Color fallback)
    {
        if (_current is not null
            && _current.TryGetResource(key, null, out object? v)
            && v is IBrush b)
            return b;
        return new SolidColorBrush(fallback);
    }

    /// <summary>给非编辑器区（外壳）用的静态查询入口。</summary>
    public static IBrush ShellBrush(string key, Color fallback)
        => Current?.Brush(key, fallback) ?? new SolidColorBrush(fallback);

    /// <summary>
    /// 只取颜色值（不带画刷）。
    /// 窗口外框走的是 Win32 DWM 接口，它要的是 <c>COLORREF</c> 而不是 <c>IBrush</c>，
    /// 所以每个调用点都得先把画刷里的颜色掏出来 —— 与其各处写 `as ISolidColorBrush`，
    /// 不如在这里收一个口。
    /// </summary>
    public Color ColorOf(string key, string hexFallback)
        => Brush(key, Color.Parse(hexFallback)) is ISolidColorBrush s ? s.Color : Color.Parse(hexFallback);
}
