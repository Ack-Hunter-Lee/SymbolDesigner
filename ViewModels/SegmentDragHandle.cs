using CommunityToolkit.Mvvm.ComponentModel;

namespace SymbolDesigner.ViewModels;

/// <summary>段拖拽手柄模型：对应 ThickPolyline 的一条线段（顶点 i 到 i+1）。
/// 用于以线段为整体平移（垂直于线段方向），不是按点调整。
/// 拖动时同一线段两端顶点同步移动（保持段长度不变），选中状态可高亮。
/// X/Y 是画布坐标（线段中点），View 端 TwoWay 绑定。</summary>
public partial class SegmentDragHandle : ObservableObject
{
    /// <summary>段序号（0=第一段，N-1=最后一段）。</summary>
    public int Index { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public SegmentDragHandle(int index, double x, double y)
    {
        Index = index;
        _x = x;
        _y = y;
    }
}