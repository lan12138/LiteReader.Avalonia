// LiteReader · Avalonia 跨平台版 —— 标签页视图模型
// 作者：hujie  创建：2026-09-18  修改：2026-09-18
//
// 修订说明（2026-09-18）：加入光标/选区/水平滚动/首行 四个视图状态。
//   它们放在标签 VM 上（而不是控件里）的原因：TabControl 切标签会销毁并重建内容控件，
//   状态留在这里才能在切回来时恢复到原来的位置。

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiteReader.Models;

namespace LiteReader.ViewModels;

public sealed partial class DocumentTabViewModel : ObservableObject
{
    public DocumentModel Document { get; }

    public DocumentTabViewModel(DocumentModel document)
    {
        Document = document;
        Document.Changed += OnDocumentChanged;
    }

    /// <summary>
    /// 由 MainWindowViewModel 在创建标签时注入（命令里捕获了 tab 自身与主 VM）。
    /// 这样 XAML 里写 Command="{Binding CloseCommand}" 就够了，
    /// 不必用 RelativeSource 一路回溯到 Window 的 DataContext。
    /// </summary>
    public IRelayCommand? CloseCommand { get; set; }

    public string Title => Document.IsDirty ? Document.FileName + " *" : Document.FileName;

    // ---------------- 视图状态（切标签后恢复用） ----------------

    [ObservableProperty]
    private int _firstVisibleLine;

    [ObservableProperty]
    private double _horizontalOffset;

    /// <summary>光标字符偏移（全局）。</summary>
    [ObservableProperty]
    private int _caretOffset;

    /// <summary>选区锚点。与 CaretOffset 相同表示无选区。</summary>
    [ObservableProperty]
    private int _anchorOffset;

    /// <summary>状态栏用的光标行列（1 基，UTF-16 列号）。</summary>
    public string CaretPosition
    {
        get
        {
            int line = Document.LineIndexOf(CaretOffset);
            return $"{line + 1}:{CaretOffset - Document.LineStartOffset(line) + 1}";
        }
    }

    private void OnDocumentChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CaretPosition));
    }

    /// <summary>
    /// 光标偏移由 EditorSurface 双向绑定写回，这里顺手刷新状态栏的行列号 ——
    /// 打字时光标会动但文本也动，两条路都能触发，重复通知无副作用。
    /// </summary>
    partial void OnCaretOffsetChanged(int value) => OnPropertyChanged(nameof(CaretPosition));

    public void Load(string path)
    {
        Document.Load(path);
        CaretOffset = 0;
        AnchorOffset = 0;
        FirstVisibleLine = 0;
        HorizontalOffset = 0;
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CaretPosition));
    }

    /// <summary>状态栏用的一行摘要。</summary>
    public string Summary =>
        $"{Document.EncodingName}{(Document.HasBom ? "+BOM" : string.Empty)}   " +
        $"行 {Document.LineCount:N0}   字符 {Document.CharCount:N0}";
}
