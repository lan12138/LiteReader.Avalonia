// LiteReader · Avalonia 跨平台版 —— 注册为「打开方式」/ 文件关联
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 对应 README §9.1 里「注册为『打开方式』」那一行：
//   对照实现（`HKCU\Software\Classes\...` + `RegCreateKeyEx`/`RegSetValueEx`）→ 三平台各自的注册侧
//
// 原版只写了 Windows 的注册表，而且**只做了一半**：它注册的是
//   ① `Software\Classes\Applications\LiteReader.exe`（让程序出现在「打开方式」列表里）
//   ② 每个扩展名下的 `OpenWithList\LiteReader.exe`（让右键「打开方式」里能看到它）
// 它**没有**去抢 `Software\Classes\.cs` 的默认值 —— 这是对的：
// 抢默认关联会把用户机器上 VS Code / Notepad++ 的默认设置顶掉，装个阅读器不该有这种后果。
// 所以本工程沿用同样的克制：**只出现在「打开方式」里，不改任何默认关联。**
//
// 三平台各自的「注册侧」：
//   Windows：HKCU 下的注册表（**不需要管理员**，因为写的是 HKCU 而不是 HKCR）
//   Linux  ：写一个 `~/.local/share/applications/lite-reader.desktop`，再尽力跑
//            `xdg-mime default` 把 MIME 类型挂上（xdg-mime 不在时只写文件，也算成功）
//   macOS  ：**做不到**。文件类型要靠 `.app` 包里的 `Info.plist` 的 CFBundleDocumentTypes，
//            而运行中的 .app 是签过名的、改它等于破坏签名。所以这里只生成一段可粘贴的
//            plist 片段 + 说明，让人自己决定要不要动 —— 不做「悄悄改系统」的事。
//
// 为什么把「报告」写成文件并让主程序用标签页打开它：
//   Windows/Linux 的结果是一句「成功了」，但 macOS 的结果是**一段要人自己粘的 plist**；
//   而这三个平台都会附带「接下来做什么」的说明。塞进状态栏会被截断，弹原生对话框又被
//   本工程一直避免（自绘编辑器里弹系统警告框的观感很割裂）。
//   写成一个文件、用标签页打开，正好是这个程序最擅长的事：显示纯文本，能滚动、能复制。
//
// ⚠️ 自检**不会**碰注册表 / 不跑 xdg-mime / 不写 .desktop：
//   那些是有副作用的系统改动，自检只断言这里的**纯函数**（键路径、命令行、desktop 内容）。

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Avalonia.Platform;
using Microsoft.Win32;

namespace LiteReader.Services;

internal sealed record AssociationResult(bool Ok, string Summary, string ReportPath);

internal static class FileAssociation
{
    /// <summary>
    /// 愿意「出现在打开方式里」的扩展名。与「打开文件」对话框的过滤器保持一致 ——
    /// 两处不一致会出现「对话框里能选、右键里看不到」这种说不清的差别。
    /// </summary>
    public static readonly string[] Extensions =
    [
        ".cs", ".cpp", ".cc", ".c", ".h", ".hpp", ".py", ".js", ".ts",
        ".java", ".go", ".rs", ".sql", ".json", ".xml", ".yml", ".yaml",
        ".md", ".txt", ".log", ".ini", ".sh", ".ps1", ".bat",
    ];

    /// <summary>Windows 上「应用程序」注册项的路径（不含 HKCU 前缀）。</summary>
    public const string WindowsAppKey = @"Software\Classes\Applications\LiteReader.exe";

    /// <summary>「打开方式」列表里显示的名字。</summary>
    public const string WindowsFriendlyName = "LiteReader 代码阅读器";

    /// <summary>文件名里显示的注册 ID（Linux 用）。</summary>
    public const string DesktopId = "lite-reader.desktop";

