using System.Windows.Input;

namespace SymbolDesigner.ViewModels;

/// <summary>右侧面板两页按钮的展示模型（属性 / 元素）。
/// IsSelected 与 RightPanePage 联动：高亮态由 DataTrigger 切换。</summary>
public class RightPaneChoice
{
    public RightPanePage Page { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public ICommand Command { get; init; } = null!;
    public object? CommandParameter { get; init; }
}