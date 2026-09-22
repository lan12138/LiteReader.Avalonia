// LiteReader · Avalonia 跨平台版 —— 主题字典代码后置
// 作者：hujie  创建：2026-09-18
//
// 为什么每套主题要有一个 C# 类（而不是让 ThemeService 按 URI 反射加载 .axaml）：
//   AvaloniaXamlLoader.Load(Uri) 带 [RequiresUnreferencedCode]，在 PublishTrimmed 下报 IL2026，
//   而且理论上存在「字典资源被裁掉、切主题时静默取不到色」的风险。
//   改成编译期可见的类之后，XAML 编译器把 ResourceInclude 直接编进程序集，裁剪器看得见，
//   运行时只需要 new 一下 —— 零反射、零 URI 字符串、零裁剪告警。
//
// 新增主题的完整步骤（三步）：
//   1) 复制 Themes/OneDarkPro.axaml 改色，根节点写 x:Class="LiteReader.Themes.XXX"
//   2) 在本文件加一个对应的分部类
//   3) 在 ThemeService.Catalog 里登记一行工厂委托

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LiteReader.Themes;

/// <summary>One Dark Pro（暗）。</summary>
public partial class OneDarkPro : ResourceDictionary
{
    public OneDarkPro() => AvaloniaXamlLoader.Load(this);
}

/// <summary>One Light（亮）。</summary>
public partial class OneLight : ResourceDictionary
{
    public OneLight() => AvaloniaXamlLoader.Load(this);
}

/// <summary>VS Code Dark+（暗）。</summary>
public partial class VsCodeDark : ResourceDictionary
{
    public VsCodeDark() => AvaloniaXamlLoader.Load(this);
}

/// <summary>IntelliJ IDEA Darcula（暗）。</summary>
public partial class IntelliJIdea : ResourceDictionary
{
    public IntelliJIdea() => AvaloniaXamlLoader.Load(this);
}

/// <summary>极致黑 AMOLED（暗，纯黑底省电）。</summary>
public partial class Amoled : ResourceDictionary
{
    public Amoled() => AvaloniaXamlLoader.Load(this);
}
