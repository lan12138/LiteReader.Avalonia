// LiteReader · Avalonia 跨平台版 —— 单实例 + 文件转发
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 对应 README §9.1 里「单实例 + 文件转发」那一行：
//   对照实现（`CreateMutexW` + `FindWindowW` + `WM_COPYDATA`）→ 跨平台做法
//
// 为什么不能照搬原版：
//   原版是「命名互斥体判重 + 按窗口类名 FindWindow 找到那个窗口 + WM_COPYDATA 递路径」。
//   三个环节里有两个是 Win32 独有的：
//     · `EnumWindows`/`FindWindow` 在 **Linux/Wayland 下根本枚举不到窗口**
//       （协议上就不给客户端这个能力），macOS 也没有等价物；
//     · `WM_COPYDATA` 更不用说。
//   所以「找到那个窗口」这一步必须换掉 —— 换成**命名管道**直接跟对端进程说话，
//   不依赖窗口系统的任何能力。
//
// 现在的机制（两个名字，一个管「谁先来的」，一个管「怎么说话」）：
//   ① 命名互斥体 `LiteReader.SingleInstance.v1` —— 首次创建者就是第一实例（三平台都支持）；
//   ② 命名管道   `LiteReader.SingleInstance.v1` —— 第一实例在后台线程上收，
//      后来者把「要打开的文件」按二进制帧写进去就退出。
//
// 为什么不用 TCP 回环端口：端口会被占、会被防火墙问、多用户下还会互相打架。
// 命名管道在 Windows 是内核对象，在 Unix 上 .NET 会落到 `$TMPDIR/.dotnet/corefx/` 下的
// 本地 socket 文件 —— 天然「同用户可见、跨用户隔离」，正是单实例想要的语义。
//
// ⚠️ 别把「互斥体」当唯一判据：不排除极端情况下第一实例已经死了但名字还挂着
//    （见 TrySend 的失败分支）。所以**发不出去就当没有第一实例，自己起来**，
//    宁可多开一个窗口，也不要双击图标之后什么都没发生。
//
// 为什么用 BinaryWriter/Reader 而不是 JSON：
//   管道是本机、同用户、短连接，不需要人类的可读性；而文件名里可能有空格、中文、
//   甚至（在 Linux 上合法地）换行和引号 —— 7-bit 长度前缀 + UTF-8 是最省心的编码，
//   也顺手避开了「裁剪模式下反射版 JSON 要不要保护」这类无关问题。

using System.IO.Pipes;
using System.Text;

namespace LiteReader.Services;

internal static class SingleInstance
{
    /// <summary>判重用的名字。改这个字符串等于把所有老实例当成另一程序（升级时才会想改）。</summary>
    public const string MutexName = "LiteReader.SingleInstance.v1";

    /// <summary>转发通道的名字。</summary>
    public const string PipeName = "LiteReader.SingleInstance.v1";

    /// <summary>单条消息最多接受多少个路径 —— 纯粹是防呆，正常不会超过个位数。</summary>
    private const int MaxPathsPerMessage = 4096;

    /// <summary>
    /// 第一实例的持有者。**必须是静态字段**：命名互斥体靠「有没有句柄开着」来判断名字是否还存在，
    /// 句柄一旦被 GC 回收，「第一个实例」这件事就自动失效了，第二个进程会误判成自己是第一实例。
    /// </summary>
    private static readonly List<Mutex> Held = [];

    private static string? _judgedName;
    private static bool _judgedFirst;

    /// <summary>
    /// 本进程是不是第一个实例。同一个名字只判定一次（后续调用直接返回缓存值）。
    /// </summary>
    public static bool IsFirstInstance(string name = MutexName)
    {
        if (_judgedName == name) return _judgedFirst;

        bool first = !ProbeNameTaken(name);
        if (first) Hold(name);

        _judgedName = name;
        _judgedFirst = first;
        return first;
    }

    /// <summary>
    /// 原始探测：这个名字是否已被（别的进程）创建。
    /// 与 <see cref="IsFirstInstance"/> 分开是为了让自检能直接测这个机制本身，
    /// 而不是被「同名字只判定一次」的缓存挡住。
    /// </summary>
    internal static bool ProbeNameTaken(string name)
    {
        try
        {
            using var m = new Mutex(initiallyOwned: false, name, out bool createdNew);
            return !createdNew;
        }
        catch
        {
            // 名字非法 / 平台不支持 / 权限问题：当作「没人占」，让程序能起来。
            // 单实例是体验优化，不是正确性约束 —— 绝不能因为它起不来。
            return false;
        }
    }

