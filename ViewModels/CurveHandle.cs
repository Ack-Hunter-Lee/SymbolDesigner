using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>曲线手柄的角色：起点 / 两个贝塞尔控制点 / 终点。</summary>
public enum CurveHandleKind
{
    /// <summary>曲线起点（M 命令）。</summary>
    Start,
    /// <summary>第一控制点（C 命令的第一个控制点），决定起点处的切线方向与弯曲量。</summary>
    Control1,
    /// <summary>第二控制点（C 命令的第二个控制点），决定终点处的切线方向与弯曲量。</summary>
    Control2,
    /// <summary>曲线终点。</summary>
    End,
}

/// <summary>曲线（三次贝塞尔）手柄模型：X/Y 是画布坐标（已含图元 X/Y 与旋转），
/// View 用 Canvas.Left/Top 定位；拖动时通过 ViewModel.UpdateCurveHandle 写回 PathData。</summary>
public partial class CurveHandle : ObservableObject
{
    public CurveHandleKind Kind { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public CurveHandle(CurveHandleKind kind, double x, double y)
    {
        Kind = kind;
        _x = x;
        _y = y;
    }
}