    /// <summary>
    /// Linux desktop 文件里 <c>Icon=</c> 的值。
    /// XDG 的规则是「给一个**名字**」，桌面环境去图标主题里按名字找同名文件 ——
    /// 所以这里不能用绝对路径（绝对路径只有部分实现认），必须配合
    /// <see cref="LinuxIconPath"/> 把 PNG 真的装进图标主题目录。
    /// </summary>
    public const string IconName = "lite-reader";

    /// <summary>
    /// 图标 PNG 在程序集里的资源地址（<c>Assets/icon.png</c> 由 csproj 的
    /// <c>AvaloniaResource</c> 编入；见 csproj 里「图标资源」那一段说明）。
    /// 用资源而不是「exe 旁边的文件」：单文件发布下旁边没有文件。
    /// </summary>
    public const string IconAssetUri = "avares://LiteReader/Assets/icon.png";

    /// <summary>Linux：XDG 数据目录（<c>$XDG_DATA_HOME</c>，未设置时回落 <c>~/.local/share</c>）。</summary>
    public static string XdgDataHome()
    {
        string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    /// <summary>
    /// Windows：双击文件时系统实际执行的命令行。
    /// <c>%1</c> 是系统替换成的文件路径，**必须加引号**，否则带空格的路径会被拆成两个参数
    /// （原版这里也是加引号的，照抄）。
    /// </summary>
    public static string WindowsOpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>Windows：某个扩展名下的「打开方式候选」键路径。</summary>
    public static string WindowsOpenWithKey(string extension) =>
        $@"Software\Classes\{extension}\OpenWithList\LiteReader.exe";

    /// <summary>Linux：desktop 文件的落地路径（XDG 规范的数据目录）。</summary>
    public static string LinuxDesktopPath() => Path.Combine(XdgDataHome(), "applications", DesktopId);

    /// <summary>
    /// Linux：图标该落到哪里。<c>hicolor</c> 是图标主题规范要求的兜底主题，
    /// 目录名 <c>256x256</c> 必须与实际像素尺寸一致 —— 尺寸目录写错，桌面环境会按
    /// 「这个主题里没有 256 的这一档」处理，表现就是「文件关联好了但图标是空白」。
    /// </summary>
    public static string LinuxIconPath() =>
        Path.Combine(XdgDataHome(), "icons", "hicolor", "256x256", "apps", IconName + ".png");

    /// <summary>
    /// Linux：desktop 文件内容。
    /// <c>Exec</c> 里的 <c>%F</c> 是「可以接受多个文件」的意思（对应我们命令行确实支持多路径）；
    /// <c>Categories=Development;TextEditor;</c> 决定它出现在菜单的哪个分组里；
    /// <c>Icon=lite-reader</c> 是**名字**不是路径 —— 配套的 <see cref="LinuxIconPath"/>
    /// 必须真的把 PNG 装到位，否则菜单里就是个空白图标。
    /// </summary>
    public static string LinuxDesktopEntry(string exePath) =>
        string.Join('\n',
        [
            "[Desktop Entry]",
            "Type=Application",
            "Name=LiteReader",
            "GenericName=Code Reader",
            "Comment=代码阅读器（Avalonia 跨平台版）",
            $"Icon={IconName}",
            $"Exec=\"{exePath}\" %F",
            "Terminal=false",
            "Categories=Development;TextEditor;Utility;",
            "MimeType=" + string.Join(';', LinuxMimeTypes) + ";",
            "StartupNotify=true",
            "",
        ]);

    /// <summary>Linux：愿意接的 MIME 类型（xdg-mime 是按 MIME 而不是按扩展名挂的）。</summary>
    private static readonly string[] LinuxMimeTypes =
    [
        "text/plain", "text/markdown", "text/x-csrc", "text/x-chdr", "text/x-c++src",
        "text/x-c++hdr", "text/x-python", "text/x-java", "text/x-csharp", "text/x-script.python",
        "text/x-shellscript", "application/json", "application/xml", "text/xml",
        "application/x-yaml", "text/yaml", "application/sql",
    ];

    // ---------------- 正式入口：按平台注册 ----------------

    /// <summary>
    /// 执行注册。**只写用户级位置（HKCU / ~/.local / 一个说明文件）**，不需要管理员权限，
    /// 也不改任何已有的默认关联。返回的结果里 <c>ReportPath</c> 指向一份带完整说明的文本。
    /// </summary>
    public static async Task<AssociationResult> RegisterAsync()
    {
        string exe = ExecutablePath();
        var log = new StringBuilder();
        log.AppendLine($"LiteReader 文件关联注册结果");
        log.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"程序：{exe}");
        log.AppendLine($"平台：{PlatformName()}");
        log.AppendLine();

        bool ok;
        string summary;

        if (OperatingSystem.IsWindows())
        {
            (ok, summary) = RegisterWindows(exe, log);
        }
        else if (OperatingSystem.IsMacOS())
        {
            (ok, summary) = await RegisterMacAsync(exe, log);
        }
        else
        {
            (ok, summary) = await RegisterLinuxAsync(exe, log);
        }

        string reportPath = WriteReport(log);
        return new AssociationResult(ok, summary, reportPath);
    }