    /// <summary>长期持有该名字（把句柄挂到静态列表上，本次进程内不再释放）。</summary>
    private static void Hold(string name)
    {
        try { Held.Add(new Mutex(initiallyOwned: false, name)); }
        catch { /* 见 ProbeNameTaken 的说明 */ }
    }

    // ---------------- 后来者：把文件递过去 ----------------

    /// <summary>
    /// 把 <paramref name="paths"/> 发给已存在的实例。
    /// 空列表 = 「只是把已有窗口叫到前面来」（双击图标启动、没带文件的情形）。
    /// </summary>
    /// <returns>成功送达返回 true；连接不上（或一直连不上）返回 false。</returns>
    public static bool TrySend(IReadOnlyList<string> paths, string? pipeName = null, int timeoutMs = 2000)
    {
        string name = pipeName ?? PipeName;
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(100, timeoutMs));

        // 轮询重试：第一实例可能刚被拉起、管道还没创建好。
        // 对照 LiteReader.cpp 的「60 次 × 20 ms 等窗口出现」，这里给 2 秒。
        while (true)
        {
            try
            {
                int remain = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.None);
                client.Connect(Math.Max(50, remain));
                WritePayload(client, paths);
                client.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException
                                          or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                if (DateTime.UtcNow >= deadline) return false;
                Thread.Sleep(50);
            }
            catch
            {
                return false;   // 其余异常不重试（例如参数非法），直接认输让调用方自己起来
            }
        }
    }

    // ---------------- 第一实例：收 ----------------

    /// <summary>开始在后台线程收消息。返回的监听器 Dispose 时会停线程并放出管道名字。</summary>
    public static Listener StartListener(Action<IReadOnlyList<string>> onPaths, string? pipeName = null)
        => new(onPaths, pipeName ?? PipeName);

    /// <summary>命名管道上的一个「接收端」。一个连接传一批路径，读满即断。</summary>
    public sealed class Listener : IDisposable
    {
        private readonly Action<IReadOnlyList<string>> _onPaths;
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;

        internal Listener(Action<IReadOnlyList<string>> onPaths, string pipeName)
        {
            _onPaths = onPaths;
            _pipeName = pipeName;
            _thread = new Thread(Loop) { IsBackground = true, Name = "LiteReader-IPC" };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    // 每轮新建一个 server stream：一个连接一批消息，读完就断。
                    // 用 Disconnect() 复用同一个对象在 Windows 上可以，在 Unix 后端语义没那么确定，
                    // 而「重连时的微小空窗」由客户端的轮询重试兜住了，所以这里选更保险的写法。
                    using var server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    // 不用 WaitForConnectionAsync(token)：Unix 后端对「取消正在进行的 accept」
                    // 支持得不彻底，退出时可能卡住。改成定时轮询取消标志 —— 最多延迟 200 ms，可接受。
                    Task wait = server.WaitForConnectionAsync();
                    while (!wait.Wait(200))
                    {
                        if (_cts.IsCancellationRequested) return;
                    }

                    IReadOnlyList<string> paths = ReadPayload(server);
                    _onPaths(paths);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch
                {
                    // 连接刚建立就被对端掐断（ReadInt32 撞到流尾）之类：不是错误，接着等下一个。
                    if (_cts.IsCancellationRequested) return;
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* 已释放 */ }
            // 有界等待：线程是后台线程，真卡住也不会拖住进程退出，但自检需要它确实放开管道名字。
            _thread.Join(1000);
            _cts.Dispose();
        }
    }

    // ---------------- 消息编码 ----------------

    /// <summary>
    /// 写一批路径。<see cref="BinaryWriter.Write(string)"/> 用的是「7-bit 长度前缀 + UTF-8」，
    /// 恰好就是我们要的编码，不必自己动手 —— 只要求读写两端都用同一套。
    /// </summary>
    internal static void WritePayload(Stream s, IReadOnlyList<string> paths)
    {
        var bw = new BinaryWriter(s, new UTF8Encoding(false), leaveOpen: true);
        bw.Write(paths.Count);
        foreach (string p in paths) bw.Write(p);
        bw.Flush();
    }

    /// <summary>读一批路径。消息头不合法或流提前结束都抛异常，由调用方决定怎么处理。</summary>
    internal static List<string> ReadPayload(Stream s)
    {
        var br = new BinaryReader(s, new UTF8Encoding(false), leaveOpen: true);
        int n = br.ReadInt32();
        if (n < 0 || n > MaxPathsPerMessage)
            throw new InvalidDataException($"消息头异常：{n}");

        var list = new List<string>(n);
        for (int i = 0; i < n; i++) list.Add(br.ReadString());
        return list;
    }
}
