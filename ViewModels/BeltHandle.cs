using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>传送带端点手柄的类型：Start = 自由端点 A，End = 自由端点 B。</summary>
public enum BeltHandleKind
{
    Start,
    End,
}

/// <summary>传送带（Belt）自由端点的拖拽手柄（画布坐标）。
/// 与 <see cref="RingHandle"/> 同构：带 X/Y 可绑定属性 + 类型标记，由 View 的 ItemsControl 渲染。
/// 皮带没有"半径/带宽"类手柄——只有两个绳头，拖它们即可改变皮带走向（自动穿绕/脱开/闭合）。</summary>
public partial class BeltHandle : ObservableObject
{
    public BeltHandleKind Kind { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public BeltHandle(BeltHandleKind kind, double x, double y)
    {
        Kind = kind;
        _x = x;
        _y = y;
    }
}