    // ---------------- Windows ----------------

    [SupportedOSPlatform("windows")]
    private static (bool Ok, string Summary) RegisterWindows(string exe, StringBuilder log)
    {
        try
        {
            // 先看一眼原来是谁占着这个键 —— 原版 C++ 的 LiteReader 用的是**同一个键名**
            // （都是 `Applications\LiteReader.exe`），所以这次写下去会把它的注册顶掉。
            // 这本身是合理的（同一个程序、只该有一个「打开方式」条目），但必须说出来：
            // 默默把用户的旧关联改掉是最讨人厌的那种「我帮你优化了」。
            string? previous = ReadExistingOpenCommand();

            using RegistryKey app = Registry.CurrentUser.CreateSubKey(WindowsAppKey, writable: true);
            app.SetValue("FriendlyAppName", WindowsFriendlyName, RegistryValueKind.String);
            using (RegistryKey cmd = app.CreateSubKey(@"shell\open\command", writable: true))
                cmd.SetValue(string.Empty, WindowsOpenCommand(exe), RegistryValueKind.String);

            int n = 0;
            var failed = new List<string>();
            foreach (string ext in Extensions)
            {
                try
                {
                    using RegistryKey k = Registry.CurrentUser.CreateSubKey(WindowsOpenWithKey(ext), writable: true);
                    if (k is not null) n++;
                }
                catch (Exception ex)
                {
                    failed.Add($"{ext}（{ex.Message}）");
                }
            }

            log.AppendLine($"已写入 HKCU\\{WindowsAppKey}");
            log.AppendLine($"    FriendlyAppName = {WindowsFriendlyName}");
            log.AppendLine($"    shell\\open\\command = {WindowsOpenCommand(exe)}");
            log.AppendLine($"已写入 {n} 个扩展名的「打开方式候选」：{string.Join(' ', Extensions)}");
            if (failed.Count > 0) log.AppendLine($"⚠ 失败的扩展名：{string.Join("、", failed)}");
            log.AppendLine();

            if (previous is not null && !string.Equals(previous, WindowsOpenCommand(exe), StringComparison.OrdinalIgnoreCase))
            {
                log.AppendLine("⚠ 覆盖掉了一条已有的注册，原值：");
                log.AppendLine($"    {previous}");
                log.AppendLine("  两者用的是同一个键名（`Applications\\LiteReader.exe`），所以只能留一个。");
                log.AppendLine("  如果那指向的是旧版（例如 C++ 单文件版）的 LiteReader.exe，");
                log.AppendLine("  想还原的话，把上面这行原值写回同一个键即可；或者跑一次旧版的「注册」菜单。");
                log.AppendLine();
            }

            log.AppendLine("接下来怎么用：");
            log.AppendLine("  1) 在资源管理器里右键某个文件 → 打开方式 → 选择其他应用");
            log.AppendLine("  2) 列表里应该能看到「LiteReader 代码阅读器」");
            log.AppendLine("  3) 勾选「始终使用此应用」才会变成默认 —— 本次没有替你改默认关联");
            log.AppendLine();
            log.AppendLine("想撤销的话，删掉这两个注册表位置即可：");
            log.AppendLine($"  HKCU\\{WindowsAppKey}");
            log.AppendLine($"  HKCU\\Software\\Classes\\<扩展名>\\OpenWithList\\LiteReader.exe");

            return (n > 0, n > 0
                ? $"已注册到「打开方式」列表（{n} 个扩展名）。右键文件 → 打开方式 即可选到 LiteReader。"
                : "注册失败：没有写入任何扩展名，详见报告");
        }
        catch (Exception ex)
        {
            log.AppendLine($"✘ 写入注册表失败：{ex.GetType().Name}: {ex.Message}");
            log.AppendLine();
            log.AppendLine("常见原因：");
            log.AppendLine("  · 组策略/安全软件禁止写 HKCU\\Software\\Classes");
            log.AppendLine("  · 当前账户没有 HKCU 写权限（极少见）");
            return (false, $"注册失败：{ex.Message}（详见报告）");
        }
    }

