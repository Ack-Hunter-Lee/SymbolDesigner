using System.Windows.Input;

namespace SymbolDesigner.ViewModels;

/// <summary>三模式按钮的展示模型（阶段 2 引入：让 XAML 用 ItemsControl 渲染，省掉三段复制）。
/// IsSelected 与当前 OutlineEditMode 联动：高亮态由 DataTrigger 切换。</summary>
public class EditModeChoice
{
    public OutlineEditMode Mode { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public ICommand Command { get; init; } = null!;
    public object? CommandParameter { get; init; }
}