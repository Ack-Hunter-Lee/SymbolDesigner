using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>圆环手柄类型。</summary>
public enum RingHandleKind
{
    /// <summary>中心手柄：拖动 = 平移整个圆环（保持半径）。</summary>
    Center,
    /// <summary>外圆手柄：拖动 = 改变外径（width/height 等比变，圆心不动）。</summary>
    Outer,
    /// <summary>内圆手柄：拖动 = 改变内径（inner radius，圆心不动）。</summary>
    Inner,
}

/// <summary>圆环手柄所在方向（N=北/上，E=东/右，S=南/下，W=西/左）。Center 无方向。</summary>
public enum RingHandleDirection
{
    N, E, S, W,
}

/// <summary>圆环拖拽手柄模型：画布坐标 + 类型 + 方向。
/// X/Y 为画布坐标（含 SelectedItem.X/Y 偏移），拖拽中由 VM 直接更新（集合不重建，UI 平滑跟随）。</summary>
public partial class RingHandle : ObservableObject
{
    public RingHandleKind Kind { get; }
    public RingHandleDirection Direction { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public RingHandle(RingHandleKind kind, double x, double y, RingHandleDirection direction = RingHandleDirection.N)
    {
        Kind = kind;
        _x = x;
        _y = y;
        Direction = direction;
    }
}