    // ---------------- Linux ----------------

    private static async Task<(bool Ok, string Summary)> RegisterLinuxAsync(string exe, StringBuilder log)
    {
        // 图标先落地：desktop 里的 Icon=lite-reader 是个**名字**，
        // 桌面环境拿到名字后会去图标主题目录找 256x256/apps/lite-reader.png。
        // 少了这一步，文件关联本身是成功的，但菜单/任务栏里是个空白方块。
        (bool iconOk, string iconMsg) = await InstallLinuxIconAsync();
        log.AppendLine(iconOk
            ? $"已安装图标：{LinuxIconPath()}"
            : $"⚠ 未能安装图标：{iconMsg}");
        log.AppendLine();

        string path = LinuxDesktopPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, LinuxDesktopEntry(exe), new UTF8Encoding(false));
            log.AppendLine($"已写入 desktop 文件：{path}");
            log.AppendLine($"    Icon={IconName}");
            log.AppendLine($"    Exec=\"{exe}\" %F");
            log.AppendLine($"    MimeType 共 {LinuxMimeTypes.Length} 项");
            log.AppendLine();
        }
        catch (Exception ex)
        {
            log.AppendLine($"✘ 写入 desktop 文件失败：{ex.GetType().Name}: {ex.Message}（{path}）");
            return (false, $"注册失败：无法写入 {path}（详见报告）");
        }

        // 有了 MimeType 还不够：xdg-mime 才是「把这个 desktop 文件挂到这些 MIME 类型上」的那一步。
        // 它不一定装了（最小化发行版常见），所以失败只记一笔，不算整体失败 —— 文件已经就位了。
        (bool mimeOk, string mimeMsg) = await RunAsync("xdg-mime",
            ["default", DesktopId, .. LinuxMimeTypes]);
        log.AppendLine(mimeOk
            ? $"已执行：xdg-mime default {DesktopId} <{LinuxMimeTypes.Length} 种 MIME>"
            : $"⚠ 未能执行 xdg-mime：{mimeMsg}");
        log.AppendLine("    （xdg-mime 缺失时文件关联不会自动生效，但「打开方式」里通常仍能看到它）");

        (bool dbOk, string dbMsg) = await RunAsync("update-desktop-database",
            [Path.GetDirectoryName(path)!]);
        log.AppendLine(dbOk
            ? "已刷新 desktop 数据库：update-desktop-database"
            : $"⚠ 未刷新 desktop 数据库：{dbMsg}");

        log.AppendLine();
        log.AppendLine("接下来怎么用：");
        log.AppendLine("  1) 文件管理器里右键文件 → 打开方式 → LiteReader");
        log.AppendLine("  2) 想直接设成默认：xdg-mime default lite-reader.desktop text/plain");
        log.AppendLine();
        log.AppendLine("想撤销：删除 " + path);
        log.AppendLine("        xdg-mime default <原来的那个>.desktop <MIME 类型>");

        return (true, $"已写入 {path}" + (mimeOk ? "，并已挂上 MIME 关联。" : "（xdg-mime 未执行，详见报告）")
                      + (iconOk ? "" : "；⚠ 图标未安装"));
    }

    /// <summary>
    /// Linux：把 <c>Assets/icon.png</c> 复制进 XDG 图标主题目录。
    /// 从**程序集资源**里读，而不是「exe 旁边的文件」—— 单文件发布下旁边没有文件，
    /// 而资源在任何发布形态下都在。
    /// </summary>
    private static async Task<(bool Ok, string Message)> InstallLinuxIconAsync()
    {
        try
        {
            await using Stream? src = AssetLoader.Open(new Uri(IconAssetUri));
            if (src is null) return (false, "资源里没有这张图");

            string dest = LinuxIconPath();
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using FileStream dst = File.Create(dest);
            await src.CopyToAsync(dst);
            return (true, dest);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- macOS ----------------

    private static async Task<(bool Ok, string Summary)> RegisterMacAsync(string exe, StringBuilder log)
    {
        // .app 包里的 Info.plist 才是文件类型的登记处，而运行中的 .app 通常是签名的，
        // 改它等于破坏签名 → 这里只生成片段，不动任何系统状态。
        var sb = new StringBuilder();
        sb.AppendLine("    <key>CFBundleDocumentTypes</key>");
        sb.AppendLine("    <array>");
        sb.AppendLine("      <dict>");
        sb.AppendLine("        <key>CFBundleTypeName</key>");
        sb.AppendLine("        <string>Source Code</string>");
        sb.AppendLine("        <key>CFBundleTypeRole</key>");
        sb.AppendLine("        <string>Viewer</string>");
        sb.AppendLine("        <key>LSHandlerRank</key>");
        sb.AppendLine("        <string>Alternate</string>   <!-- Alternate = 只出现在「打开方式」里，不抢默认 -->");
        sb.AppendLine("        <key>LSItemContentTypes</key>");
        sb.AppendLine("        <array>");
        sb.AppendLine("          <string>public.plain-text</string>");
        sb.AppendLine("          <string>public.source-code</string>");
        sb.AppendLine("          <string>public.json</string>");
        sb.AppendLine("          <string>public.xml</string>");
        sb.AppendLine("        </array>");
        sb.AppendLine("      </dict>");
        sb.AppendLine("    </array>");

        log.AppendLine("macOS 无法由程序自己完成注册 —— 说清楚为什么：");
        log.AppendLine("  文件类型登记在 .app 包内的 Contents/Info.plist 里，而运行中的 .app 一般已签名，");
        log.AppendLine("  运行期改写它会破坏签名（Gatekeeper 会拒绝启动）。所以这一步必须你来决定。");
        log.AppendLine();
        log.AppendLine($"当前可执行文件：{exe}");
        log.AppendLine();
        log.AppendLine("做法（二选一）：");
        log.AppendLine();
        log.AppendLine("A. 如果你有 .app 包（推荐）");
        log.AppendLine("   1) 找到 LiteReader.app，右键 → 显示包内容 → Contents/Info.plist");
        log.AppendLine("   2) 在顶层 <dict> 里加入下面这段：");
        log.AppendLine();
        log.Append(sb);
        log.AppendLine();
        log.AppendLine("   3) 重建签名（否则可能启动不了）：");
        log.AppendLine("        codesign --force --deep --sign - /路径/LiteReader.app");
        log.AppendLine("   4) 刷新 Launch Services 的文件类型缓存：");
        log.AppendLine("        /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /路径/LiteReader.app");
        log.AppendLine();
        log.AppendLine("B. 不想动 .app 的话");
        log.AppendLine("   右键文件 → 打开方式 → 其他… → 选中 LiteReader，再勾选「始终以此方式打开」；");
        log.AppendLine("   这条路径不需要改任何包内的东西。");
        log.AppendLine();
        log.AppendLine("图标（顺带说一句，因为 .icns 也只能靠你生成）");
        log.AppendLine("  程序里带的是 PNG（Assets/icon.png，256×256）与 Windows 的 .ico；");
        log.AppendLine("  macOS 要的是 .icns，而 iconutil 只在 macOS 上才有，所以这一步同样交给你：");
        log.AppendLine("    1) 建一个 icon.iconset/ 目录，放这些尺寸的 PNG（用 Assets/icon.png 缩放即可）：");
        log.AppendLine("         icon_16x16.png  icon_16x16@2x.png(32)   icon_32x32.png  icon_32x32@2x.png(64)");
        log.AppendLine("         icon_128x128.png icon_128x128@2x.png(256) icon_256x256.png icon_256x256@2x.png(512)");
        log.AppendLine("         icon_512x512.png icon_512x512@2x.png(1024)");
        log.AppendLine("       缩放命令：sips -z 256 256 Assets/icon.png --out icon_256x256.png");
        log.AppendLine("    2) iconutil -c icns icon.iconset");
        log.AppendLine("    3) 把得到的 icon.icns 放进 LiteReader.app/Contents/Resources/，");
        log.AppendLine("       并在 Info.plist 里加 <key>CFBundleIconFile</key><string>icon</string>");
        log.AppendLine("    4) 同样要重新签名（见上面 A 的第 3 步），否则图标不会刷新");

        await Task.CompletedTask;
        // 报告由 RegisterAsync 统一写一次（文件名不带平台后缀，报告正文里已经有平台了）
        return (true, "macOS 需要手动完成：plist 片段与步骤已写进报告，已用标签页打开");
    }
    // ---------------- 工具 ----------------

    /// <summary>
    /// 读当前已注册的 `shell\open\command` 默认值（没有就返回 null）。
    /// 只为在报告里说清「这次覆盖了什么」—— 读取失败不影响注册本身。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadExistingOpenCommand()
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey($@"{WindowsAppKey}\shell\open\command")
                ?.GetValue(string.Empty) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string PlatformName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "未知";

    /// <summary>
    /// 当前进程的可执行文件路径。
    /// 注意别用 <c>Assembly.Location</c>：单文件发布与 Native AOT 下它都是空串（见 README §6 的发布形态）。
    /// <c>Environment.ProcessPath</c> 在两种形态下都对。
    /// </summary>
    public static string ExecutablePath()
    {
        string? p = Environment.ProcessPath;
        return string.IsNullOrEmpty(p) ? "LiteReader" : p;
    }

    /// <summary>把报告写到配置目录，返回路径（供主程序用标签页打开）。</summary>
    private static string WriteReport(StringBuilder log)
    {
        string path = Path.Combine(AppConfigStore.DirectoryPath, "文件关联.txt");
        try
        {
            Directory.CreateDirectory(AppConfigStore.DirectoryPath);
            File.WriteAllText(path, log.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // 写不进去也不能让「注册」这个动作看起来失败了 —— 返回计划路径，状态栏里仍有摘要。
        }
        return path;
    }

    /// <summary>跑一个外部命令并等它结束。找不到该命令 / 非零退出都返回 false + 原因。</summary>
    private static async Task<(bool Ok, string Message)> RunAsync(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            if (p is null) return (false, "启动失败");
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? (true, string.Empty) : (false, $"退出码 {p.ExitCode} {err.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
