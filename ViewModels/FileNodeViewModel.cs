// LiteReader · Avalonia 跨平台版 —— 文件树节点
// 作者：hujie  创建：2026-09-18  修改：2026-09-22
//
// 修订说明（2026-09-22）：
//   1) 去掉懒加载：整棵树在载入时一次性递归建好（原来是展开一次才枚举一层，
//      每次看下一层都要双击一次，翻目录很累）。改为「一次性建树」。
//      ★ 代价是载入耗时随目录规模上升 —— 因此加了 MaxDepth（默认 4 层）与
//      MaxNodes（默认 20000）两道闸：超限的子树留一个占位节点，双击可继续展开。
//      枚举时一律跳过隐藏项与符号链接（后者防成环），这两条策略与改动前一致。
//   2) 新增 Icon / Kind：文件夹与各类文件在树上显示不同图标文字。
//      图标用 Unicode 字形（Segoe Fluent Icons / Segoe MDL2 Assets 有，其他平台回退），
//      不引入图片资源 —— 一棵几万节点的树给每个节点配位图会明显拖慢渲染。
//
// 跨平台注意：目录分隔符、隐藏文件判断（Windows 看属性，Unix 看点前缀）、
// 符号链接成环，都收敛在这里。

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiteReader.ViewModels;

/// <summary>节点种类 —— 同时决定图标与显示样式。</summary>
public enum FileNodeKind
{
    Folder,
    Code,       // 源码
    Markup,     // 标记 / 配置（xml/json/yml…）
    Document,   // 文档 / 纯文本
    Image,
    Archive,
    Binary,     // 可执行 / 库
    Unknown,
}

public sealed partial class FileNodeViewModel : ObservableObject
{
    /// <summary>
    /// 递归建树的最大深度（根算第 0 层）。取 6 的依据：
    /// 典型工程路径 `src/Proj/obj/Release/net10.0/win-x64/` 到第 6 层才见文件，
    /// 取 4 会在 `net10.0/` 那一层就停住 —— 而这一层的文件夹恰恰是最常被翻的。
    /// 再深收益递减，而节点数按层指数增长（这正是另一个闸 MaxNodes 存在的理由）。
    /// </summary>
    public const int MaxDepth = 6;

    /// <summary>整棵树最多建多少个节点，超过就停止展开，避免在大目录（如 System32）上失控。</summary>
    public const int MaxNodes = 20000;

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    /// <summary>节点种类（文件夹 / 源码 / 图片 …），供图标与样式使用。</summary>
    public FileNodeKind Kind { get; }

    /// <summary>树上显示的图标字形。</summary>
    public string Icon => IconOf(Kind, IsExpanded);

    /// <summary>是否因为深度 / 节点数上限而被截断（此时斜体显示，双击可继续展开）。</summary>
    public bool IsTruncated
    {
        get => _isTruncated;
        private set
        {
            // 用 SetProperty（ObservableObject 提供）：XAML 里 Classes.more 绑在它上面，
            // 而「截断状态」是**先展开后可能再变**的（被截断 → 手动展开 → 补建完就不再截断），
            // 不通知的话斜体样式会一直留在已经展开完的节点上。
            SetProperty(ref _isTruncated, value);
        }
    }

    private bool _isTruncated;

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>子项是否已枚举（懒加载禁用后，只在「被截断后手动展开」时用得上）。</summary>
    private bool _loaded;

    /// <summary>本节点所在层数（根 = 0）。</summary>
    private readonly int _depth;

    /// <summary>
    /// 整棵树的节点配额。用引用类型在递归里传递，而**不是静态字段** ——
    /// 静态字段会让「同时打开两个文件夹」的两棵树互相偷配额；用引用类型也不用担心
    /// 父子实参的值传递问题（int 传下去，父节点的剩余量会被同步更新）。
    /// </summary>
    private sealed class NodeBudget
    {
        public int Remaining = MaxNodes;
    }

    public FileNodeViewModel(string path, bool isDirectory)
        : this(path, isDirectory, depth: 0, new NodeBudget())
    {
    }

