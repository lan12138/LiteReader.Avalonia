// LiteReader · Avalonia 跨平台版 —— 启动参数解析
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 启动参数一共就三类，但**两个地方都要用同一套判断**（Program 决定要不要走单实例转发、
// App 决定要打开哪些文件），所以规则只能有一份 —— 复制第二份迟早会漂移成
// 「单实例转发过去的文件和真正打开的文件不一样」这种难查的问题。
//
//   ① 诊断开关：任何以 `-` 开头的参数（--selftest / --fuzz / --bench-edit / 未来的都算）
//   ② 文件路径：不以 `-` 开头、且**确实存在**的参数
//   ③ 其余（不存在的路径、拼错的开关）：忽略
//
// 为什么诊断模式要单独成一类：诊断带任何开关时都**不做单实例转发**。
// 否则「已经开着一个 LiteReader，再跑 --selftest」会被判成第二次启动，
// 把样本文件丢给已开着的窗口就自己退了 —— 自检压根不会跑，而 CI 上还会看到退出码 0。

namespace LiteReader.Services;

internal static class StartupFiles
{
    /// <summary>是不是诊断模式（带了任何一个以 `-` 开头的参数）。</summary>
    public static bool IsDiagnostic(string[] args) => args.Any(a => a.StartsWith('-'));

    /// <summary>参数里确实存在的文件路径，按原顺序。重复的会在这里去掉（同一路径给两次只开一个标签）。</summary>
    public static List<string> ExistingFiles(string[] args)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith('-')) continue;
            if (!File.Exists(a)) continue;
            if (seen.Add(Path.GetFullPath(a))) list.Add(a);
        }
        return list;
    }
}
