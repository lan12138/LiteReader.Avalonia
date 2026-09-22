// LiteReader · Avalonia 跨平台版 —— 在文件所在目录打开外部终端
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 对应 README §9.1 里「在文件所在目录打开终端」那一行：
//   对照实现（`ShellExecuteW("open", "wt.exe"/"powershell.exe"/"cmd.exe")`）→ 三平台各自探测
//
// 原版逻辑是「先试 Windows Terminal，再试 PowerShell，最后 cmd」，谁先起来算谁赢 ——
// **这个「有序尝试、失败就换下一个」的形状本身就跨平台**，换掉的只是候选清单：
//   Windows：wt.exe → powershell.exe → cmd.exe      （与原版逐字一致）
//   Linux  ：gnome-terminal → konsole → xfce4-terminal → alacritty → kitty
//            → x-terminal-emulator（Debian 系的多态入口）→ xterm
//   macOS  ：open -a Terminal
//
// 为什么不像原版那样先「枚举已存在的终端窗口」把它叫到前面来：
//   Linux/Wayland 不允许客户端枚举窗口，macOS 也没有等价 API —— 写出来就是三份平台代码，
//   而且注定有一份在 Wayland 上是废的。所以这里只保证「能起一个 cd 到目标目录的终端」，
//   不承诺复用已开的那个窗口。功能上退了一步，但三平台行为一致、代码还短。
//
// 目录从哪来：当前焦点标签所在目录 → 上次打开的文件夹 → 用户主目录
// （原版是「当前文件所在目录 → 我的文档」，把中间那级换成上次文件夹，比直接跳主目录更有用）。

using System.Diagnostics;
using System.Globalization;

namespace LiteReader.Services;

/// <summary>
/// 一个候选终端。
/// <paramref name="DirArgFormat"/> 为 null 表示「这个终端没有指定起始目录的开关」——
/// 那就只靠进程的工作目录（大部分终端会继承它，少数不会，但至少有终端可用）；
/// 非 null 时用 <c>{0}</c> 占位，路径按原样塞进去（调用方负责加引号）。
/// </summary>
internal sealed record TerminalCandidate(string Exe, string? DirArgFormat);

/// <summary>候选清单按平台分组。抽成枚举只为一件事：让自检能断言**另外两个平台**的清单结构
/// —— 本机只能跑 Windows，Linux/macOS 那两份列表否则一行都验不到。</summary>
internal enum TerminalPlatform { Windows, Linux, MacOS }

internal static class TerminalLauncher
{
    public static TerminalPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? TerminalPlatform.Windows
        : OperatingSystem.IsMacOS() ? TerminalPlatform.MacOS
        : TerminalPlatform.Linux;

    /// <summary>当前平台的候选终端，按优先级排列。</summary>
    public static IReadOnlyList<TerminalCandidate> Candidates() => CandidatesFor(CurrentPlatform());

    /// <summary>
    /// 指定平台的候选终端。
    /// 抽成纯函数是为了能被自检直接断言（真的去起终端在 CI 上不可行、也不该有副作用）。
    /// </summary>
    public static IReadOnlyList<TerminalCandidate> CandidatesFor(TerminalPlatform platform) => platform switch
    {
        TerminalPlatform.Windows =>
        [
            new("wt.exe", "-d \"{0}\""),          // Windows Terminal：-d 指定起始目录
            new("powershell.exe", null),          // 进程工作目录即起始目录
            new("cmd.exe", null),
        ],

        // Terminal.app 用 open 打开一个目录就等于「在新窗口里 cd 到那儿」
        TerminalPlatform.MacOS => [new("open", "-a Terminal \"{0}\"")],

        _ =>
        [
            new("gnome-terminal", "--working-directory=\"{0}\""),
            new("konsole", "--workdir \"{0}\""),
            new("xfce4-terminal", "--working-directory=\"{0}\""),
            new("alacritty", "--working-directory \"{0}\""),
            new("kitty", "--directory \"{0}\""),
            new("x-terminal-emulator", null),     // Debian 系的多态入口，无法传目录
            new("xterm", null),
        ],
    };

    /// <summary>
    /// 依次尝试候选终端，第一个起来的算成功。
    /// </summary>
    /// <param name="used">成功的那个可执行文件名（用于给用户一句确切的反馈）。</param>
    /// <returns>全部失败返回 false。</returns>
    public static bool TryOpen(string directory, out string used)
    {
        foreach (TerminalCandidate c in Candidates())
        {
            try
            {
                var psi = new ProcessStartInfo(c.Exe)
                {
                    UseShellExecute = true,          // 交给系统按 PATH/注册表去找；也顺带把进程「脱手」
                    WorkingDirectory = directory,
                };
                if (c.DirArgFormat is { } fmt)
                    psi.Arguments = string.Format(CultureInfo.InvariantCulture, fmt, directory);

                Process.Start(psi);
                used = c.Exe;
                return true;
            }
            catch
            {
                // 这个终端没装 / 启动失败 —— 试下一个。
                // 不区分异常类型：Win32Exception(文件不存在)、平台不支持、权限…在这里处理方式都一样。
            }
        }

        used = string.Empty;
        return false;
    }

    /// <summary>
    /// 决定在哪儿开终端：当前文件所在目录 → 上次打开的文件夹 → 用户主目录。
    /// </summary>
    public static string ResolveDirectory(string? currentFile, string? lastFolder)
    {
        if (!string.IsNullOrEmpty(currentFile))
        {
            string? d = Path.GetDirectoryName(currentFile);
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
        }

        if (!string.IsNullOrEmpty(lastFolder) && Directory.Exists(lastFolder)) return lastFolder;

        // Personal：Windows 是「我的文档」、Linux 是 $HOME、macOS 是家目录 —— 与原版 CSIDL_PERSONAL 同义。
        string personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrEmpty(personal) && Directory.Exists(personal)) return personal;

        return OperatingSystem.IsWindows() ? "C:\\" : "/";
    }
}