    private FileNodeViewModel(string path, bool isDirectory, int depth, NodeBudget budget)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name)) Name = path;   // 根目录（"/" 或 "C:\"）
        IsDirectory = isDirectory;
        Kind = isDirectory ? FileNodeKind.Folder : Classify(path);
        _depth = depth;
        BuildChildren(depth, budget);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(Icon));
        // 懒加载已取消，这里只服务被截断的子树：用户双击展开时才补建。
        if (value && !_loaded && IsDirectory) BuildChildren(_depth + 1, new NodeBudget());
    }

    private void BuildChildren(int depth, NodeBudget budget)
    {
        if (_loaded || !IsDirectory) return;
        _loaded = true;
        IsTruncated = false;
        Children.Clear();

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(FullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(dir)) continue;
                if (IsSymlink(dir)) continue;   // 避免符号链接成环

                if (depth >= MaxDepth || budget.Remaining <= 0)
                {
                    IsTruncated = true;         // 到此为止，双击再展开
                    return;
                }
                budget.Remaining--;
                Children.Add(new FileNodeViewModel(dir, true, depth + 1, budget));
            }

            foreach (string file in Directory.EnumerateFiles(FullPath)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(file)) continue;
                if (budget.Remaining <= 0) { IsTruncated = true; return; }
                budget.Remaining--;
                Children.Add(new FileNodeViewModel(file, false, depth + 1, budget));
            }
        }
        catch (UnauthorizedAccessException) { /* 无权限目录：留空 */ }
        catch (IOException) { /* 设备/网络路径异常：留空 */ }

        // 被截断时子节点已经建完，后续展开无需重复枚举。
        if (IsTruncated) _loaded = false;
    }

    // ---------------- 类型识别 ----------------

    /// <summary>按扩展名归类。顺序即优先级，逐段判断，不构建字典（调用点只在这一个类）。</summary>
    public static FileNodeKind Classify(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs" or ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hpp" or ".hh"
                or ".py" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".java"
                or ".go" or ".rs" or ".rb" or ".php" or ".swift" or ".kt" or ".scala"
                or ".lua" or ".pl" or ".r" or ".m" or ".mm" or ".asm" or ".s"
                or ".vb" or ".fs" or ".dart" or ".vue" or ".svelte" or ".cshtml"
                or ".xaml" or ".axaml" or ".razor" or ".gradle" or ".cmake"
                => FileNodeKind.Code,

            ".json" or ".xml" or ".yml" or ".yaml" or ".toml" or ".ini" or ".cfg"
                or ".conf" or ".props" or ".targets" or ".csproj" or ".sln"
                or ".editorconfig" or ".env" or ".gitignore" or ".gitattributes"
                => FileNodeKind.Markup,

            ".md" or ".markdown" or ".txt" or ".rst" or ".log" or ".csv" or ".tsv"
                or ".tex" or ".pdf" or ".doc" or ".docx" or ".odt" or ".rtf"
                or ".xls" or ".xlsx" or ".ppt" or ".pptx"
                => FileNodeKind.Document,

            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg"
                or ".ico" or ".tif" or ".tiff" or ".avif"
                => FileNodeKind.Image,

            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst"
                => FileNodeKind.Archive,

            ".exe" or ".dll" or ".so" or ".dylib" or ".pdb" or ".lib" or ".a"
                or ".o" or ".obj" or ".class" or ".jar" or ".nupkg" or ".snk"
                or ".bin" or ".dat" or ".wasm"
                => FileNodeKind.Binary,

            _ => FileNodeKind.Unknown,
        };
    }

    /// <summary>
    /// 图标字形。用的是 Windows 上随系统自带的 Segoe Fluent Icons（Win11）/ Segoe MDL2 Assets（Win10），
    /// 不打包字体；其他平台缺字形时会显示成方框 —— 因此样式里图标与名称是「并列」而不是「名称靠图标区分」，
    /// 字号、颜色上的区分同样独立成立（见 MainWindow.axaml 的 TreeView 样式）。
    /// </summary>
    public static string IconOf(FileNodeKind kind, bool isExpanded) => kind switch
    {
        FileNodeKind.Folder     => isExpanded ? "\uE838" : "\uE8B7",   // FolderOpen / Folder
        FileNodeKind.Code       => "\uE943",                            // Code
        FileNodeKind.Markup     => "\uE8A5",                            // Document（配置类）
        FileNodeKind.Document   => "\uE8A5",                            // Document
        FileNodeKind.Image      => "\uEB9F",                            // Photo
        FileNodeKind.Archive    => "\uF012",                            // ZipFolder
        FileNodeKind.Binary     => "\uE7B8",                            // Package
        _                       => "\uE7C3",                            // Page
    };

    /// <summary>Unix 用点前缀；Windows 查 Hidden 属性。</summary>
    public static bool IsHidden(string path)
    {
        string name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith('.')) return true;              // Linux / macOS
        try
        {
            FileAttributes attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.Hidden);      // Windows
        }
        catch { return false; }
    }

    public static bool IsSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch { return false; }
    }
}
