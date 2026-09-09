using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>扇形手柄类型。</summary>
public enum PieHandleKind
{
    /// <summary>中心手柄：拖动 = 平移整个扇形（保持半径与角度）。</summary>
    Center,
    /// <summary>半径手柄：拖动 = 改变半径（Width/Height 等比变，圆心不动）。</summary>
    Radius,
    /// <summary>起始角手柄：位于圆周起始角位置，拖动 = 旋转起始半径线（改 StartAngle）。</summary>
    Start,
    /// <summary>扫掠角手柄：位于圆周终止角位置，拖动 = 改变扫掠角（SweepAngle）。</summary>
    End,
}

/// <summary>扇形拖拽手柄模型：画布坐标 + 类型。
/// X/Y 为画布坐标（含 SelectedItem.X/Y 偏移），拖拽中由 VM 直接更新（集合不重建，UI 平滑跟随）。</summary>
public partial class PieHandle : ObservableObject
{
    public PieHandleKind Kind { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public PieHandle(PieHandleKind kind, double x, double y)
    {
        Kind = kind;
        _x = x;
        _y = y;
    }
}