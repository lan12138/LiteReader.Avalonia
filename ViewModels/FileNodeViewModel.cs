// LiteReader · Avalonia 跨平台版 —— 文件树节点
// 作者：hujie  创建：2026-09-18
//
// 跨平台注意：目录分隔符、隐藏文件判断（Windows 看属性，Unix 看点前缀）、
// 符号链接成环，都收敛在这里。展开时才枚举子项（懒加载），避免大目录卡 UI。

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiteReader.ViewModels;

public sealed partial class FileNodeViewModel : ObservableObject
{
    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    private bool _loaded;

    public FileNodeViewModel(string path, bool isDirectory)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name)) Name = path;   // 根目录（"/" 或 "C:\"）
        IsDirectory = isDirectory;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) Load();
    }

    /// <summary>枚举一层子项。目录优先、再按名字排序；懒加载只做一次。</summary>
    public void Load()
    {
        if (_loaded || !IsDirectory) return;
        _loaded = true;
        Children.Clear();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(FullPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(dir)) continue;
                if (IsSymlink(dir)) continue;   // 避免符号链接成环
                Children.Add(new FileNodeViewModel(dir, true) { });
            }
            foreach (string file in Directory.EnumerateFiles(FullPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(file)) continue;
                Children.Add(new FileNodeViewModel(file, false) { });
            }
        }
        catch (UnauthorizedAccessException) { /* 无权限目录：留空 */ }
        catch (IOException) { /* 设备/网络路径异常：留空 */ }
    }

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
