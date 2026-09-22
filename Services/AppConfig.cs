// LiteReader · Avalonia 跨平台版 —— 配置持久化
// 作者：hujie  创建：2026-09-18
//
// 跨平台配置目录约定（对应 README §9.1 里「配置持久化」那一行）：
//   Windows : %APPDATA%\LiteReader\config.json          —— 与原版 INI 同一位置，迁移用户的直觉不变
//   Linux   : $XDG_CONFIG_HOME/lite-reader/config.json  —— 无该变量时回落 ~/.config
//   macOS   : ~/Library/Application Support/LiteReader/config.json
// 不写注册表、不写 exe 同目录：前者只有 Windows 有，后者在 Linux/macOS 上通常只读（/usr/lib 之类）。
//
// 为什么用 System.Text.Json 的源生成器（JsonSerializerContext）而不是反射版：
//   反射版在裁剪（PublishTrimmed）下需要 DynamicDependency 之类的手工保护，
//   源生成器在编译期就把读写代码生成好，裁剪器完全看得见，零告警。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteReader.Services;

/// <summary>落盘的配置。字段增减都安全：缺的走默认值，多的被忽略。</summary>
public sealed class AppConfig
{
    public string Theme { get; set; } = ThemeService.DefaultThemeName;
    public double FontSize { get; set; } = EditorSettings.DefaultFontSize;
    public int TabWidth { get; set; } = EditorSettings.DefaultTabWidth;

    /// <summary>代码补全总开关（候选列表 + 括号/引号自动配对）。</summary>
    public bool AutoComplete { get; set; } = EditorSettings.DefaultAutoComplete;

    /// <summary>查找是否区分大小写（查找条的「Aa」开关）。</summary>
    public bool FindMatchCase { get; set; }

    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double WindowX { get; set; } = double.NaN;   // NaN = 交给窗口管理器居中
    public double WindowY { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }

    /// <summary>上次打开的文件夹（下次启动自动恢复文件树）。</summary>
    public string? LastFolder { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

/// <summary>配置读写。任何异常都不向外抛 —— 配置坏了不应该让程序起不来。</summary>
public static class AppConfigStore
{
    public static string DirectoryPath { get; } = ResolveDirectory();

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "config.json");

    public static AppConfig Current { get; private set; } = new();

    /// <summary>
    /// 置为 true 时所有落盘写入变成空操作。
    /// 自检模式（--selftest / --fuzz）会切主题、改字号、动窗口位置，那些是诊断行为，
    /// 不应该覆盖用户真实保存的偏好设置。
    /// </summary>
    public static bool SuppressWrites { get; set; }

    private static string ResolveDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData)) return Path.Combine(appData, "LiteReader");
        }
        else if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "LiteReader");
        }

        string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "lite-reader");

        string h = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(h, ".config", "lite-reader");
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            string json = File.ReadAllText(FilePath);
            AppConfig? cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
            if (cfg is not null) Current = cfg;
        }
        catch
        {
            Current = new AppConfig();   // 解析失败就当没有配置，用默认值继续
        }
    }

    public static void Save()
    {
        if (SuppressWrites) return;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            string json = JsonSerializer.Serialize(Current, ConfigJsonContext.Default.AppConfig);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 无写权限 / 磁盘满：忽略。配置丢失不是致命错误，不该打断用户使用。
        }
    }

    /// <summary>把内存里改过的配置写回并落盘（设置项的通用收尾动作）。</summary>
    public static void Persist(Action<AppConfig> mutate)
    {
        mutate(Current);
        Save();
    }
}
